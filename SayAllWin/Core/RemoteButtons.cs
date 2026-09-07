namespace SayAll.Core;

/// <summary>遥控器按键（usage 值自 RemoteButtons.swift 原样保留）�?/summary>
public enum RemoteButton : byte
{
    Power = 0x66,
    Up = 0x52,
    Left = 0x50,
    Ok = 0x28,
    Right = 0x4F,
    Down = 0x51,
    Back = 0xF1,
    VolumeUp = 0x80,
    Home = 0x4A,
    VolumeDown = 0x81,
    Menu = 0x65,
    Tv = 0x35,
}

public enum ButtonTrigger
{
    SingleClick,
    DoubleClick,
    LongPress,
}

/// <summary>普通按键动作（Windows 语义映射�?KeyboardInjector）�?/summary>
public enum ButtonAction
{
    Disabled,
    Escape,
    ReturnKey,
    CommandReturn,
    ShiftReturn,
    CommandCopy,
    CommandPaste,
    CommandClose,
    CommandQuit,
    CommandCut,
    CommandSelectAll,
    CommandUndo,
    CommandRedo,
    CommandFind,
    CommandSave,
    CommandDelete,
    ArrowUp,
    ArrowDown,
    ArrowLeft,
    ArrowRight,
    ScrollUp,
    ScrollDown,
    DeleteBackward,
    ShowDesktop,
    ContextMenu,
    AppSwitcher,
    VolumeUp,
    VolumeDown,
    VolumeMute,
    PlayPause,
    PreviousCommandLeft,
    NextCommandRight,
    CustomShortcut,
    FocusInput,
    OpenCustomApplication,
    ToggleLongRecording,
    OpenCodex,
    OpenClaude,
    OpenCursor,
    OpenWeChat,
    OpenWeCom,
    OpenNeteaseMusic,
    OpenChrome,
    OpenZed,
    OpenSlack,
    OpenTerminal,
    OpenNotepad,
}

public sealed class CustomKeyboardShortcut
{
    /// <summary>Windows 虚拟键码�?/summary>
    public ushort Vk { get; set; }

    /// <summary>修饰键掩码（Ctrl/Alt/Shift/Win），�?Windows 一致�?/summary>
    public byte Modifiers { get; set; }

    /// <summary>展示标签（如 "Ctrl+C"）�?/summary>
    public string Label { get; set; } = "";

    public bool IsStandaloneModifier => IsModifierOnly(Vk);

    public static bool IsModifierOnly(ushort vk) =>
        vk is 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C or 0x5D;
}

/// <summary>单个按键在某个触发方式下的动作配置�?/summary>
public sealed class ConfiguredButtonAction
{
    public ButtonAction Action { get; set; } = ButtonAction.Disabled;
    public CustomKeyboardShortcut? Shortcut { get; set; }
    public string? CustomAppId { get; set; }
}

public static class RemoteButtons
{
    public static readonly RemoteButton[] All =
    [
        RemoteButton.Power, RemoteButton.Up, RemoteButton.Left, RemoteButton.Ok,
        RemoteButton.Right, RemoteButton.Down, RemoteButton.Back, RemoteButton.VolumeUp,
        RemoteButton.Home, RemoteButton.VolumeDown, RemoteButton.Menu, RemoteButton.Tv,
    ];

    public static bool IsDirectionOrOk(RemoteButton b) =>
        b is RemoteButton.Up or RemoteButton.Down or RemoteButton.Left or RemoteButton.Right
            or RemoteButton.Ok or RemoteButton.Back;

    /// <summary>
    /// RC003 HID 报告解析：报�?ID 1，之后每两个字节组成一�?16 �?usage（与
    /// RemoteHIDReportParser 一致）。可选的前导报告 ID 字节会被剥离�?    /// </summary>
    public static HashSet<ushort>? Usages(byte reportID, byte[] data, int offset, int length)
    {
        if (reportID != 1) return null;
        var bytes = data;
        var start = offset;
        var count = length;
        if (count == 7 && bytes[start] == (byte)reportID)
        {
            start += 1;
            count -= 1;
        }
        if (count <= 0 || count % 2 != 0) return null;

        var result = new HashSet<ushort>();
        for (var i = 0; i < count; i += 2)
        {
            var usage = (ushort)(bytes[start + i] | (bytes[start + i + 1] << 8));
            if (usage != 0) result.Add(usage);
        }
        return result;
    }

