using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PrivacyIsland.Core;
using PrivacyIsland.Tray;
using PrivacyIsland.UI;

namespace PrivacyIsland;

/// <summary>
/// 应用入口：单实例互斥、托盘初始化、启动隐私监控。
/// </summary>
public partial class App : Application
{
    private const string MutexName = "PrivacyIsland.SingleInstance.Mutex";
    private const string ActivateEventName = "PrivacyIsland.SingleInstance.Activate";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activateEvent;
    private RegisteredWaitHandle? _activateWait;

    private UsageMonitor? _monitor;
    private IslandWindow? _island;
    private TrayIcon? _tray;
    private bool _paused;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ---------- 单实例：第二个实例提示已有托盘图标后退出 ----------
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool isNewInstance);
        if (!isNewInstance)
        {
            SignalFirstInstance();
            Shutdown();
            return;
        }

        // 全局异常兜底：写日志，保持运行
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // 监听第二实例的"唤醒"信号（用于在已有托盘上弹出提示）
        _activateEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, ActivateEventName);
        _activateWait = ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) => Dispatcher.BeginInvoke(new Action(() => _tray?.ShowAlreadyRunningBalloon())),
            state: null,
            millisecondsTimeOutInterval: -1,
            executeOnlyOnce: false);

        // ---------- 胶囊窗口（常驻，胶囊内容默认隐藏） ----------
        _island = new IslandWindow();
        _island.Show();

        // ---------- 隐私监控 ----------
        _monitor = new UsageMonitor();
        _monitor.IsAnyDeviceInUseChanged += OnAnyDeviceInUseChanged;
        _monitor.NewUsageDetected += OnNewUsageDetected;
        _monitor.Start();

        // ---------- 托盘 ----------
        _tray = new TrayIcon();
        _tray.PauseToggled += OnPauseToggled;
        _tray.AutoStartToggled += OnAutoStartToggled;
        _tray.ExitRequested += OnExitRequested;
    }

    private void OnAnyDeviceInUseChanged(bool inUse)
    {
        // 事件来自轮询线程 → 切回 UI 线程
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_paused)
            {
                return;
            }

            _island?.HandleUsageState(inUse);
        }));
    }

    private void OnNewUsageDetected(DeviceUsage usage)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_paused)
            {
                return;
            }

            _island?.HandleNewUsage();
        }));
    }

    private void OnPauseToggled(bool paused)
    {
        _paused = paused;

        if (paused)
        {
            _monitor?.Pause();
            _island?.ForceHide();
        }
        else
        {
            // 恢复后第一轮扫描会强制同步一次真实状态
            _monitor?.Resume();
        }
    }

    private void OnAutoStartToggled(bool enabled)
    {
        try
        {
            AutoStartManager.SetEnabled(enabled);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }
    }

    private void OnExitRequested()
    {
        ShutdownAll();
    }

    private void ShutdownAll()
    {
        try { _monitor?.Dispose(); } catch { }
        try { _activateWait?.Unregister(null); } catch { }
        try { _activateEvent?.Dispose(); } catch { }
        try { _tray?.Dispose(); } catch { }
        try { _island?.Close(); } catch { }
        try { _instanceMutex?.Dispose(); } catch { }

        Shutdown();
    }

    private static void SignalFirstInstance()
    {
        // 第一个实例可能尚未创建好事件对象，做几次短重试
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using EventWaitHandle evt = EventWaitHandle.OpenExisting(ActivateEventName);
                evt.Set();
                return;
            }
            catch
            {
                Thread.Sleep(120);
            }
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write(e.Exception);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            ErrorLog.Write(ex);
        }
    }
}

/// <summary>
/// 极简错误日志：写入 %LOCALAPPDATA%\PrivacyIsland\error.log；任何失败都静默忽略。
/// </summary>
internal static class ErrorLog
{
    private static readonly object Gate = new();

    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PrivacyIsland",
        "error.log");

    public static void Write(Exception ex)
    {
        try
        {
            lock (Gate)
            {
                string? dir = Path.GetDirectoryName(LogFile);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.AppendAllText(LogFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }

        Debug.WriteLine(ex);
    }
}
