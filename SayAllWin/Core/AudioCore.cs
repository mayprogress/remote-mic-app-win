using System.Runtime.InteropServices;

namespace SayAll.Core;

/// <summary>
/// Windows Core Audio (WASAPI) COM 互操作。
/// 对应 macOS 端的 CoreAudioDeviceCatalog / VirtualAudioOutput：把 16 kHz 单声道语音
/// 写入用户选择的回环设备（如 VB-CABLE 的 "CABLE Input"），目标应用把 "CABLE Output"
/// 选为麦克风即可收到语音。共享模式下由系统完成重采样。
/// </summary>
public static class AudioNative
{
    public const int ERender = 0;
    public const int ECapture = 1;
    public const int DeviceStateActive = 0x1;
    public const int ClsCtxAll = 0x17;

    // AUDCLNT_STREAMFLAGS
    public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const uint AUDCLNT_STREAMFLAGS_NOPERSIST = 0x00080000;

    public static Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    public class MMDeviceEnumeratorComObject
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient pClient);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint pcDevices);
        [PreserveSig] int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out int pdwState);
    }

    [ComImport, Guid("C8AFBDD0-4B88-45d3-9CEC-AD9E8B0A0C30"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMNotificationClient
    {
        void OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int newState);
        void OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
        void OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId);
        void OnDefaultDeviceChanged(int dataFlow, int role, [MarshalAs(UnmanagedType.LPWStr)] string pwstrDefaultDeviceId);
        void OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string pwstrDeviceId, int key);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PropertyKey pkey);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant pv);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant propvar);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropertyKey
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PropVariant
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr p;
        public int i;
    }

    public static readonly PropertyKey PKEY_Device_FriendlyName = new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14,
    };

    public static string? GetDeviceFriendlyName(IMMDevice device)
    {
        try
        {
            // STGM_READ = 0（此前误传 1=STGM_WRITE 导致打开失败，回退成原始设备 ID）
            device.OpenPropertyStore(0 /* STGM_READ */, out var store);
            var pv = new PropVariant();
            var key = PKEY_Device_FriendlyName;
            string? text = null;
            if (store.GetValue(ref key, out pv) == 0 && pv.vt == 31 /* VT_LPWSTR */ && pv.p != IntPtr.Zero)
            {
                text = Marshal.PtrToStringUni(pv.p);
                // VT_LPWSTR 由 CoTaskMem 分配，直接释放；不使用 P/Invoke PropVariantClear（入口点解析异常）
                Marshal.FreeCoTaskMem(pv.p);
            }
            if (text is null)
            {
                AppLogger.Write($"AUDIO NAME read_failed vt={pv.vt}");
            }
            return text;
        }
        catch (Exception ex)
        {
            AppLogger.Write("AUDIO NAME ex=" + ex.GetType().Name + " msg=" + ex.Message);
            return null;
        }
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, Guid audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint pNumBufferFrames);
        [PreserveSig] int GetStreamLatency(out long phnsLatency);
        [PreserveSig] int GetCurrentPadding(out uint pNumPaddingFrames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr pFormat, IntPtr ppClosestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr ppDeviceFormat);
        [PreserveSig] int GetDevicePeriod(out long phnsDefaultDevicePeriod, out long phnsMinimumDevicePeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid riid, out IntPtr ppInterface);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint numFramesRequested, out IntPtr ppData);
        [PreserveSig] int ReleaseBuffer(uint numFramesWritten, uint dwFlags);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }
}

/// <summary>音频设备描述。</summary>
public sealed class AudioDeviceInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsRender { get; init; }

    public bool IsVirtualCable =>
        Name.Contains("CABLE Input", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("BlackHole", StringComparison.OrdinalIgnoreCase) ||
        Name.Contains("MiRemoteV", StringComparison.OrdinalIgnoreCase);
}

public static class AudioDeviceCatalog
{
    public static List<AudioDeviceInfo> OutputDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        try
        {
            var enumerator = (AudioNative.IMMDeviceEnumerator)new AudioNative.MMDeviceEnumeratorComObject();
            if (enumerator.EnumAudioEndpoints(AudioNative.ERender, AudioNative.DeviceStateActive, out var collection) != 0)
            {
                return devices;
            }
            collection.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                try
                {
                    collection.Item(i, out var device);
                    device.GetId(out var id);
                    var name = AudioNative.GetDeviceFriendlyName(device) ?? id;
                    devices.Add(new AudioDeviceInfo { Id = id, Name = name, IsRender = true });
                }
                catch
                {
                    // 跳过异常设备
                }
            }
        }
        catch
        {
            // 枚举失败返回空列表
        }
        return devices.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>虚拟音频输出：WASAPI 共享模式渲染，16 kHz 单声道 Float32 写入。</summary>
