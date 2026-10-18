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
| 前行者 EWEADN X87S 2.4G 接收器 (VID 0x3151 PID 0x504E) | 厂商 HID 特征报告，从官方 E HUB 2.0 网页驱动（RY 方案）中整理 | 见 `src/Providers/X87sHidProvider.cs`，只发送官方驱动每 2 秒轮询的只读状态查询 0xF7；键盘休眠时不显示 |
| 笔记本电池 | `Windows.Devices.Power.Battery` | |

其他品牌的 2.4G 接收器都是私有协议，需要逐个适配：实现 `IBatteryProvider`，然后在 `BatteryMonitor` 里注册即可。
`tools/HidProbe` 可以列出某个 VID 的 HID 集合与报告 ID，方便分析：

```bash
dotnet run --project tools/HidProbe -- 3 3151
```

## 诊断

- `BattWatch.exe --dump`：读取一次所有来源，把结果写到 `%APPDATA%\BattWatch\dump.txt`，详细过程写到 `battwatch.log`
- `BattWatch.exe --snapshot <目录>`：把各页面渲染成 PNG
