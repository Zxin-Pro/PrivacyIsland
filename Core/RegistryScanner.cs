using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace PrivacyIsland.Core;

/// <summary>
/// 扫描 Windows 隐私记录注册表，找出"正在使用"设备（摄像头 / 麦克风）的应用。
/// <para>
/// 注册表结构（HKCU）：
/// <code>
/// ...\CapabilityAccessManager\ConsentStore\webcam
///   ├─ &lt;PackageFamilyName&gt;                        ← 打包应用
///   │     LastUsedTimeStart / LastUsedTimeStop (REG_QWORD, FILETIME)
///   └─ NonPackaged
///         └─ C:#Program Files#Foo#foo.exe          ← 非打包应用（'\' 替换为 '#'）
///               LastUsedTimeStart / LastUsedTimeStop
/// </code>
/// 判定"正在使用"：LastUsedTimeStart != 0 且 LastUsedTimeStop == 0。
/// </para>
/// </summary>
public static class RegistryScanner
{
    private const string WebcamStorePath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

    private const string MicrophoneStorePath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    private const string NonPackagedKeyName = "NonPackaged";
    private const string ValueStart = "LastUsedTimeStart";
    private const string ValueStop = "LastUsedTimeStop";

    /// <summary>
    /// 扫描两处注册表，把"正在使用"的条目填入 <paramref name="results"/>（会先清空）。
    /// 结果列表由调用方复用，避免每轮分配。
    /// </summary>
    public static void Scan(List<DeviceUsage> results)
    {
        results.Clear();
        ScanStore(WebcamStorePath, DeviceKind.Camera, results);
        ScanStore(MicrophoneStorePath, DeviceKind.Microphone, results);
    }

    private static void ScanStore(string storePath, DeviceKind kind, List<DeviceUsage> results)
    {
        try
        {
            using RegistryKey? store = Registry.CurrentUser.OpenSubKey(storePath, writable: false);
            if (store is null)
            {
                return;
            }

            string[] names = store.GetSubKeyNames();
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i];

                if (string.Equals(name, NonPackagedKeyName, StringComparison.OrdinalIgnoreCase))
                {
                    ScanNonPackaged(store, kind, results);
                    continue;
                }

                // 打包应用的子键名就是 PackageFamilyName
                ScanEntry(store, name, kind, results);
            }
        }
        catch
        {
            // 注册表被并发修改 / 权限不足：忽略本次
        }
    }

    private static void ScanNonPackaged(RegistryKey store, DeviceKind kind, List<DeviceUsage> results)
    {
        try
        {
            using RegistryKey? nonPackaged = store.OpenSubKey(NonPackagedKeyName, writable: false);
            if (nonPackaged is null)
            {
                return;
            }

            string[] names = nonPackaged.GetSubKeyNames();
            for (int i = 0; i < names.Length; i++)
            {
                // 非打包应用的键名：exe 完整路径，其中 '\' 被替换为 '#'
                ScanEntry(nonPackaged, names[i], kind, results);
            }
        }
        catch
        {
        }
    }

    private static void ScanEntry(RegistryKey parent, string subKeyName, DeviceKind kind, List<DeviceUsage> results)
    {
        try
        {
            using RegistryKey? sub = parent.OpenSubKey(subKeyName, writable: false);
            if (sub is null)
            {
                return;
            }

            object? startRaw = sub.GetValue(ValueStart);
            object? stopRaw = sub.GetValue(ValueStop);

            // 没有任何时间记录的子键不是使用记录条目，跳过
            if (startRaw is null && stopRaw is null)
            {
                return;
            }

            long start = ToInt64(startRaw);
            long stop = ToInt64(stopRaw);

            if (start != 0 && stop == 0)
            {
                results.Add(new DeviceUsage(subKeyName, kind));
            }
        }
        catch
        {
        }
    }

    private static long ToInt64(object? value)
    {
        switch (value)
        {
            case long l:
                return l;
            case int i:
                return i;
            case uint u:
                return u;
            case byte[] bytes when bytes.Length >= 8:
                return BitConverter.ToInt64(bytes, 0);
            default:
                return 0L;
        }
    }
}
