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
                        var pathPtr = IntPtr.Add(buffer, IntPtr.Size == 8 ? 8 : 6);
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

    /// <summary>路径是否匹配 RC003（VID 0x2717 / PID 0x32B8）。</summary>
    public static bool IsTargetDevicePath(string path)
    {
        var upper = path.ToUpperInvariant();
        return upper.Contains(VidPidPattern) || upper.Contains("VID_2717") && upper.Contains("PID_32B8");
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
                    var vk = (ushort)data.vkCode;
                    // 遥控器活跃期原生 F5 一律吞掉：系统按键路径早于 HID 报告回调，Arm 时序不可依赖（含首 down 竞态与 key-repeat）。
                    // 钩子线程内不做任何 IO/日志；副作用是遥控器连接期物理键盘 F5 也被吞，刷新可用 Ctrl+R 替代（文档已注明）。
                    if (vk == 0x74 && Environment.TickCount64 - _lastReportTickCount <= RemoteActiveWindowMs)
                    {
                        Interlocked.Increment(ref _f5SwallowCount);
                        return new IntPtr(1);
                    }
                    if (ShouldSuppress(vk, message))
                    {
                        // 吞掉该事件，避免与注入动作重复
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

/// <summary>设备生命周期：轮询 HID 接口枚举，报告新增/移除（对应 IOHID 匹配/移除回调）。</summary>
public sealed class HidDeviceWatcher : IDisposable
{
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly System.Threading.Timer _timer;
    private volatile bool _running;

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
        List<string> current;
        try
        {
            current = HidNative.EnumerateHidDevicePaths()
                .Where(HidNative.IsTargetDevicePath)
                .ToList();
        }
        catch
        {
            return;
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