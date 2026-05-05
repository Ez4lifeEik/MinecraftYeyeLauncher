namespace ArclightLauncher.Models;

/// <summary>
/// 启动器自身运行时配置，对应 appsettings.json 中的 "Launcher" 节
/// </summary>
public class LauncherConfig
{
    /// <summary>manifest.json 的公网 HTTPS 地址</summary>
    public string ManifestUrl { get; set; } = string.Empty;

    /// <summary>用于自动更新检查的 GitHub 仓库，格式 "owner/repo"</summary>
    public string GitHubRepo { get; set; } = string.Empty;

    /// <summary>Azure 应用 Client ID，用于 Microsoft 正版账号设备代码登录。</summary>
    public string MicrosoftClientId { get; set; } = string.Empty;

    /// <summary>Microsoft 登录租户；正版玩家通常使用 consumers。</summary>
    public string MicrosoftTenantId { get; set; } = "consumers";

    /// <summary>Microsoft 授权码登录回调地址；桌面应用通常使用 http://localhost。</summary>
    public string MicrosoftRedirectUri { get; set; } = "http://localhost";

    /// <summary>Microsoft OAuth scopes。</summary>
    public string[] MicrosoftScopes { get; set; } =
    [
        "XboxLive.signin",
        "offline_access",
        "openid",
        "email",
        "profile"
    ];
}