public sealed class VirtualAudioOutput : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<short[]> _pending = new();
    private readonly AutoResetEvent _wake = new(false);

    private Thread? _writer;
    private CancellationTokenSource? _cts;
    private AudioNative.IMMDevice? _device;
    private AudioNative.IAudioClient? _client;
    private AudioNative.IAudioRenderClient? _render;
    private uint _bufferFrames;
    private volatile bool _running;
    private long _pendingSamples;
    private string _statusKey = "connection.audio.none_selected";
    private string? _statusArg;

    public AudioDeviceInfo? SelectedDevice { get; private set; }

    public event Action? OnConfigurationChange;

    public string StatusKey => _statusKey;
    public string? StatusArg => _statusArg;

    public bool IsActive
    {
        get { lock (_gate) return _running && SelectedDevice != null; }
    }

    public long PendingSamples
    {
        get { lock (_gate) return _pendingSamples; }
    }

    public bool HasAllocatedOutputResources
    {
        get
        {
            lock (_gate) return _client != null || _render != null || SelectedDevice != null;
        }
    }

    public bool Configure(string? deviceId)
    {
        Stop();
        if (string.IsNullOrEmpty(deviceId))
        {
            _statusKey = "connection.audio.none_selected";
            _statusArg = null;
            AppLogger.Write("AUDIO CONFIGURE skipped reason=no_selected_device");
            return false;
        }

        var available = AudioDeviceCatalog.OutputDevices();
        var device = available.FirstOrDefault(d => d.Id == deviceId);
        if (device is null)
        {
            _statusKey = "connection.audio.selected_unavailable";
            _statusArg = null;
            AppLogger.Write("AUDIO CONFIGURE failed reason=selected_device_unavailable");
            return false;
        }
        AppLogger.Write($"AUDIO CONFIGURE begin target={{name_present={(string.IsNullOrEmpty(device.Name) ? 0 : 1)},name_hash={AppLogger.NameHash(device.Name)}}}");
        try
        {
            var enumerator = (AudioNative.IMMDeviceEnumerator)new AudioNative.MMDeviceEnumeratorComObject();
            if (enumerator.GetDevice(device.Id, out var endpoint) != 0)
            {
                _statusKey = "connection.audio.selected_unavailable";
                return false;
            }
            var client = ActivateAudioClient(endpoint);
            if (client is null)
            {
                _statusKey = "connection.audio.core_audio_open_failed";
                return false;
            }
            if (client.Initialize(0 /* shared */, AudioNative.AUDCLNT_STREAMFLAGS_NOPERSIST,
                    0, 0, IntPtr.Zero, Guid.Empty) != 0)
            {
                _statusKey = "connection.audio.select_failed";
                Marshal.FinalReleaseComObject(client);
                return false;
            }
            client.GetBufferSize(out var bufferFrames);
            if (client.GetService(ref AudioNative.IID_IAudioRenderClient, out var renderPtr) != 0)
            {
                _statusKey = "connection.audio.select_failed";
                Marshal.FinalReleaseComObject(client);
                return false;
            }

            lock (_gate)
            {
                _device = endpoint;
                _client = client;
                _render = (AudioNative.IAudioRenderClient)Marshal.GetObjectForIUnknown(renderPtr);
                _bufferFrames = bufferFrames;
                SelectedDevice = device;
            }

            _cts = new CancellationTokenSource();
            _writer = new Thread(() => WriterLoop(_cts.Token)) { IsBackground = true, Name = "SayAllAudioWriter" };
            _writer.Start();
            _running = true;

            _statusKey = "connection.audio.current_format";
            _statusArg = device.Name;
            AppLogger.Write($"AUDIO READY target={{name_present=1,name_hash={AppLogger.NameHash(device.Name)}}}");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Write("AUDIO ERROR start_failed error=" + ex.Message);
            _statusKey = "connection.audio.start_failed";
            return false;
        }
    }

    private static AudioNative.IAudioClient? ActivateAudioClient(AudioNative.IMMDevice endpoint)
    {
        var iid = AudioNative.IID_IAudioClient;
        if (endpoint.Activate(ref iid, AudioNative.ClsCtxAll, IntPtr.Zero, out var ptr) != 0)
        {
            return null;
        }
        return (AudioNative.IAudioClient)Marshal.GetObjectForIUnknown(ptr);
    }

    private static readonly float InvShortMax = 1f / 32767f;

    private void WriterLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            short[]? chunk = null;
            lock (_gate)
            {
                if (_pending.Count > 0) chunk = _pending.Dequeue();
            }
            if (chunk is null)
            {
                _wake.WaitOne(10);
                continue;
            }

            // 推送模型：等到端点有足够可用帧再写入；共享模式由系统重采样。
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    _client!.GetCurrentPadding(out var padding);
                    var available = _bufferFrames - padding;
                    if (available >= chunk.Length)
                    {
                        _render!.GetBuffer((uint)chunk.Length, out var ptr);
                        unsafe
                        {
                            var dest = (float*)ptr;
                            for (var i = 0; i < chunk.Length; i++)
                            {
                                dest[i] = chunk[i] * InvShortMax;
                            }
                        }
                        _render!.ReleaseBuffer((uint)chunk.Length, 0);
                        lock (_gate)
                        {
                            _pendingSamples = Math.Max(0, _pendingSamples - chunk.Length);
                        }
                        break;
                    }
                    Thread.Sleep(5);
                }
            }
            catch
            {
                // COM 失败：设备消失，通知配置变化
                TriggerConfigurationChange();
                break;
            }
        }
    }

    private void TriggerConfigurationChange()
    {
        try
        {
            OnConfigurationChange?.Invoke();
        }
        catch
        {
            // 忽略回调异常
        }
    }

    /// <summary>
    /// 入队一段 PCM（16 kHz 单声道），由写入线程送往端点。
    /// 与 macOS enqueue(samples:) 语义一致：配置健康时才接受。
    /// </summary>
    public bool Enqueue(short[] samples)
    {
        if (samples.Length == 0) return false;
        if (!IsActive) return false;
        lock (_gate)
        {
            _pending.Enqueue(samples);
            _pendingSamples += samples.Length;
        }
        _wake.Set();
        return true;
    }

    /// <summary>排空：等待队列播放完成（最长 0.75s）后回调，随后清空播放器（对应 endSessionAfterDraining）。</summary>
    public void EndSessionAfterDraining(double maximumDelay = 0.75, Action? completion = null)
    {
        bool immediate;
        lock (_gate)
        {
            immediate = _pending.Count == 0;
        }
        if (immediate)
        {
            Flush();
            completion?.Invoke();
            return;
        }

        var deadline = DateTime.UtcNow.AddSeconds(maximumDelay);
        System.Threading.Timer? timer = null;
        timer = new System.Threading.Timer(_ =>
        {
            bool drained;
            lock (_gate)
            {
                drained = _pending.Count == 0;
            }
            if (!drained && DateTime.UtcNow < deadline)
            {
                return; // 继续等
            }
            timer!.Dispose();
            Flush();
            completion?.Invoke();
        }, null, 50, 50);
    }

    public void EndSession()
    {
        Flush();
    }

    /// <summary>清空排队缓冲（对应 macOS flushPlayer）。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            _pending.Clear();
            _pendingSamples = 0;
        }
        _wake.Set();
    }

    public bool PlayTestTone()
    {
        if (!IsActive) return false;
        var samples = TestToneGenerator.Samples(16000);
        return Enqueue(samples);
    }

    public void CancelTestTone() => Flush();

    public void Stop()
    {
        _running = false;
        _cts?.Cancel();
        _wake.Set();
        _writer?.Join(500);
        _cts?.Dispose();
        _cts = null;
        _writer = null;

        lock (_gate)
        {
            _pending.Clear();
            _pendingSamples = 0;
            if (_render != null) { Marshal.FinalReleaseComObject(_render); _render = null; }
            if (_client != null) { try { _client.Stop(); } catch { } Marshal.FinalReleaseComObject(_client); _client = null; }
            if (_device != null) { Marshal.FinalReleaseComObject(_device); _device = null; }
            SelectedDevice = null;
            _bufferFrames = 0;
        }
    }

    public void Dispose() => Stop();
}

/// <summary>测试音生成器（440 Hz、1 秒、0.15 振幅，与 TestTone.swift 一致）。</summary>
public static class TestToneGenerator
{
    public const double Duration = 1.0;
    public const double Frequency = 440.0;
    public const double Amplitude = 0.15;

    public static short[] Samples(double sampleRate)
    {
        if (sampleRate <= 0) return [];
        var frameCount = (int)Math.Round(sampleRate * Duration);
        if (frameCount <= 0) return [];
        var peak = (double)short.MaxValue * Amplitude;
        var result = new short[frameCount];
        for (var i = 0; i < frameCount; i++)
        {
            var phase = 2.0 * Math.PI * Frequency * i / sampleRate;
            result[i] = (short)Math.Round(peak * Math.Sin(phase));
        }
        return result;
    }
}