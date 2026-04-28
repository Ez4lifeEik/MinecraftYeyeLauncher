using System.IO;
using ArclightLauncher.Models;

namespace ArclightLauncher.Services;

/// <summary>
/// 负责推荐游戏目录：优先选择可用空间最多的非系统盘（>5 GB），
/// 否则回退到默认的 AppData 路径。
/// </summary>
public class GameDirectoryService
{
    private const long MinFreeSpaceBytes = 5L * 1024 * 1024 * 1024; // 5 GB

    /// <summary>
    /// 返回推荐的 .minecraft 根路径。
    /// 逻辑：在所有就绪的固定硬盘中，排除系统盘，选取可用空间最大且 >5 GB 的磁盘。
    /// </summary>
    public string SuggestDefaultPath()
    {
        var systemRoot = Path.GetPathRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? "C:\\";

        var best = DriveInfo.GetDrives()
            .Where(d => d.IsReady
                     && d.DriveType == DriveType.Fixed
                     && !string.Equals(d.Name, systemRoot, StringComparison.OrdinalIgnoreCase)
                     && d.AvailableFreeSpace > MinFreeSpaceBytes)
            .OrderByDescending(d => d.AvailableFreeSpace)
            .FirstOrDefault();

        if (best != null)
            return Path.Combine(best.RootDirectory.FullName, "ArclightLauncher", ".minecraft");

        return LauncherSettings.DefaultGameDir;
    }

    /// <summary>首次运行：设置中 GameDirConfirmed 为 false 时返回 true。</summary>
    public bool IsFirstRun(LauncherSettings settings) => !settings.GameDirConfirmed;

    /// <summary>
    /// 获取指定路径所在磁盘的信息文本（如"C:\\ — 可用 23.4 GB / 共 237 GB"）。
    /// 失败时返回空字符串。
    /// </summary>
    public string GetDriveInfo(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return string.Empty;
            var di = new DriveInfo(root);
            if (!di.IsReady) return string.Empty;
            double freeMb  = di.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0;
            double totalMb = di.TotalSize          / 1024.0 / 1024.0 / 1024.0;
            return $"{di.Name}  可用 {freeMb:F1} GB / 共 {totalMb:F0} GB";
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>路径是否在系统盘上。</summary>
    public bool IsOnSystemDrive(string path)
    {
        var systemRoot = Path.GetPathRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.System));
        var pathRoot   = Path.GetPathRoot(path);
        return string.Equals(systemRoot, pathRoot, StringComparison.OrdinalIgnoreCase);
    }
}
