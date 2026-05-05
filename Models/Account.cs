using System.Text.Json.Serialization;

namespace ArclightLauncher.Models;

/// <summary>
/// 账户模型。支持离线账户与 Microsoft 正版账户。
/// 保存到 %APPDATA%\ArclightLauncher\accounts.json
/// </summary>
public class Account
{
    /// <summary>账户类型："offline" 或 "microsoft"</summary>
    public string Type { get; set; } = "offline";

    /// <summary>玩家名（离线模式直接用）</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// UUID。离线模式由 "OfflinePlayer:{Username}" 的 MD5 生成，
    /// 格式为标准 UUID（8-4-4-4-12）
    /// </summary>
    public string Uuid { get; set; } = string.Empty;

    /// <summary>Microsoft 账户邮箱；部分账号可能不会返回。</summary>
    public string? Email { get; set; }

    /// <summary>Minecraft Services AccessToken，仅用于展示/缓存，不作为刷新凭据。</summary>
    public string? AccessToken { get; set; }

    /// <summary>Microsoft OAuth AccessToken，用于换取 Xbox/Minecraft 登录状态。</summary>
    public string? MicrosoftAccessToken { get; set; }

    /// <summary>Microsoft OAuth RefreshToken，用于下次静默刷新正版登录。</summary>
    public string? MicrosoftRefreshToken { get; set; }

    /// <summary>Microsoft OAuth AccessToken 过期时间。</summary>
    public DateTimeOffset? MicrosoftTokenExpiresAt { get; set; }

    public string? Skin { get; set; }
    public string? Cape { get; set; }
    public string? XBoxUid { get; set; }

    [JsonIgnore]
    public bool IsMicrosoft =>
        string.Equals(Type, "microsoft", StringComparison.OrdinalIgnoreCase);
}
