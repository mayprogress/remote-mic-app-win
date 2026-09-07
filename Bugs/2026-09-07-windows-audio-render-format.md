# 2026-09-07 Windows 版音频渲染：Initialize 空格式指针 / 缺 Start / 写入格式不匹配

- 功能点：Windows 版语音输出（WASAPI 渲染到所选播放设备）与「发送测试音」。
- 触发条件：启动后自动恢复所选设备显示成功（UI 下拉显示 ★CABLE Input），但点「发送测试音」提示"无法发送测试音"。

## 复现证据

- 用户现场截图：语音输出下拉已显示 CABLE Input，点「发送测试音」弹"无法发送测试音：请先选择语音输出设备…"。
- 日志：每次启动有 `AUDIO CONFIGURE begin`，从未出现 `AUDIO READY`，也无任何失败日志（静默失败路径未记日志）。
- settings.json 中 `SelectedAudioDeviceId={0.0.0.00000000}.{3e6ef71a-…}` 与当前 CABLE Input 端点 ID 一致，排除设备失效。
- 独立诊断程序（复刻 Configure 的 COM 序列，对全部 9 个活动渲染端点逐步执行）：`Activate` 与 `GetMixFormat` 全部成功；`Initialize(shared, pFormat=null)` 一律返回 `0x80004003 (E_POINTER)`；`Initialize(shared, pFormat=mixFormat)` 全部成功。

## 根因（三层，均在移植代码）

1. 共享模式 `IAudioClient.Initialize` 的 `pFormat` 传了 `IntPtr.Zero`。WASAPI 共享模式要求传入混合格式指针，`null` 返回 E_POINTER，导致所有端点一律配置失败。
2. `Initialize` 成功后未调用 `IAudioClient.Start()`，渲染客户端不启动则写入的缓冲不会被端点消费。
3. `WriterLoop` 把 16 kHz 单声道 PCM16 样本直接按端点帧写入（帧数=样本数、单 float 通道），与端点混合格式（如 48 kHz 立体声 float32）不匹配；即使前三步成功也会导致 3 倍速播放与声道错乱。

## 修复

- `Configure`：`GetMixFormat` → 解析声道/采样率/位深 → `Initialize` 传入混合格式指针 → `Start()`；位深仅支持 32/16，混合格式指针用后 `FreeCoTaskMem`。
- `WriterLoop`：16 kHz → 端点采样率线性插值重采样，按声道数展开写入；按端点可用帧数分块，帧数按采样率比例换算；支持 float32 与 int16 两种端点位深。
- 日志补齐：`get_device`/`activate_failed`/`mix_format`/`unsupported_mix`/`initialize`/`start`/`render_client` 各失败路径带 HRESULT；`AppState.PlayTestTone` 记录 `AUDIO TESTTONE queued / rejected reason=…`。

## 验证

- 构建 0 错误；启动日志首次出现 `AUDIO READY`。
- 独立环回测试（诊断程序直接向 CABLE Input 写 2 秒 440 Hz，同时采集 CABLE Output）：`peak=0.2000 LOOPBACK OK`，证明 VB-CABLE 环回正常。
- 端到端：UIA 不可用、后台进程注入点击被 Windows 前台锁拦截；用 `AttachThreadInput` 切前台后真实点击「发送测试音」，日志 `AUDIO TESTTONE queued`，同步采集 CABLE Output `peak=0.1499 SIGNAL DETECTED`——测试音从 SayAll → CABLE Input → 环回 → CABLE Output 全链路到达。
- 边界：真实语音会话（按住遥控器语音键 → 语音到达接收端）待用户实测；环境备注：该机器装有 VoiceMeeter/Sonar 等多个虚拟声卡软件，若个别应用跟随"默认输入"收不到语音，应在应用内直接指定 `CABLE Output`。
