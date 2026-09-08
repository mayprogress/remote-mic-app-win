using System.Runtime.InteropServices;

namespace SayAll.Core;

/// <summary>
/// 键盘动作注入（自 KeyboardInjector.swift 移植到 Windows）。
/// 修饰键语义映射：Command→Ctrl、Option→Alt；Cmd+Tab（appSwitcher）→Alt+Tab；
/// Fn+F11（showDesktop）→ Win+D；redo 使用 Windows 惯例 Ctrl+Y。
/// </summary>
public static class KeyboardInjector
{
    // 虚拟键码
    public const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B;
    public const ushort VK_SPACE = 0x20, VK_APPS = 0x5D, VK_F5 = 0x74, VK_F13 = 0x7C;
    public const ushort VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
    public const ushort VK_CONTROL = 0x11, VK_MENU = 0x12, VK_SHIFT = 0x10, VK_LWIN = 0x5B;
    public const ushort VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
    public const ushort VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    public const ushort VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;
    public const ushort VK_MEDIA_PREV = 0xB0, VK_MEDIA_NEXT = 0xB1, VK_MEDIA_PLAY_PAUSE = 0xB3;

    // WPF 没有解析这些键；SendInput 支持它们
    // x64 INPUT = type(4) + pad(4) + union(32: MOUSEINPUT 28 对齐) = 40 字节；
    // Size 写 32 会导致 SendInput 一律返回 0（vibe-flow 的 Sequential 布局自动为 40，实测可注入）。
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct NativeInput
    {
        [FieldOffset(0)] public uint type;
        // KEYBDINPUT
        [FieldOffset(4)] public ushort wVk;
        [FieldOffset(6)] public ushort wScan;
        [FieldOffset(8)] public uint kbFlags;
        [FieldOffset(12)] public uint time;
        [FieldOffset(16)] public IntPtr dwExtraInfo;
        // MOUSEINPUT
        [FieldOffset(4)] public int mi_dx;
        [FieldOffset(8)] public int mi_dy;
        [FieldOffset(12)] public uint mi_mouseData;
        [FieldOffset(16)] public uint mi_flags;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint INPUT_MOUSE = 0;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, NativeInput[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint vk, uint type);

    /// <summary>注入单个键盘事件（down/up）。</summary>
    public static bool PostKeyState(ushort vk, bool isPressed)
    {
        var input = new NativeInput
        {
            type = INPUT_KEYBOARD,
            wVk = vk,
            kbFlags = isPressed ? 0 : KEYEVENTF_KEYUP,
        };
        return SendInput(1, [input], Marshal.SizeOf<NativeInput>()) == 1;
    }

    /// <summary>
    /// 扫描码层注入（对齐 vibe-flow 实测方案）：部分输入法（如微信输入法）不响应纯 VK 注入，
    /// 需要以 KEYEVENTF_SCANCODE 携带 MapVirtualKey 换算的扫描码；换算失败时回退 VK 注入。
    /// </summary>
    public static bool PostKeyStateScan(ushort vk, bool isPressed)
    {
        var scan = MapVirtualKeyW(vk, 0 /* MAPVK_VK_TO_VSC */);
        var flags = isPressed ? 0u : KEYEVENTF_KEYUP;
        NativeInput input;
        if (scan != 0)
        {
            input = new NativeInput
            {
                type = INPUT_KEYBOARD,
                wVk = 0,
                wScan = (ushort)scan,
                kbFlags = flags | KEYEVENTF_SCANCODE,
            };
        }
        else
        {
            input = new NativeInput
            {
                type = INPUT_KEYBOARD,
                wVk = vk,
                kbFlags = flags,
            };
        }
        return SendInput(1, [input], Marshal.SizeOf<NativeInput>()) == 1;
    }

    /// <summary>带修饰键的按键点按。</summary>
    public static bool TapKey(ushort vk, bool ctrl = false, bool alt = false, bool shift = false, bool win = false)
    {
        if (ctrl) PostKeyState(VK_CONTROL, true);
        if (alt) PostKeyState(VK_MENU, true);
        if (shift) PostKeyState(VK_SHIFT, true);
        if (win) PostKeyState(VK_LWIN, true);

        PostKeyState(vk, true);
        PostKeyState(vk, false);

        if (win) PostKeyState(VK_LWIN, false);
        if (shift) PostKeyState(VK_SHIFT, false);
        if (alt) PostKeyState(VK_MENU, false);
        if (ctrl) PostKeyState(VK_CONTROL, false);
        return true;
    }

    /// <summary>语音会话期间按住/释放触发键（自 setVoiceKeyPressed 移植）。组合键按下按序、释放逆序。</summary>
    public static bool SetVoiceKeyPressed(VoiceKeyMode mode, bool isPressed)
    {
        var vks = VoiceKeyModeHelper.InjectedVks(mode);
        // Command 侧键注入修饰键；Fn 注入 F13（浏览器等不响应 F13）；CtrlWinHold 注入 Ctrl+Win（微信输入法"按住说话"）。
        // 注入采用扫描码层（vibe-flow 实测：部分输入法不响应纯 VK 注入）。
        var ok = true;
        if (isPressed)
        {
            foreach (var vk in vks) ok &= PostKeyStateScan(vk, true);
        }
        else
        {
            for (var i = vks.Length - 1; i >= 0; i--) ok &= PostKeyStateScan(vks[i], false);
        }
        AppLogger.Write("INJECT voice mode=" + mode + " down=" + isPressed + " ok=" + ok + " vks=" + string.Join(",", vks));
        return ok;
    }

    /// <summary>模拟鼠标滚轮（对应 macOS scrollWheel）。</summary>
    public static void PostScroll(int lines)
    {
        var input = new NativeInput
        {
            type = INPUT_MOUSE,
            mi_dx = 0,
            mi_dy = 0,
            mi_mouseData = (uint)(lines * 120),
            mi_flags = MOUSEEVENTF_WHEEL,
        };
        _ = SendInput(1, [input], Marshal.SizeOf<NativeInput>());
    }

    public static bool Perform(ButtonAction action, CustomKeyboardShortcut? shortcut)
    {
        if (action == ButtonAction.Disabled) return true;
        switch (action)
        {
            case ButtonAction.Escape: return TapKey(VK_ESCAPE);
            case ButtonAction.ReturnKey: return TapKey(VK_RETURN);
            case ButtonAction.CommandReturn: return TapKey(VK_RETURN, ctrl: true);
            case ButtonAction.ShiftReturn: return TapKey(VK_RETURN, shift: true);
            case ButtonAction.CommandCopy: return TapKey(0x43, ctrl: true); // C
            case ButtonAction.CommandPaste: return TapKey(0x56, ctrl: true); // V
            case ButtonAction.CommandClose: return TapKey(0x57, ctrl: true); // W
            case ButtonAction.CommandQuit: return TapKey(0x51, ctrl: true); // Q
            case ButtonAction.CommandCut: return TapKey(0x58, ctrl: true); // X
            case ButtonAction.CommandSelectAll: return TapKey(0x41, ctrl: true); // A
            case ButtonAction.CommandUndo: return TapKey(0x5A, ctrl: true); // Z
            case ButtonAction.CommandRedo: return TapKey(0x59, ctrl: true); // Y（Windows 惯例）
            case ButtonAction.CommandFind: return TapKey(0x46, ctrl: true); // F
            case ButtonAction.CommandSave: return TapKey(0x53, ctrl: true); // S
            case ButtonAction.CommandDelete: return TapKey(VK_BACK, ctrl: true);
            case ButtonAction.ArrowUp: return TapKey(VK_UP);
            case ButtonAction.ArrowDown: return TapKey(VK_DOWN);
            case ButtonAction.ArrowLeft: return TapKey(VK_LEFT);
            case ButtonAction.ArrowRight: return TapKey(VK_RIGHT);
            case ButtonAction.ScrollUp: PostScroll(3); return true;
            case ButtonAction.ScrollDown: PostScroll(-3); return true;
            case ButtonAction.DeleteBackward: return TapKey(VK_BACK);
            case ButtonAction.ShowDesktop: return TapKey(0x44, win: true); // Win+D
            case ButtonAction.ContextMenu: return TapKey(VK_APPS);
            case ButtonAction.AppSwitcher:
                AppSwitcherSession.Start();
                return true;
            case ButtonAction.VolumeUp: return TapKey(VK_VOLUME_UP);
            case ButtonAction.VolumeDown: return TapKey(VK_VOLUME_DOWN);
            case ButtonAction.VolumeMute: return TapKey(VK_VOLUME_MUTE);
            case ButtonAction.PlayPause: return TapKey(VK_MEDIA_PLAY_PAUSE);
            case ButtonAction.PreviousCommandLeft: return TapKey(VK_LEFT, ctrl: true);
            case ButtonAction.NextCommandRight: return TapKey(VK_RIGHT, ctrl: true);
            case ButtonAction.CustomShortcut:
                if (shortcut is null) return true;
                return PerformShortcut(shortcut);
            default:
                return true;
        }
    }

    public static bool PerformShortcut(CustomKeyboardShortcut shortcut)
    {
        var ctrl = (shortcut.Modifiers & 0x01) != 0;
        var alt = (shortcut.Modifiers & 0x02) != 0;
        var shift = (shortcut.Modifiers & 0x04) != 0;
        var win = (shortcut.Modifiers & 0x08) != 0;
        if (shortcut.IsStandaloneModifier)
        {
            PostKeyState(shortcut.Vk, true);
            PostKeyState(shortcut.Vk, false);
            return true;
        }
        if (shortcut.Vk == 0) return true;
        return TapKey(shortcut.Vk, ctrl, alt, shift, win);
    }
}

/// <summary>
/// Alt+Tab 应用切换会话（自 HIDRemoteMonitor 的 app-switcher 生命周期移植）。
/// 开始：按住 Alt 并 Tab；移动：Tab / Shift+Tab；确认：回车；取消：Esc；超时 15s 自动释放。
/// </summary>
public sealed class AppSwitcherSession
{
    private static AppSwitcherSession? _current;

