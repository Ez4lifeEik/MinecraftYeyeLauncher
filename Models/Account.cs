namespace ArclightLauncher.Models;

/// <summary>
/// 账户模型。v0.1 仅支持离线账户。
/// 保存到 %APPDATA%\ArclightLauncher\accounts.json
/// </summary>
public class Account
{
    /// <summary>账户类型（"offline" 或未来的 "microsoft"）</summary>
    public string Type { get; set; } = "offline";

    /// <summary>玩家名（离线模式直接用）</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// UUID。离线模式由 "OfflinePlayer:{Username}" 的 MD5 生成，
    /// 格式为标准 UUID（8-4-4-4-12）
    /// </summary>
    public string Uuid { get; set; } = string.Empty;
}
