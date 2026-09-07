# 2026-09-07 Windows 版语音键原生 F5 泄漏（钩子未启动 + 竞态 + VID 格式 + 路径偏移 + 键盘独占五重缺陷）

- 功能点：Windows 版语音键抑制器（遥控器物理语音键产生的原生 F5 系统事件）与 HID 报告路径。
- 触发条件：按住/点按遥控器语音键时，焦点所在应用把 F5 当快捷键（浏览器刷新等）；语音键映射/注入（微信 Ctrl+Win 模式）完全不工作。

## 复现证据

- F13 注入修复（commit `4b82316`）已生效并重启后，用户现场反馈"按一下语音键还是会刷新页面"——注入键 F13 浏览器无响应，刷新只能来自原生 F5 事件泄漏。
- 代码复核发现**第一层根因**：`KeyboardEventSuppressor.Start()`（钩子线程 + `SetWindowsHookExW` 安装）在 Windows 版中**没有任何调用点**（macOS 移植时丢失）——低级键盘钩子从未安装，历史全部抑制逻辑（180ms 窗口、sticky 会话级）从未生效。修复后启动日志首次出现 `HID HOOK installed ok=1`。
- **第二层根因（时序竞态）**：系统把遥控器语音键转换成 F5 走内核键盘栈直通路径，早于用户态 HID 原始报告回调；"收到 HID 报告后才 Arm 抑制窗口"从时序上无法覆盖首个 down；按住期间系统 key-repeat 的 F5 down 同样绕过单发窗口。
- **第三层根因（VID 匹配格式）**：BLE HID 设备实例 ID 为 `HID\{00001812-…}_DEV_VID&012717_PID&32B8_…`——BLE 用 `VID&012717`（`&01` 总线前缀 + 十进制 0x2717），而 USB 惯用 `VID_2717`。枚举过滤只匹配 USB 格式，`matched=0` 导致永远 waiting_for_device。
- **第四层根因（watcher 竞态）**：`HidDeviceWatcher` 随字段构造即开始轮询并缓存设备路径到 `_known`，`HidRemoteMonitor.Start()` 晚于首个发现事件订阅——发现事件在订阅前触发被丢弃，`_known` 已含路径后永不重发。
- **第五层根因（接口路径偏移）**：`SetupDiGetDeviceInterfaceDetail` 的 `SP_DEVICE_INTERFACE_DETAIL_DATA.DevicePath`（wchar 数组）紧跟 `cbSize`（uint）位于 offset 4；原实现按指针大小读 offset 8，`CreateFile` 收到丢失 `\\?\` 前缀的路径报 123（ERROR_INVALID_NAME）。
- **第六层根因（Windows 键盘独占）**：路径修正后 `CreateFile(GENERIC_READ)` 仍报 5（ERROR_ACCESS_DENIED）——Windows 对键盘类 HID collection 强制独占，用户态进程永远无法直接 ReadFile 键盘接口（与 macOS 需要 IOHIDManager seize 不同的系统边界）。

## 根因（按层）

1. 钩子启动调用缺失，抑制器整体未生效；
2. Arm 时序竞态 + key-repeat 使窗口/sticky 机制对首个 down 与重复 down 均不可靠；
3. BLE `VID&012717` 格式不匹配 `VID_2717` 过滤；
4. watcher 发现事件早于订阅且 `_known` 缓存导致事件被吞；
5. DevicePath 读取偏移错误（+8 应为 +4）；
6. Windows 键盘 HID collection 用户态独占，ReadFile 通道不可行，必须改用 Raw Input。

## 修复

- `HidRemoteMonitor.Start()` 现在调用 `_suppressor.Start()` 并记录 `HID HOOK installed ok=<0|1>`；`Start()` 改为返回安装结果（钩子线程 TCS 等待 2 秒）。
- **VID 双格式匹配**：`VID_2717 || VID&012717`，均要求 `32B8`。
- **报告源切换 Raw Input**：新增 `RawInputListener`（`RegisterRawInputDevices` UsagePage=1/Usage=6 + `RIDEV_INPUTSINK`，message-only 窗口收 `WM_INPUT`，`GetRawInputData` 取 `RAWKEYBOARD`，`GetRawInputDeviceInfo(RIDI_DEVICENAME)` 缓存设备路径并按目标 VID/PID 过滤）。Windows 键盘事件唯一可编程通道即 Raw Input；`HidDeviceReader`（ReadFile 线程）保留但不再接线。`VkToUsage` 做 VK→HID usage 转换（F5↔0x3E 语音键、方向、Return=OK、Esc=Back、Home、Menu）；音量/电源等 consumer page 键不在 keyboard 通道内，保持系统直通。
- **活跃期键位表硬拦截**：`NotifyRemoteReport()` 刷新活跃时间戳；LL 钩子对**非注入事件**且 VK 属于遥控器键位表（F5/方向/OK/Back/Home/Menu）且最近 30 秒有遥控器操作时一律吞掉（钩子线程内无 IO/日志，Interlocked 计数）。扩展自仅 F5 拦截：Raw Input 激活后程序开始执行按键动作，方向键等若不拦系统原生事件会产生"系统 + 注入"双份动作（macOS 的 seize 语义在 Windows 的等效实现）。对首 down 竞态、key-repeat、蓝牙延迟三条泄漏路径全部免疫。
- 诊断日志：`HID POLL total= matched=`（设备总数变化时）、`HID DEVICE connected source=rawinput fingerprint=…`、`HID VOICEKEY down f5_total=<n>`、`RAWINPUT listener ok=<0|1>`。
- 已知副作用：SayAll 运行且遥控器活跃（最近 30 秒有遥控器操作）期间，**物理键盘同键位（F5/方向/Return/Esc/Home/Menu）也被吞**；浏览器刷新可用 Ctrl+R 替代，遥控器闲置 30 秒后自动恢复。该取舍在 README 键位适配节注明。

## 验证

- 构建 0 错误；启动日志链 `HID HOOK installed ok=1` + `RAWINPUT listener ok=1` + BLE READY（历史版本无 HOOK/RAWINPUT 日志，证实此前从未启动）。
- SetupAPI 枚举验证：`HID POLL total=17 matched=1`（VID 双格式修复后命中 RC003）。
- 用户实测边界：语音键不再触发 F5、物理键盘 F5 行为、微信输入法（Ctrl+Win 按住说话）文字上屏——待用户真机确认后在本记录补记结论。
