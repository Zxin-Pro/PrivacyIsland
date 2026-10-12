using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using PrivacyIsland.Interop;

namespace PrivacyIsland.UI;

/// <summary>
/// 灵动岛式隐私提示窗口。
/// <para>
/// • 窗口常驻、整体透明、鼠标穿透、不抢焦点、不进任务栏 / Alt+Tab；
/// • 内部胶囊默认隐藏，摄像头 / 麦克风被使用时从屏幕顶部滑入，全部结束后滑出；
/// • 通过 Topmost + 定时重宣誓 + WinEvent 钩子持续保持最顶层（可覆盖 PPT 放映）。
/// </para>
/// 所有公开方法均会自动切到 UI 线程执行，可安全从任意线程调用。
/// </summary>
public partial class IslandWindow : Window
{
    // ---- 布局常量（WPF 逻辑像素 / DIP）----
    private const double CapsuleWidth = 76;
    private const double CapsuleHeight = 40;
    private const double CapsuleTopOffset = 6;   // 胶囊顶部与主屏工作区顶部的距离
    private const double GlowMargin = 24;        // 窗口四周为光晕预留的空间

    // 置顶重申的最小间隔（毫秒）：EVENT_OBJECT_REORDER 触发极为频繁，做轻量节流
    private const long ReassertMinIntervalMs = 50;

    private enum IslandState
    {
        Idle,
        Showing,
        Visible,
        Hiding,
    }

    private readonly AnimationHelper _anim;
    private readonly DispatcherTimer _reassertTimer;
    private readonly DispatcherTimer _hideDebounce;
    private readonly NativeMethods.WinEventDelegate _winEventProc;

    private IslandState _state = IslandState.Idle;
    private bool _anyInUse;

    private IntPtr _hwnd;
    private HwndSource? _hwndSource;
    private IntPtr _hookForeground;
    private IntPtr _hookReorder;
    private long _lastReassertTick;

    public IslandWindow()
    {
        InitializeComponent();

        _winEventProc = OnWinEvent;

        _anim = new AnimationHelper(this, Capsule, CapsuleScale, CapsuleTranslate, PupilScale, Glow);
        _anim.ShowCompleted += OnShowAnimationCompleted;
        _anim.HideCompleted += OnHideAnimationCompleted;

        // 定时重宣誓置顶（1.5 秒）
        _reassertTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(1500),
        };
        _reassertTimer.Tick += (_, _) => ReassertTopmost();

        // 隐藏防抖（400ms）
        _hideDebounce = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _hideDebounce.Tick += OnHideDebounceTick;

        Loaded += OnLoaded;
        Closed += OnClosed;
        DpiChanged += OnDpiChanged;

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    // =====================================================================
    // 状态机（由 App 转发使用状态，可跨线程调用）
    // =====================================================================