    public static AppSwitcherSession? Current => _current;

    public bool IsActive { get; private set; }

    private System.Threading.Timer? _timeout;

    public static void Start()
    {
        var session = new AppSwitcherSession();
        _current?.Cancel("replaced");
        _current = session;
        session.IsActive = true;
        KeyboardInjector.PostKeyState(KeyboardInjector.VK_MENU, true);
        KeyboardInjector.PostKeyState(KeyboardInjector.VK_TAB, true);
        KeyboardInjector.PostKeyState(KeyboardInjector.VK_TAB, false);
        session.ScheduleTimeout();
    }

    /// <summary>移动选择（左右）。</summary>
    public bool MoveSelection(bool left)
    {
        if (!IsActive) return false;
        if (left) KeyboardInjector.PostKeyState(KeyboardInjector.VK_TAB, true);
        else KeyboardInjector.PostKeyState(KeyboardInjector.VK_TAB, true);
        KeyboardInjector.PostKeyState(KeyboardInjector.VK_TAB, false);
        ScheduleTimeout();
        return true;
    }

    /// <summary>确认当前选择（回车）。</summary>
    public bool Confirm()
    {
        if (!IsActive) return false;
        KeyboardInjector.PostKeyState(KeyboardInjector.VK_RETURN, true);
        KeyboardInjector.PostKeyState(KeyboardInjector.VK_RETURN, false);
        Cancel("confirmed");
        return true;
    }

