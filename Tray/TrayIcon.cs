using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;
using PrivacyIsland.Interop;

namespace PrivacyIsland.Tray;

/// <summary>
/// 托盘图标与右键菜单：暂停监控 / 开机自启 / 退出。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _autoStartItem;
    private bool _disposed;

    /// <summary>暂停监控开关（true = 已暂停）。</summary>
    public event Action<bool>? PauseToggled;

    /// <summary>开机自启开关（true = 已写入自启）。</summary>
    public event Action<bool>? AutoStartToggled;

    /// <summary>请求退出应用。</summary>
    public event Action? ExitRequested;

    public TrayIcon()
    {
        _menu = new ContextMenuStrip();

        _pauseItem = new ToolStripMenuItem("暂停监控") { CheckOnClick = true };
        _pauseItem.CheckedChanged += (_, _) => PauseToggled?.Invoke(_pauseItem.Checked);

        _autoStartItem = new ToolStripMenuItem("开机自启") { CheckOnClick = true };
        _autoStartItem.Checked = AutoStartManager.IsEnabled();
        _autoStartItem.CheckedChanged += (_, _) => AutoStartToggled?.Invoke(_autoStartItem.Checked);

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(_autoStartItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "PrivacyIsland — 摄像头 / 麦克风使用提示",
            ContextMenuStrip = _menu,
            Visible = true,
        };
    }

    /// <summary>同步"暂停监控"菜单的勾选状态。</summary>
    public void SetPaused(bool paused)
    {
        if (_pauseItem.Checked != paused)
        {
            _pauseItem.Checked = paused;
        }
    }

    /// <summary>当第二个实例启动时，在现有托盘图标上弹出提示。</summary>
    public void ShowAlreadyRunningBalloon()
    {
        try
        {
            _notifyIcon.ShowBalloonTip(
                3000,
                "PrivacyIsland",
                "PrivacyIsland 已在运行中（见系统托盘）。",
                ToolTipIcon.Info);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
        }
        catch
        {
        }
    }

    private static Icon LoadAppIcon()
    {
        // 优先使用嵌入的多尺寸 icon.ico
        try
        {
            using System.IO.Stream? stream =
                typeof(TrayIcon).Assembly.GetManifestResourceStream("PrivacyIsland.icon.ico");

            if (stream is not null)
            {
                return new Icon(stream);
            }
        }
        catch
        {
            // 资源缺失时走动态绘制
        }

        return CreateFallbackIcon();
    }

    /// <summary>
    /// 动态绘制一个"胶囊 + 眼睛"图标（32x32），作为嵌入资源不可用时的兜底。
    /// </summary>
    private static Icon CreateFallbackIcon()
    {
        using var bitmap = new Bitmap(32, 32);

        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // 深色胶囊底
            using var capsulePath = CreateRoundedRect(new RectangleF(0.5f, 5f, 31f, 22f), 11f);
            using var capsuleBrush = new SolidBrush(Color.FromArgb(235, 28, 28, 30));
            using var capsulePen = new Pen(Color.FromArgb(60, 255, 255, 255), 1f);
            g.FillPath(capsuleBrush, capsulePath);
            g.DrawPath(capsulePen, capsulePath);

            // 眼睛轮廓
            using var eyePath = new GraphicsPath();
            eyePath.AddBezier(
                new PointF(8.5f, 16f), new PointF(12f, 11f),
                new PointF(20f, 11f), new PointF(23.5f, 16f));
            eyePath.AddBezier(
                new PointF(23.5f, 16f), new PointF(20f, 21f),
                new PointF(12f, 21f), new PointF(8.5f, 16f));
            using var eyePen = new Pen(Color.White, 1.8f) { LineJoin = LineJoin.Round };
            g.DrawPath(eyePen, eyePath);

            // 绿色瞳孔 + 高光
            using var pupilBrush = new SolidBrush(Color.FromArgb(52, 199, 89));
            g.FillEllipse(pupilBrush, 13.5f, 13.5f, 5f, 5f);

            using var highlightBrush = new SolidBrush(Color.White);
            g.FillEllipse(highlightBrush, 14.6f, 14.4f, 1.4f, 1.4f);
        }

        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using Icon temporary = Icon.FromHandle(hIcon);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath CreateRoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float diameter = radius * 2;

        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }
}

/// <summary>
/// 开机自启管理：读写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run。
/// </summary>
internal static class AutoStartManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PrivacyIsland";

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null)
        {
            return;
        }

        if (enabled)
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                return;
            }

            key.SetValue(ValueName, $"\"{exePath}\"", RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
