using ArclightLauncher.Models;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArclightLauncher.Services;

/// <summary>
/// 账户管理：创建离线账号、保存/读取 accounts.json
/// </summary>
public class AccountService
{
    private readonly ILogger<AccountService> _logger;

    // 账号文件路径：%APPDATA%\ArclightLauncher\accounts.json
    private static readonly string AccountsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ArclightLauncher", "accounts.json");

    public AccountService(ILogger<AccountService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 用用户名创建离线账户。
    /// UUID 算法：MD5("OfflinePlayer:{username}")，格式化为标准 UUID。
    /// </summary>
    public Account CreateOfflineAccount(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("用户名不能为空", nameof(username));

        var bytes = MD5.HashData(Encoding.UTF8.GetBytes($"OfflinePlayer:{username}"));

        // 设置 UUID version 3 和 variant bits
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

        var uuid = new Guid(bytes).ToString();

        _logger.LogDebug("创建离线账户 {Username}，UUID={Uuid}", username, uuid);

        return new Account
        {
            Type = "offline",
            Username = username,
            Uuid = uuid
        };
    }

    /// <summary>
    /// 将账户保存到 accounts.json（目前只存一个）
    /// </summary>
    public async Task SaveAsync(Account account)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AccountsPath)!);
        var list = new List<Account> { account };
        var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(AccountsPath, json);
        _logger.LogInformation("账户已保存：{Username}", account.Username);
    }

    /// <summary>
    /// 从 accounts.json 读取第一个账户；文件不存在时返回 null
    /// </summary>
    public async Task<Account?> LoadFirstAsync()
    {
        if (!File.Exists(AccountsPath))
            return null;

        var json = await File.ReadAllTextAsync(AccountsPath);
        var list = JsonSerializer.Deserialize<List<Account>>(json);
        return list?.FirstOrDefault();
    }
}
