using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArclightLauncher.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProjBobcat.Class.Model;
using ProjBobcat.Class.Model.Auth;
using ProjBobcat.Class.Model.Microsoft.Graph;
using ProjBobcat.Class.Model.MicrosoftAuth;
using ProjBobcat.DefaultComponent.Authenticator;
using ProjBobcat.DefaultComponent.Launch;
using ProjBobcat.Interface;

namespace ArclightLauncher.Services;

/// <summary>
/// Microsoft 正版登录服务。
/// 这里只负责获取/刷新 OAuth 凭据，并把凭据交给 ProjBobcat 的 MicrosoftAuthenticator。
/// 游戏启动本身仍由 LaunchService 使用原有流程完成。
/// </summary>
public class MicrosoftAuthService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly AccountService _accountService;
    private readonly ILogger<MicrosoftAuthService> _logger;
    private readonly string _clientId;
    private readonly string _tenantId;
    private readonly string _redirectUri;
    private readonly string[] _scopes;

    public MicrosoftAuthService(
        IHttpClientFactory httpFactory,
        AccountService accountService,
        IConfiguration configuration,
        ILogger<MicrosoftAuthService> logger)
    {
        _httpFactory = httpFactory;
        _accountService = accountService;
        _logger = logger;
        _clientId = configuration["Launcher:MicrosoftClientId"]?.Trim() ?? string.Empty;
        _tenantId = configuration["Launcher:MicrosoftTenantId"]?.Trim() ?? "consumers";
        _redirectUri = configuration["Launcher:MicrosoftRedirectUri"]?.Trim() ?? "http://localhost";
        _scopes = ReadScopes(configuration);
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_clientId);

    public string ConfigurationHint =>
        "请先在 appsettings.json 的 Launcher:MicrosoftClientId 填入你的 Azure 应用 Client ID，并开启公共客户端登录。";

    public async Task<Account> LoginInteractiveAsync(
        string gameDir,
        Func<Uri, CancellationToken, Task<Uri?>> authorizeAsync,
        CancellationToken ct = default)
    {
        ConfigureAuthenticatorOrThrow();

        var codeVerifier = CreateCodeVerifier();
        var state = CreateCodeVerifier()[..32];
        var authorizeUri = BuildAuthorizationUri(codeVerifier, state);
        var redirectUri = await authorizeAsync(authorizeUri, ct);

        if (redirectUri is null)
            throw new OperationCanceledException("Microsoft 登录已取消", ct);

        var query = ParseQuery(redirectUri.Query);
        if (query.TryGetValue("error", out var error))
        {
            query.TryGetValue("error_description", out var description);
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(description)
                    ? $"Microsoft 登录失败：{error}"
                    : description);
        }

        if (!query.TryGetValue("state", out var returnedState) ||
            !string.Equals(returnedState, state, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Microsoft 登录返回状态不匹配，请重新登录");
        }

        if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Microsoft 登录没有返回授权码");

        var graphToken = await RedeemAuthorizationCodeAsync(code, codeVerifier, ct);
        return await CompleteMinecraftLoginAsync(gameDir, graphToken);
    }

    public async Task<Account> LoginAsync(
        string gameDir,
        Action<DeviceIdResponseModel> deviceCodeReceived,
        CancellationToken ct = default)
    {
        ConfigureAuthenticatorOrThrow();

        var deviceCode = await RequestDeviceCodeAsync(ct);
        deviceCodeReceived(deviceCode);

        var graphToken = await PollDeviceCodeAsync(deviceCode, ct);
        return await CompleteMinecraftLoginAsync(gameDir, graphToken);
    }

    private async Task<Account> CompleteMinecraftLoginAsync(
        string gameDir,
        GraphAuthResultModel graphToken)
    {
        var parser = new DefaultLauncherAccountParser(gameDir, LaunchService.ClientToken);
        var auth = new MicrosoftAuthenticator
        {
            LauncherAccountParser = parser,
            CacheTokenProvider = () => Task.FromResult<(bool, GraphAuthResultModel?)>((true, graphToken))
        };

        var authResult = await auth.AuthTaskAsync();
        if (authResult is not MicrosoftAuthResult microsoftResult ||
            microsoftResult.AuthStatus != AuthStatus.Succeeded)
        {
            throw new InvalidOperationException(BuildAuthErrorMessage(authResult));
        }

        var account = _accountService.CreateMicrosoftAccount(
            microsoftResult,
            graphToken.AccessToken,
            graphToken.RefreshToken,
            DateTimeOffset.Now.AddSeconds(Math.Max(60, graphToken.ExpiresIn)));

        await _accountService.SaveAsync(account);
        return account;
    }

    public IAuthenticator CreateAuthenticator(Account account, ILauncherAccountParser parser)
    {
        if (!account.IsMicrosoft)
            throw new InvalidOperationException("当前账户不是 Microsoft 正版账户");

        ConfigureAuthenticatorOrThrow();

        return new MicrosoftAuthenticator
        {
            Email = account.Email,
            LauncherAccountParser = parser,
            CacheTokenProvider = async () =>
            {
                var graphToken = await GetFreshMicrosoftTokenAsync(account);
                await _accountService.SaveAsync(account);
                return (true, graphToken);
            }
        };
    }

    private async Task<DeviceIdResponseModel> RequestDeviceCodeAsync(CancellationToken ct)
    {
        var client = _httpFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, MicrosoftAuthenticator.MSDeviceTokenRequestUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["scope"] = string.Join(' ', _scopes)
            })
        };

        using var response = await client.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ExtractOAuthError(content, "无法获取 Microsoft 登录验证码"));

        var deviceCode = JsonSerializer.Deserialize<DeviceIdResponseModel>(content, JsonOptions)
            ?? throw new InvalidOperationException("Microsoft 没有返回有效的设备验证码");

        if (string.IsNullOrWhiteSpace(deviceCode.DeviceCode) ||
            string.IsNullOrWhiteSpace(deviceCode.UserCode))
        {
            throw new InvalidOperationException("Microsoft 返回的设备验证码不完整");
        }

        return deviceCode;
    }

    private async Task<GraphAuthResultModel> PollDeviceCodeAsync(
        DeviceIdResponseModel deviceCode,
        CancellationToken ct)
    {
        var client = _httpFactory.CreateClient();
        var start = DateTimeOffset.Now;
        var expiresAt = start.AddSeconds(Math.Max(60, deviceCode.ExpiresIn));
        var interval = Math.Max(3, deviceCode.Interval);

        while (DateTimeOffset.Now < expiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);

            using var request = new HttpRequestMessage(HttpMethod.Post, MicrosoftAuthenticator.MSDeviceTokenStatusUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = MicrosoftAuthenticator.MSGrantType,
                    ["client_id"] = _clientId,
                    ["device_code"] = deviceCode.DeviceCode
                })
            };

            using var response = await client.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
                return DeserializeGraphToken(content, "Microsoft 登录成功但没有返回访问令牌");

            var error = ReadOAuthErrorType(content);
            switch (error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    interval += 2;
                    continue;
                case "authorization_declined":
                    throw new InvalidOperationException("Microsoft 登录已被取消");
                case "expired_token":
                    throw new InvalidOperationException("Microsoft 登录验证码已过期，请重新点击正版登录");
                case "bad_verification_code":
                    throw new InvalidOperationException("Microsoft 登录验证码无效，请重新点击正版登录");
                default:
                    throw new InvalidOperationException(ExtractOAuthError(content, "Microsoft 登录失败"));
            }
        }

        throw new InvalidOperationException("Microsoft 登录验证码已过期，请重新点击正版登录");
    }

    private async Task<GraphAuthResultModel> RedeemAuthorizationCodeAsync(
        string code,
        string codeVerifier,
        CancellationToken ct)
    {
        var client = _httpFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, MicrosoftAuthenticator.MSDeviceTokenStatusUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = _redirectUri,
                ["code_verifier"] = codeVerifier,
                ["scope"] = string.Join(' ', _scopes)
            })
        };

        using var response = await client.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ExtractOAuthError(content, "Microsoft 登录授权码换取失败"));

        return DeserializeGraphToken(content, "Microsoft 登录成功但没有返回访问令牌");
    }

    private async Task<GraphAuthResultModel> GetFreshMicrosoftTokenAsync(Account account)
    {
        if (!string.IsNullOrWhiteSpace(account.MicrosoftAccessToken) &&
            account.MicrosoftTokenExpiresAt is { } expiresAt &&
            expiresAt > DateTimeOffset.Now.AddMinutes(5) &&
            !string.IsNullOrWhiteSpace(account.MicrosoftRefreshToken))
        {
            return new GraphAuthResultModel
            {
                AccessToken = account.MicrosoftAccessToken,
                RefreshToken = account.MicrosoftRefreshToken,
                ExpiresIn = (int)Math.Max(60, (expiresAt - DateTimeOffset.Now).TotalSeconds)
            };
        }

        if (string.IsNullOrWhiteSpace(account.MicrosoftRefreshToken))
            throw new InvalidOperationException("正版账号登录状态已失效，请重新登录 Microsoft 账号");

        var refreshed = await RefreshMicrosoftTokenAsync(account.MicrosoftRefreshToken, CancellationToken.None);
        ApplyGraphToken(account, refreshed);
        return refreshed;
    }

    private async Task<GraphAuthResultModel> RefreshMicrosoftTokenAsync(string refreshToken, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, MicrosoftAuthenticator.MSRefreshTokenRequestUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _clientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["scope"] = string.Join(' ', _scopes)
            })
        };

        using var response = await client.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(ExtractOAuthError(content, "Microsoft 登录状态刷新失败，请重新正版登录"));

        var token = DeserializeGraphToken(content, "Microsoft 登录状态刷新失败：返回令牌为空");
        if (!string.IsNullOrWhiteSpace(token.RefreshToken))
            return token;

        return new GraphAuthResultModel
        {
            AccessToken = token.AccessToken,
            RefreshToken = refreshToken,
            ExpiresIn = token.ExpiresIn,
            IdToken = token.IdToken,
            Scope = token.Scope,
            TokenType = token.TokenType
        };
    }

    private void ApplyGraphToken(Account account, GraphAuthResultModel token)
    {
        account.MicrosoftAccessToken = token.AccessToken;
        account.MicrosoftRefreshToken = token.RefreshToken;
        account.MicrosoftTokenExpiresAt = DateTimeOffset.Now.AddSeconds(Math.Max(60, token.ExpiresIn));
        _logger.LogDebug("Microsoft 正版账号令牌已刷新：{Username}", account.Username);
    }

    private void ConfigureAuthenticatorOrThrow()
    {
        if (!IsConfigured)
            throw new InvalidOperationException(ConfigurationHint);

        MicrosoftAuthenticator.Configure(new MicrosoftAuthenticatorAPISettings
        {
            ClientId = _clientId,
            TenentId = string.IsNullOrWhiteSpace(_tenantId) ? "consumers" : _tenantId,
            Scopes = _scopes
        });
    }

    private Uri BuildAuthorizationUri(string codeVerifier, string state)
    {
        var codeChallenge = CreateCodeChallenge(codeVerifier);
        var query = new Dictionary<string, string>
        {
            ["client_id"] = _clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = _redirectUri,
            ["response_mode"] = "query",
            ["scope"] = string.Join(' ', _scopes),
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "select_account"
        };

        var url =
            $"https://login.microsoftonline.com/{Uri.EscapeDataString(_tenantId)}/oauth2/v2.0/authorize?" +
            string.Join('&', query.Select(item =>
                $"{Uri.EscapeDataString(item.Key)}={Uri.EscapeDataString(item.Value)}"));

        return new Uri(url);
    }

    private static string CreateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(64);
        return Base64UrlEncode(bytes);
    }

    private static string CreateCodeChallenge(string codeVerifier)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var text = query.TrimStart('?');
        if (string.IsNullOrWhiteSpace(text))
            return result;

        foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            var value = parts.Length > 1
                ? Uri.UnescapeDataString(parts[1].Replace('+', ' '))
                : string.Empty;
            result[key] = value;
        }

        return result;
    }

    private static GraphAuthResultModel DeserializeGraphToken(string content, string fallbackMessage)
        => JsonSerializer.Deserialize<GraphAuthResultModel>(content, JsonOptions)
           ?? throw new InvalidOperationException(fallbackMessage);

    private static string[] ReadScopes(IConfiguration configuration)
    {
        var fromArray = configuration
            .GetSection("Launcher:MicrosoftScopes")
            .GetChildren()
            .Select(item => item.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .ToArray();

        if (fromArray.Length > 0)
            return fromArray;

        var raw = configuration["Launcher:MicrosoftScopes"];
        if (!string.IsNullOrWhiteSpace(raw))
        {
            return raw
                .Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return ["XboxLive.signin", "offline_access", "openid", "email", "profile"];
    }

    private static string BuildAuthErrorMessage(ProjBobcat.Class.Model.Auth.AuthResultBase result)
    {
        var error = result.Error;
        if (error is null)
            return "Microsoft 正版登录失败";

        return !string.IsNullOrWhiteSpace(error.ErrorMessage)
            ? error.ErrorMessage
            : !string.IsNullOrWhiteSpace(error.Error)
                ? error.Error
                : error.Cause ?? "Microsoft 正版登录失败";
    }

    private static string? ReadOAuthErrorType(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            return document.RootElement.TryGetProperty("error", out var error)
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ExtractOAuthError(string content, string fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var error = root.TryGetProperty("error", out var errorElement)
                ? errorElement.GetString()
                : null;
            var description = root.TryGetProperty("error_description", out var descElement)
                ? descElement.GetString()
                : null;

            if (!string.IsNullOrWhiteSpace(description))
                return description;
            if (!string.IsNullOrWhiteSpace(error))
                return $"{fallback}：{error}";
        }
        catch (JsonException)
        {
            // Fall through to the friendly message below.
        }

        return fallback;
    }
}
