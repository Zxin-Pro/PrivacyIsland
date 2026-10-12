namespace PrivacyIsland.Core;

/// <summary>
/// 设备类型：摄像头 / 麦克风。
/// </summary>
public enum DeviceKind
{
    Camera,
    Microphone,
}

/// <summary>
/// 一条"正在使用中"的设备记录。
/// <para>
/// RawName 为注册表中的原始键名：
/// 打包应用是 PackageFamilyName；非打包应用是 exe 完整路径（'\' 已被替换为 '#'）。
/// </para>
/// </summary>
public record DeviceUsage(string RawName, DeviceKind Kind);