    /// <summary>整体使用状态变化：true = 有任意设备在使用。</summary>
    public void HandleUsageState(bool anyInUse)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => HandleUsageState(anyInUse)));
            return;
        }

        _anyInUse = anyInUse;

        if (anyInUse)
        {
            switch (_state)
            {
                case IslandState.Idle:
                    ShowCapsule();
                    break;

                case IslandState.Hiding:
                    // 隐藏过程中又重新使用：取消隐藏、恢复显示并播放脉冲
                    CancelHideAndPulse();
                    break;

                case IslandState.Visible:
                    // 防抖期间状态回滚：取消计划中的隐藏
                    CancelScheduledHide();
                    break;

                case IslandState.Showing:
                    // 入场动画进行中，不打断
                    break;
            }
        }
        else
        {
            switch (_state)
            {
                case IslandState.Visible:
                    ScheduleHide();
                    break;

                case IslandState.Showing:
                    // 等入场动画结束，由 OnShowAnimationCompleted 统一处理
                    break;

                case IslandState.Hiding:
                case IslandState.Idle:
                default:
                    break;
            }
        }
    }

    /// <summary>已显示状态下又出现新的使用项：播放一次脉冲。</summary>
    public void HandleNewUsage()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(HandleNewUsage));
            return;
        }

        if (_state == IslandState.Visible)
        {
            _anim.PlayPulse();
        }
    }

    /// <summary>强制收起（例如暂停监控时），无条件进入隐藏流程。</summary>
    public void ForceHide()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(ForceHide));
            return;
        }

        _hideDebounce.Stop();

        // 已隐藏或正在隐藏：无需处理（避免打断 / 重播正在播放的退场动画）
        if (_state == IslandState.Idle || _state == IslandState.Hiding)
        {
            return;
        }

        _state = IslandState.Hiding;
        _anim.PlayHide();
    }

    // =====================================================================
    // 显示 / 隐藏流程
    // =====================================================================

    private void ShowCapsule()
    {
        _hideDebounce.Stop();
        _state = IslandState.Showing;

        Capsule.Visibility = Visibility.Visible;
        ReassertTopmost();
        _anim.PlayShow(); // 完成后触发 OnShowAnimationCompleted
    }

    private void OnShowAnimationCompleted()
    {
        if (_state != IslandState.Showing)
        {
            return;
        }

        _state = IslandState.Visible;
        _anim.StartBreath();

        // 入场动画期间使用已结束：立即进入隐藏流程
        if (!_anyInUse)
        {
            ScheduleHide();
        }
    }

    private void ScheduleHide()
    {
        _hideDebounce.Stop();
        _hideDebounce.Start();
    }

    private void CancelScheduledHide()
    {
        _hideDebounce.Stop();
    }

    private void OnHideDebounceTick(object? sender, EventArgs e)
    {
        _hideDebounce.Stop();

        if (_anyInUse || _state != IslandState.Visible)
        {
            return;
        }

        BeginHide();
    }

    private void BeginHide()
    {
        _state = IslandState.Hiding;
        _anim.PlayHide(); // 完成后触发 OnHideAnimationCompleted
    }

    private void OnHideAnimationCompleted()
    {
        if (_state != IslandState.Hiding)
        {
            return;
        }

        _state = IslandState.Idle;

        _anim.StopBreath();

        Capsule.Visibility = Visibility.Collapsed;
        CapsuleTranslate.Y = -60;
        CapsuleScale.ScaleX = 1;
        CapsuleScale.ScaleY = 1;
        Capsule.Opacity = 1;
    }

    private void CancelHideAndPulse()
    {
        _hideDebounce.Stop();

        // 停掉 Hide（含其它残留动画），恢复显示几何，再补一次脉冲
        _anim.StopAll();
        _anim.SetVisibleState();

        Capsule.Visibility = Visibility.Visible;

        _anim.StartBreath();
        _anim.PlayPulse();

        _state = IslandState.Visible;
        ReassertTopmost();
    }

    // =====================================================================
    // 置顶强化：SetWindowPos 重宣誓 + WinEvent 钩子
    // =====================================================================

    private bool IsCapsuleShown => Capsule.Visibility == Visibility.Visible;

    private void ReassertTopmost()
    {
        if (_hwnd == IntPtr.Zero || !IsCapsuleShown)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (!IsCapsuleShown)
        {
            return;
        }

        // EVENT_OBJECT_REORDER 频率极高：50ms 节流，避免无谓的 SetWindowPos
        long now = Environment.TickCount64;
        if (now - _lastReassertTick < ReassertMinIntervalMs)
        {
            return;
        }

        _lastReassertTick = now;
        ReassertTopmost();
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_MOUSEACTIVATE = 0x0021;
        const int MA_NOACTIVATE = 0x0003;

        // 双保险：即使收到鼠标激活消息也拒绝激活（正常情况下 WS_EX_TRANSPARENT 已让消息穿透）
        if (msg == WM_MOUSEACTIVATE)
        {
            handled = true;
            return new IntPtr(MA_NOACTIVATE);
        }

        return IntPtr.Zero;
    }

    // =====================================================================
    // 窗口生命周期
    // =====================================================================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProcHook);

        // 扩展样式：不激活 / 工具窗口（不进 Alt+Tab）/ 分层 / 鼠标穿透 / 置顶
        long exStyle = NativeMethods.GetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
        exStyle |= NativeMethods.WS_EX_NOACTIVATE
                 | NativeMethods.WS_EX_TOOLWINDOW
                 | NativeMethods.WS_EX_LAYERED
                 | NativeMethods.WS_EX_TRANSPARENT
                 | NativeMethods.WS_EX_TOPMOST;
        NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));

        // 首次定位 + 置顶（带 SWP_SHOWWINDOW）
        PositionWindow();
        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        // WinEvent 钩子：前台窗口变化 / Z 序重排时立即抢回置顶
        try
        {
            _hookForeground = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                NativeMethods.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _winEventProc,
                0,
                0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);

            _hookReorder = NativeMethods.SetWinEventHook(
                NativeMethods.EVENT_OBJECT_REORDER,
                NativeMethods.EVENT_OBJECT_REORDER,
                IntPtr.Zero,
                _winEventProc,
                0,
                0,
                NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        }
        catch
        {
        }

        _reassertTimer.Start();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 窗口真正显示后再校正一次位置（此时尺寸 / DPI 已稳定，覆盖 WPF 的初始布局位置）
        PositionWindow();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _reassertTimer.Stop();
        _hideDebounce.Stop();

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;

        try
        {
            if (_hookForeground != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_hookForeground);
            }

            if (_hookReorder != IntPtr.Zero)
            {
                NativeMethods.UnhookWinEvent(_hookReorder);
            }
        }
        catch
        {
        }

        _hookForeground = IntPtr.Zero;
        _hookReorder = IntPtr.Zero;

        try { _hwndSource?.RemoveHook(WndProcHook); } catch { }
        _hwndSource = null;
    }

    private void OnDpiChanged(object sender, DpiChangedEventArgs e)
    {
        // 等 WPF 完成自身的 DPI 缩放调整后再校正位置
        Dispatcher.BeginInvoke(new Action(PositionWindow), DispatcherPriority.Loaded);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(PositionWindow), DispatcherPriority.Loaded);
    }

    // =====================================================================
    // 定位（主屏工作区顶部居中）
    // =====================================================================

    private void PositionWindow()
    {
        if (_hwnd == IntPtr.Zero)
        {
            return;
        }

        IntPtr hMonitor = NativeMethods.MonitorFromPoint(
            new NativeMethods.POINT { X = 0, Y = 0 },
            NativeMethods.MONITOR_DEFAULTTOPRIMARY);

        var monitorInfo = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };

        if (!NativeMethods.GetMonitorInfo(hMonitor, ref monitorInfo))
        {
            return;
        }

        double scale = GetMonitorScale(hMonitor);

        int workLeft = monitorInfo.rcWork.Left;
        int workTop = monitorInfo.rcWork.Top;
        int workWidth = monitorInfo.rcWork.Right - monitorInfo.rcWork.Left;

        double windowWidthPhys = (CapsuleWidth + GlowMargin * 2) * scale;

        // 胶囊水平居中；胶囊顶部 = 工作区顶部 + 6 逻辑像素
        int x = (int)Math.Round(workLeft + (workWidth - windowWidthPhys) / 2.0);
        int y = (int)Math.Round(workTop + (CapsuleTopOffset - GlowMargin) * scale);

        NativeMethods.SetWindowPos(
            _hwnd,
            NativeMethods.HWND_TOPMOST,
            x,
            y,
            0,
            0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        // 同步 WPF 逻辑坐标（唯一基准是主屏缩放；物理位置以 SetWindowPos 为准）
        Left = x / scale;
        Top = y / scale;
    }

    private static double GetMonitorScale(IntPtr hMonitor)
    {
        uint dpi = 0;

        try
        {
            if (NativeMethods.GetDpiForMonitor(hMonitor, NativeMethods.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0)
            {
                dpi = dpiX;
            }
        }
        catch
        {
            // Shcore 不可用（极老系统）时走下面的 fallback
        }

        if (dpi == 0)
        {
            dpi = NativeMethods.GetDpiForSystem();
        }

        if (dpi == 0)
        {
            dpi = 96;
        }

        return dpi / 96.0;
    }
}
