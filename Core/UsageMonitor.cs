using System;
using System.Collections.Generic;
using System.Threading;

namespace PrivacyIsland.Core;

/// <summary>
/// 周期性（默认 700ms）扫描注册表并对设备使用状态做差分。
/// <para>
/// 对外只暴露一个布尔状态 <see cref="IsAnyDeviceInUse"/> 和两类事件：
/// <list type="bullet">
/// <item><see cref="IsAnyDeviceInUseChanged"/>：整体布尔状态翻转（false→true / true→false）时触发；</item>
/// <item><see cref="NewUsageDetected"/>：已有使用中的基础上又有新使用项出现时触发（用于脉冲动画）。</item>
/// </list>
/// 首轮扫描只建立基线、不发事件（避免历史残留误报）。
/// </para>
/// 事件在后台轮询线程触发，订阅方需自行切回 UI 线程。
/// </summary>
public sealed class UsageMonitor : IDisposable
{
    /// <summary>轮询间隔（毫秒）。</summary>
    public const int PollIntervalMs = 700;

    private readonly Timer _timer;

    // 差分专用容器：只被 timer 回调（单线程、不重入）访问，因此无需加锁
    private readonly List<DeviceUsage> _found = new(64);
    private readonly HashSet<string> _foundKeys = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DeviceUsage> _current = new(StringComparer.Ordinal);
    private readonly List<string> _removedScratch = new(16);
    private readonly List<DeviceUsage> _added = new(16);

    private bool _baselinePending = true;
    private bool _resyncPending;

    private volatile bool _anyInUse;
    private volatile bool _running;
    private volatile bool _disposed;

    /// <summary>整体状态翻转时触发（参数为新的状态）。</summary>
    public event Action<bool>? IsAnyDeviceInUseChanged;

    /// <summary>"已在使用中，又新增了使用项"时触发。</summary>
    public event Action<DeviceUsage>? NewUsageDetected;

    /// <summary>当前是否有任意摄像头 / 麦克风正在被使用。</summary>
    public bool IsAnyDeviceInUse => _anyInUse;

    public UsageMonitor()
    {
        _timer = new Timer(OnTick, state: null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>启动监控（首轮只建基线，不发事件）。</summary>
    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        _baselinePending = true;
        _resyncPending = false;
        _running = true;
        _timer.Change(0, Timeout.Infinite);
    }

    /// <summary>暂停监控（停止轮询）。</summary>
    public void Pause()
    {
        if (_disposed)
        {
            return;
        }

        _running = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>
    /// 恢复监控：下一轮重建基线（吸收暂停期间的变化），
    /// 然后强制同步一次整体状态，保证胶囊与真实状态一致。
    /// </summary>
    public void Resume()
    {
        if (_disposed || _running)
        {
            return;
        }

        _resyncPending = true;
        _running = true;
        _timer.Change(0, Timeout.Infinite);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _running = false;

        try { _timer.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        try { _timer.Dispose(); } catch { }
    }

    private void OnTick(object? state)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            ProcessOnce();
        }
        catch
        {
            // 注册表读取偶发失败：跳过本轮
        }
        finally
        {
            // 单次触发模式：完成后再安排下一轮，天然防重入
            if (!_disposed && _running)
            {
                try { _timer.Change(PollIntervalMs, Timeout.Infinite); } catch { }
            }
        }
    }

    private void ProcessOnce()
    {
        RegistryScanner.Scan(_found);

        if (_baselinePending)
        {
            _baselinePending = false;
            RebuildFromScan();
            return; // 首轮静默
        }

        if (_resyncPending)
        {
            _resyncPending = false;
            RebuildFromScan();
            RaiseStateChanged(_anyInUse); // 强制同步一次
            return;
        }

        bool wasAny = _current.Count > 0;

        // 本轮正在使用的键集合
        _foundKeys.Clear();
        for (int i = 0; i < _found.Count; i++)
        {
            _foundKeys.Add(MakeKey(_found[i]));
        }

        // 移除已消失的项
        _removedScratch.Clear();
        foreach (string key in _current.Keys)
        {
            if (!_foundKeys.Contains(key))
            {
                _removedScratch.Add(key);
            }
        }

        for (int i = 0; i < _removedScratch.Count; i++)
        {
            _current.Remove(_removedScratch[i]);
        }

        // 收集新增项
        _added.Clear();
        for (int i = 0; i < _found.Count; i++)
        {
            DeviceUsage usage = _found[i];
            string key = MakeKey(usage);
            if (!_current.ContainsKey(key))
            {
                _added.Add(usage);
                _current[key] = usage;
            }
        }

        bool nowAny = _current.Count > 0;
        _anyInUse = nowAny;

        // 整体状态翻转（显示 / 隐藏的分界）
        if (wasAny != nowAny)
        {
            RaiseStateChanged(nowAny);
        }

        // 已在使用中，又出现新使用项 → 脉冲（而非重新播放入场）
        if (wasAny && nowAny && _added.Count > 0)
        {
            for (int i = 0; i < _added.Count; i++)
            {
                RaiseNewUsage(_added[i]);
            }
        }
    }

    private void RebuildFromScan()
    {
        _current.Clear();
        for (int i = 0; i < _found.Count; i++)
        {
            DeviceUsage usage = _found[i];
            _current[MakeKey(usage)] = usage;
        }

        _anyInUse = _current.Count > 0;
    }

    private static string MakeKey(DeviceUsage usage)
    {
        string prefix = usage.Kind == DeviceKind.Camera ? "Camera" : "Microphone";
        return prefix + ":" + usage.RawName;
    }

    private void RaiseStateChanged(bool inUse)
    {
        try { IsAnyDeviceInUseChanged?.Invoke(inUse); } catch { }
    }

    private void RaiseNewUsage(DeviceUsage usage)
    {
        try { NewUsageDetected?.Invoke(usage); } catch { }
    }
}
