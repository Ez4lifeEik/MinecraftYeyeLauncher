using System.IO;
using System.Net.Http;
using ArclightLauncher.Helpers;
using Microsoft.Extensions.Logging;

namespace ArclightLauncher.Services;

/// <summary>
/// 带重试、SHA1 校验、进度回调的文件下载封装
/// </summary>
public class DownloadService
{
    private const int MaxRetries = 3;

    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<DownloadService> _logger;

    public DownloadService(IHttpClientFactory httpFactory, ILogger<DownloadService> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    /// <summary>
    /// 依次尝试多个镜像下载同一个文件，任一成功即返回。
    /// 每个候选 URL 内部仍按 <see cref="DownloadFileAsync"/> 的规则重试并校验 SHA1。
    /// 只有全部候选都失败才抛异常，异常携带最后一次错误。
    /// </summary>
    /// <param name="urls">按优先级排列的候选下载地址（如 [镜像, 源站]）</param>
    public async Task DownloadFileWithFallbackAsync(
        IReadOnlyList<string> urls,
        string destPath,
        string? expectedSha1 = null,
        Action<long, long>? onProgress = null,
        CancellationToken ct = default)
    {
        var candidates = urls
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
            throw new ArgumentException("未提供任何下载地址", nameof(urls));

        Exception? lastError = null;
        for (var i = 0; i < candidates.Count; i++)
        {
            var url = candidates[i];
            try
            {
                await DownloadFileAsync(url, destPath, expectedSha1, onProgress, ct);
                return;
            }
            // 用户主动取消时立即停止；其余失败则尝试下一个镜像
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                lastError = ex;
                _logger.LogWarning(
                    "镜像下载失败（{Index}/{Total}），尝试下一个源：{Url}",
                    i + 1, candidates.Count, url);
            }
        }

        throw new InvalidOperationException(
            $"文件 {Path.GetFileName(destPath)} 下载失败（已尝试 {candidates.Count} 个下载源）",
            lastError);
    }

    /// <summary>
    /// 下载单个文件并校验 SHA1。
    /// </summary>
    /// <param name="url">下载地址</param>
    /// <param name="destPath">目标路径（含文件名）</param>
    /// <param name="expectedSha1">预期 SHA1（小写十六进制），传 null 则跳过校验</param>
    /// <param name="onProgress">进度回调：(已下载字节, 总字节)</param>
    /// <param name="ct">取消令牌</param>
    public async Task DownloadFileAsync(
        string url,
        string destPath,
        string? expectedSha1 = null,
        Action<long, long>? onProgress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        for (var attempt = 1; attempt <= MaxRetries; attempt++)
        {
            try
            {
                await DownloadOnceAsync(url, destPath, onProgress, ct);

                // SHA1 校验
                if (!string.IsNullOrEmpty(expectedSha1))
                {
                    var actual = await HashHelper.ComputeSha1Async(destPath, ct);
                    if (!actual.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning(
                            "SHA1 不匹配（第 {Attempt} 次）：{File}\n  期望 {Exp}\n  实际 {Act}",
                            attempt, Path.GetFileName(destPath), expectedSha1, actual);

                        TryDelete(destPath);

                        if (attempt == MaxRetries)
                            throw new InvalidOperationException(
                                $"文件 {Path.GetFileName(destPath)} SHA1 校验失败（已重试 {MaxRetries} 次）");

                        continue; // 重试
                    }
                }

                _logger.LogDebug("下载完成：{File}", Path.GetFileName(destPath));
                return;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "下载失败（第 {Attempt}/{Max} 次）：{Url}", attempt, MaxRetries, url);
                TryDelete(destPath);

                if (attempt == MaxRetries)
                    throw;

                await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct); // 退避
            }
        }
    }

    // ── 单次下载（支持进度回调）──────────────────────────────────────────

    private async Task DownloadOnceAsync(
        string url,
        string destPath,
        Action<long, long>? onProgress,
        CancellationToken ct)
    {
        var client = _httpFactory.CreateClient();

        using var response = await client.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1L;

        await using var srcStream = await response.Content.ReadAsStreamAsync(ct);
        await using var destStream = new FileStream(
            destPath, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 81920, useAsync: true);

        var buffer = new byte[81920];
        long downloaded = 0;
        int read;

        while ((read = await srcStream.ReadAsync(buffer, ct)) > 0)
        {
            await destStream.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            onProgress?.Invoke(downloaded, totalBytes);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 删除失败不影响流程 */ }
    }
}
