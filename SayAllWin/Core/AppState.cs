using System.Text.RegularExpressions;

namespace SayAll.Core;

/// <summary>
/// 应用状态编排层（自 BridgeAppModel.swift 的语音会话/统计/录音/回眸逻辑移植）。
/// 蓝牙、音频、HID、语音键映射和 UI 状态的协调层。
/// </summary>
public sealed partial class AppState : IDisposable
{
    public AppSettings Settings { get; }
    public XiaomiBluetoothBridge Bridge { get; private set; }
    public VirtualAudioOutput AudioOutput { get; } = new();
    public HidRemoteMonitor? HidMonitor { get; private set; }
    public SayAllMcpServer? McpServer { get; private set; }

    private VoiceRecordingSession? _recording;
    private bool _voiceSessionActive;
    private DateTime _voiceSessionStartedAt;
    private string _voiceSource = "bluetooth_remote";
    private string? _sessionApp;
    private string? _sessionTextSnapshot;
    private System.Threading.Timer? _transcriptPoll;

    // Fn 点按兼容（Typeless 式）：pre-roll 缓存 + 开始/结束点按
    private bool _fnTapArmed;
    private readonly List<short[]> _preRoll = new();
    private const int PreRollMaxSamples = 8000; // 0.5s @16k

    private bool _disposed;

    public event Action? UiRefreshRequested;

    public AppState(AppSettings settings)
    {
        Settings = settings;
        var targetAddress = settings.CachedDeviceAddress is { } addr
            && ulong.TryParse(addr, out var parsed) ? parsed : (ulong?)null;
        Bridge = new XiaomiBluetoothBridge(settings, targetAddress);
        Bridge.StateChanged += OnBridgeStateChanged;
        Bridge.VoiceStarted += OnVoiceStarted;
        Bridge.VoiceStopped += OnVoiceStopped;
        Bridge.SamplesDecoded += OnSamplesDecoded;
        Bridge.BatteryLevelChanged += _ => UiRefreshRequested?.Invoke();
        Bridge.PowerStateChanged += _ => UiRefreshRequested?.Invoke();
        Bridge.ModelIdentified += _ => UiRefreshRequested?.Invoke();

        HidMonitor = new HidRemoteMonitor(settings, this);
        HidMonitor.StatusChanged += () => UiRefreshRequested?.Invoke();

        AudioOutput.OnConfigurationChange += OnAudioConfigurationChange;

        // 系统唤醒后强制重连（对应 recoverAfterSystemWake）
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;

        ConfigureAudioFromSettings();
        if (settings.CustomMappingEnabled) HidMonitor.Start();
        ApplyAutoStart();
    }

    public void Start()
    {
        Bridge.Start();
    }

