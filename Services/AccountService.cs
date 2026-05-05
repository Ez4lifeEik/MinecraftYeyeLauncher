using ArclightLauncher.Models;
using Microsoft.Extensions.Logging;
using ProjBobcat.Class.Model.Auth;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArclightLauncher.Services;

/// <summary>
/// 账户管理：创建离线/正版账号，支持多账号保存/切换。
/// </summary>
public class AccountService
{
    private readonly ILogger<AccountService> _logger;

    private static readonly string AccountsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ArclightLauncher", "accounts.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AccountService(ILogger<AccountService> logger)
    {
        _logger = logger;
    }

    public Account CreateOfflineAccount(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("用户名不能为空", nameof(username));

        var bytes = MD5.HashData(Encoding.UTF8.GetBytes($"OfflinePlayer:{username}"));
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

    public Account CreateMicrosoftAccount(
        MicrosoftAuthResult authResult,
        string? microsoftAccessToken,
        string? microsoftRefreshToken,
        DateTimeOffset? microsoftTokenExpiresAt)
    {
        var username = authResult.SelectedProfile?.Name
            ?? authResult.User?.UserName
            ?? authResult.Email
            ?? throw new InvalidOperationException("Microsoft 登录成功但没有返回玩家名");

        var uuid = authResult.SelectedProfile?.UUID.ToGuid().ToString()
            ?? authResult.User?.UUID.ToGuid().ToString()
            ?? authResult.Id.ToString();

        _logger.LogInformation("创建 Microsoft 正版账户 {Username}，UUID={Uuid}", username, uuid);

        return new Account
        {
            Type = "microsoft",
            Username = username,
            Uuid = uuid,
            Email = authResult.Email,
            AccessToken = authResult.AccessToken,
            MicrosoftAccessToken = microsoftAccessToken,
            MicrosoftRefreshToken = microsoftRefreshToken ?? authResult.RefreshToken,
            MicrosoftTokenExpiresAt = microsoftTokenExpiresAt,
            Skin = authResult.Skin,
            Cape = authResult.Cape,
            XBoxUid = authResult.XBoxUid
        };
    }

    public async Task<List<Account>> LoadAllAsync()
    {
        if (!File.Exists(AccountsPath))
            return [];

        try
        {
            var json = await File.ReadAllTextAsync(AccountsPath, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<Account>>(json) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 accounts.json 失败");
            return [];
        }
    }

    public async Task<Account?> LoadFirstAsync()
    {
        var list = await LoadAllAsync();
        return list.FirstOrDefault();
    }

    public async Task SaveAllAsync(List<Account> accounts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AccountsPath)!);
        var json = JsonSerializer.Serialize(accounts, JsonOpts);
        await File.WriteAllTextAsync(AccountsPath, json, Encoding.UTF8);
    }

    /// <summary>
    /// 保存单个账户（向后兼容，合并到现有列表）。
    /// </summary>
    public async Task SaveAsync(Account account)
    {
        var list = await LoadAllAsync();
        await AddOrUpdateAsync(list, account);
    }

    /// <summary>
    /// 新增或更新账户（按 UUID 匹配），返回更新后的列表。
    /// </summary>
    public async Task<List<Account>> AddOrUpdateAsync(List<Account> accounts, Account account)
    {
        var existing = accounts.FindIndex(a =>
            string.Equals(a.Uuid, account.Uuid, StringComparison.OrdinalIgnoreCase));

        if (existing >= 0)
            accounts[existing] = account;
        else
            accounts.Add(account);

        await SaveAllAsync(accounts);
        _logger.LogInformation("账户已保存：{Username} ({Type})", account.Username, account.Type);
        return accounts;
    }

    /// <summary>
    /// 删除账户（按 UUID 匹配），返回更新后的列表。
    /// </summary>
    public async Task<List<Account>> RemoveAsync(List<Account> accounts, string uuid)
    {
        accounts.RemoveAll(a =>
            string.Equals(a.Uuid, uuid, StringComparison.OrdinalIgnoreCase));

        await SaveAllAsync(accounts);
        _logger.LogInformation("已删除账户：{Uuid}", uuid);
        return accounts;
    }
}
