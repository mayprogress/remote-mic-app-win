# 无线麦 SayAll — Windows 移植版（SayAllWin）

把小米蓝牙遥控器 2 Pro（RC003）变成 Windows 的无线麦克风：按住遥控器语音键说话，松开停止；语音经 BLE ATVV 通道解码后写入虚拟麦克风设备（如 VB-CABLE），目标应用把「CABLE Output」当作麦克风即可。

本目录是 macOS 版（仓库根目录的 SwiftUI 工程）向 Windows 的同源移植，GPL-3.0 许可证继承自上游项目。所有协议字节、时序常量与 macOS 版保持一致。

## 系统要求

- Windows 10 19041 或更高（64 位）
- .NET SDK 10.0（构建需要；运行可用框架依赖或自包含发布）
- 蓝牙适配器（支持 BLE GATT）
- 小米蓝牙遥控器 2 Pro（RC003），已与系统配对
- 虚拟声卡：推荐 [VB-CABLE](https://vb-audio.com/Cable/)（免费捐赠制）。本程序把 16 kHz 单声道 PCM 渲染到「CABLE Input」，应用从「CABLE Output」录音

## 构建与运行

```powershell
# 或直接运行 build.ps1
dotnet build Windows\SayAllWin\SayAllWin.csproj -c Debug

# 产物
Windows\SayAllWin\bin\Debug\net10.0-windows10.0.19041.0\SayAll.exe
```

启动后进入系统托盘；`SayAll.exe` 直接启动会打开设置窗口，`--tray` 只进托盘，`--mcp-stdio` 进入本地 Agent 访问模式（见下文）。

## 功能映射（macOS → Windows）

| 能力 | macOS 实现 | Windows 实现 |
| --- | --- | --- |
| 蓝牙/ATVV 语音 | CoreBluetooth | WinRT `BluetoothLEDevice`（GATT Notify） |
| 音频输出（虚拟麦克风） | CoreAudio Tap | WASAPI 共享模式渲染到虚拟声卡 |
| 遥控器按键 | IOHID（系统级 seizure） | SetupAPI 枚举 + `ReadFile` 原始报告（1.5s 轮询热插拔） |
| 物理事件抑制 | IOHID seize | `WH_KEYBOARD_LL` 低级钩子在原始报告边缘 180ms 窗口内吞掉对应事件（尽力而为，见限制） |
| 按键注入 | CGEvent | `SendInput` |
| Fn 语音键 | 硬件 Fn（UserKeyMapping） | 遥控器语音键 = HID 用法 0x3E（F5），无 Windows Fn 概念 |
| 系统唤醒重连 | NSWorkspace willSleep/wake | `SystemEvents.PowerModeChanged(Resume)` |
| 开机自启 | SMAppService（Login Item） | HKCU `Run` 注册表键 |
| 本地 Agent 访问 | 独立 SayAllMCP 可执行 | `SayAll.exe --mcp-stdio`（stdio JSON-RPC，只读回眸检索） |
| 应用切换器 | Cmd+Tab 会话 | Alt+Tab 会话（15s 超时语义一致） |

## 键位适配

- 命令键 → Ctrl（复制/粘贴/撤销等全部 Cmd 快捷键转为 Ctrl）
- 显示桌面 → Win+D；重做 → Ctrl+Y；contextMenu → VK_APPS
- fn（语音键）→ 注入 F13：普通模式按住说话期间保持 F13 按下；「Fn 点按」模式改为开始/结束各一次点按（配合 Typeless 等点按式工具），点按模式下语音开头 pre-roll 最多缓存 0.5s，点按完成后写入
- 语音键只支持按下/释放会话生命周期，没有双击/长按触发器（与 macOS 版边界一致）

## 虚拟麦克风设置

1. 安装 VB-CABLE 后重启。
2. 在 SayAll 设置 →「连接与语音」选择输出设备（带 ★ 的为识别出的虚拟声卡）。
3. 目标应用（输入法、会议、录音工具）的输入设备选择「CABLE Output (VB-Audio Virtual Cable)」。
4. 点「发送 1 秒测试音」，目标应用应有 440Hz 提示音。

无虚拟声卡时也可选择任何真实播放设备试听，但应用无法将其选为麦克风。

## 本地 Agent 访问（MCP）

```text
命令：SayAll.exe --mcp-stdio
传输：stdio，换行分隔 JSON-RPC 2.0
方法：initialize / ping / tools/list / tools/call
工具：transcript_search {query?, app?, limit?} — 只读检索回眸文字记录
```

不监听网络端口；只能访问 SayAll 自身保存的回眸记录，不能访问音频或其他应用数据。

## 日志与数据位置

- 日志：`%LOCALAPPDATA%\SayAll\logs\runtime.log`（10 MiB × 3 滚动，脱敏：不含语音内容、蓝牙地址、路径）
- 设置：`%APPDATA%\SayAll\settings.json`
- 回眸记录：`%APPDATA%\SayAll\transcripts.json`（上限 500 条）
- 原始录音（可选）：`Documents\SayAll Recordings\*.wav`（16 kHz 单声道 PCM16）

## 已知限制

- Windows 无法像 macOS 那样「独占/seize」HID 设备：遥控器方向键的原生系统事件通过低级钩子在时间窗口内尽力吞掉，个别前台（管理员权限/游戏）可能仍收到原生事件。
- 「Fn 点按」注入的是 F13 键，不是物理 Fn 键；需在目标工具中把触发键设为 F13。遥控器语音键的 F5 由系统级 Scancode Map 变形为 F13（重启生效），物理键盘 F5 因此失效（Ctrl+R 替代刷新）。
- 未签名构建可能触发 SmartScreen 提示；发布流程遵循仓库 `BRANCH_MANAGEMENT.md` 与免费自签 Authenticode 决策。
- 真实遥控器的语音闭环验收仍在进行中，见 `Testing/WindowsPort.md` 的验证边界。

## 分发到其他电脑

发布自带 .NET 运行时的便携包（目标电脑无需安装 .NET）：

```powershell
dotnet publish Windows\SayAllWin\SayAllWin.csproj -c Release -r win-x64 --self-contained true
Compress-Archive -Path "Windows\SayAllWin\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\*" -DestinationPath SayAll-portable.zip
```

目标电脑要求：Windows 10 19041+ x64、蓝牙适配器、已配对的 RC003 遥控器、VB-CABLE（虚拟声卡）。首次运行会出现 SmartScreen 提示（构建未签名），点「更多信息 → 仍要运行」。设置保存在 `%APPDATA%\SayAll\`，按用户隔离，不随包携带。

## 源码结构

```text
Windows/SayAllWin/
├── App.xaml / App.xaml.cs     # 入口：托盘、单实例、--mcp-stdio CLI 模式
├── MainWindow.xaml(.cs)       # 设置窗口：连接与语音/按键映射/统计/回眸/关于
├── UI/  L10n.cs               # 中英文案（System/zh-Hans/en）
├── UI/  TrayIcon.cs           # 托盘图标与菜单
├── Core/ ATVVProtocol.cs      # ATVV 命令、IMA-ADPCM、后处理、重连策略（同 macOS 常量）
├── Core/ XiaomiBluetoothBridge.cs  # WinRT BLE：发现/连接/订阅/MIC_OPEN/租期延长
├── Core/ HidRemoteMonitor.cs  # 原始 HID 报告 → 手势识别 → 动作执行 + 语音键
├── Core/ HidNative.cs         # SetupAPI 枚举、ReadFile、LL 键盘钩子抑制器
├── Core/ AudioCore.cs         # WASAPI 渲染设备枚举与写入线程、测试音
├── Core/ KeyboardInjector.cs  # SendInput 注入、Alt+Tab 会话、应用启动器
├── Core/ RemoteButtons.cs     # 按键枚举/用法解析/手势状态机（时序同 macOS）
├── Core/ Stores.cs            # 设置/统计/回眸/录音 WAV 会话
├── Core/ AppState.cs          # 编排：会话生命周期、FnTap pre-roll、UIA 回眸捕获
└── Core/ McpServer.cs         # stdio JSON-RPC 只读回眸检索
```
