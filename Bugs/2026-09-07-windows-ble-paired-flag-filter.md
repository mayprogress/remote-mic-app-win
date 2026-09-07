# 2026-09-07 Windows 版 BLE 连接：已配对过滤与短 UUID 解析双缺陷

- 功能点：Windows 版蓝牙连接（真机 RC003 首次连接）
- 触发条件：遥控器已与 Windows 系统配对并显示"已连接"，启动 SayAll 后一直停留在"正在搜索遥控器…"。

## 复现证据

- 用户现场截图：Windows「蓝牙和其他设备」显示"小米蓝牙语音遥控器 已连接 电量 99%"，SayAll 连接状态卡为"正在搜索遥控器…"。
- 本机日志（同一台真机）：多次启动只有 `BLE SCANNING`，从未出现 `BLE CONNECTING`；三条 `delay_ms=100` 的 RECONNECT 为手动点「重新连接」触发。
- 诊断实验（只读）：WinRT `BluetoothLEDevice.GetDeviceSelector()` 枚举可见该设备、名称与白名单匹配，但 `Pairing.IsPaired=False`；绕过配对标志直接 `FromIdAsync` 后 `ConnectionStatus=Connected`，ATVV 服务 `AB5E0001-…` 存在。

## 根因（两层）

1. `XiaomiBluetoothBridge.ProbePairedDevices` 以 `Pairing.IsPaired` 作为硬性过滤。部分 HID-over-GATT 配对路径下 WinRT 将该标志报告为 false，设备被静默跳过，连接尝试从未发起。
2. 修复第 1 层后连接成功，但 `OnConnectedFlow` 中以 `Guid.Parse("180F")` 等短格式解析标准电池/设备信息服务 UUID，短格式不是合法 Guid，抛 "Unrecognized Guid format"，被归因为 `voice_service_discovery_failed`。此前从未真机连上，故未暴露。

## 修复

- `ProbePairedDevices`：名称白名单命中即尝试连接，不再要求配对标志（白名单本身即安全边界）；补脱敏日志 `BLE PAIRED probe done matched=<n> paired_present=<0|1>`。
- 标准服务/特征 UUID 统一改为完整蓝牙基准格式 `0000XXXX-0000-1000-8000-00805F9B34FB`（180F/2A19/2BED/180A/2A24）。

## 验证

- 构建：`dotnet build SayAllWin\SayAllWin.csproj -c Release` 通过（0 错误）。
- 真机（同一台 Windows、同一只 RC003）：首次尝试 `voice_channel_discovery_failed`（服务发现偶发失败，重连策略按设计自动重试），第二次尝试日志顺序 `CONNECTING source=paired_device → CONNECTED → BATTERY level=99 → MODEL identified=Rc003 → ATVV CAPS → BLE READY`，UI 连接状态卡显示绿点"已连接（小米蓝牙语音遥控器）电量 99% 型号 Rc003"。
- 边界：ATVV 语音闭环（按住语音键 → 音频写入所选设备）仍需用户按物理语音键验收，见 `Testing/WindowsPort.md` 用例 2/3。
