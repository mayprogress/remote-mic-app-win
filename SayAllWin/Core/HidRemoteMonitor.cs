using System.Runtime.InteropServices;

namespace SayAll.Core;

/// <summary>
/// 聚焦当前前台窗口的可编辑输入框（对应 macOS 的 focusFrontmostComposer）。
/// 使用 UI Automation 只读定位，不读取输入内容。
/// </summary>
public static class FocusHelper
{
    public static bool FocusFrontmostInput()
    {
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero) return false;

            var focused = GetFocus();
            if (focused != IntPtr.Zero)
            {
                var className = GetClassNameSafe(focused);
                if (className.Contains("Edit", StringComparison.OrdinalIgnoreCase)
                    || className.Contains("Scintilla", StringComparison.OrdinalIgnoreCase)
                    || className.Contains("RichEdit", StringComparison.OrdinalIgnoreCase)
                    || className.Contains("CodeWindow", StringComparison.OrdinalIgnoreCase)
                    || className.Contains("Windows.UI", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            var uia = System.Windows.Automation.AutomationElement.FromHandle(foreground);
            if (uia is null) return false;
            if (TryFocusEditable(System.Windows.Automation.AutomationElement.FocusedElement)) return true;

            var condition = new System.Windows.Automation.AndCondition(
                new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.IsEnabledProperty, true),
                new System.Windows.Automation.OrCondition(
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.ControlTypeProperty,
                        System.Windows.Automation.ControlType.Edit),
                    new System.Windows.Automation.PropertyCondition(
                        System.Windows.Automation.AutomationElement.ControlTypeProperty,
                        System.Windows.Automation.ControlType.Document)));
            var first = uia.FindFirst(System.Windows.Automation.TreeScope.Descendants, condition);
            return TryFocusEditable(first);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryFocusEditable(System.Windows.Automation.AutomationElement? element)
    {
        try
        {
            if (element is null) return false;
            if (element.TryGetCurrentPattern(System.Windows.Automation.ValuePattern.Pattern, out var pattern)
                && pattern is System.Windows.Automation.ValuePattern)
            {
                element.SetFocus();
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    private static string GetClassNameSafe(IntPtr hwnd)
    {
        try
        {
            var sb = new System.Text.StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
        catch
        {
            return "";
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
}

/// <summary>
/// HID 遥控器监控（自 HIDRemoteMonitor.swift 移植）。
/// 原始 HID 报告 → usage 边沿 → 手势识别（单击/双击/长按）→ 动作执行；
/// usage 0x3E（F5，语音键）按下/释放 → 语音会话控制信号。
/// </summary>
public sealed class HidRemoteMonitor : IDisposable
{
    public const ushort VoiceKeyUsage = 0x3E;

    private readonly AppSettings _settings;
    private readonly AppState _app;

    private readonly KeyboardEventSuppressor _suppressor = new();
    private readonly HidDeviceWatcher _watcher = new();
    private readonly Dictionary<string, HidDeviceReader> _readers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    private readonly Dictionary<RemoteButton, System.Threading.Timer> _repeatTimers = new();
    private readonly Dictionary<RemoteButton, System.Threading.Timer> _doubleClickTimers = new();
    private readonly Dictionary<RemoteButton, System.Threading.Timer> _longPressTimers = new();
    private readonly Dictionary<RemoteButton, System.Threading.Timer> _nonRepeatableReleaseTimers = new();
    private readonly HashSet<RemoteButton> _nonRepeatablePressed = new();
    private readonly RemoteButtonGestureRecognizer _gesture = new();

    private HashSet<ushort> _activeUsages = new();
    private string? _deviceFingerprint;
    private volatile bool _monitoring;

    public string StatusKey { get; private set; } = "button_mapping.status.disabled";
    public event Action? StatusChanged;
    public event Action<string?>? DeviceConnected;

    public HidRemoteMonitor(AppSettings settings, AppState app)
    {
        _settings = settings;
        _app = app;
    }

    public void Start()
    {
        Stop();
        lock (_gate)
        {
            _monitoring = true;
        }
        _suppressor.Clear();
        _watcher.DeviceAdded += OnDeviceAdded;
        _watcher.DeviceRemoved += OnDeviceRemoved;
        UpdateStatus("button_mapping.status.waiting_for_device");
        AppLogger.Write("HID START mode=adaptive windows");
    }

    public void Stop()
    {
        _watcher.DeviceAdded -= OnDeviceAdded;
        _watcher.DeviceRemoved -= OnDeviceRemoved;
        lock (_gate)
        {
            foreach (var reader in _readers.Values) reader.Dispose();
            _readers.Clear();
            _deviceFingerprint = null;
        }
        CancelAllTimers();
        _suppressor.Stop();
        _suppressor.Clear();
        _activeUsages.Clear();
        _gesture.Reset();
        _monitoring = false;
    }

    private void OnDeviceAdded(string path)
    {
        lock (_gate)
        {
            if (!_monitoring) return;
            if (_readers.ContainsKey(path)) return;
            var reader = new HidDeviceReader(path);
            var error = reader.Open();
            if (!string.IsNullOrEmpty(error))
            {
                AppLogger.Write($"HID DEVICE OPEN FAILED path={path} error={error}");
                return;
            }
            reader.ReportReceived += OnReport;
            _readers[path] = reader;
            _deviceFingerprint ??= reader.Fingerprint;
        }
        UpdateStatus("button_mapping.status.connected");
        AppLogger.Write("HID DEVICE connected fingerprint=" + Truncate(_deviceFingerprint));
        try { DeviceConnected?.Invoke(_deviceFingerprint); } catch { }
    }

    private void OnDeviceRemoved(string path)
    {
        lock (_gate)
        {
            if (_readers.Remove(path, out var reader))
            {
                reader.Dispose();
            }
            if (_readers.Count == 0)
            {
                _deviceFingerprint = null;
                ResetInputState();
                UpdateStatus("button_mapping.status.disconnected");
                AppLogger.Write("HID DISCONNECTED");
            }
        }
    }

    private static string Truncate(string? s) =>
        string.IsNullOrEmpty(s) ? "unknown" : (s.Length > 8 ? s[..8] : s) + "…";

    private void OnReport(HidDeviceReader reader, byte reportID, byte[] data, int length)
    {
        var usages = RemoteButtons.Usages(reportID, data, 0, length);
        if (usages is null)
        {
            AppLogger.Write("HID REPORT rejected reason=parse_failed bytes=" + length);
            return;
        }

        // 语音键 usage 0x3E：直接驱动语音会话；其原生 F5 事件由抑制器在会话期间持续吞掉（含 key-repeat）
        var voiceDown = usages.Contains(VoiceKeyUsage);
        var voiceWasDown = _activeUsages.Contains(VoiceKeyUsage);
        if (voiceDown && !voiceWasDown)
        {
            _app.OnRemoteVoiceKeyPressed();
            ArmSuppression(0x74 /* VK_F5 */, true, sticky: true);
        }
        else if (!voiceDown && voiceWasDown)
        {
            _app.OnRemoteVoiceKeyReleased();
            ArmSuppression(0x74, false);
        }

        Process(usages);
    }

    private void Process(HashSet<ushort> usages)
    {
        var pressed = usages.Except(_activeUsages).OrderBy(u => u).ToList();
        var released = _activeUsages.Except(usages).OrderBy(u => u).ToList();
        _activeUsages = new HashSet<ushort>(usages);

        foreach (var usage in pressed)
        {
            if (usage == VoiceKeyUsage) continue;
            var button = RemoteButtons.FromUsage(usage);
            if (button is null) continue;
            HandleButtonPress(button.Value);
        }
        foreach (var usage in released)
        {
            if (usage == VoiceKeyUsage) continue;
            var button = RemoteButtons.FromUsage(usage);
            if (button is null) continue;
            HandleButtonRelease(button.Value);
        }
    }

    private void HandleButtonPress(RemoteButton button)
    {
        if (AppSwitcherSession.Current?.IsActive == true && HandleAppSwitcherPress(button))
        {
            return;
        }

        var doubleClick = _settings.GetConfiguredAction(button, ButtonTrigger.DoubleClick).Action != ButtonAction.Disabled;
        var longPress = _settings.GetConfiguredAction(button, ButtonTrigger.LongPress).Action != ButtonAction.Disabled;
        var action = _settings.GetConfiguredAction(button, ButtonTrigger.SingleClick).Action;

        if (_settings.CustomMappingEnabled)
        {
            ArmNativeForButton(button, true);
        }

        if (doubleClick || longPress || _gesture.IsTracking(button))
        {
            var commands = _gesture.Press(button, doubleClick, longPress);
            ProcessGestureCommands(commands);
            return;
        }

        if (!ShouldAcceptRawPress(button, action))
        {
            return;
        }
        PerformConfiguredAction(button, ButtonTrigger.SingleClick);
        StartRepeatIfNeeded(button, action);
    }

    private void HandleButtonRelease(RemoteButton button)
    {
        CancelRepeat(button);
        if (_settings.CustomMappingEnabled)
        {
            ArmNativeForButton(button, false);
        }
        ScheduleNonRepeatableRelease(button);
        var commands = _gesture.Release(button);
        ProcessGestureCommands(commands);
    }

    private bool HandleAppSwitcherPress(RemoteButton button)
    {
        var session = AppSwitcherSession.Current;
        switch (button)
        {
            case RemoteButton.Ok:
                session?.Confirm();
                return true;
            case RemoteButton.Back:
                session?.Cancel("back");
                return true;
            case RemoteButton.Left:
                session?.MoveSelection(left: true);
                return true;
            case RemoteButton.Right:
            case RemoteButton.Tv:
                session?.MoveSelection(left: false);
                return true;
            default:
                return false;
        }
    }

    private void ProcessGestureCommands(List<RemoteButtonGestureRecognizer.Command> commands)
    {
        foreach (var command in commands)
        {
            switch (command.Kind)
            {
                case RemoteButtonGestureRecognizer.CommandKind.ScheduleDoubleClickTimeout:
                    ScheduleDoubleClickTimeout(command.Button);
                    break;
                case RemoteButtonGestureRecognizer.CommandKind.CancelDoubleClickTimeout:
                    if (_doubleClickTimers.Remove(command.Button, out var dc)) dc.Dispose();
                    break;
                case RemoteButtonGestureRecognizer.CommandKind.ScheduleLongPressTimeout:
                    ScheduleLongPressTimeout(command.Button);
                    break;
                case RemoteButtonGestureRecognizer.CommandKind.CancelLongPressTimeout:
                    if (_longPressTimers.Remove(command.Button, out var lp)) lp.Dispose();
                    break;
                case RemoteButtonGestureRecognizer.CommandKind.Trigger:
                    PerformConfiguredAction(command.Button, command.Trigger);
                    break;
            }
        }
    }

    private void ScheduleDoubleClickTimeout(RemoteButton button)
    {
        if (_doubleClickTimers.Remove(button, out var existing)) existing.Dispose();
        var timer = new System.Threading.Timer(_ =>
        {
            if (_doubleClickTimers.Remove(button, out var t)) t.Dispose();
            ProcessGestureCommands(_gesture.DoubleClickTimedOut(button));
        }, null, HidTiming.DoubleClickMilliseconds, System.Threading.Timeout.Infinite);
        _doubleClickTimers[button] = timer;
    }

    private void ScheduleLongPressTimeout(RemoteButton button)
    {
        if (_longPressTimers.Remove(button, out var existing)) existing.Dispose();
        var timer = new System.Threading.Timer(_ =>
        {
            if (_longPressTimers.Remove(button, out var t)) t.Dispose();
            ProcessGestureCommands(_gesture.LongPressTimedOut(button));
        }, null, HidTiming.LongPressMilliseconds, System.Threading.Timeout.Infinite);
        _longPressTimers[button] = timer;
    }

    private bool ShouldAcceptRawPress(RemoteButton button, ButtonAction action)
    {
        if (ShouldRepeat(action)) return true;
        if (_settings.AllowsRapidPress(button))
        {
            FinishNonRepeatablePress(button);
            return true;
        }
        CancelNonRepeatableRelease(button);
        return _nonRepeatablePressed.Add(button);
    }

    private static bool ShouldRepeat(ButtonAction action) => AllowsRepeat(action);

    private static bool AllowsRepeat(ButtonAction action) => action switch
    {
        ButtonAction.CustomShortcut or ButtonAction.FocusInput or ButtonAction.OpenCustomApplication
            or ButtonAction.CommandReturn or ButtonAction.ShiftReturn or ButtonAction.CommandCopy
            or ButtonAction.CommandPaste or ButtonAction.CommandClose or ButtonAction.CommandQuit
            or ButtonAction.CommandCut or ButtonAction.CommandSelectAll or ButtonAction.CommandUndo
            or ButtonAction.CommandRedo or ButtonAction.CommandFind or ButtonAction.CommandSave
            or ButtonAction.CommandDelete or ButtonAction.PreviousCommandLeft
            or ButtonAction.NextCommandRight or ButtonAction.AppSwitcher
            or ButtonAction.ToggleLongRecording or ButtonAction.OpenCodex or ButtonAction.OpenClaude
            or ButtonAction.OpenCursor or ButtonAction.OpenWeChat or ButtonAction.OpenWeCom
            or ButtonAction.OpenNeteaseMusic or ButtonAction.OpenChrome or ButtonAction.OpenZed
            or ButtonAction.OpenSlack or ButtonAction.OpenTerminal or ButtonAction.OpenNotepad => false,
        _ => true,
    };

    private void StartRepeatIfNeeded(RemoteButton button, ButtonAction action)
    {
        var interval = HidTiming.RepeatIntervalMilliseconds(button);
        if (interval is null) return;
        if (_settings.HasSecondaryAction(button) || action == ButtonAction.Disabled) return;
        if (!ShouldRepeat(action)) return;

        CancelRepeat(button);
        var usage = (ushort)button;
        var timer = new System.Threading.Timer(_ =>
        {
            if (!_activeUsages.Contains(usage)) return;
            if (_settings.HasSecondaryAction(button) || !ShouldRepeat(
                    _settings.GetConfiguredAction(button, ButtonTrigger.SingleClick).Action))
            {
                CancelRepeat(button);
                return;
            }
            PerformConfiguredAction(button, ButtonTrigger.SingleClick);
        }, null, HidTiming.RepeatStartMilliseconds, interval.Value);
        _repeatTimers[button] = timer;
    }

    private void CancelRepeat(RemoteButton button)
    {
        if (_repeatTimers.Remove(button, out var timer)) timer.Dispose();
    }

    private void ScheduleNonRepeatableRelease(RemoteButton button)
    {
        if (!_nonRepeatablePressed.Contains(button)) return;
        CancelNonRepeatableRelease(button);
        var timer = new System.Threading.Timer(_ => FinishNonRepeatablePress(button), null,
            HidTiming.StableReleaseMilliseconds, System.Threading.Timeout.Infinite);
        _nonRepeatableReleaseTimers[button] = timer;
    }

    private void CancelNonRepeatableRelease(RemoteButton button)
    {
        if (_nonRepeatableReleaseTimers.Remove(button, out var timer)) timer.Dispose();
    }

    private void FinishNonRepeatablePress(RemoteButton button)
    {
        CancelNonRepeatableRelease(button);
        _nonRepeatablePressed.Remove(button);
    }

    private void PerformConfiguredAction(RemoteButton button, ButtonTrigger trigger)
    {
        var configured = _settings.GetConfiguredAction(button, trigger);
        var action = configured.Action;

        if (action == ButtonAction.AppSwitcher)
        {
            if (AppSwitcherSession.Current?.IsActive == true)
            {
                AppSwitcherSession.Current.MoveSelection(left: false);
            }
            else
            {
                AppSwitcherSession.Start();
            }
            return;
        }

        if (action == ButtonAction.FocusInput)
        {
            FocusHelper.FocusFrontmostInput();
            AppLogger.Write($"HID BUTTON button={button} trigger={trigger} action={action}");
            return;
        }

        if (action == ButtonAction.OpenCustomApplication)
        {
            if (configured.CustomAppId is not null
                && _settings.CustomApps.TryGetValue(configured.CustomAppId, out var app)
                && app.Path is not null)
            {
                AppLauncher.Launch(app.Path);
                return;
            }
            return;
        }

        if (action is ButtonAction.OpenCodex or ButtonAction.OpenClaude or ButtonAction.OpenCursor
            or ButtonAction.OpenWeChat or ButtonAction.OpenWeCom or ButtonAction.OpenNeteaseMusic
            or ButtonAction.OpenChrome or ButtonAction.OpenZed or ButtonAction.OpenSlack
            or ButtonAction.OpenTerminal or ButtonAction.OpenNotepad)
        {
            AppLauncher.Launch(AppLauncher.FindExecutable(action));
            return;
        }

        if (action == ButtonAction.ToggleLongRecording)
        {
            _app.ToggleLongRecording();
            return;
        }

        KeyboardInjector.Perform(action, configured.Shortcut);
        _settings.RecordButtonPress(button.ToString(), DateTime.Now);
        AppLogger.Write($"HID BUTTON button={button} trigger={trigger} action={action}");
    }

    private void ArmNativeForButton(RemoteButton button, bool isDown)
    {
        var vk = NativeVkForButton(button);
        if (vk is not null) ArmSuppression(vk.Value, isDown);
    }

    private void ArmSuppression(ushort vk, bool isDown, bool sticky = false)
    {
        // 语音键 F5 始终抑制（所有模式下语音键都用于语音）；其它键仅自定义映射开启时抑制
        if (vk != 0x74 && !_settings.CustomMappingEnabled) return;
        _suppressor.Arm(vk, isDown, sticky);
    }

    public static ushort? NativeVkForButton(RemoteButton button) => button switch
    {
        RemoteButton.Power => 0x5F,   // VK_SLEEP（尽力抑制；部分系统电源事件不可拦截）
        RemoteButton.Up => 0x26,
        RemoteButton.Left => 0x25,
        RemoteButton.Ok => 0x0D,
        RemoteButton.Right => 0x27,
        RemoteButton.Down => 0x28,
        RemoteButton.Back => 0x08,
        RemoteButton.VolumeUp => 0xAF,
        RemoteButton.Home => 0x24,    // VK_HOME
        RemoteButton.VolumeDown => 0xAE,
        RemoteButton.Menu => 0x5D,    // VK_APPS
        RemoteButton.Tv => null,      // 自定义 usage，系统无原生键
        _ => null,
    };

    private void ResetInputState()
    {
        CancelAllTimers();
        if (AppSwitcherSession.Current?.IsActive == true)
        {
            AppSwitcherSession.Current.Cancel("input_reset");
        }
        _activeUsages.Clear();
        _gesture.Reset();
        _nonRepeatablePressed.Clear();
    }

    private void CancelAllTimers()
    {
        foreach (var timer in _repeatTimers.Values) timer.Dispose();
        _repeatTimers.Clear();
        foreach (var timer in _doubleClickTimers.Values) timer.Dispose();
        _doubleClickTimers.Clear();
        foreach (var timer in _longPressTimers.Values) timer.Dispose();
        _longPressTimers.Clear();
        foreach (var timer in _nonRepeatableReleaseTimers.Values) timer.Dispose();
        _nonRepeatableReleaseTimers.Clear();
    }

    private void UpdateStatus(string key)
    {
        StatusKey = key;
        AppLogger.Write("HID STATUS " + key);
        try { StatusChanged?.Invoke(); } catch { }
    }

    public void Dispose() => Stop();
}