    public static RemoteButton? ButtonFor(ushort usage) =>
        usage is >= 0x28 and <= 0x81 or 0xF1
            ? (RemoteButton?)FromUsage(usage)
            : null;

    public static RemoteButton? FromUsage(ushort usage) =>
        Enum.IsDefined(typeof(RemoteButton), usage) ? (RemoteButton)usage : null;
}

/// <summary>HID 时序常量（自 HIDRemoteTiming 移植）�?/summary>
public static class HidTiming
{
    public const int DoubleClickMilliseconds = 300;
    public const int LongPressMilliseconds = 550;
    public const int RepeatStartMilliseconds = 350;
    public const int StableReleaseMilliseconds = 600;
    public const int PermissionPollMilliseconds = 1000;
    public const int AppSwitcherTimeoutMilliseconds = 15000;
    public const int AppSwitcherFrontmostPollMilliseconds = 500;
    public const int AppSwitcherConfirmationProbeMilliseconds = 300;
    public const int NativeSuppressWindowMilliseconds = 180;

    public static int? RepeatIntervalMilliseconds(RemoteButton button) => button switch
    {
        RemoteButton.Back => 50,
        RemoteButton.Up or RemoteButton.Down or RemoteButton.Left or RemoteButton.Right
            or RemoteButton.VolumeUp or RemoteButton.VolumeDown => 100,
        _ => null,
    };
}

/// <summary>
/// 单击/双击/长按手势识别器（�?RemoteButtonGestureRecognizer 移植）：
/// 双击等待 300ms，长�?550ms 触发并抑制单击�?/// </summary>
public sealed class RemoteButtonGestureRecognizer
{
    public enum CommandKind
    {
        ScheduleDoubleClickTimeout,
        CancelDoubleClickTimeout,
        ScheduleLongPressTimeout,
        CancelLongPressTimeout,
        Trigger,
    }

    public readonly record struct Command(CommandKind Kind, RemoteButton Button, ButtonTrigger Trigger)
    {
        public static Command ScheduleDoubleClick(RemoteButton b) => new(CommandKind.ScheduleDoubleClickTimeout, b, ButtonTrigger.DoubleClick);
        public static Command CancelDoubleClick(RemoteButton b) => new(CommandKind.CancelDoubleClickTimeout, b, ButtonTrigger.DoubleClick);
        public static Command ScheduleLongPress(RemoteButton b) => new(CommandKind.ScheduleLongPressTimeout, b, ButtonTrigger.LongPress);
        public static Command CancelLongPress(RemoteButton b) => new(CommandKind.CancelLongPressTimeout, b, ButtonTrigger.LongPress);
        public static Command Fire(RemoteButton b, ButtonTrigger t) => new(CommandKind.Trigger, b, t);
    }

    private sealed class State
    {
        public bool IsPressed = true;
        public bool IsSecondPress;
        public bool WaitingForSecondPress;
        public bool LongPressTriggered;
        public bool RecognizesDoubleClick;
        public bool RecognizesLongPress;
    }

    private readonly Dictionary<RemoteButton, State> _states = new();

    public bool IsTracking(RemoteButton button) => _states.ContainsKey(button);

    public List<Command> Press(RemoteButton button, bool recognizesDoubleClick, bool recognizesLongPress)
    {
        if (_states.TryGetValue(button, out var state))
        {
            if (!state.WaitingForSecondPress) return [];
            state.IsPressed = true;
            state.IsSecondPress = true;
            state.WaitingForSecondPress = false;

            var commands = new List<Command> { Command.CancelDoubleClick(button) };
            if (state.RecognizesLongPress) commands.Add(Command.ScheduleLongPress(button));
            return commands;
        }

        _states[button] = new State
        {
            RecognizesDoubleClick = recognizesDoubleClick,
            RecognizesLongPress = recognizesLongPress,
        };
        return recognizesLongPress ? [Command.ScheduleLongPress(button)] : [];
    }

