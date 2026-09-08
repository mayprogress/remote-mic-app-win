using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SayAll.Core;

/// <summary>
/// Windows HID 原生访问：SetupAPI 枚举 HID 接口并打开设备读取原始输入报告
/// （对应 macOS IOHIDManager）。同时提供键盘 LL 钩子用于“抑制器”逻辑：
/// 在收到遥控器原始报告后的 180 ms 窗口内抑制匹配的原生系统按键事件。
/// </summary>
public static class HidNative
{
    // SetupAPI
    public static readonly Guid GUID_DEVINTERFACE_HID = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    public const int DIGCF_PRESENT = 0x00000002;
    public const int DIGCF_DEVICEINTERFACE = 0x00000010;

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SP_DEVICE_INTERFACE_DETAIL_DATA
    {
        public uint cbSize;
        // 设备路径：紧随 cbSize（64 位下 8 字节对齐）；通过内存偏移读取。
    }

    // 设备文件访问
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 0x00000001;
    public const uint FILE_SHARE_WRITE = 0x00000002;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
    public const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CancelIoEx(IntPtr hFile, IntPtr lpOverlapped);

    public const int GCR_SELECT = 0x0001;
    public const int GCR_TYPE = 0x0002;

    [DllImport("hid.dll", SetLastError = true)]
    public static extern void HidD_GetHidGuid(out Guid hidGuid);

    // 键盘 LL 钩子
    public const int WH_KEYBOARD_LL = 13;
    public const int WM_KEYDOWN = 0x0100;
    public const int WM_KEYUP = 0x0101;
    public const int WM_SYSKEYDOWN = 0x0104;
    public const int WM_SYSKEYUP = 0x0105;
    public const uint LLKHF_INJECTED = 0x00000010;

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandleW(string lpModuleName);