    public void Stop()
    {
        Bridge.Stop();
        HidMonitor?.Stop();
        CancelTranscriptPoll();
        _recording?.Finish();
        _recording = null;
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }

    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Resume)
        {
            AppLogger.Write("BLE WAKE recovery_requested");
            Bridge.ReconnectNow();
        }
    }

    private void OnBridgeStateChanged(BluetoothBridgeState state)
    {
        if (state == BluetoothBridgeState.Ready && Bridge.DeviceAddress is { } address)
        {
            Settings.CachedDeviceAddress = address.ToString();
            Settings.Save();
            // 语音/按键链路就绪后确保 HID 监控启动（无论自定义映射是否开启，语音键都需要）
            HidMonitor?.Start();
        }
        UiRefreshRequested?.Invoke();
    }

    private void OnAudioConfigurationChange()
    {
        AppLogger.Write("AUDIO ENGINE configuration_changed");
        ConfigureAudioFromSettings();
        UiRefreshRequested?.Invoke();
    }

    public void ConfigureAudioFromSettings()
    {
        AudioOutput.Configure(Settings.SelectedAudioDeviceId);
        UiRefreshRequested?.Invoke();
    }

    public void RefreshAudioDevices() => UiRefreshRequested?.Invoke();

    public bool PlayTestTone()
    {
        if (_voiceSessionActive)
        {
            return false;
        }
        return AudioOutput.PlayTestTone();
    }

    // ---------------- 语音会话 ----------------

    public void OnRemoteVoiceKeyPressed()
    {
        if (_voiceSessionActive) return;
        Bridge.RequestMicrophoneOpen();
    }

    public void OnRemoteVoiceKeyReleased()
    {
        if (!_voiceSessionActive) return;
        Bridge.RequestMicrophoneClose();
    }

    public bool IsVoiceSessionActive => _voiceSessionActive;

    private void OnVoiceStarted()
    {
        if (_voiceSessionActive) return;
        _voiceSessionActive = true;
        _voiceSessionStartedAt = DateTime.Now;
        _sessionApp = FrontmostAppName();
        _sessionTextSnapshot = null;

        SetupFnTapOnStart();
        CancelTestTone();

        if (Settings.LocalOriginalAudioRecordingEnabled)
        {
            _recording?.Finish();
            _recording = new VoiceRecordingSession(_sessionApp);
        }
        if (Settings.LocalTranscriptHistoryEnabled)
        {
            StartTranscriptPoll();
        }
        Settings.RecordButtonPress("voice", DateTime.Now);
        UiRefreshRequested?.Invoke();
        AppLogger.Write("VOICE SESSION started");
    }

    private void OnVoiceStopped()
    {
        if (!_voiceSessionActive) return;
        _voiceSessionActive = false;
        var endedAt = DateTime.Now;
        var duration = (endedAt - _voiceSessionStartedAt).TotalSeconds;
        Settings.RecordVoiceDuration(duration, _voiceSessionStartedAt, endedAt);
        Settings.Save();

        FinishFnTapOnEnd();
        if (_recording is not null)
        {
            _recording.Finish();
            _recording = null;
        }
        if (Settings.LocalTranscriptHistoryEnabled)
        {
            FinishTranscriptCapture(endedAt);
        }
        UiRefreshRequested?.Invoke();
        AppLogger.Write($"VOICE SESSION ended duration={duration:F1}s");
    }

    private void SetupFnTapOnStart()
    {
        var useFnTap = Settings.VoiceFnTapModeEnabled && Settings.VoiceKeyMode == VoiceKeyMode.Function;
        if (useFnTap)
        {
            _fnTapArmed = true;
            _preRoll.Clear();
            // 开始点按：先让目标工具进入录音，再排空 pre-roll
            KeyboardInjector.TapKey(0x74); // F5 点按（对应 Fn 开始点按）
            FlushPreRoll();
        }
        else
        {
            KeyboardInjector.SetVoiceKeyPressed(Settings.VoiceKeyMode, true);
        }
    }

    private void FinishFnTapOnEnd()
    {
        if (_fnTapArmed)
        {
            _fnTapArmed = false;
            FlushPreRoll();
            AudioOutput.EndSessionAfterDraining(0.75, () => KeyboardInjector.TapKey(0x74));
        }
        else
        {
            KeyboardInjector.SetVoiceKeyPressed(Settings.VoiceKeyMode, false);
            AudioOutput.EndSession();
        }
    }

    private void OnSamplesDecoded(short[] samples)
    {
        if (_fnTapArmed)
        {
            _preRoll.Add(samples);
            var total = _preRoll.Sum(s => s.Length);
            while (total > PreRollMaxSamples)
            {
                var oldest = _preRoll[0];
                AudioOutput.Enqueue(oldest);
                _preRoll.RemoveAt(0);
                total -= oldest.Length;
            }
            return;
        }
        AudioOutput.Enqueue(samples);
        _recording?.Append(samples);
    }

    private void FlushPreRoll()
    {
        foreach (var chunk in _preRoll)
        {
            AudioOutput.Enqueue(chunk);
        }
        _preRoll.Clear();
    }

    private void CancelTestTone()
    {
        AudioOutput.CancelTestTone();
    }

    // ---------------- 回眸（轻量 UIA 文本捕获） ----------------

    private void StartTranscriptPoll()
    {
        CancelTranscriptPoll();
        _sessionTextSnapshot = ReadFocusedText();
        _transcriptPoll = new System.Threading.Timer(_ =>
        {
            var text = ReadFocusedText();
            if (!string.IsNullOrEmpty(text)) _sessionTextSnapshot = text;
        }, null, 600, 600);
    }

    private void CancelTranscriptPoll()
    {
        _transcriptPoll?.Dispose();
        _transcriptPoll = null;
    }

    private void FinishTranscriptCapture(DateTime endedAt)
    {
        CancelTranscriptPoll();
        var text = ReadFocusedText() ?? _sessionTextSnapshot;
        if (string.IsNullOrWhiteSpace(text)) return;
        Settings.AppendTranscript(new AppSettings.TranscriptRecord
        {
            StartedAt = _voiceSessionStartedAt,
            EndedAt = endedAt,
            App = _sessionApp,
            Text = text,
            Source = _voiceSource,
        });
        AppLogger.Write("TRANSCRIPT saved characters=" + text.Length);
    }

    [GeneratedRegex(@"[^\S\r\n]{2,}")]
    private static partial Regex CollapseWhitespaceRegex();

    private static string? ReadFocusedText()
    {
        try
        {
            var element = System.Windows.Automation.AutomationElement.FocusedElement;
            if (element is null) return null;
            var valuePattern = element.GetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern)
                as System.Windows.Automation.ValuePattern;
            var text = valuePattern?.Current.Value;
            if (string.IsNullOrEmpty(text)) return null;
            return CollapseWhitespaceRegex().Replace(text, " ").Trim();
        }
        catch
        {
            return null;
        }
    }

    private static string? FrontmostAppName()
    {
        try
        {
            // 前台窗口标题作为应用标识（不读取内容）
            var hwnd = User32.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;
            var sb = new System.Text.StringBuilder(256);
            User32.GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString();
            return string.IsNullOrWhiteSpace(title) ? null : title;
        }
        catch
        {
            return null;
        }
    }

    public void ToggleLongRecording()
    {
        Settings.ExperimentalContinuousRecordingEnabled = !Settings.ExperimentalContinuousRecordingEnabled;
        Settings.Save();
        AppLogger.Write("LONG RECORDING toggled enabled=" + Settings.ExperimentalContinuousRecordingEnabled);
    }

    // ---------------- 其它入口 ----------------

    public void StartMcpServer()
    {
        if (McpServer is not null) return;
        McpServer = new SayAllMcpServer(Settings);
        McpServer.Start();
        AppLogger.Write("MCP server started");
    }

    public void StopMcpServer()
    {
        McpServer?.Stop();
        McpServer = null;
    }

    public void ApplyAutoStart()
    {
        AutoStart.SetEnabled(Settings.LaunchAtLogin);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        AudioOutput.Dispose();
        HidMonitor?.Dispose();
        StopMcpServer();
    }
}

internal static class User32
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
}

/// <summary>开机自启（对应 LoginItemService：注册表 Run 键）。</summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SayAll";

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    key.SetValue(ValueName, $"\"{exe}\" --tray");
                }
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Write("AUTOSTART failed error=" + ex.Message);
        }
    }
}