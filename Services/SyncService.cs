using System.IO;
using ArclightLauncher.Helpers;
using ArclightLauncher.Models;
using Microsoft.Extensions.Logging;

namespace ArclightLauncher.Services;

/// <summary>
/// Mod / 资源包 / 光影包增量同步服务
///
/// 同步规则：
///   · manifest 中 side == "client" 或 "both" 的文件为目标集合
///   · 文件在 disabledMods 列表中且 user_removable == true
///       → 若已在目标目录则移至 *_disabled_by_launcher/，跳过下载
///   · 目标目录已有且 SHA1 一致（forceRevalidate 为 false）→ 跳过
///   · 禁用目录有缓存文件且 SHA1 一致 → 直接还原，省略下载
///   · 其余情况 → 下载
///   · 目标目录中不在 manifest 的多余文件 → 移至 *_disabled_by_launcher/
/// </summary>
public class SyncService
{
    private readonly DownloadService _downloader;
    private readonly ILogger<SyncService> _logger;

    private static readonly HashSet<string> BlockedExtraMods = new(StringComparer.OrdinalIgnoreCase)
    {
        "controlify-3.0.0-beta.3+1.21.11-fabric.jar"
    };

    public event EventHandler<double>? ProgressChanged;
    public event EventHandler<string>? StatusChanged;

    public SyncService(DownloadService downloader, ILogger<SyncService> logger)
    {
        _downloader = downloader;
        _logger = logger;
    }

    /// <summary>
    /// 同步整合包文件。
    /// </summary>
    /// <param name="manifest">manifest</param>
    /// <param name="gameDir">游戏根目录（.minecraft）</param>
    /// <param name="disabledMods">
    ///   用户禁用的 mod 文件名集合（大小写不敏感）；
    ///   传 null 表示 OfficialServer 模式，忽略禁用列表强制全量同步
    /// </param>
    /// <param name="forceRevalidate">true 时跳过 SHA1 缓存，强制重新下载所有文件</param>
    /// <param name="progressStart">进度区间起始</param>
    /// <param name="progressEnd">进度区间结束</param>
    public async Task SyncAsync(
        ServerManifest manifest,
        string gameDir,
        IReadOnlySet<string>? disabledMods = null,
        bool forceRevalidate = false,
        double progressStart = 55,
        double progressEnd = 95,
        CancellationToken ct = default)
    {
        var groups = new[]
        {
            (Files: manifest.Mods
                .Where(f => f.Side is "client" or "both")
                .ToList(),
             SubDir: "mods",
             Label: "Mod"),

            (Files: manifest.Resourcepacks
                .Where(f => f.Side is "client" or "both")
                .ToList(),
             SubDir: "resourcepacks",
             Label: "资源包"),

            (Files: manifest.Shaderpacks
                .Where(f => f.Side is "client" or "both")
                .ToList(),
             SubDir: "shaderpacks",
             Label: "光影包"),
        };

        int totalFiles = groups.Sum(g => g.Files.Count);
        int doneFiles  = 0;

        foreach (var (files, subDir, label) in groups)
        {
            if (files.Count == 0) continue;

            var targetDir  = Path.Combine(gameDir, subDir);
            var disabledDir = Path.Combine(gameDir, $"{subDir}_disabled_by_launcher");
            Directory.CreateDirectory(targetDir);

            // 先处理多余文件（目标目录中不在 manifest 的文件）
            await DisableExtraFilesAsync(
                targetDir,
                files,
                disabledDir,
                preserveUnknownExtras: subDir == "mods",
                ct);

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();

                var destPath     = Path.Combine(targetDir,  file.Filename);
                var disabledPath = Path.Combine(disabledDir, file.Filename);

                // ── 1. 应该禁用 ──────────────────────────────────────────
                if (disabledMods != null && file.UserRemovable && disabledMods.Contains(file.Filename))
                {
                    if (File.Exists(destPath))
                    {
                        Directory.CreateDirectory(disabledDir);
                        // 若禁用目录已有同名文件，加时间戳
                        var realDest = File.Exists(disabledPath)
                            ? BuildTimestampedPath(disabledDir, file.Filename)
                            : disabledPath;
                        await Task.Run(() => File.Move(destPath, realDest, overwrite: false), ct);
                        _logger.LogInformation("已禁用，移至禁用目录：{File}", file.Filename);
                    }
                    doneFiles++;
                    ReportProgress(CalcProgress(progressStart, progressEnd, doneFiles, totalFiles));
                    continue;
                }

                // ── 2. 已在目标目录且 SHA1 一致 ──────────────────────────
                if (!forceRevalidate && File.Exists(destPath))
                {
                    var actual = await HashHelper.ComputeSha1Async(destPath, ct);
                    if (actual.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogDebug("{Label} 已是最新，跳过：{File}", label, file.Filename);
                        doneFiles++;
                        ReportProgress(CalcProgress(progressStart, progressEnd, doneFiles, totalFiles));
                        continue;
                    }
                    _logger.LogInformation("{Label} SHA1 不匹配，需重新下载：{File}", label, file.Filename);
                }

                // ── 3. 禁用目录有缓存且 SHA1 一致，直接还原 ──────────────
                if (!forceRevalidate && File.Exists(disabledPath))
                {
                    var cached = await HashHelper.ComputeSha1Async(disabledPath, ct);
                    if (cached.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        ReportStatus($"正在还原{label}：{file.Filename}");
                        await Task.Run(() => File.Move(disabledPath, destPath, overwrite: true), ct);
                        _logger.LogInformation("从禁用目录还原：{File}", file.Filename);
                        doneFiles++;
                        ReportProgress(CalcProgress(progressStart, progressEnd, doneFiles, totalFiles));
                        continue;
                    }
                }

                // ── 4. 启动器内置包有缓存，直接复制 ───────────────────────
                if (await TryRestoreFromBundledPackAsync(file, subDir, destPath, label, ct))
                {
                    doneFiles++;
                    ReportProgress(CalcProgress(progressStart, progressEnd, doneFiles, totalFiles));
                    continue;
                }

                // ── 5. 下载 ───────────────────────────────────────────────
                ReportStatus($"正在下载{label}：{file.Filename}");
                await _downloader.DownloadFileAsync(file.Url, destPath, file.Sha1, null, ct);

                doneFiles++;
                ReportProgress(CalcProgress(progressStart, progressEnd, doneFiles, totalFiles));
            }
        }

