using System.IO;
using System.Net.Http;
using System.Text.Json;
using ArclightLauncher.Models;
using Microsoft.Extensions.Logging;
using ProjBobcat.Class.Model;
using ProjBobcat.DefaultComponent;
using ProjBobcat.DefaultComponent.Authenticator;
using ProjBobcat.DefaultComponent.Installer.ForgeInstaller;
using ProjBobcat.DefaultComponent.Launch;
using ProjBobcat.DefaultComponent.Launch.GameCore;
using ProjBobcat.DefaultComponent.ResourceInfoResolver;

namespace ArclightLauncher.Services;

/// <summary>
/// 游戏安装与启动服务（v0.2，基于 ProjBobcat 1.40.0 真实 API）
///
/// 启动流程：
///   1. 确保 Vanilla MC 版本 JSON 已就绪
///   2. DefaultResourceCompleter 补全游戏资源
///   3. 下载 Forge installer JAR 并运行 HighVersionForgeInstaller
///   4. SyncService 增量同步 mod / 资源包 / 光影包
///   5. DefaultGameCore.LaunchTaskAsync() 启动游戏
/// </summary>
public class LaunchService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly SyncService _syncService;
    private readonly ILogger<LaunchService> _logger;

    // ── 默认游戏目录（供外部引用）────────────────────────────────────────
    public static string GameDir => LauncherSettings.DefaultGameDir;

    // ── 启动器客户端令牌 ──────────────────────────────────────────────────
    private static readonly Guid ClientToken = new("f7d4e8a2-3c1b-4e5f-9a0d-6b2c7e8f1234");

    // ── Mojang 版本清单 ───────────────────────────────────────────────────
    private const string MojangManifestUrl =
        "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    // ── 进度 / 状态事件 ───────────────────────────────────────────────────
    public event EventHandler<double>? ProgressChanged;
    public event EventHandler<string>? StatusChanged;

    public LaunchService(
        IHttpClientFactory httpFactory,
        SyncService syncService,
        ILogger<LaunchService> logger)
    {
        _httpFactory = httpFactory;
        _syncService = syncService;
        _logger = logger;

        _syncService.StatusChanged   += (_, msg) => StatusChanged?.Invoke(this, msg);
        _syncService.ProgressChanged += (_, pct) => ProgressChanged?.Invoke(this, pct);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 主入口
    // ─────────────────────────────────────────────────────────────────────

    public async Task RunAsync(
        Account account,
        string javaExe,
        ServerManifest manifest,
        LaunchMode mode,
        string gameDir,
        int maxMemoryMb,
        string jvmArgs = "",
        string? customServerAddress = null,
        int customServerPort = 25565,
        IReadOnlySet<string>? disabledMods = null,
        bool forceRevalidate = false,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(gameDir);

        var mcVer         = manifest.MinecraftVersion;
        var forgeVer      = manifest.Loader.Version;
        var forgeVersionId = $"{mcVer}-forge-{forgeVer}";

        var versionLocator = new DefaultVersionLocator(gameDir, ClientToken);

        // 步骤 1：确保 Vanilla MC 已安装
        await EnsureVanillaMcAsync(mcVer, versionLocator, gameDir, ct);

        // 步骤 2：确保 Forge 已安装
        await EnsureForgeAsync(javaExe, mcVer, forgeVer, forgeVersionId, versionLocator, gameDir, ct);

        // 步骤 3：同步 mod / 资源包 / 光影包（进度 55 → 95）
        ReportStatus("正在同步整合包文件……");
        // OfficialServer 模式忽略禁用列表（effectiveDisabled = null）
        var effectiveDisabled = mode == LaunchMode.OfficialServer ? null : disabledMods;
        await _syncService.SyncAsync(manifest, gameDir, effectiveDisabled, forceRevalidate, 55, 95, ct);

        // 步骤 4：启动游戏
        await LaunchGameAsync(
            account, javaExe, manifest, forgeVersionId,
            mode, customServerAddress, customServerPort,
            versionLocator, gameDir, maxMemoryMb);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Vanilla MC 安装
    // ─────────────────────────────────────────────────────────────────────

    private async Task EnsureVanillaMcAsync(
        string mcVer,
        DefaultVersionLocator versionLocator,
        string gameDir,
        CancellationToken ct)
    {
        var versionJson = Path.Combine(gameDir, "versions", mcVer, $"{mcVer}.json");

        if (File.Exists(versionJson))
        {
            ReportStatus($"Minecraft {mcVer} 版本文件已就绪");
            ReportProgress(44);
            return;
        }

        ReportStatus($"正在获取 Minecraft {mcVer} 版本信息……");
        ReportProgress(40);
        Directory.CreateDirectory(Path.GetDirectoryName(versionJson)!);

        var client = _httpFactory.CreateClient();

        var manifestJson = await client.GetStringAsync(MojangManifestUrl, ct);
        using var doc    = JsonDocument.Parse(manifestJson);

        var versionUrl = doc.RootElement
            .GetProperty("versions")
            .EnumerateArray()
            .Where(v => v.GetProperty("id").GetString() == mcVer)
            .Select(v => v.GetProperty("url").GetString())
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Mojang 版本清单中找不到 Minecraft {mcVer}");

        ReportStatus($"正在下载 Minecraft {mcVer} 版本 JSON……");
        var content = await client.GetStringAsync(versionUrl, ct);
        await File.WriteAllTextAsync(versionJson, content, ct);
        _logger.LogInformation("Minecraft {Ver} 版本 JSON 已保存", mcVer);

        ReportStatus($"正在补全 Minecraft {mcVer} 游戏文件……");
        ReportProgress(42);
        // 用新建的 locator，确保它能看到刚写入的 version JSON
        await CompleteResourcesAsync(mcVer, new DefaultVersionLocator(gameDir, ClientToken), gameDir, ct);
        ReportProgress(48);
    }

    private async Task CompleteResourcesAsync(
        string versionId,
        DefaultVersionLocator versionLocator,
        string gameDir,
        CancellationToken ct)
    {
        VersionInfo? versionInfo = null;
        try { versionInfo = versionLocator.GetGame(versionId); }
        catch (Exception ex) { _logger.LogWarning(ex, "GetGame({Id}) 异常，跳过资源补全", versionId); }

        if (versionInfo is null)
        {
            _logger.LogWarning("VersionLocator 找不到 {Id}，跳过资源补全", versionId);
            return;
        }

        using var completer = new DefaultResourceCompleter
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            ResourceInfoResolvers =
            [
                new VersionInfoResolver
                {
                    BasePath       = gameDir,
                    CheckLocalFiles = true,
                    VersionInfo    = versionInfo
                },
                new LibraryInfoResolver
                {
                    BasePath       = gameDir,
                    CheckLocalFiles = true,
                    VersionInfo    = versionInfo
                },
                new AssetInfoResolver
                {
                    BasePath       = gameDir,
                    CheckLocalFiles = true,
                    VersionInfo    = versionInfo
                },
                new GameLoggingInfoResolver
                {
                    BasePath       = gameDir,
                    CheckLocalFiles = true,
                    VersionInfo    = versionInfo
                }
            ]
        };

        var result = await completer.CheckAndDownloadTaskAsync();
        _logger.LogInformation("资源补全完成，状态：{Status}", result.TaskStatus);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Forge 安装
    // ─────────────────────────────────────────────────────────────────────

    private async Task EnsureForgeAsync(
        string javaExe,
        string mcVer,
        string forgeVer,
        string forgeVersionId,
        DefaultVersionLocator versionLocator,
        string gameDir,
        CancellationToken ct)
    {
        var forgeVersionJson = Path.Combine(
            gameDir, "versions", forgeVersionId, $"{forgeVersionId}.json");

        if (File.Exists(forgeVersionJson))
        {
            ReportStatus($"Forge {forgeVer} 已就绪");
            ReportProgress(55);
            return;
        }

        var installerUrl =
            $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mcVer}-{forgeVer}" +
            $"/forge-{mcVer}-{forgeVer}-installer.jar";

        ReportStatus($"正在下载 Forge {forgeVer} 安装包……");
        ReportProgress(49);

        var installerPath = Path.Combine(
            Path.GetTempPath(), $"forge-{mcVer}-{forgeVer}-installer.jar");

        var client = _httpFactory.CreateClient();
        var bytes  = await client.GetByteArrayAsync(installerUrl, ct);
        await File.WriteAllBytesAsync(installerPath, bytes, ct);
        _logger.LogInformation("Forge 安装包已下载：{Size:N0} 字节", bytes.Length);

        ReportStatus($"正在安装 Forge {forgeVer}，请稍候（可能需要几分钟）……");
        ReportProgress(50);

        // 确保目标目录存在，否则 HighVersionForgeInstaller 写 JSON 时会报 DirectoryNotFoundException
        Directory.CreateDirectory(Path.Combine(gameDir, "versions", forgeVersionId));

        var installer = new HighVersionForgeInstaller
        {
            JavaExecutablePath = javaExe,
            MineCraftVersionId = mcVer,
            MineCraftVersion   = mcVer,
            RootPath           = gameDir,
            ForgeExecutablePath = installerPath,
            VersionLocator     = versionLocator,
            DownloadUrlRoot    = "https://libraries.minecraft.net/"
        };

        var result = await installer.InstallForgeTaskAsync();
        try { File.Delete(installerPath); } catch { /* 临时文件删除失败不影响流程 */ }

        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"Forge {forgeVer} 安装失败：{result.Error?.Error ?? "未知错误"}");

        _logger.LogInformation("Forge {Ver} 安装成功", forgeVer);
        ReportStatus($"Forge {forgeVer} 安装完成");
        ReportProgress(55);
    }

    // ─────────────────────────────────────────────────────────────────────
    // 启动游戏
    // ─────────────────────────────────────────────────────────────────────

    private async Task LaunchGameAsync(
        Account account,
        string javaExe,
        ServerManifest manifest,
        string forgeVersionId,
        LaunchMode mode,
        string? customServerAddress,
        int customServerPort,
        DefaultVersionLocator versionLocator,
        string gameDir,
        int maxMemoryMb)
    {
        ReportStatus("正在启动游戏……");
        ReportProgress(95);

        var accountParser = new DefaultLauncherAccountParser(gameDir, ClientToken);
        var auth = new OfflineAuthenticator
        {
            Username             = account.Username,
            LauncherAccountParser = accountParser
        };

        // 根据模式确定服务器连接参数
        ServerSettings? serverSettings = mode switch
        {
            LaunchMode.OfficialServer =>
                string.IsNullOrEmpty(manifest.Server.Address)
                    ? null
                    : new ServerSettings
                    {
                        Address = manifest.Server.Address,
                        Port    = (ushort)manifest.Server.Port
                    },
            LaunchMode.CustomServer when !string.IsNullOrEmpty(customServerAddress) =>
                new ServerSettings
                {
                    Address = customServerAddress,
                    Port    = (ushort)Math.Clamp(customServerPort, 1, 65535)
                },
            _ => null   // Singleplayer 和 CustomServer 未填 IP
        };

        var launchSettings = new LaunchSettings
        {
            GameName          = "ArclightLauncher",
            GamePath          = gameDir,
            GameResourcePath  = gameDir,
            Version           = forgeVersionId,
            VersionLocator    = versionLocator,
            Authenticator     = auth,
            GameArguments     = new GameArguments
            {
                JavaExecutable = javaExe,
                MaxMemory      = (uint)maxMemoryMb,
                GcType         = GcType.G1Gc,
                ServerSettings = serverSettings
            }
        };

        var gameCore = new DefaultGameCore
        {
            RootPath       = gameDir,
            ClientToken    = ClientToken,
            VersionLocator = versionLocator
        };

        gameCore.GameExitEventDelegate += (_, e) =>
            _logger.LogInformation("游戏已退出，退出码：{Code}", e.ExitCode);

        var launchResult = await gameCore.LaunchTaskAsync(launchSettings);

        if (launchResult.Error != null)
            throw new InvalidOperationException($"游戏启动失败：{launchResult.Error.Error}");

        ReportProgress(100);
        ReportStatus("游戏已启动 🎮");
        _logger.LogInformation("游戏进程已启动");
    }

    // ─────────────────────────────────────────────────────────────────────
    // 工具
    // ─────────────────────────────────────────────────────────────────────

    private void ReportStatus(string msg)
    {
        StatusChanged?.Invoke(this, msg);
        _logger.LogInformation("[Launch] {Msg}", msg);
    }

    private void ReportProgress(double pct)
        => ProgressChanged?.Invoke(this, Math.Clamp(pct, 0, 100));
}
