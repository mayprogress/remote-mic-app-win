using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace SayAll.Core;

/// <summary>蓝牙桥状态（�?BluetoothBridgeState 移植）�?/summary>
public enum BluetoothBridgeState
{
    Stopped,
    BluetoothUnavailable,
    Scanning,
    Connecting,
    Discovering,
    Ready,
    Reconnecting,
    Failed,
}

/// <summary>
/// 小米蓝牙遥控器（RC003）ATVV 语音桥（�?XiaomiBluetoothBridge.swift 移植�?WinRT BLE）�?/// 协议字节、能力协商、状态机、重连退避、ADPCM 解码全部保持原逻辑�?/// </summary>
public sealed class XiaomiBluetoothBridge
{
    private static readonly ATVVCapabilities DefaultCapabilities = new(0x0100, 0x02, 0x03, 120, 0x02, 16000);

    private readonly AppSettings _settings;
    private readonly BluetoothReconnectPolicy _reconnect = new();
    private readonly object _gate = new();

    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEDevice? _device;
    private GattDeviceService? _atvvService;
    private GattCharacteristic? _transmit;
    private GattCharacteristic? _audio;
    private GattCharacteristic? _control;
    private GattCharacteristic? _batteryLevel;
    private GattCharacteristic? _batteryStatus;
    private GattCharacteristic? _modelNumber;

    private ulong? _targetAddress;
    private ulong _generation;
    private string _phase = "stopped";
    private bool _shouldRun;
    private bool _capabilitiesRequested;
    private bool _capabilitiesConfirmed;
    private DateTime? _cancelledMicrophoneOpenAt;
    private bool _microphoneOpened;
    private bool _streaming;
    private byte _sessionID;
    private DateTime? _lastStopAt;
    private (int Predictor, int StepIndex)? _pendingSync;

    private CancellationTokenSource? _reconnectCts;
    private CancellationTokenSource? _connectTimeoutCts;
    private CancellationTokenSource? _initTimeoutCts;
    private CancellationTokenSource? _extendCts;

    private ATVVCapabilities _capabilities = DefaultCapabilities;
    private readonly IMAADPCMDecoder _decoder = new();
    private readonly FrameAccumulator _accumulator = new();

    public BluetoothBridgeState State { get; private set; } = BluetoothBridgeState.Stopped;
    public string? ReadyDeviceName { get; private set; }
    public int? BatteryLevel { get; private set; }
    public RemotePowerState? PowerState { get; private set; }
    public XiaomiRemoteModel? RemoteModel { get; private set; }

    public event Action<BluetoothBridgeState>? StateChanged;
    public event Action? VoiceStarted;
    public event Action? VoiceStopped;
    public event Action<short[]>? SamplesDecoded;
    public event Action<int?>? BatteryLevelChanged;
    public event Action<RemotePowerState?>? PowerStateChanged;
    public event Action<XiaomiRemoteModel>? ModelIdentified;

    public ulong? DeviceAddress => _device?.BluetoothAddress;

    public XiaomiBluetoothBridge(AppSettings settings, ulong? targetAddress = null)
    {
        _settings = settings;
        _targetAddress = targetAddress;
    }

    public void SetTargetAddress(ulong? address)
    {
        _targetAddress = address;
    }

    public void Start()
    {
        lock (_gate) { _shouldRun = true; }
        CancelReconnect();
        _reconnect.Reset();
        BeginConnectionCycle();
    }

    public void Stop()
    {
        lock (_gate) { _shouldRun = false; }
        CancelReconnect();
        StopWatcher();
        CloseMicrophoneIfNeeded();
        DisconnectDevice("stop");
        ResetSession();
        SetState(BluetoothBridgeState.Stopped);
    }

