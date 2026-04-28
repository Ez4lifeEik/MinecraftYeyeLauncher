namespace ArclightLauncher.Models;

/// <summary>
/// 启动器自身运行时配置，对应 appsettings.json 中的 "Launcher" 节
/// </summary>
public class LauncherConfig
{
    /// <summary>manifest.json 的公网 HTTPS 地址</summary>
    public string ManifestUrl { get; set; } = string.Empty;
}
