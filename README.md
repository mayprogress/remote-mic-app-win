# 无线麦 SayAll — Windows 适配版（SayAllWin）

把小米蓝牙遥控器 2 Pro（RC003）变成 Windows 的无线麦克风与语音快捷键：按住遥控器语音键说话、松开停止，语音经蓝牙进入电脑；遥控器其余按键成为可自定义的快捷键。

> **项目来源**：本项目是 [HD838A/remote-mic-app](https://github.com/HD838A/remote-mic-app)（macOS 版，SwiftUI）的 **Windows 适配**。`Windows/SayAllWin` 下的全部 C#/XAML 代码是针对 Windows 平台的同源移植实现：所有 ATVV 协议字节、IMA-ADPCM 语音解码、重连时序常量与 macOS 版保持一致，界面与交互对齐 macOS 版布局。
>
> **开源协议**：本项目遵循 **GPL-3.0** 许可证，完整协议文本见 [LICENSE.md](LICENSE.md)。

## 功能概览

| 能力 | Windows 实现 |
| --- | --- |
| 蓝牙 / ATVV 语音 | WinRT `BluetoothLEDevice`（GATT Notify），MIC_OPEN / 租期延长 / 电量与充电状态 |
| 音频输出（虚拟麦克风） | WASAPI 共享模式渲染到所选播放设备（虚拟声卡由使用者自行准备，本程序不捆绑、不安装任何驱动） |
| 遥控器按键 | SetupAPI 枚举 + `ReadFile` 原始报告（1.5 秒轮询热插拔） |
| 物理事件抑制 | `WH_KEYBOARD_LL` 低级钩子在原始报告边缘 180ms 窗口内吞掉对应事件（尽力而为） |
| 按键注入 | `SendInput` |
| 语音键 | 遥控器语音键 = HID 用法 0x3E；Windows 无 Fn 概念，注入 F5（按住 / 点按两种语义） |
| 系统唤醒重连 | `SystemEvents.PowerModeChanged(Resume)` |
| 开机自启 | HKCU `Run` 注册表键 |
| 本地 Agent 访问 | `SayAll.exe --mcp-stdio`（stdio JSON-RPC，只读回眸检索，不监听网络） |
| 应用切换器 | Alt+Tab 会话（15 秒超时语义与 macOS 版一致） |

设置窗口包含五个页面：**连接与语音 / 按键映射 / 统计 / 回眸 / 关于**（中文界面，附英文；按键映射页以原仓库 RC003 实物照片为参照，卡片引线连接到对应按键，聚焦设置框时卡片与引线蓝色高亮）。

## 构建与运行（源码）

从源码构建需要安装支持 `net10.0-windows10.0.19041.0` 目标的 .NET SDK 与 Windows 10 19041+ 环境：

```powershell
dotnet build SayAllWin\SayAllWin.csproj -c Debug

# 自包含发布（产物在本机使用，仓库不分发）
dotnet publish SayAllWin\SayAllWin.csproj -c Release -r win-x64 --self-contained true
```

语音链路说明：程序把遥控器语音渲染到所选播放设备；接收端应用（识别工具、录音软件或任意应用）把对应录音设备选为麦克风即可。**语音转文字由接收端应用完成，本程序不负责识别**，也不限定具体工具。

## 键位适配

- 命令键 → Ctrl（复制/粘贴/撤销等全部 Cmd 快捷键转为 Ctrl）
- 显示桌面 → Win+D；重做 → Ctrl+Y；contextMenu → VK_APPS
- fn（语音键）→ 注入 F5：普通模式按住说话期间保持 F5 按下；「Fn 点按」模式改为开始/结束各一次点按（配合使用方的点按式语音工具），点按模式下语音开头 pre-roll 最多缓存 0.5 秒，点按完成后写入
- 语音键只支持按下/释放会话生命周期，没有双击/长按触发器（与 macOS 版边界一致）

## 仓库范围声明

**本仓库只包含纯净源码**：不分发安装包、可执行程序或运行时环境，不捆绑任何第三方驱动、虚拟声卡或输入法应用；也不包含面向最终用户的发行版安装教程。构建产物请自行从源码生成，使用中涉及的第三方组件由使用者自行准备并遵守其自身许可。

## 目录结构

```text
SayAllWin/
├── App.xaml / App.xaml.cs       # 入口：托盘、单实例、--mcp-stdio CLI 模式
├── MainWindow.xaml(.cs)         # 设置窗口：连接与语音/按键映射/统计/回眸/关于
├── UI/  L10n.cs                 # 中英文案（System/zh-Hans/en）
├── UI/  TrayIcon.cs             # 托盘图标与菜单
├── Core/ ATVVProtocol.cs        # ATVV 命令、IMA-ADPCM、后处理、重连策略（同 macOS 常量）
├── Core/ XiaomiBluetoothBridge.cs  # WinRT BLE：发现/连接/订阅/MIC_OPEN/租期延长
├── Core/ HidRemoteMonitor.cs    # 原始 HID 报告 → 手势识别 → 动作执行 + 语音键
├── Core/ HidNative.cs           # SetupAPI 枚举、ReadFile、LL 键盘钩子抑制器
├── Core/ AudioCore.cs           # WASAPI 渲染设备枚举与写入线程、测试音
├── Core/ KeyboardInjector.cs    # SendInput 注入、Alt+Tab 会话、应用启动器
├── Core/ RemoteButtons.cs       # 按键枚举/用法解析/手势状态机（时序同 macOS）
├── Core/ Stores.cs              # 设置/统计/回眸/录音 WAV 会话
├── Core/ AppState.cs            # 编排：会话生命周期、FnTap pre-roll、UIA 回眸捕获
└── Core/ McpServer.cs           # stdio JSON-RPC 只读回眸检索
```

## 数据与日志位置

- 日志：`%LOCALAPPDATA%\SayAll\logs\runtime.log`（10 MiB × 3 滚动，脱敏：不含语音内容、蓝牙地址、路径）
- 设置：`%APPDATA%\SayAll\settings.json`
- 回眸记录：`%APPDATA%\SayAll\transcripts.json`（上限 500 条）
- 原始录音（可选）：`Documents\SayAll Recordings\*.wav`（16 kHz 单声道 PCM16）

## 已知限制

- Windows 无法像 macOS 那样「独占/seize」HID 设备：遥控器方向键的原生系统事件通过低级钩子在时间窗口内尽力吞掉，个别前台（管理员权限/游戏）可能仍收到原生事件。
- 「Fn 点按」注入的是 F5 键，不是物理 Fn 键；需在使用方工具中把触发键设为 F5。

## 许可证与致谢

- 本项目以 **GPL-3.0** 发布（见 [LICENSE.md](LICENSE.md)），协议与功能设计继承自上游项目。
- 感谢上游 [HD838A/remote-mic-app](https://github.com/HD838A/remote-mic-app) 完成的协议逆向与 macOS 实现——本项目的小米遥控器 ATVV 语音通道、按键手势识别等核心成果均基于上游工作。
