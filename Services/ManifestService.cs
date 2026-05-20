using ArclightLauncher.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArclightLauncher.Services;

public class ManifestService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ManifestService> _logger;
    private readonly string[] _manifestUrls;
    private readonly string? _announcementUrl;
    private readonly string _manifestCachePath;
    // 配置后启用 manifest 签名校验：远端清单必须带有效 .sig 才被接受（fail-closed）。空 = 不校验（向后兼容）。
    private readonly string _manifestPublicKey;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ServerManifest? CachedManifest { get; private set; }

    public ManifestService(
        IHttpClientFactory httpFactory,
        ILogger<ManifestService> logger,
        IConfiguration configuration)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _manifestUrls = ReadManifestUrls(configuration);
        _announcementUrl = configuration["Launcher:AnnouncementUrl"];
        _manifestPublicKey = configuration["Launcher:ManifestPublicKey"]?.Trim() ?? string.Empty;
        _manifestCachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ArclightLauncher",
            "manifest-cache.json");
    }

    public async Task<ServerManifest> FetchAsync(CancellationToken ct = default)
    {
        var errors = new List<string>();
        foreach (var configuredUrl in _manifestUrls)
        {
            if (IsPlaceholderUrl(configuredUrl))
                continue;

            var manifestUrl = BuildNoCacheUrl(configuredUrl);
            try
            {
                _logger.LogInformation("Fetching manifest: {Url}", manifestUrl);
                var rawBytes = await GetBytesWithTimeoutAsync(manifestUrl, ct);

                // \u542F\u7528\u7B7E\u540D\u6821\u9A8C\u65F6\uFF0C\u8FDC\u7AEF\u6E05\u5355\u5FC5\u987B\u5E26\u6709\u6548\u7B7E\u540D\u624D\u88AB\u63A5\u53D7\uFF1B\u5426\u5219\u8DF3\u8FC7\u8BE5\u6765\u6E90
                if (_manifestPublicKey.Length > 0)
                    await VerifyManifestSignatureAsync(rawBytes, configuredUrl, ct);

                var json = Encoding.UTF8.GetString(rawBytes).TrimStart('\uFEFF');
                var manifest = ParseManifest(json, configuredUrl);
                await SaveCacheAsync(json, ct);

                CachedManifest = manifest;
                _logger.LogInformation(
                    "Manifest loaded from remote. Pack version {Ver}, Minecraft {Mc}",
                    manifest.PackVersion,
                    manifest.MinecraftVersion);
                return manifest;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException or InvalidOperationException)
            {
                errors.Add($"{configuredUrl}：{ex.Message}");
                _logger.LogWarning(ex, "远程 manifest 获取失败：{Url}", configuredUrl);
            }
        }

        if (TryLoadManifestFromFile(_manifestCachePath, "本地缓存", out var cachedManifest))
        {
            CachedManifest = cachedManifest;
            return cachedManifest!;
        }

        foreach (var bundledPath in GetBundledManifestCandidates())
        {
            if (TryLoadManifestFromFile(bundledPath, "启动器内置整合包", out var bundledManifest))
            {
                CachedManifest = bundledManifest;
                return bundledManifest!;
            }
        }

        var allUrls = string.Join("\n  • ", _manifestUrls.Where(u => !IsPlaceholderUrl(u)));
        throw new InvalidOperationException(
            $"无法获取整合包清单，所有下载源均已尝试失败。\n\n已尝试的地址：\n  • {allUrls}\n\n可能原因：\n  1. 网络连接不稳定，请检查网络后重启启动器\n  2. 防火墙或安全软件阻止了网络请求\n  3. 如果问题持续，请联系管理员\n\n提示：启动器将使用上次缓存的整合包（如有），下次启动时重试。"
            + (errors.Count == 0 ? string.Empty : $"\n\n最近错误：{errors[0]}"));
    }

    private static string BuildNoCacheUrl(string url)
    {
        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
    }

    private async Task<string> GetStringWithTimeoutAsync(string url, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
        return await _httpFactory.CreateClient().GetStringAsync(url, timeoutCts.Token);
    }

    private async Task<byte[]> GetBytesWithTimeoutAsync(string url, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(20));
        return await _httpFactory.CreateClient().GetByteArrayAsync(url, timeoutCts.Token);
    }

    /// <summary>
    /// 校验远端清单的分离签名（&lt;manifestUrl&gt;.sig，内容为 base64 的 RSA-SHA256 签名）。
    /// 签名缺失或不匹配时抛 InvalidOperationException（fail-closed），由调用方跳过该来源。
    /// </summary>
    private async Task VerifyManifestSignatureAsync(byte[] manifestBytes, string configuredUrl, CancellationToken ct)
    {
        var sigUrl = BuildNoCacheUrl(configuredUrl + ".sig");

        string sigText;
        try
        {
            sigText = await GetStringWithTimeoutAsync(sigUrl, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"未能获取 manifest 签名文件：{configuredUrl}.sig", ex);
        }

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(sigText.Trim());
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("manifest 签名文件格式无效（应为 base64）", ex);
        }

        if (!VerifyRsaSignature(manifestBytes, signature, _manifestPublicKey))
            throw new InvalidOperationException($"manifest 签名校验未通过，已拒绝来源：{configuredUrl}");

        _logger.LogInformation("manifest 签名校验通过：{Source}", configuredUrl);
    }

    private static bool VerifyRsaSignature(byte[] data, byte[] signature, string publicKey)
    {
        try
        {
            using var rsa = RSA.Create();
            if (publicKey.Contains("BEGIN", StringComparison.Ordinal))
                rsa.ImportFromPem(publicKey);
            else
                rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);

            return rsa.VerifyData(data, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private ServerManifest ParseManifest(string json, string source)
    {
        var manifest = JsonSerializer.Deserialize<ServerManifest>(json, JsonOpts)
            ?? throw new InvalidOperationException($"manifest.json deserialized to null: {source}");

        ValidateManifestFileNames(manifest);
        _logger.LogInformation(
            "Manifest loaded. Source={Source}, Pack version {Ver}, Minecraft {Mc}",
            source,
            manifest.PackVersion,
            manifest.MinecraftVersion);

        return manifest;
    }

    private async Task SaveCacheAsync(string json, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_manifestCachePath)!);
            await File.WriteAllTextAsync(_manifestCachePath, json, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "保存 manifest 本地缓存失败");
        }
    }

    private bool TryLoadManifestFromFile(string path, string label, out ServerManifest? manifest)
    {
        manifest = null;
        try
        {
            if (!File.Exists(path))
                return false;

            var json = File.ReadAllText(path).TrimStart('\uFEFF');
            manifest = ParseManifest(json, label);
            _logger.LogInformation("已使用 {Label} manifest：{Path}", label, path);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Label} manifest 无法使用：{Path}", label, path);
            return false;
        }
    }

    private static string[] ReadManifestUrls(IConfiguration configuration)
    {
        var urls = new List<string>();
        var primary = configuration["Launcher:ManifestUrl"];
        if (!string.IsNullOrWhiteSpace(primary))
        {
            var trimmed = primary.Trim();
            urls.Add(trimmed);

            // Auto-generate mirrors for GitHub raw URLs
            var mirror = ConvertGitHubRawToMirrors(trimmed);
            if (mirror is not null)
                urls.AddRange(mirror);
        }

        urls.AddRange(
            configuration.GetSection("Launcher:ManifestMirrorUrls")
                .GetChildren()
                .Select(item => item.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim()));

        return urls
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[]? ConvertGitHubRawToMirrors(string url)
    {
        // raw.githubusercontent.com/{owner}/{repo}/{branch}/{path}
        // → cdn.jsdelivr.net/gh/{owner}/{repo}@{branch}/{path}
        const string githubRaw = "https://raw.githubusercontent.com/";
        if (!url.StartsWith(githubRaw, StringComparison.OrdinalIgnoreCase))
            return null;

        var path = url[githubRaw.Length..];
        var parts = path.Split('/', 4);
        if (parts.Length < 4)
            return null;

        var owner = parts[0];
        var repo = parts[1];
        var branch = parts[2];
        var file = parts[3];

        return new[]
        {
            $"https://cdn.jsdelivr.net/gh/{owner}/{repo}@{branch}/{file}",
            $"https://ghproxy.net/{url}"
        };
    }

    private static IEnumerable<string> GetBundledManifestCandidates()
    {
        var baseDir = AppContext.BaseDirectory;
        return new[]
        {
            Path.Combine(baseDir, "Assets", "PackCache", "manifest.json"),
            Path.Combine(baseDir, "..", "..", "..", "Assets", "PackCache", "manifest.json")
        }.Select(Path.GetFullPath);
    }

    private static bool IsPlaceholderUrl(string url)
        => string.IsNullOrWhiteSpace(url) ||
           url.Contains("your-bucket", StringComparison.OrdinalIgnoreCase) ||
           url.Contains("example.com", StringComparison.OrdinalIgnoreCase);

    private static void ValidateManifestFileNames(ServerManifest manifest)
    {
        ValidateGroup("mod", manifest.Mods);
        ValidateGroup("resourcepack", manifest.Resourcepacks);
        ValidateGroup("shaderpack", manifest.Shaderpacks);
    }

    private static void ValidateGroup(string label, IEnumerable<PackFile> files)
    {
        var invalidChars = Path.GetInvalidFileNameChars();

        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.Filename))
                throw new InvalidOperationException($"manifest has an empty {label} filename.");

            if (!string.Equals(Path.GetFileName(file.Filename), file.Filename, StringComparison.Ordinal))
                throw new InvalidOperationException($"manifest {label} filename must not contain a directory: {file.Filename}");

            if (file.Filename.IndexOfAny(invalidChars) >= 0)
                throw new InvalidOperationException($"manifest {label} filename contains Windows-invalid characters: {file.Filename}");
        }
    }

    public async Task<string> FetchAnnouncementAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_announcementUrl))
            return string.Empty;

        try
        {
            return await _httpFactory.CreateClient().GetStringAsync(_announcementUrl, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch announcement.");
            return string.Empty;
        }
    }
}