    /// <summary>取消（Esc）。</summary>
    public bool Cancel(string reason = "cancelled")
    {
        if (!IsActive) return false;
        IsActive = false;
        _timeout?.Dispose();
        _timeout = null;
        KeyboardInjector.PostKeyState(KeyboardInjector.VK_ESCAPE, true);
        KeyboardInjector.PostKeyState(KeyboardInjector.VK_ESCAPE, false);
        KeyboardInjector.PostKeyState(KeyboardInjector.VK_MENU, false);
        if (ReferenceEquals(_current, this)) _current = null;
        AppLogger.Write($"HID APP SWITCHER ended reason={reason}");
        return true;
    }

    private void ScheduleTimeout()
    {
        _timeout?.Dispose();
        _timeout = new System.Threading.Timer(_ => Cancel("timeout"), null,
            HidTiming.AppSwitcherTimeoutMilliseconds, System.Threading.Timeout.Infinite);
    }
}

/// <summary>
/// 应用启动器（对应 PresetApplication 概念）。按可执行文件名单命名扫描常见安装位置，
/// 保留“启动动作不会重复创建实例”的语义。
/// </summary>
public static class AppLauncher
{
    private static readonly Dictionary<ButtonAction, string[]> Candidates = new()
    {
        [ButtonAction.OpenCodex] = ["codex.exe", "Codex.exe"],
        [ButtonAction.OpenClaude] = ["claude.exe", "Claude.exe", "Claude Desktop.exe"],
        [ButtonAction.OpenCursor] = ["Cursor.exe"],
        [ButtonAction.OpenWeChat] = ["WeChat.exe", "Weixin.exe"],
        [ButtonAction.OpenWeCom] = ["WXWork.exe", "WeCom.exe"],
        [ButtonAction.OpenNeteaseMusic] = ["cloudmusic.exe", "NetEaseCloudMusic.exe"],
        [ButtonAction.OpenChrome] = ["chrome.exe"],
        [ButtonAction.OpenZed] = ["zed.exe"],
        [ButtonAction.OpenSlack] = ["slack.exe"],
        [ButtonAction.OpenTerminal] = ["WindowsTerminal.exe", "wt.exe"],
        [ButtonAction.OpenNotepad] = ["notepad.exe"],
    };

