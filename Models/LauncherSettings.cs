using System.IO;

namespace ArclightLauncher.Models;

/// <summary>
/// 启动器设置，持久化到 %APPDATA%\ArclightLauncher\settings.json
/// </summary>
public class LauncherSettings
{
    // ── 账户 ──────────────────────────────────────────────────────────────
    /// <summary>玩家用户名（离线模式）</summary>
    public string Username { get; set; } = string.Empty;

    // ── 路径 ──────────────────────────────────────────────────────────────
    /// <summary>游戏目录（.minecraft 根路径）</summary>
    public string GameDir { get; set; } = DefaultGameDir;

    /// <summary>Java 可执行文件路径；空字符串表示自动检测</summary>
    public string JavaExe { get; set; } = string.Empty;

    /// <summary>未找到合适 Java 时自动下载启动器托管的 Eclipse Temurin JRE。</summary>
    public bool AutoDownloadJava { get; set; } = true;

    // ── 性能 ──────────────────────────────────────────────────────────────
    /// <summary>最大 JVM 堆内存（MB）</summary>
    public int MaxMemory { get; set; } = ComputeDefaultMemoryMb();

    /// <summary>附加 JVM 参数（高级选项）</summary>
    public string JvmArgs { get; set; } = string.Empty;

    // ── 外观 ──────────────────────────────────────────────────────────────
    /// <summary>主题：Light / Dark / System</summary>
    public string Theme { get; set; } = "System";

    /// <summary>自定义背景图路径；空字符串表示使用内置随机背景</summary>
    public string BackgroundImagePath { get; set; } = string.Empty;

    // ── 启动行为 ──────────────────────────────────────────────────────────
    /// <summary>启动后行为：Keep / Minimize / Close</summary>
    public string PostLaunchBehavior { get; set; } = "Minimize";

    // ── Mod 管理 ──────────────────────────────────────────────────────────
    /// <summary>用户主动禁用的 mod 文件名列表（仅对 user_removable: true 的 mod 有效）</summary>
    public List<string> DisabledMods { get; set; } = [];

    /// <summary>下次启动时跳过 SHA1 缓存、强制重新下载所有文件</summary>
    public bool ForceRevalidate { get; set; }

    // ── 启动模式（持久化）────────────────────────────────────────────────
    /// <summary>上次选择的启动模式</summary>
    public LaunchMode LastLaunchMode { get; set; } = LaunchMode.OfficialServer;

    /// <summary>自定义服务器地址</summary>
    public string CustomServerAddress { get; set; } = string.Empty;

    /// <summary>自定义服务器端口</summary>
    public int CustomServerPort { get; set; } = 25565;

    /// <summary>玩家自选启动的 Minecraft 版本</summary>
    public string PlayerMinecraftVersion { get; set; } = "1.21.10";

    /// <summary>玩家自选启动的 Fabric Loader 版本，latest 表示自动选择最新版</summary>
    public string PlayerFabricLoaderVersion { get; set; } = "latest";

    /// <summary>版本选择器是否显示快照版</summary>
    public bool IncludeSnapshotsInVersionList { get; set; } = true;

    // ── 首次运行 ──────────────────────────────────────────────────────────
    /// <summary>用户已通过首次运行引导确认了游戏目录</summary>
    public bool GameDirConfirmed { get; set; } = false;

    // ── 默认值辅助 ────────────────────────────────────────────────────────

    public static readonly string DefaultGameDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ArclightLauncher", ".minecraft");

    /// <summary>
    /// 计算默认内存：物理内存的 75%，向上取整到最近 512 MB，
    /// 下限 1024 MB，上限 4096 MB。
    /// </summary>
    public static int ComputeDefaultMemoryMb()
    {
        try
        {
            var totalMb = (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024);
            var raw = (int)(totalMb * 0.75);
            // 向上取整到最近 512
            var rounded = ((raw + 511) / 512) * 512;
            return Math.Clamp(rounded, 1024, 4096);
        }
        catch
        {
            return 2048;
        }
    }

    /// <summary>物理内存 75% 上限（用于滑块 Maximum）</summary>
    public static int PhysicalMemory75Pct()
    {
        try
        {
            var totalMb = (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024);
            return (int)(totalMb * 0.75);
        }
        catch
        {
            return 8192;
        }
    }
}