    public void ReconnectNow()
    {
        if (!_shouldRun) return;
        CancelReconnect();
        _reconnect.Reset();
        StopWatcher();
        if (_device is not null && _device.ConnectionStatus == BluetoothConnectionStatus.Connected)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                DisconnectDevice("manual_reconnect");
                ScheduleReconnect(0.1, bypassCachedTarget: false);
            });
            return;
        }
        FinishAttempt(reconnectAfter: 0.1);
    }

    // ---------------- 语音会话命令 ----------------

    public bool RequestMicrophoneOpen()
    {
        lock (_gate)
        {
            if (!CanOpenMicrophone()) return false;
            if (_microphoneOpened || _streaming) return false;
        }
        var command = ATVVProtocol.MicrophoneOpen(_capabilities.Version, _capabilities.SelectedCodec);
        if (!Write(command)) return false;
        _cancelledMicrophoneOpenAt = null;
        _microphoneOpened = true;
        AppLogger.Write("ATVV MIC_OPEN host_request");
        return true;
    }

    public bool RequestMicrophoneExtend()
    {
        lock (_gate)
        {
            if (!_microphoneOpened || !_streaming) return false;
        }
        var command = ATVVProtocol.MicrophoneExtend(_capabilities.Version, _sessionID);
        if (command is null) return false;
        if (!Write(command)) return false;
        AppLogger.Write($"ATVV MIC_EXTEND request session={_sessionID}");
        return true;
    }

    public bool RequestMicrophoneClose()
    {
        lock (_gate)
        {
            if (!_microphoneOpened && !_streaming) return true;
        }
        var cancelledOpenAt = _microphoneOpened && !_streaming ? DateTime.UtcNow : (DateTime?)null;
        var didWrite = Write(ATVVProtocol.MicrophoneClose(_capabilities.Version, _sessionID));
        _microphoneOpened = false;
        _cancelledMicrophoneOpenAt = cancelledOpenAt;
        AppLogger.Write($"ATVV MIC_CLOSE request session={_sessionID} written={didWrite}");
        return didWrite;
    }

    private bool CanOpenMicrophone()
    {
        return _phase.StartsWith("ready") && _capabilitiesConfirmed
            && ATVVProtocol.SupportsAudio(_capabilities.SampleRate);
    }

    // ---------------- 连接周期 ----------------

    private void BeginConnectionCycle()
    {
        if (!_shouldRun) return;
        ulong generation;
        lock (_gate)
        {
            _generation += 1;
            generation = _generation;
            _phase = $"scanning:{generation}";
        }
        AppLogger.Write("BLE SCANNING");
        SetState(BluetoothBridgeState.Scanning);

        // 优先使用缓存的已配对地址（对�?retrievePeripherals�?
        if (_reconnect.AllowsCachedTargetRetrieval && _targetAddress is { } cached)
        {
            _ = TryConnect(cached, generation, source: "target_address", usesCachedTarget: true);
            return;
        }

        StartWatcher(generation);
    }

    private void StartWatcher(ulong generation)
    {
        StopWatcher();
        lock (_gate) { _device = null; _phase = $"scanning:{generation}"; }

        var watcher = new BluetoothLEAdvertisementWatcher
        {
            ScanningMode = BluetoothLEScanningMode.Active,
        };
        watcher.Received += (_, args) => OnAdvertisementReceived(args, generation);
        _watcher = watcher;
        watcher.Start();

        // 同时探测已配对设备（对应 retrieveConnectedPeripherals�?
        _ = ProbePairedDevices(generation);
    }

    private void StopWatcher()
    {
        try
        {
            if (_watcher is not null)
            {
                _watcher.Stop();
                _watcher = null;
            }
        }
        catch { }
    }

    private async Task ProbePairedDevices(ulong generation)
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelector());
            var matched = 0;
            var pairedSeen = false;
            foreach (var info in devices)
            {
                if (!IsCurrentGeneration(generation) || _device is not null) return;
                if (!XiaomiVoiceRemoteNameMatcher.Matches(info.Name)) continue;
                matched += 1;
                if (info.Pairing.IsPaired) pairedSeen = true;
                // 真机实测：部分 HID-over-GATT 配对路径下 WinRT 的 Pairing.IsPaired 可能为 false，
                // 名称白名单命中即尝试连接，不把配对标志作为硬性条件（白名单本身即安全边界）。
                // DeviceInformation.Id 是字符串，需要先解析出真实蓝牙地址
                var resolved = await BluetoothLEDevice.FromIdAsync(info.Id);
                if (resolved is null) continue;
                var address = resolved.BluetoothAddress;
                resolved.Dispose();
                await TryConnect(address, generation, source: "paired_device", usesCachedTarget: false);
                return;
            }
            AppLogger.Write($"BLE PAIRED probe done matched={matched} paired_present={(pairedSeen ? 1 : 0)}");
        }
        catch (Exception ex)
        {
            AppLogger.Write("BLE PAIRED probe_failed error=" + ex.Message);
        }
    }

    private void OnAdvertisementReceived(BluetoothLEAdvertisementReceivedEventArgs args, ulong generation)
    {
        if (!IsCurrentGeneration(generation)) return;
        lock (_gate)
        {
            if (_device is not null) return;
            if (!_phase.StartsWith("scanning")) return;
        }
        var advertisedName = args.Advertisement.LocalName;
        var serviceMatch = args.Advertisement.ServiceUuids.Contains(Guid.Parse(ATVVProtocol.ServiceUUID));
        if (!serviceMatch && !XiaomiVoiceRemoteNameMatcher.Matches(advertisedName)) return;
        _ = TryConnect(args.BluetoothAddress, generation, source: "scan", usesCachedTarget: false);
    }

    private async Task TryConnect(ulong address, ulong generation, string source, bool usesCachedTarget)
    {
        StopWatcher();
        lock (_gate)
        {
            if (!IsCurrentGeneration(generation)) return;
            if (_device is not null) return;
            _phase = $"connecting:{generation}";
        }
        SetState(BluetoothBridgeState.Connecting);
        AppLogger.Write($"BLE CONNECTING source={source}");
        StartConnectTimeout(generation);

        try
        {
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address);
            if (device is null)
            {
                HandleConnectFailed(generation, usesCachedTarget);
                return;
            }
            lock (_gate)
            {
                if (!IsCurrentGeneration(generation)) { device.Dispose(); return; }
                _device = device;
            }
            device.ConnectionStatusChanged += OnConnectionStatusChanged;
            if (device.ConnectionStatus == BluetoothConnectionStatus.Connected)
            {
                _ = OnConnectedFlow(generation);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Write("BLE CONNECT FAILED error=" + ex.Message);
            HandleConnectFailed(generation, usesCachedTarget);
        }
    }

    private void OnConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (!ReferenceEquals(sender, _device)) return;
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Connected)
        {
            ulong generation;
            lock (_gate) { generation = _generation; }
            _ = OnConnectedFlow(generation);
        }
        else
        {
            HandleDisconnect("device_disconnected");
        }
    }

    private async Task OnConnectedFlow(ulong generation)
    {
        CancelConnectTimeout();
        lock (_gate)
        {
            if (!IsCurrentGeneration(generation)) return;
            if (!_phase.StartsWith("connecting")) return;
            _phase = $"discovering:{generation}";
        }
        SetState(BluetoothBridgeState.Discovering);
        StartInitTimeout(generation);
        AppLogger.Write("BLE CONNECTED name_present=" + (string.IsNullOrEmpty(_device!.Name) ? "0" : "1"));

        try
        {
            // 服务发现
            var servicesResult = await _device!.GetGattServicesAsync(BluetoothCacheMode.Uncached);
            if (servicesResult.Status != GattCommunicationStatus.Success)
            {
                FailInitialization("voice_service_discovery_failed");
                return;
            }
            var atvv = servicesResult.Services.FirstOrDefault(s => s.Uuid == Guid.Parse(ATVVProtocol.ServiceUUID));
            if (atvv is null)
            {
                FailInitialization("voice_service_missing");
                return;
            }
            _atvvService = atvv;

            var charsResult = await atvv.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (charsResult.Status != GattCommunicationStatus.Success)
            {
                FailInitialization("voice_channel_discovery_failed");
                return;
            }
            foreach (var c in charsResult.Characteristics)
            {
                if (c.Uuid == Guid.Parse(ATVVProtocol.TransmitUUID)) _transmit = c;
                else if (c.Uuid == Guid.Parse(ATVVProtocol.AudioUUID)) _audio = c;
                else if (c.Uuid == Guid.Parse(ATVVProtocol.ControlUUID)) _control = c;
            }
            if (_transmit is null || _audio is null || _control is null)
            {
                FailInitialization("voice_channel_incomplete");
                return;
            }

            // 订阅音频/控制通知
            var audioSubOk = await SubscribeNotify(_audio);
            var controlSubOk = await SubscribeNotify(_control);
            if (!audioSubOk || !controlSubOk)
            {
                FailInitialization("voice_channel_subscription_failed");
                return;
            }

            // 电池与型号（可选服务）
            var batteryService = servicesResult.Services.FirstOrDefault(s => s.Uuid == Guid.Parse("0000180F-0000-1000-8000-00805F9B34FB"));
            if (batteryService is not null)
            {
                try
                {
                    var batteryChars = await batteryService.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                    foreach (var c in batteryChars.Characteristics)
                    {
                        if (c.Uuid == Guid.Parse("00002A19-0000-1000-8000-00805F9B34FB")) { _batteryLevel = c; await ReadBattery(c); await SubscribeNotify(c); }
                        else if (c.Uuid == Guid.Parse("00002BED-0000-1000-8000-00805F9B34FB")) { _batteryStatus = c; await ReadBatteryStatus(c); await SubscribeNotify(c); }
                    }
                }
                catch { /* 可选服务失败不阻断 */ }
            }
            var infoService = servicesResult.Services.FirstOrDefault(s => s.Uuid == Guid.Parse("0000180A-0000-1000-8000-00805F9B34FB"));
            if (infoService is not null)
            {
                try
                {
                    var modelChars = await infoService.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
                    var model = modelChars.Characteristics.FirstOrDefault(c => c.Uuid == Guid.Parse("00002A24-0000-1000-8000-00805F9B34FB"));
                    if (model is not null) { _modelNumber = model; await ReadModel(model); }
                }
                catch { }
            }

            RequestCapabilitiesIfPossible(generation);
        }
        catch (Exception ex)
        {
            AppLogger.Write("BLE INIT error=" + ex.GetType().Name + ": " + ex.Message);
            AppLogger.Write("BLE INIT stack=" + ex.StackTrace?.Replace("\r\n", " | "));
            FailInitialization("voice_service_discovery_failed");
        }
    }

    private async Task<bool> SubscribeNotify(GattCharacteristic characteristic)
    {
        try
        {
            characteristic.ValueChanged += OnValueChanged;
            var status = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            return status == GattCommunicationStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    private async Task ReadBattery(GattCharacteristic c)
    {
        try
        {
            var result = await c.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (result.Status == GattCommunicationStatus.Success && result.Value.Length >= 1)
            {
                BatteryLevel = DataReader.FromBuffer(result.Value).ReadByte();
                BatteryLevelChanged?.Invoke(BatteryLevel);
                AppLogger.Write($"BLE BATTERY level={BatteryLevel}");
            }
        }
        catch (Exception ex) { AppLogger.Write("BLE BATTERY read_failed error=" + ex.Message); }
    }

    private async Task ReadBatteryStatus(GattCharacteristic c)
    {
        try
        {
            var result = await c.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (result.Status == GattCommunicationStatus.Success)
            {
                var bytes = BufferToBytes(result.Value);
                PowerState = RemotePowerStateParser.Decode(bytes);
                PowerStateChanged?.Invoke(PowerState);
            }
        }
        catch (Exception ex) { AppLogger.Write("BLE POWER read_failed error=" + ex.Message); }
    }

    private async Task ReadModel(GattCharacteristic c)
    {
        try
        {
            var result = await c.ReadValueAsync(BluetoothCacheMode.Uncached);
            if (result.Status == GattCommunicationStatus.Success)
            {
                var bytes = BufferToBytes(result.Value);
                var modelNumber = System.Text.Encoding.UTF8.GetString(bytes).Trim().ToUpperInvariant();
                if (modelNumber.Contains("ARN9"))
                {
                    _decoder.LowNibbleFirst = true;
                    AppLogger.Write("BLE MODEL ARN9 adpcm=low-nibble-first");
                }
                var model = XiaomiRemoteModelHelper.Identified(modelNumber);
                if (model is not null)
                {
                    RemoteModel = model;
                    ModelIdentified?.Invoke(model.Value);
                    AppLogger.Write($"BLE MODEL identified={model}");
                }
            }
        }
        catch (Exception ex) { AppLogger.Write("BLE MODEL read_failed error=" + ex.Message); }
    }

    private void OnValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (sender.Equals(_audio))
        {
            var bytes = BufferToBytes(args.CharacteristicValue);
            HandleAudio(bytes);
        }
        else if (sender.Equals(_control))
        {
            var bytes = BufferToBytes(args.CharacteristicValue);
            HandleControl(bytes);
        }
        else if (sender.Equals(_batteryLevel))
        {
            var bytes = BufferToBytes(args.CharacteristicValue);
            if (bytes.Length >= 1)
            {
                BatteryLevel = bytes[0];
                BatteryLevelChanged?.Invoke(BatteryLevel);
            }
        }
        else if (sender.Equals(_batteryStatus))
        {
            var bytes = BufferToBytes(args.CharacteristicValue);
            PowerState = RemotePowerStateParser.Decode(bytes);
            PowerStateChanged?.Invoke(PowerState);
        }
    }

    private static byte[] BufferToBytes(IBuffer buffer)
    {
        var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[buffer.Length];
        reader.ReadBytes(bytes);
        return bytes;
    }

    /// <summary>控制通道（与 Swift handleControl 一致）�?/summary>
    private void HandleControl(byte[] bytes)
    {
        if (bytes.Length == 0) return;
        var opcode = bytes[0];
        switch (opcode)
        {
            case 0x0B: // 能力
                if (!_phase.StartsWith("awaitingCapabilities")) return;
                var parsed = ATVVCapabilities.Parse(bytes);
                if (parsed is null)
                {
                    FailInitialization("invalid_voice_response");
                    return;
                }
                _capabilities = parsed;
                AppLogger.Write($"ATVV CAPS version={parsed.Version} codec={parsed.SelectedCodec} frame={parsed.FrameSize}");
                if (!ATVVProtocol.SupportsAudio(parsed.SampleRate))
                {
                    RejectUnsupportedAudio("unsupported_16khz_codec");
                    return;
                }
                _capabilitiesConfirmed = true;
                CancelInitTimeout();
                _reconnect.Reset();
                lock (_gate) { _phase = $"ready:{_generation}"; }
                ReadyDeviceName = _device?.Name ?? "MI RC";
                SetState(BluetoothBridgeState.Ready);
                AppLogger.Write("BLE READY");
                break;
            case 0x08: // 遥控器请求开�?
                if (!RequestMicrophoneOpen())
                {
                    AppLogger.Write("ATVV MIC_OPEN remote_request_ignored");
                    return;
                }
                AppLogger.Write("ATVV MIC_OPEN remote_request");
                break;
            case 0x04: // 流开�?
                if (!CanOpenMicrophone())
                {
                    AppLogger.Write("ATVV STREAM_START ignored_not_ready");
                    return;
                }
                if (bytes.Length >= 3)
                {
                    var codec = bytes[2];
                    _capabilities = new ATVVCapabilities(
                        _capabilities.Version, _capabilities.Codecs, bytes[1],
                        _capabilities.FrameSize, codec,
                        codec == 0x02 ? 16000 : 8000);
                }
                if (!ATVVProtocol.SupportsAudio(_capabilities.SampleRate))
                {
                    RejectUnsupportedAudio("unsupported_8khz_codec");
                    return;
                }
                var receivedSessionID = bytes.Length >= 4 ? bytes[3] : (byte)0;
                if (ShouldIgnoreStreamAfterCancelledOpen())
                {
                    _ = Write(ATVVProtocol.MicrophoneClose(_capabilities.Version, receivedSessionID));
                    AppLogger.Write($"ATVV STREAM_START ignored_cancelled session={receivedSessionID}");
                    return;
                }
                _cancelledMicrophoneOpenAt = null;
                _sessionID = receivedSessionID;
                StartStreaming();
                break;
            case 0x00: // 流停�?
                StopStreaming();
                break;
            case 0x0A: // 同步�?
                if (bytes.Length < 7) return;
                var predictor = (short)((bytes[4] << 8) | bytes[5]);
                _pendingSync = (predictor, bytes[6]);
                var partialFrameBytes = _accumulator.Pending.Length;
                if (partialFrameBytes > 0)
                {
                    AppLogger.Write($"ATVV FRAME discarded session={_sessionID} reason=sync partial_frame_bytes={partialFrameBytes}");
                }
                _accumulator.Reset();
                break;
        }
    }

    private void StartStreaming()
    {
        var partialFrameBytes = _accumulator.Pending.Length;
        if (partialFrameBytes > 0)
        {
            AppLogger.Write($"ATVV FRAME discarded session={_sessionID} reason=stream_start streaming_before={_streaming} partial_frame_bytes={partialFrameBytes}");
        }
        _accumulator.Reset();
        _pendingSync = null;
        _decoder.Reset();
        _lastStopAt = null;
        if (_streaming) return;
        _streaming = true;
        AppLogger.Write($"ATVV STREAM START session={_sessionID}");
        StartExtendTimer();
        try { VoiceStarted?.Invoke(); } catch { }
    }

    private void StopStreaming()
    {
        if (!_streaming) return;
        _streaming = false;
        _microphoneOpened = false;
        var partialFrameBytes = _accumulator.Pending.Length;
        _accumulator.Reset();
        _pendingSync = null;
        _lastStopAt = DateTime.UtcNow;
        CancelExtendTimer();
        AppLogger.Write($"ATVV STREAM STOP session={_sessionID} partial_frame_bytes={partialFrameBytes}");
        try { VoiceStopped?.Invoke(); } catch { }
    }

    private void StartExtendTimer()
    {
        CancelExtendTimer();
        _extendCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                while (!_extendCts.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(40), _extendCts.Token);
                    if (_extendCts.IsCancellationRequested) return;
                    // MIC_EXTEND：RC003 �?60 秒语音窗口的租期续约（ATVV 假设�?
                    RequestMicrophoneExtend();
                }
            }
            catch (TaskCanceledException) { }
        });
    }

    private void CancelExtendTimer()
    {
        _extendCts?.Cancel();
        _extendCts?.Dispose();
        _extendCts = null;
    }

    /// <summary>音频通道（与 Swift handleAudio 一致）�?/summary>
    private void HandleAudio(byte[] data)
    {
        if (!CanOpenMicrophone())
        {
            AppLogger.Write("ATVV AUDIO ignored_not_ready");
            return;
        }
        if (ShouldIgnoreStreamAfterCancelledOpen())
        {
            AppLogger.Write("ATVV AUDIO ignored_cancelled_open");
            return;
        }
        _cancelledMicrophoneOpenAt = null;
        if (!_streaming)
        {
            var receivedAt = DateTime.UtcNow;
            if (_lastStopAt is not null && (receivedAt - _lastStopAt.Value).TotalSeconds < 0.3)
            {
                var delayMs = Math.Max(0, (int)((receivedAt - _lastStopAt.Value).TotalMilliseconds));
                AppLogger.Write($"ATVV AUDIO ignored_after_stop session={_sessionID} delay_ms={delayMs} bytes={data.Length}");
                return;
            }
            AppLogger.Write("ATVV STREAM implicit_audio_race");
            StartStreaming();
        }

        foreach (var frame in _accumulator.Append(data, _capabilities.FrameSize))
        {
            if (_pendingSync is { } sync)
            {
                _decoder.Reset(sync.Predictor, sync.StepIndex);
                _pendingSync = null;
            }
            var decoded = _decoder.Decode(frame);
            var samples = PCMPostprocessor.Process(decoded, _settings.GainDB);
            try { SamplesDecoded?.Invoke(samples); } catch { }
        }
    }

    private bool ShouldIgnoreStreamAfterCancelledOpen()
    {
        if (_cancelledMicrophoneOpenAt is null) return false;
        return DateTime.UtcNow < _cancelledMicrophoneOpenAt.Value.AddSeconds(2);
    }

    private void RejectUnsupportedAudio(string reasonKey)
    {
        SetState(BluetoothBridgeState.Failed);
        CloseMicrophoneIfNeeded();
        if (_streaming) StopStreaming();
        else
        {
            _accumulator.Reset();
            _pendingSync = null;
            _decoder.Reset();
        }
        ScheduleReconnect(bypassCachedTarget: true);
        _ = reasonKey;
    }

    private void FailInitialization(string reasonKey)
    {
        _accumulator.Reset();
        _pendingSync = null;
        _decoder.Reset();
        CancelInitTimeout();
        SetState(BluetoothBridgeState.Failed);
        AppLogger.Write($"BLE INIT FAILED reason={reasonKey}");
        ScheduleReconnect(bypassCachedTarget: true);
    }

    private void HandleConnectFailed(ulong generation, bool usesCachedTarget)
    {
        CancelConnectTimeout();
        AppLogger.Write("BLE CONNECT FAILED");
        var delay = _shouldRun ? (_reconnect.NextAutomaticDelay(usesCachedTarget, Random.Shared.NextDouble())) : 0;
        if (!_shouldRun)
        {
            FinishAttempt(reconnectAfter: null);
            return;
        }
        ScheduleReconnect(delay, bypassCachedTarget: usesCachedTarget);
        _ = generation;
    }

    private void HandleDisconnect(string reason)
    {
        CancelConnectTimeout();
        CancelInitTimeout();
        var bypass = _phase.StartsWith("connecting") || _phase.StartsWith("discovering")
            || _phase.StartsWith("awaitingCapabilities");
        AppLogger.Write($"BLE DISCONNECTED phase={_phase} reason={reason}");
        var delay = _shouldRun
            ? (_reconnect.NextAutomaticDelay(bypass, Random.Shared.NextDouble()))
            : 0.0;
        if (!_shouldRun)
        {
            FinishAttempt(reconnectAfter: null);
            return;
        }
        ScheduleReconnect(delay, bypassCachedTarget: bypass);
    }

    private void ScheduleReconnect(double? delay = null, bool bypassCachedTarget = false)
    {
        StopWatcher();
        ResetPeripheral();
        if (!_shouldRun) { SetState(BluetoothBridgeState.Stopped); return; }
        var effective = delay ?? _reconnect.NextAutomaticDelay(bypassCachedTarget, Random.Shared.NextDouble());
        SetState(BluetoothBridgeState.Reconnecting);
        AppLogger.Write($"BLE RECONNECT scheduled failure_count={_reconnect.ConsecutiveFailureCount} delay_ms={(int)(effective * 1000)}");
        CancelReconnect();
        var cts = new CancellationTokenSource();
        _reconnectCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(effective), cts.Token);
                if (cts.IsCancellationRequested) return;
                lock (_gate)
                {
                    if (!_shouldRun) return;
                    _phase = "stopped";
                }
                BeginConnectionCycle();
            }
            catch (TaskCanceledException) { }
        });
        _ = bypassCachedTarget;
    }

    private void FinishAttempt(double? reconnectAfter)
    {
        StopWatcher();
        ResetPeripheral();
        if (!_shouldRun)
        {
            SetState(BluetoothBridgeState.Stopped);
            return;
        }
        if (reconnectAfter is null) { SetState(BluetoothBridgeState.Stopped); return; }
        ScheduleReconnect(reconnectAfter.Value, bypassCachedTarget: true);
    }

    private void CancelReconnect()
    {
        _reconnectCts?.Cancel();
        _reconnectCts?.Dispose();
        _reconnectCts = null;
    }

    private void StartConnectTimeout(ulong generation)
    {
        CancelConnectTimeout();
        _connectTimeoutCts = new CancellationTokenSource();
        var cts = _connectTimeoutCts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), cts.Token);
                if (cts.IsCancellationRequested) return;
                if (!IsCurrentGeneration(generation)) return;
                lock (_gate)
                {
                    if (!_phase.StartsWith("connecting")) return;
                }
                AppLogger.Write("BLE CONNECT TIMEOUT");
                DisconnectDevice("connect_timeout");
                var delay = _reconnect.NextAutomaticDelay(false, Random.Shared.NextDouble());
                ScheduleReconnect(delay, bypassCachedTarget: false);
            }
            catch (TaskCanceledException) { }
        });
    }

    private void CancelConnectTimeout()
    {
        _connectTimeoutCts?.Cancel();
        _connectTimeoutCts?.Dispose();
        _connectTimeoutCts = null;
    }

    private void StartInitTimeout(ulong generation)
    {
        CancelInitTimeout();
        _initTimeoutCts = new CancellationTokenSource();
        var cts = _initTimeoutCts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(8), cts.Token);
                if (cts.IsCancellationRequested) return;
                if (!IsCurrentGeneration(generation)) return;
                lock (_gate)
                {
                    if (!_phase.StartsWith("discovering") && !_phase.StartsWith("awaitingCapabilities")) return;
                }
                FailInitialization("voice_service_timeout");
            }
            catch (TaskCanceledException) { }
        });
    }

    private void CancelInitTimeout()
    {
        _initTimeoutCts?.Cancel();
        _initTimeoutCts?.Dispose();
        _initTimeoutCts = null;
    }

    private void RequestCapabilitiesIfPossible(ulong generation)
    {
        if (_capabilitiesRequested) return;
        if (_transmit is null || _audio is null || _control is null) return;
        lock (_gate)
        {
            if (!IsCurrentGeneration(generation)) return;
            if (!_phase.StartsWith("discovering")) return;
            _phase = $"awaitingCapabilities:{generation}";
        }
        _capabilitiesRequested = true;
        Write(ATVVProtocol.GetCapabilitiesV10);
        SetState(BluetoothBridgeState.Discovering);
        AppLogger.Write("ATVV CAPABILITIES requested");
    }

    private bool Write(byte[] data)
    {
        if (_transmit is null) return false;
        try
        {
            var option = (_transmit.CharacteristicProperties & GattCharacteristicProperties.WriteWithoutResponse) != 0
                ? GattWriteOption.WriteWithoutResponse
                : GattWriteOption.WriteWithResponse;
            var buffer = data.AsBuffer();
            var result = _transmit.WriteValueAsync(buffer, option).AsTask().GetAwaiter().GetResult();
            return result == GattCommunicationStatus.Success || option == GattWriteOption.WriteWithoutResponse;
        }
        catch (Exception ex)
        {
            AppLogger.Write("ATVV WRITE failed error=" + ex.Message);
            return false;
        }
    }

    private void CloseMicrophoneIfNeeded() => _ = RequestMicrophoneClose();

    private void ResetPeripheral()
    {
        lock (_gate)
        {
            _atvvService?.Dispose();
            _atvvService = null;
            _transmit = null;
            _audio = null;
            _control = null;
            _batteryLevel = null;
            _batteryStatus = null;
            _modelNumber = null;
            _capabilitiesRequested = false;
            _capabilitiesConfirmed = false;
            _capabilities = DefaultCapabilities;
            _pendingSync = null;
            _decoder.LowNibbleFirst = false;
            _decoder.Reset();
            _device = null;
        }
        ResetSession();
    }

    private void ResetSession()
    {
        if (_streaming)
        {
            _streaming = false;
            try { VoiceStopped?.Invoke(); } catch { }
        }
        _microphoneOpened = false;
        _cancelledMicrophoneOpenAt = null;
        _sessionID = 0;
        _lastStopAt = null;
        _accumulator.Reset();
        _pendingSync = null;
        CancelExtendTimer();
    }

    private void DisconnectDevice(string reason)
    {
        var device = _device;
        if (device is not null)
        {
            device.ConnectionStatusChanged -= OnConnectionStatusChanged;
        }
        try
        {
            _device?.Dispose();
        }
        catch { }
        _device = null;
        _ = reason;
    }

    private bool IsCurrentGeneration(ulong generation)
    {
        lock (_gate)
        {
            if (!_shouldRun) return false;
            return _generation == generation;
        }
    }

    private void SetState(BluetoothBridgeState state)
    {
        if (State == state) return;
        State = state;
        AppLogger.Write($"BLE STATE {state}");
        try { StateChanged?.Invoke(state); } catch { }
    }
}

internal static class ByteArrayExtensions
{
    public static IBuffer AsBuffer(this byte[] bytes)
    {
        var writer = new DataWriter();
        writer.WriteBytes(bytes);
        return writer.DetachBuffer();
    }
}