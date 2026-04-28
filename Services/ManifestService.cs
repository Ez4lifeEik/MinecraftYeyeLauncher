using ArclightLauncher.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Text.Json;

namespace ArclightLauncher.Services;

/// <summary>
/// 从 OSS 拉取并反序列化 manifest.json；可选拉取公告文本
/// </summary>
public class ManifestService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<ManifestService> _logger;
    private readonly string _manifestUrl;
    private readonly string? _announcementUrl;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>最近一次成功拉取到的 manifest（供 ModManagerDialog 使用）</summary>
    public ServerManifest? CachedManifest { get; private set; }

    public ManifestService(
        IHttpClientFactory httpFactory,
        ILogger<ManifestService> logger,
        IConfiguration configuration)
    {
        _httpFactory = httpFactory;
        _logger = logger;
        _manifestUrl = configuration["Launcher:ManifestUrl"]
            ?? throw new InvalidOperationException("appsettings.json 中缺少 Launcher:ManifestUrl");
        _announcementUrl = configuration["Launcher:AnnouncementUrl"];
    }

    /// <summary>
    /// 拉取并反序列化 manifest，失败时抛出异常。
    /// URL 未填写时抛出含 "ManifestUrl" 关键字的 InvalidOperationException。
    /// </summary>
    public async Task<ServerManifest> FetchAsync(CancellationToken ct = default)
    {
        if (_manifestUrl.Contains("your-bucket") || _manifestUrl.Contains("example.com"))
            throw new InvalidOperationException(
                "ManifestUrl 尚未配置，请在 appsettings.json 中填写真实的 OSS 地址");

        _logger.LogInformation("正在拉取 manifest：{Url}", _manifestUrl);

        var client = _httpFactory.CreateClient();
        var json = await client.GetStringAsync(_manifestUrl, ct);

        var manifest = JsonSerializer.Deserialize<ServerManifest>(json, JsonOpts)
            ?? throw new InvalidOperationException("manifest.json 反序列化结果为 null");

        _logger.LogInformation(
            "manifest 拉取成功，整合包版本 {Ver}，MC {Mc}",
            manifest.PackVersion, manifest.MinecraftVersion);

        CachedManifest = manifest;
        return manifest;
    }

    /// <summary>
    /// 拉取公告文本（AnnouncementUrl 未配置或拉取失败时返回空字符串）
    /// </summary>
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
            _logger.LogWarning(ex, "公告拉取失败");
            return string.Empty;
        }
    }
}