    public List<Command> Release(RemoteButton button)
    {
        if (!_states.TryGetValue(button, out var state) || !state.IsPressed) return [];
        state.IsPressed = false;

        var commands = new List<Command>();
        if (state.RecognizesLongPress) commands.Add(Command.CancelLongPress(button));
        if (state.LongPressTriggered)
        {
            _states.Remove(button);
            return commands;
        }
        if (state.IsSecondPress)
        {
            _states.Remove(button);
            commands.Add(Command.Fire(button, ButtonTrigger.DoubleClick));
            return commands;
        }
        if (state.RecognizesDoubleClick)
        {
            state.WaitingForSecondPress = true;
            commands.Add(Command.ScheduleDoubleClick(button));
            return commands;
        }

        _states.Remove(button);
        commands.Add(Command.Fire(button, ButtonTrigger.SingleClick));
        return commands;
    }

    public List<Command> DoubleClickTimedOut(RemoteButton button)
    {
        if (!_states.TryGetValue(button, out var state) || !state.WaitingForSecondPress || state.IsPressed)
        {
            return [];
        }
        _states.Remove(button);
        return [Command.Fire(button, ButtonTrigger.SingleClick)];
    }

    public List<Command> LongPressTimedOut(RemoteButton button)
    {
        if (!_states.TryGetValue(button, out var state) || !state.IsPressed || !state.RecognizesLongPress)
        {
            return [];
        }
        state.LongPressTriggered = true;
        return [Command.Fire(button, ButtonTrigger.LongPress)];
    }

    public void Reset() => _states.Clear();
}

/// <summary>设备指纹：Raw Input 设备路径�?SHA-256（对�?macOS 的指纹思想）�?/summary>
public static class DeviceFingerprint
{
    public static string? Of(string? devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath)) return null;
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(devicePath));
        return Convert.ToHexStringLower(hash);
    }
}

/// <summary>声音键触发方式。Windows �?Fn 键，Command 侧键映射�?Ctrl（文档说明）�?/summary>
public enum VoiceKeyMode
{
    Function,
    LeftCommand,
    RightCommand,
}

public static class VoiceKeyModeHelper
{
    public static VoiceKeyMode Parse(string raw) => raw switch
    {
        "fn" => VoiceKeyMode.Function,
        "left_command" => VoiceKeyMode.LeftCommand,
        "right_command" => VoiceKeyMode.RightCommand,
        _ => VoiceKeyMode.Function,
    };

    /// <summary>
    /// 语音会话期间注入�?Windows 虚拟键�?    /// fn �?F5（RC003 物理语音键）默认注入 F5 按住；Command �?�?对应 Ctrl�?    /// </summary>
    public static ushort InjectedVk(VoiceKeyMode mode) => mode switch
    {
        VoiceKeyMode.Function => 0x7C, // F13（无系统副作用；原生 F5 由抑制器吞掉）
        VoiceKeyMode.LeftCommand => 0xA2, // LCtrl
        VoiceKeyMode.RightCommand => 0xA3, // RCtrl
        _ => 0x7C,
    };

    public static bool RequiresAccessibility(VoiceKeyMode mode) => mode != VoiceKeyMode.Function;

    public static string Raw(VoiceKeyMode mode) => mode switch
    {
        VoiceKeyMode.Function => "fn",
        VoiceKeyMode.LeftCommand => "left_command",
        VoiceKeyMode.RightCommand => "right_command",
        _ => "fn",
    };

    /// <summary>注入时是否需要同时执�?Fn 化（�?macOS 默认 fn 模式）；Windows 恒为软件注入�?/summary>
    public static bool UsesHardwareMapping(VoiceKeyMode mode) => mode == VoiceKeyMode.Function;
}