    [DllImport("user32.dll")]
    public static extern bool GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int pt_x;
        public int pt_y;
    }

    /// <summary>
    /// 枚举所有已连接的 HID 设备接口路径；调用方按 VID/PID 过滤。
    /// </summary>
    public static List<string> EnumerateHidDevicePaths()
    {
        var paths = new List<string>();
        var guid = GUID_DEVINTERFACE_HID;
        var handle = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (handle == INVALID_HANDLE_VALUE || handle == IntPtr.Zero) return paths;

        try
        {
            uint index = 0;
            while (true)
            {
                var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                if (!SetupDiEnumDeviceInterfaces(handle, IntPtr.Zero, ref guid, index, ref ifData))
                {
                    break; // ERROR_NO_MORE_ITEMS
                }
                index++;

                if (!SetupDiGetDeviceInterfaceDetail(handle, ref ifData, IntPtr.Zero, 0, out var required, IntPtr.Zero))
                {
                    if (Marshal.GetLastWin32Error() != 122 /* ERROR_INSUFFICIENT_BUFFER */) continue;
                }
                var buffer = Marshal.AllocHGlobal((int)required);
                try
                {
                    var detail = new SP_DEVICE_INTERFACE_DETAIL_DATA { cbSize = IntPtr.Size == 8 ? 8u : 6u };
                    Marshal.StructureToPtr(detail, buffer, false);
                    if (SetupDiGetDeviceInterfaceDetail(handle, ref ifData, buffer, required, out _, IntPtr.Zero))
                    {
                        // DevicePath（wchar 数组）紧跟 cbSize（uint，4 字节）之后：offset 4。
                        // 此前按指针大小读 offset 8，导致路径丢失 "\\?\" 前缀（CreateFile 报 123）。
                        var pathPtr = IntPtr.Add(buffer, 4);
                        var path = Marshal.PtrToStringUni(pathPtr);
                        if (!string.IsNullOrEmpty(path)) paths.Add(path);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(handle);
        }
        return paths;
    }

    private const string VidPidPattern = "VID_2717&PID_32B8";

    /// <summary>路径是否匹配 RC003。USB 枚举格式为 VID_2717&PID_32B8；BLE HOGP 枚举格式为 {00001812-…}_DEV_VID&012717_PID&32B8_REV&…。</summary>
    public static bool IsTargetDevicePath(string path)
    {
        var upper = path.ToUpperInvariant();
        var vidOk = upper.Contains("VID_2717") || upper.Contains("VID&012717");
        return vidOk && upper.Contains("32B8");
    }
}

/// <summary>
/// 键盘事件抑制器（自 KeyboardEventSuppressor 移植）：在收到遥控器原始报告后的
/// 180ms 窗口内抑制原生系统按键，避免遥控器既产生原始报告又产生系统按键导致双重触发。
/// </summary>
public sealed class KeyboardEventSuppressor : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<ushort, DateTime> _armingTimes = new();
    private readonly HashSet<ushort> _pendingUp = new();
    // 会话级抑制（语音键）：Arm(down) 起持续吞到 Arm(up)，覆盖系统 key-repeat；带超时保护防释放事件丢失后永久吞键。
    private readonly Dictionary<ushort, DateTime> _sticky = new();
    private IntPtr _hook;
    private Thread? _thread;
    private volatile bool _running;
    private readonly HidNative.LowLevelKeyboardProc _proc;

    public bool IsRunning => _running;

    public KeyboardEventSuppressor()
    {
        _proc = HookCallback;
    }

    /// <summary>目标应用应抑制的原生系统按键 → 遥控器按压/释放边沿。sticky=true 时按住期间持续抑制（语音键会话语义）。</summary>
    public void Arm(ushort vk, bool isDown, bool sticky = false)
    {
        lock (_gate)
        {
            if (isDown)
            {
                if (sticky) _sticky[vk] = DateTime.UtcNow;
                _armingTimes[vk] = DateTime.UtcNow;
            }
            else
            {
                _sticky.Remove(vk);
                // 释放事件通常紧随按下；记录为待抑制状态
                _pendingUp.Add(vk);
                _armingTimes[vk] = DateTime.UtcNow;
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _armingTimes.Clear();
            _pendingUp.Clear();
            _sticky.Clear();
        }
    }

    // 遥控器报告活跃窗口：最近收到遥控器 HID 报告后 30 秒内视为遥控器连接期，原生 F5 全程拦截。
    // 背景：系统把遥控器语音键转换成 F5 的内核路径早于用户态 HID 报告回调，按 Arm 时序抑制存在天然竞态，首个 down 会泄漏。
    private long _lastReportTickCount = long.MinValue;
    private long _f5SwallowCount;
    private const long RemoteActiveWindowMs = 30_000;

    /// <summary>HID 报告线程调用：标记遥控器报告活跃（非钩子线程，无 IO）。</summary>
    public void NotifyRemoteReport()
    {
        _lastReportTickCount = Environment.TickCount64;
    }

    /// <summary>累计吞掉的原生 F5 事件数（诊断用）。</summary>
    public long F5SwallowCount => Interlocked.Read(ref _f5SwallowCount);

    /// <summary>启动钩子线程并安装低级键盘钩子；返回安装是否成功。</summary>
    public bool Start()
    {
        if (_running) return _hook != IntPtr.Zero;
        _running = true;
        var installed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            _hook = HidNative.SetWindowsHookExW(HidNative.WH_KEYBOARD_LL, _proc,
                HidNative.GetModuleHandleW("SayAll.exe"), 0);
            installed.TrySetResult(_hook != IntPtr.Zero);
            HidNative.MSG msg;
            while (_running && HidNative.GetMessageW(out msg, IntPtr.Zero, 0, 0))
            {
            }
            if (_hook != IntPtr.Zero)
            {
                HidNative.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        })
        { IsBackground = true, Name = "SayAllKeyHook" };
        _thread.Start();
        try
        {
            return installed.Task.Wait(2000) && installed.Task.Result;
        }
        catch
        {
            return false;
        }
    }

    public void Stop()
    {
        _running = false;
        if (_hook != IntPtr.Zero)
        {
            HidNative.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
        _thread?.Join(300);
        _thread = null;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            if (message is HidNative.WM_KEYDOWN or HidNative.WM_KEYUP
                or HidNative.WM_SYSKEYDOWN or HidNative.WM_SYSKEYUP)
            {
                var data = Marshal.PtrToStructure<HidNative.KBDLLHOOKSTRUCT>(lParam);
                if ((data.flags & HidNative.LLKHF_INJECTED) == 0)
                {
                    // 实测结论：LL 钩子只能吞掉或放行事件，修改 KBDLLHOOKSTRUCT 不影响系统处理（改写 F5→F13 无效已验证）。
                    // F5 的系统级消除由 Scancode Map（HKLM …\Keyboard Layout，scan 0x3E→0x64=F13，重启生效）完成：
                    // 系统从底层收到的就是 F13，Raw Input 收到同一事件且保留真实来源设备标识，
                    // 按键映射按设备路径区分遥控器（→语音键）与物理键盘（→忽略）。
                    // 该钩子仅保留 Arm 窗口抑制（自定义映射场景的注入去重）。
                    if (ShouldSuppress((ushort)data.vkCode, message))
                    {
                        return new IntPtr(1);
                    }
                }
            }
        }
        return HidNative.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private bool ShouldSuppress(ushort vk, int message)
    {
        lock (_gate)
        {
            var isDown = message is HidNative.WM_KEYDOWN or HidNative.WM_SYSKEYDOWN;
            if (_sticky.TryGetValue(vk, out var stickyAt))
            {
                // 语音键会话进行中：按住产生的 down/up（含系统 key-repeat）全部吞掉；
                // 超时保护：释放事件丢失（蓝牙断连）时 65 秒后自动放行，避免永久吞键。
                if (DateTime.UtcNow - stickyAt <= TimeSpan.FromSeconds(65))
                {
                    if (!isDown) _pendingUp.Add(vk);
                    return true;
                }
                _sticky.Remove(vk);
            }
            if (_pendingUp.Remove(vk) && !isDown)
            {
                return true;
            }
            if (_armingTimes.TryGetValue(vk, out var armedAt))
            {
                var elapsed = DateTime.UtcNow - armedAt;
                if (elapsed.TotalMilliseconds <= HidTiming.NativeSuppressWindowMilliseconds)
                {
                    if (isDown)
                    {
                        _pendingUp.Add(vk);
                    }
                    _armingTimes.Remove(vk);
                    return true;
                }
                _armingTimes.Remove(vk);
            }
            return false;
        }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// Raw Input 键盘监听：Windows 对键盘类 HID collection 强制独占（用户态 ReadFile 返回拒绝访问），
/// 键盘事件唯一可编程通道是 Raw Input（RIDEV_INPUTSINK 后台接收，含设备句柄可区分来源）。
/// 提供 (设备路径, 虚拟键, 按下/释放) 边沿事件，供按键映射与语音键驱动使用。
/// </summary>
public sealed class RawInputListener : IDisposable
{
    private const uint WM_INPUT = 0x00FF;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const int GWLP_WNDPROC = -4;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public IntPtr ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUT_KBD
    {
        public RAWINPUTHEADER header;
        public RAWKEYBOARD keyboard;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr pData, ref uint size, uint headerSize);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfoW(IntPtr hDevice, uint command, IntPtr pData, ref uint size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrW(IntPtr hwnd, int index, IntPtr newProc);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProcW(IntPtr prevProc, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetMessageW(out HidNative.MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref HidNative.MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref HidNative.MSG msg);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private IntPtr _hwnd;
    private IntPtr _prevProc = IntPtr.Zero;
    private Thread? _thread;
    private volatile bool _running;
    private readonly Dictionary<IntPtr, string> _deviceNames = new();
    private readonly WndProcDelegate _proc;

    /// <summary>(设备接口路径, VK, 是否按下)。在监听线程回调。</summary>
    public event Action<string, ushort, bool>? DeviceKey;

    public RawInputListener()
    {
        _proc = WndProc;
    }

    public bool Start()
    {
        if (_running) return _hwnd != IntPtr.Zero;
        _running = true;
        var created = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            try
            {
                _hwnd = CreateWindowExW(0, "STATIC", "SayAllRawInput", 0, 0, 0, 0, 0,
                    new IntPtr(-3) /* HWND_MESSAGE */, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (_hwnd == IntPtr.Zero) { created.TrySetResult(false); return; }
                _prevProc = SetWindowLongPtrW(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_proc));
                var dev = new RAWINPUTDEVICE
                {
                    usUsagePage = 1, // Generic Desktop
                    usUsage = 6,     // Keyboard
                    dwFlags = RIDEV_INPUTSINK,
                    hwndTarget = _hwnd,
                };
                var ok = RegisterRawInputDevices(new[] { dev }, 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
                created.TrySetResult(ok);
                while (_running && GetMessageW(out HidNative.MSG msg, IntPtr.Zero, 0, 0))
                {
                    TranslateMessage(ref msg);
                    DispatchMessageW(ref msg);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Write("RAWINPUT exception=" + ex.GetType().Name + " msg=" + ex.Message);
                created.TrySetResult(false);
            }
        })
        { IsBackground = true, Name = "SayAllRawInput" };
        _thread.Start();
        try
        {
            return created.Task.Wait(2000) && created.Task.Result;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _running = false;
        if (_hwnd != IntPtr.Zero)
        {
            PostMessageW(_hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT)
        {
            try { HandleRawInput(lParam); } catch { }
        }
        return _prevProc != IntPtr.Zero
            ? CallWindowProcW(_prevProc, hwnd, msg, wParam, lParam)
            : DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void HandleRawInput(IntPtr lParam)
    {
        var bufSize = (uint)(Marshal.SizeOf<RAWINPUTHEADER>() + Marshal.SizeOf<RAWKEYBOARD>());
        var buf = Marshal.AllocHGlobal((int)bufSize);
        try
        {
            var got = GetRawInputData(lParam, RID_INPUT, buf, ref bufSize, (uint)Marshal.SizeOf<RAWINPUTHEADER>());
            if (got == unchecked((uint)-1)) return;
            var raw = Marshal.PtrToStructure<RAWINPUT_KBD>(buf);
            if (raw.header.dwType != 1 /* RIM_TYPEKEYBOARD */) return;
            var k = raw.keyboard;
            var down = (k.Flags & 0x01) == 0; // RI_KEY_BREAK
            var vk = k.VKey;
            if (vk == 0) return;
            // 诊断：关注键位（遥控器键位+F13）与未知设备，节流记录；物理键盘常规输入不记。
            var watched = vk is 0x74 or 0x7C or 0x25 or 0x26 or 0x27 or 0x28 or 0x0D or 0x1B or 0x24 or 0x5D;
            var name = DeviceNameFor(raw.header.hDevice);
            if (watched)
            {
                AppLogger.Write("RAWINPUT vk=0x" + vk.ToString("X2") + " down=" + (down ? "1" : "0")
                    + " dev=" + (name is null ? "none" : TruncateDev(name)));
            }
            if (name is null || !HidNative.IsTargetDevicePath(name))
            {
                if (watched)
                {
                    AppLogger.Write("RAWINPUT dropped vk=0x" + vk.ToString("X2")
                        + " reason=" + (name is null ? "no_device" : "not_target"));
                }
                return;
            }
            DeviceKey?.Invoke(name, vk, down);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static string TruncateDev(string s) => s.Length > 40 ? s[..40] + "…" : s;

    private string? DeviceNameFor(IntPtr hDevice)
    {
        if (hDevice == IntPtr.Zero) return null;
        lock (_deviceNames)
        {
            if (_deviceNames.TryGetValue(hDevice, out var cached)) return cached;
        }
        var size = 0u;
        _ = GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref size);
        if (size == 0) return null;
        var buf = Marshal.AllocHGlobal((int)size * 2);
        try
        {
            if (GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, buf, ref size) == unchecked((uint)-1)) return null;
            var name = Marshal.PtrToStringUni(buf);
            if (name is not null)
            {
                lock (_deviceNames) { _deviceNames[hDevice] = name; }
            }
            return name;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }
}

/// <summary>设备生命周期：轮询 HID 接口枚举，报告新增/移除（对应 IOHID 匹配/移除回调）。</summary>
public sealed class HidDeviceWatcher : IDisposable
{
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;
    private volatile bool _running;
    private int _lastPollTotal = -1;
    private int _lastMatched = -1;

    public event Action<string>? DeviceAdded;
    public event Action<string>? DeviceRemoved;

    public HidDeviceWatcher()
    {
        _timer = new System.Threading.Timer(_ => Poll(), null, 500, 1500);
        _running = true;
    }

    private void Poll()
    {
        if (!_running) return;
        List<string> all;
        List<string> current;
        try
        {
            all = HidNative.EnumerateHidDevicePaths();
            current = all.Where(HidNative.IsTargetDevicePath).ToList();
        }
        catch (Exception ex)
        {
            AppLogger.Write("HID POLL exception=" + ex.GetType().Name + " msg=" + ex.Message);
            return;
        }
        if (all.Count != _lastPollTotal || current.Count != _lastMatched)
        {
            _lastPollTotal = all.Count;
            _lastMatched = current.Count;
            AppLogger.Write("HID POLL total=" + all.Count + " matched=" + current.Count);
        }
        lock (_gate)
        {
            foreach (var path in current)
            {
                if (_known.Add(path))
                {
                    try { DeviceAdded?.Invoke(path); } catch { }
                }
            }
            foreach (var path in _known.Where(p => !current.Contains(p)).ToList())
            {
                _known.Remove(path);
                try { DeviceRemoved?.Invoke(path); } catch { }
            }
        }
    }

    /// <summary>清空已知设备集并立即重扫：Start 订阅事件后调用，避免 watcher 早于订阅的发现事件被丢弃。</summary>
    public void Rescan()
    {
        lock (_gate)
        {
            _known.Clear();
        }
        Poll();
    }

    public void Dispose()
    {
        _running = false;
        _timer.Dispose();
    }
}

/// <summary>单个 HID 设备读取线程：读原始报告并回调。</summary>
public sealed class HidDeviceReader : IDisposable
{
    private readonly string _path;
    private IntPtr _handle;
    private Thread? _thread;
    private volatile bool _running;

    public string Path => _path;
    public string Fingerprint => DeviceFingerprint.Of(_path) ?? "";

    public event Action<HidDeviceReader, byte, byte[], int>? ReportReceived;

    public HidDeviceReader(string path)
    {
        _path = path;
    }

    public string Open()
    {
        _handle = HidNative.CreateFile(_path,
            HidNative.GENERIC_READ | HidNative.GENERIC_WRITE,
            HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
            IntPtr.Zero, HidNative.OPEN_EXISTING, HidNative.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        if (_handle == HidNative.INVALID_HANDLE_VALUE)
        {
            _handle = HidNative.CreateFile(_path,
                HidNative.GENERIC_READ,
                HidNative.FILE_SHARE_READ | HidNative.FILE_SHARE_WRITE,
                IntPtr.Zero, HidNative.OPEN_EXISTING, HidNative.FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
        }
        if (_handle == HidNative.INVALID_HANDLE_VALUE)
        {
            return $"{Marshal.GetLastWin32Error()}";
        }
        _running = true;
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "SayAllHidRead" };
        _thread.Start();
        return "";
    }

    private void ReadLoop()
    {
        var buffer = new byte[64];
        while (_running)
        {
            if (!HidNative.ReadFile(_handle, buffer, (uint)buffer.Length, out var read, IntPtr.Zero))
            {
                // 设备移除或读取失败
                if (_running) Thread.Sleep(200);
                continue;
            }
            if (read < 1) continue;
            byte reportID = buffer[0];
            var data = new byte[read];
            Buffer.BlockCopy(buffer, 0, data, 0, (int)read);
            try
            {
                ReportReceived?.Invoke(this, reportID, data, (int)read);
            }
            catch
            {
                // 忽略处理异常
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        if (_handle != IntPtr.Zero && _handle != HidNative.INVALID_HANDLE_VALUE)
        {
            HidNative.CancelIoEx(_handle, IntPtr.Zero);
            HidNative.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
        _thread?.Join(300);
        _thread = null;
    }
}