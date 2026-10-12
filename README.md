# PrivacyIsland

摄像头 / 麦克风使用时的**灵动岛式隐私提示**小工具（Windows 10 / 11 · .NET 8 · WPF）。

当任意应用开始使用**摄像头**或**麦克风**时，屏幕正上方居中滑入一个胶囊（灵动岛）提示条；
胶囊内只有一只矢量绘制的眼睛图标——没有文字、没有应用名、没有计时、没有设备图标、没有数量角标。
所有使用结束后胶囊自动滑出消失。

## 下载

- 开箱即用（免安装 .NET 运行时，单文件）：
  **[Releases](https://github.com/Zxin-Pro/PrivacyIsland/releases/latest)** 下载 `PrivacyIsland.exe` 直接运行。
- 或者从源码构建，见下方「构建」。

## 特性

- **检测**：轮询注册表 `CapabilityAccessManager\ConsentStore\webcam|microphone`（每 700ms），
  差分感知「开始 / 结束 / 新增使用项」，首轮只建基线避免历史残留误报。
- **丝滑动画**：入场（滑入 + 缩放 + 淡入）→ 呼吸（瞳孔缩放 / 透明度 / 光晕脉动，循环）→
  脉冲（新增使用项时 1→1.12→1 弹一下）→ 退场（滑出 + 缩放 + 淡出）。
  全部只作用于 `RenderTransform` / `Opacity` / `Effect`，不触发布局；`FillBehavior=Stop` 不锁属性。
- **绝对不干扰**：`WS_EX_NOACTIVATE`（不激活）· `WS_EX_TRANSPARENT` + `WS_EX_LAYERED`（鼠标穿透）·
  `WS_EX_TOOLWINDOW`（不进 Alt+Tab）· `ShowInTaskbar=false`（不进任务栏）· `ShowActivated=false`。
- **盖过 PPT 放映**：`Topmost` + `SetWindowPos(HWND_TOPMOST)` 重宣誓 +
  1.5s 定时器兜底 + `SetWinEventHook`（`EVENT_SYSTEM_FOREGROUND` / `EVENT_OBJECT_REORDER`）
  在前台窗口变化时立即抢回置顶。窗口保持 76×40 的小尺寸，不做全屏覆盖层。
- **多显示器 / 高 DPI**：PerMonitorV2（app.manifest 声明）；按主屏工作区定位，
  125% / 150% / 200% 缩放下位置与大小正确。
- **托盘**：暂停监控（停止轮询并隐藏胶囊）/ 开机自启（写
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`）/ 退出。
- **单实例**：Mutex 互斥；第二个实例会在现有托盘图标上弹出「已在运行」气泡后退出。
- **无需管理员权限**；单文件发布，自包含运行时。

## 构建

需要 .NET SDK 8.0（更高版本亦可）：

```powershell
# 单文件发布（自包含，无需目标机器安装 .NET 运行时）
dotnet publish -c Release
```

产物：`bin\Release\net8.0-windows\win-x64\publish\PrivacyIsland.exe`

> 提示：想要更小体积（约 60~70MB），可在 `PrivacyIsland.csproj` 中取消注释
> `<EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>`，
> 代价是首次启动会稍慢（需要解压）。

调试运行：

```powershell
dotnet run -c Debug
```

在非 Windows 系统上交叉编译（CI 场景）也可以：

```bash
dotnet publish -c Release   # EnableWindowsTargeting 已在 csproj 中开启
```

## 项目结构

```
PrivacyIsland/
├── App.xaml.cs                  # 启动、单实例互斥、托盘初始化、事件转发
├── App.xaml                     # ShutdownMode=OnExplicitShutdown
├── PrivacyIsland.csproj         # net8.0-windows / WPF / 单文件发布
├── app.manifest                 # PerMonitorV2 DPI、asInvoker
├── icon.ico                     # 应用图标 + 托盘图标（嵌入资源）
├── tools/make_icon.py           # （辅助）重新生成 icon.ico 的脚本
├── tools/add_bom.py             # （辅助）为源文件统一补 UTF-8 BOM 的脚本
├── Core/
│   ├── DeviceUsage.cs           # record: RawName, Kind
│   ├── RegistryScanner.cs       # 注册表扫描（结果写入复用 List，避免每轮重分配）
│   └── UsageMonitor.cs          # 700ms 轮询 + 差分 + IsAnyDeviceInUseChanged / NewUsageDetected
├── UI/
│   ├── IslandWindow.xaml        # 胶囊窗口：只含眼睛（纯 XAML 矢量）
│   ├── IslandWindow.xaml.cs     # 状态机 Idle/Showing/Visible/Hiding + 置顶强化
│   └── AnimationHelper.cs       # Show / Hide / Breath / Pulse 四组 Storyboard
├── Interop/
│   └── NativeMethods.cs         # SetWindowLongPtr / SetWindowPos / GetMonitorInfo / SetWinEventHook
└── Tray/
    └── TrayIcon.cs              # NotifyIcon 菜单 + 开机自启（AutoStartManager）
```

## 检测原理

Windows 在应用开始 / 停止使用摄像头或麦克风时，会把时间写入注册表：

```
HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\
    webcam\           ← 打包应用直接以 PackageFamilyName 为子键
        NonPackaged\  ← 非打包应用：子键名为 exe 完整路径（'\' 替换为 '#'）
    microphone\       ← 同上
```

每个子键下有 `LastUsedTimeStart` / `LastUsedTimeStop`（REG_QWORD，FILETIME）。
**判定正在使用：`LastUsedTimeStart != 0` 且 `LastUsedTimeStop == 0`。**

`UsageMonitor` 每 700ms 扫描一次，用 `Dictionary<string, DeviceUsage>` 做差分
（key = `"{Kind}:{RawName}"`），只在状态变化时触发事件：

- 从 false 变 true → 滑入显示；
- 从 true 变 false → 400ms 防抖后滑出；
- 已经显示又有新增使用项 → 只播放一次脉冲。

## 验收自测清单

- [ ] 打开 Windows「相机」应用 / Zoom / 微信视频通话 → 1 秒内胶囊滑入。
- [ ] 胶囊内只有一只眼睛，无任何文字 / 图标 / 计时。
- [ ] 使用期间胶囊一直显示，直到所有使用结束。
- [ ] 关闭使用 → 1.5 秒内胶囊平滑滑出消失。
- [ ] 弹窗时输入框不失焦，键盘输入不受影响（点击胶囊区域鼠标也不会被挡）。
- [ ] 任务栏与 Alt+Tab 中找不到它。
- [ ] PPT 按 F5 全屏放映后触发摄像头 → 胶囊显示在幻灯片之上；放映中反复切换窗口不掉层。
- [ ] 快速连续切换（相机 → Zoom）不闪烁，只播放脉冲。
- [ ] 托盘可暂停监控、可退出；「开机自启」勾选后写入 HKCU Run 键。

## 常见问题

**Q：摄像头打开后没有出现胶囊？**

1. 先确认系统层面确实有应用在用摄像头（任务栏相机图标 / 设置 → 隐私）。
2. 用注册表编辑器检查：
   `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam`
   下对应应用的 `LastUsedTimeStart` / `LastUsedTimeStop` 是否符合判定条件。
3. 若你在「暂停监控」状态，从托盘恢复即可。

**Q：为什么第一次启动或系统重启后没有弹？**

设计如此：首轮扫描只建立基线，避免系统历史残留（应用崩溃时 `LastUsedTimeStop` 可能留在 0）
导致的误报。从干净基线到下一次「开始使用」就会正常弹出。

**Q：日志在哪里？**

未处理异常的日志写在 `%LOCALAPPDATA%\PrivacyIsland\error.log`。

**Q：如何彻底卸载？**

托盘 →「退出」；如已开启自启，先取消勾选「开机自启」，再删除程序文件即可
（不存在任何其他系统改动）。
