using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArclightLauncher.Helpers;
using ArclightLauncher.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ArclightLauncher.Services;

public class UpdateService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly DownloadService    _downloadService;
    private readonly ILogger<UpdateService> _logger;
    private readonly string _githubRepo;
    private readonly string _updatePublisherName;

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public UpdateService(
        IHttpClientFactory httpFactory,
        DownloadService    downloadService,
        ILogger<UpdateService> logger,
        IConfiguration configuration)
    {
        _httpFactory     = httpFactory;
        _downloadService = downloadService;
        _logger          = logger;
        _githubRepo      = configuration["Launcher:GitHubRepo"] ?? string.Empty;
        // 可选：配置后将额外校验更新包 Authenticode 签名者主题（需对发布包做代码签名）
        _updatePublisherName = configuration["Launcher:UpdatePublisherName"]?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// 检查 GitHub Releases 是否有新版本。
    /// 出错或无更新时返回 null，不抛异常。
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_githubRepo))
        {
            _logger.LogWarning("Launcher:GitHubRepo 未配置，跳过更新检查");
            return null;
        }

        try
        {
            var currentVer = Assembly.GetExecutingAssembly().GetName().Version!;
            var apiUrl     = $"https://api.github.com/repos/{_githubRepo}/releases/latest";

            var client = _httpFactory.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"ArclightLauncher/{currentVer.ToString(3)}");
            client.DefaultRequestHeaders.Accept.ParseAdd(
                "application/vnd.github.v3+json");

            var json    = await client.GetStringAsync(apiUrl, ct);
            var release = JsonSerializer.Deserialize<GitHubRelease>(json, JsonOpts);
            if (release is null) return null;

            var tag = release.TagName.TrimStart('v');
            if (!Version.TryParse(tag, out var remote)) return null;

            if (remote <= currentVer)
            {
                _logger.LogInformation("已是最新版本 {Ver}", currentVer.ToString(3));
                return null;
            }

            var asset = release.Assets.FirstOrDefault(
                a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            if (asset is null)
            {
                _logger.LogWarning("Release {Tag} 中未找到 .exe 安装包", release.TagName);
                return null;
            }

            // 尝试从 release assets 中找到校验文件
            var checksumAsset = release.Assets.FirstOrDefault(
                a => a.Name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase));
            string? sha256 = null;
            if (checksumAsset is not null)
            {
                try
                {
                    sha256 = await FetchChecksumAsync(checksumAsset.BrowserDownloadUrl, asset.Name, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "获取 SHA256 校验文件失败");
                }
            }

            if (string.IsNullOrWhiteSpace(sha256))
                _logger.LogWarning(
                    "Release {Tag} 缺少有效的 .sha256 校验文件，自动更新将被拒绝（请随包发布校验文件）",
                    release.TagName);

            _logger.LogInformation("发现新版本 {New}（{Size:N0} 字节），安装包：{Url}",
                remote.ToString(3), asset.Size, asset.BrowserDownloadUrl);

            return new UpdateInfo
            {
                NewVersion   = remote,
                DownloadUrl  = asset.BrowserDownloadUrl,
                Size         = asset.Size,
                Sha256       = sha256,
                ReleaseNotes = release.Body
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "更新检查失败，已跳过");
            return null;
        }
    }

    /// <summary>
    /// 下载新版本 exe 到当前目录，创建交换脚本，启动脚本后关闭自身。
    /// 下次启动时即为新版本。
    /// </summary>
    public async Task DownloadAndApplyUpdateAsync(
        UpdateInfo         info,
        Action<long, long>? onProgress,
        CancellationToken  ct)
    {
        // 安全：拒绝无校验文件的更新（fail-closed）。发布时务必随包附带 <安装包名>.sha256。
        if (string.IsNullOrWhiteSpace(info.Sha256))
            throw new InvalidOperationException(
                "该版本未提供 SHA256 校验文件，出于安全考虑已拒绝自动更新。\n" +
                "请在 GitHub Release 中附带 “<安装包名>.exe.sha256” 后重试，或前往项目页面手动下载。");

        var appDir = AppContext.BaseDirectory;
        var currentExe = Environment.ProcessPath ?? Path.Combine(appDir, "ArclightLauncher.exe");
        var newExe = Path.Combine(appDir, $"ArclightLauncher_v{info.NewVersion.ToString(3)}.exe");

        // 下载新版本
        await _downloadService.DownloadFileAsync(
            info.DownloadUrl, newExe, null, onProgress, ct);

        // 1) 强制校验 SHA256（与下载内容比对，防止传输损坏 / 镜像篡改）
        await VerifySha256Async(newExe, info.Sha256, ct);

        // 2) 可选：校验 Authenticode 签名 + 发布者（防止仓库/Release 被投毒；需已配置发布者名）
        if (!string.IsNullOrWhiteSpace(_updatePublisherName))
        {
            AuthenticodeVerifier.Verify(newExe, _updatePublisherName);
            _logger.LogInformation("更新包 Authenticode 签名校验通过（发布者含：{Publisher}）", _updatePublisherName);
        }

        // 创建交换脚本：等待旧进程退出 → 替换 exe → 启动新版
        var batchPath = Path.Combine(appDir, "_update.bat");
        var batchContent = $"""
            @echo off
            chcp 65001 >nul
            :wait
            timeout /t 1 /nobreak >nul
            tasklist /FI "PID eq {Environment.ProcessId}" 2>NUL | find /I "{Environment.ProcessId}" >NUL
            if %ERRORLEVEL% EQU 0 goto wait

            del /f /q "{currentExe}"
            move /y "{newExe}" "{currentExe}"
            start "" "{currentExe}"
            del /f /q "%~f0"
            """;

        await File.WriteAllTextAsync(batchPath, batchContent, Encoding.UTF8, ct);
        _logger.LogInformation("更新脚本已创建：{Path}", batchPath);

        // 启动脚本（隐藏窗口），关闭自身
        var psi = new ProcessStartInfo
        {
            FileName = batchPath,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true
        };
        Process.Start(psi);
    }

    /// <summary>
    /// 清理旧版本安装残留文件。
    /// </summary>
    public static void CleanOldFiles()
    {
        var appDir = AppContext.BaseDirectory;
        try
        {
            foreach (var file in Directory.EnumerateFiles(appDir, "ArclightLauncher_v*.exe"))
            {
                try { File.Delete(file); } catch { /* 可能正被使用 */ }
            }
            var batchPath = Path.Combine(appDir, "_update.bat");
            if (File.Exists(batchPath))
            {
                try { File.Delete(batchPath); } catch { /* 可能正被使用 */ }
            }
        }
        catch { /* best-effort */ }
    }

    // ── 私有方法 ─────────────────────────────────────────────────────────

    private static string GetUpdateDownloadDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArclightLauncher", "updates");

    private async Task VerifySha256Async(string filePath, string expectedSha256, CancellationToken ct)
    {
        var actual = await HashHelper.ComputeSha256Async(filePath, ct);

        if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(filePath); } catch { /* best-effort */ }
            throw new InvalidOperationException(
                $"下载的安装包 SHA256 校验失败。\n期望：{expectedSha256}\n实际：{actual}");
        }

        _logger.LogInformation("更新包 SHA256 校验通过：{Path}", filePath);
    }

    private async Task<string?> FetchChecksumAsync(string checksumUrl, string assetName, CancellationToken ct)
    {
        var client = _httpFactory.CreateClient();
        var text = await client.GetStringAsync(checksumUrl, ct);

        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 &&
                string.Equals(parts[1].Trim(), assetName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[0].Trim();
            }
        }

        // 文件内容可能只包含哈希值本身（不带文件名）
        var trimmed = text.Trim();
        if (trimmed.Length == 64 && trimmed.All(c => char.IsAsciiHexDigit(c)))
            return trimmed;

        return null;
    }

    private static void CleanOldInstallers(string updateDir, string keepFileName)
    {
        try
        {
            if (!Directory.Exists(updateDir)) return;
            foreach (var file in Directory.EnumerateFiles(updateDir, "ArclightLauncher-Setup-*.exe"))
            {
                if (!string.Equals(Path.GetFileName(file), keepFileName, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { /* best-effort */ }
                }
            }
        }
        catch
        {
            // 清理失败不影响主流程
        }
    }

    // ── GitHub API 响应模型（私有，仅供本类使用）───────────────────────────

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string            TagName { get; init; } = string.Empty;
        [JsonPropertyName("body")]     public string?           Body    { get; init; }
        [JsonPropertyName("assets")]   public List<GitHubAsset> Assets  { get; init; } = new();
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]                 public string Name               { get; init; } = string.Empty;
        [JsonPropertyName("size")]                 public long   Size               { get; init; }
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; init; } = string.Empty;
    }
}
