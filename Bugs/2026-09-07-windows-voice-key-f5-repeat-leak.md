# 2026-09-07 Windows 版语音键原生 F5 泄漏（钩子从未安装 + key-repeat/竞态三重缺陷）

- 功能点：Windows 版语音键抑制器（遥控器物理语音键产生的原生 F5 系统事件）。
- 触发条件：按住/点按遥控器语音键时，焦点所在应用把 F5 当快捷键（浏览器刷新等）。

## 复现证据

- F13 注入修复（commit `4b82316`）已生效并重启后，用户现场反馈"按一下语音键还是会刷新页面"——注入键 F13 浏览器无响应，刷新只能来自原生 F5 事件泄漏。
- 代码复核发现**第一层根因**：`KeyboardEventSuppressor.Start()`（钩子线程 + `SetWindowsHookExW` 安装）在 Windows 版中**没有任何调用点**（macOS 移植时丢失）——低级键盘钩子从未安装，历史全部抑制逻辑（180ms 窗口、sticky 会话级）从未生效。修复后启动日志首次出现 `HID HOOK installed ok=1`。
- **第二层根因（时序竞态）**：系统把遥控器语音键转换成 F5 走内核键盘栈直通路径，早于用户态 HID 原始报告回调；"收到 HID 报告后才 Arm 抑制窗口"从时序上无法覆盖首个 down；按住期间系统 key-repeat 的 F5 down 同样绕过单发窗口。

## 根因

1. 钩子启动调用缺失，抑制器整体未生效；
2. Arm 时序竞态 + key-repeat 使窗口/sticky 机制对首个 down 与重复 down 均不可靠。

## 修复

- `HidRemoteMonitor.Start()` 现在调用 `_suppressor.Start()` 并记录 `HID HOOK installed ok=<0|1>`；`Start()` 改为返回安装结果（钩子线程 TCS 等待 2 秒）。
- 新增遥控器活跃期 F5 硬拦截：`NotifyRemoteReport()`（HID 报告线程刷新时间戳）；LL 钩子回调内对**非注入 F5** 且最近 30 秒内收到过遥控器报告时一律吞掉（钩子线程内无 IO/日志，计数用 Interlocked）。该策略对首 down 竞态、key-repeat、蓝牙延迟三条泄漏路径全部免疫。
- 诊断日志：语音键按下时输出 `HID VOICEKEY down f5_total=<n>`。
- 已知副作用：SayAll 运行且遥控器活跃（最近 30 秒有遥控器操作）期间，**物理键盘 F5 也被吞**；浏览器刷新可用 Ctrl+R 替代，遥控器闲置 30 秒后物理键盘 F5 自动恢复。该取舍在 README 键位适配节注明。

## 验证

- 构建 0 错误；启动日志 `HID HOOK installed ok=1`（历史版本无此日志，证实钩子此前从未启动）。
- 用户实测边界：语音键不再触发 F5、物理键盘 F5 行为、微信输入法（Ctrl+Win 按住说话）文字上屏——待用户真机确认后在本记录补记结论。
