# 电量哨兵 BattWatch

Windows 托盘小工具：把无线外设（蓝牙 / 2.4G）和笔记本自身的电量集中显示。

- 托盘图标直接显示最低电量（绿 / 黄 / 红），悬停可看全部设备
- 低于阈值弹出提醒（每次跌破只提醒一次，充电或回升 5% 后重新计数）
- 历史曲线（24 小时 / 7 天 / 30 天）、平均耗电速度、预计可用时间
- 桌面悬浮小组件（可拖动，双击打开主界面）
- 设备可隐藏；可开机自启（`--tray` 启动时只进托盘）

## 运行

需要 .NET 9 桌面运行时。直接运行 `dist\BattWatch.exe`，或自己构建：

```bash
dotnet publish src -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -o dist
```

数据在 `%APPDATA%\BattWatch\`：`settings.json`、`history\YYYY-MM.csv`、`battwatch.log`。

## 电量来源

| 来源 | 方式 | 说明 |
|---|---|---|
| 经典蓝牙耳机 | 设备属性 `{104EA319-…} 2`（Windows 从 HFP 拿到的电量） | 只有耳机向 Windows 上报电量时才有 |
| BLE 鼠标 / 键盘 | 同上属性，否则读 GATT 电池服务 0x180F / 0x2A19 | 只显示**当前已连接**的设备，避免 Windows 缓存的旧值 |
| MCHOSE 2.4G 接收器 (VID 0x3837) | 厂商 HID 协议，从官方网页驱动中整理 | 见 `src/Providers/MchoseHidProvider.cs`，只发送只读的“读取设备信息”命令 |
| 前行者 EWEADN X87S 等融远（RY）方案 2.4G 接收器 (VID 0x3151) | 厂商 HID 特征报告 0xF7（带 RY 校验和），布局参考 RY 官方网页驱动与 monsgeek-akko-linux 的固件逆向 | 见 `src/Providers/X87sHidProvider.cs`，只发送接收器本地应答的只读状态查询，不经射频转发；键盘关机或刚插接收器还没上报时不显示 |
| 飞智 Flydigi 黑武士4 Pro 等，XInput 模式（接收器显示为 Xbox 360 手柄 045E:028E） | 向飞智空间站服务的本地管道 `fcs.sock` 发只读的“获取已连接设备”命令，格式取自空间站服务自身的程序集 | 见 `src/Providers/FlydigiServiceProvider.cs`。需要装有飞智空间站 4.2+（服务在后台运行即可）。固件只报 5 格电量（20% 一档），服务只在手柄连上时读一次，所以数值是手柄本次开机时的电量；空间站窗口开着时管道被它占用，沿用上次读数 |
| 飞智 Flydigi 黑武士4 Pro 等，DInput 模式（VID 0x04B4 PID 0x2412，2.4G 接收器或有线） | 厂商 HID 输出报告 0x05 的“读取设备信息”命令 0xEC，布局参考 SDL 的 Flydigi 驱动、flydigictl 与空间站 | 见 `src/Providers/FlydigiHidProvider.cs`，不依赖空间站，同样是 5 格电量。尚未在真机上验证 |
| 笔记本电池 | `Windows.Devices.Power.Battery` | |

其他品牌的 2.4G 接收器都是私有协议，需要逐个适配：实现 `IBatteryProvider`，然后在 `BatteryMonitor` 里注册即可。
`tools/HidProbe` 可以列出某个 VID 的 HID 集合与报告 ID，方便分析：

```bash
dotnet run --project tools/HidProbe -- 3 3151
```

## 诊断

- `BattWatch.exe --dump`：读取一次所有来源，把结果写到 `%APPDATA%\BattWatch\dump.txt`，详细过程写到 `battwatch.log`
- `BattWatch.exe --snapshot <目录>`：把各页面渲染成 PNG