        ReportStatus("文件同步完成");
        ReportProgress(progressEnd);
        _logger.LogInformation("SyncService 完成，共处理 {Count} 个文件", doneFiles);
    }

    private async Task<bool> TryRestoreFromBundledPackAsync(
        PackFile file,
        string subDir,
        string destPath,
        string label,
        CancellationToken ct)
    {
        foreach (var candidate in GetBundledPackFileCandidates(subDir, file.Filename))
        {
            if (!File.Exists(candidate))
                continue;

            if (!string.IsNullOrWhiteSpace(file.Sha1))
            {
                var actual = await HashHelper.ComputeSha1Async(candidate, ct);
                if (!actual.Equals(file.Sha1, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "启动器内置{Label} SHA1 不匹配，跳过：{File}",
                        label,
                        file.Filename);
                    continue;
                }
            }

            ReportStatus($"正在从启动器内置包复制{label}：{file.Filename}");
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            await Task.Run(() => File.Copy(candidate, destPath, overwrite: true), ct);
            _logger.LogInformation("已从启动器内置包恢复{Label}：{File}", label, file.Filename);
            return true;
        }

        return false;
    }

    private static IEnumerable<string> GetBundledPackFileCandidates(string subDir, string filename)
    {
        var baseDir = AppContext.BaseDirectory;
        return new[]
        {
            Path.Combine(baseDir, "Assets", "PackCache", subDir, filename),
            Path.Combine(baseDir, "..", "..", "..", "Assets", "PackCache", subDir, filename)
        }.Select(Path.GetFullPath);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 多余文件处理
    // ─────────────────────────────────────────────────────────────────────

    private async Task DisableExtraFilesAsync(
        string targetDir,
        IReadOnlyCollection<PackFile> expectedFiles,
        string disabledDir,
        bool preserveUnknownExtras,
        CancellationToken ct)
    {
        if (!Directory.Exists(targetDir)) return;

        var expectedNames = expectedFiles
            .Select(f => f.Filename.ToLowerInvariant())
            .ToHashSet();

        var extras = Directory.EnumerateFiles(targetDir)
            .Where(f => !expectedNames.Contains(Path.GetFileName(f).ToLowerInvariant()))
            .ToList();

        if (extras.Count == 0) return;

        Directory.CreateDirectory(disabledDir);

        foreach (var extra in extras)
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(extra);

            // Players may add their own client-only Fabric mods. Keep unknown extra
            // mods in place, while still quarantining known crashing files.
            if (preserveUnknownExtras && !BlockedExtraMods.Contains(fileName))
            {
                _logger.LogInformation("保留玩家自定义 Mod：{File}", fileName);
                continue;
            }

            var dest     = Path.Combine(disabledDir, fileName);
            if (File.Exists(dest))
                dest = BuildTimestampedPath(disabledDir, fileName);

            _logger.LogInformation("移动多余文件至禁用目录：{File}", fileName);
            await Task.Run(() => File.Move(extra, dest, overwrite: false), ct);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 工具
    // ─────────────────────────────────────────────────────────────────────

    private static string BuildTimestampedPath(string dir, string filename)
        => Path.Combine(dir,
            $"{Path.GetFileNameWithoutExtension(filename)}_{DateTimeOffset.Now:yyyyMMddHHmmss}{Path.GetExtension(filename)}");

    private static double CalcProgress(double start, double end, int done, int total)
        => total == 0 ? end : start + (end - start) * done / total;

    private void ReportStatus(string msg)
    {
        StatusChanged?.Invoke(this, msg);
        _logger.LogInformation("[Sync] {Msg}", msg);
    }

    private void ReportProgress(double pct)
        => ProgressChanged?.Invoke(this, Math.Clamp(pct, 0, 100));
}