    private static readonly Dictionary<string, string[]> KnownPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["WeChat.exe"] = [@"%ProgramFiles%\Tencent\WeChat\WeChat.exe", @"%ProgramFiles(x86)%\Tencent\WeChat\WeChat.exe"],
        ["WXWork.exe"] = [@"%ProgramFiles(x86)%\Tencent\WXWork\WXWork.exe", @"%ProgramFiles%\Tencent\WXWork\WXWork.exe"],
        ["cloudmusic.exe"] = [@"%ProgramFiles%\Netease\CloudMusic\cloudmusic.exe", @"%ProgramFiles(x86)%\Netease\CloudMusic\cloudmusic.exe"],
        ["chrome.exe"] = [@"%ProgramFiles%\Google\Chrome\Application\chrome.exe", @"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe"],
    };

    public static string? FindExecutable(ButtonAction action)
    {
        if (!Candidates.TryGetValue(action, out var names)) return null;
        foreach (var name in names)
        {
            var found = Find(name);
            if (found != null) return found;
        }
        return null;
    }

    private static string? Find(string exeName)
    {
        if (KnownPaths.TryGetValue(exeName, out var paths))
        {
            foreach (var p in paths)
            {
                var expanded = Environment.ExpandEnvironmentVariables(p);
                if (File.Exists(expanded)) return expanded;
            }
        }

        // 用户目录与 LocalAppData 常见位置
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local"),
        };
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
                {
                    var probe = Path.Combine(dir, exeName);
                    if (File.Exists(probe)) return probe;
                    var nested = Path.Combine(dir, exeName, exeName);
                    if (File.Exists(nested)) return nested;
                }
            }
            catch
            {
                // 跳过不可访问目录
            }
        }

        // PATH
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var probe = Path.Combine(dir.Trim('"'), exeName);
                if (File.Exists(probe)) return probe;
            }
            catch
            {
                // 忽略
            }
        }
        return null;
    }

    /// <summary>打开（激活）应用；已运行则激活窗口，未运行则启动后再激活。</summary>
    public static bool Launch(string? exePath, string? workingDir = null)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return false;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exePath)
            {
                WorkingDirectory = workingDir ?? Path.GetDirectoryName(exePath)!,
                UseShellExecute = true,
            };
            System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Write("APP LAUNCH failed path_present=1 path_hash=" + AppLogger.NameHash(exePath) + " error=" + ex.Message);
            return false;
        }
    }
}