using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArclightLauncher.Models;
using Microsoft.Extensions.Logging;
using ProjBobcat.Class.Model;
using ProjBobcat.DefaultComponent;
using ProjBobcat.DefaultComponent.Authenticator;
using ProjBobcat.DefaultComponent.Installer.ForgeInstaller;
using ProjBobcat.DefaultComponent.Launch;
using ProjBobcat.DefaultComponent.Launch.GameCore;
using ProjBobcat.DefaultComponent.ResourceInfoResolver;
using ProjBobcat.Interface;

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
    private readonly DownloadService    _downloadService;
    private readonly SyncService        _syncService;
    private readonly MicrosoftAuthService _microsoftAuthService;
    private readonly ILogger<LaunchService> _logger;

    // ── 默认游戏目录（供外部引用）────────────────────────────────────────
    public static string GameDir => LauncherSettings.DefaultGameDir;

    // ── 启动器客户端令牌 ──────────────────────────────────────────────────
    private static readonly string ClientTokenPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ArclightLauncher", "client_token");

    private static Guid? _clientToken;

    public static Guid ClientToken
    {
        get
        {
            if (_clientToken is { } cached)
                return cached;

            try
            {
                if (File.Exists(ClientTokenPath) &&
                    Guid.TryParse(File.ReadAllText(ClientTokenPath).Trim(), out var persisted))
                {
                    _clientToken = persisted;
                    return persisted;
                }
            }
            catch
            {
                // 文件损坏或不可读，重新生成
            }

            var generated = Guid.NewGuid();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ClientTokenPath)!);
                File.WriteAllText(ClientTokenPath, generated.ToString());
            }
            catch
            {
                // 写入失败不影响启动，用内存中的值
            }

            _clientToken = generated;
            return generated;
        }
    }

    // ── Mojang 版本清单 ───────────────────────────────────────────────────
    private const string MojangManifestUrl =
        "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string BmclApiBaseUrl = "https://bmclapi2.bangbang93.com";
    private const string BmclMojangManifestUrl =
        BmclApiBaseUrl + "/mc/game/version_manifest_v2.json";
    private static readonly TimeSpan MetadataRequestTimeout = TimeSpan.FromSeconds(20);

    // ── 进度 / 状态事件 ───────────────────────────────────────────────────
    public event EventHandler<double>? ProgressChanged;
    public event EventHandler<string>? StatusChanged;

    public LaunchService(
        IHttpClientFactory httpFactory,
        DownloadService    downloadService,
        SyncService        syncService,
        MicrosoftAuthService microsoftAuthService,
        ILogger<LaunchService> logger)
    {
        _httpFactory     = httpFactory;
        _downloadService = downloadService;
        _syncService     = syncService;
        _microsoftAuthService = microsoftAuthService;
        _logger          = logger;

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

        var mcVer = manifest.MinecraftVersion;
        var loaderType = string.IsNullOrWhiteSpace(manifest.Loader.Type)
            ? "forge"
            : manifest.Loader.Type.Trim().ToLowerInvariant();
        var loaderVer = manifest.Loader.Version.Trim();

        // 步骤 1-2：确保 Vanilla MC 与 Mod Loader 已安装
        var loaderVersionId = await InstallVersionAsync(
            javaExe, mcVer, loaderType, loaderVer, gameDir, ct);

        var versionLocator = new DefaultVersionLocator(gameDir, ClientToken);

        // 步骤 3：同步 mod / 资源包 / 光影包（进度 55 → 95）
        ReportStatus("正在同步整合包文件……");
        // OfficialServer 模式忽略禁用列表（effectiveDisabled = null）
        var effectiveDisabled = mode == LaunchMode.OfficialServer ? null : disabledMods;
        await _syncService.SyncAsync(manifest, gameDir, effectiveDisabled, forceRevalidate, 55, 95, ct);

        // 步骤 4：启动游戏
        await LaunchGameAsync(
            account, javaExe, manifest, loaderVersionId,
            mode, customServerAddress, customServerPort,
            versionLocator, gameDir, maxMemoryMb, jvmArgs, ct);
    }

    public async Task<string> InstallVersionAsync(
        string javaExe,
        string mcVer,
        string loaderType,
        string loaderVer,
        string gameDir,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(gameDir);

        loaderType = string.IsNullOrWhiteSpace(loaderType)
            ? "forge"
            : loaderType.Trim().ToLowerInvariant();
        loaderVer = loaderVer.Trim();

        var versionLocator = new DefaultVersionLocator(gameDir, ClientToken);

        await EnsureVanillaMcAsync(mcVer, versionLocator, gameDir, ct);

        return loaderType switch
        {
            "vanilla" => mcVer,
            "forge" => await EnsureForgeAsync(javaExe, mcVer, loaderVer, versionLocator, gameDir, ct),
            "fabric" => await EnsureFabricAsync(mcVer, loaderVer, gameDir, ct),
            "quilt" => await EnsureQuiltAsync(mcVer, loaderVer, gameDir, ct),
            _ => throw new InvalidOperationException($"暂不支持的 Mod Loader：{loaderType}")
        };
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
        var versionJar  = Path.Combine(gameDir, "versions", mcVer, $"{mcVer}.jar");
        var assetsDir   = Path.Combine(gameDir, "assets");

        if (File.Exists(versionJson) && (!File.Exists(versionJar) || !Directory.Exists(assetsDir)))
        {
            _logger.LogWarning(
                "Minecraft {Ver} is missing runtime files. JarExists={JarExists}, AssetsExists={AssetsExists}",
                mcVer,
                File.Exists(versionJar),
                Directory.Exists(assetsDir));
            File.Delete(versionJson);
        }

        if (File.Exists(versionJson))
        {
            ReportStatus($"Minecraft {mcVer} 版本文件已就绪");
            ReportProgress(42);
            await RewriteMinecraftDownloadUrlsForMirrorAsync(versionJson, ct);

            if (HasUsableLocalMinecraftInstall(gameDir, versionJson, versionJar))
            {
                ReportStatus($"Minecraft {mcVer} 本地资源已就绪");
                ReportProgress(48);
                return;
            }

            ReportStatus($"正在补全 Minecraft {mcVer} 游戏资源……");
            await CompleteResourcesWithFriendlyErrorAsync(
                mcVer,
                new DefaultVersionLocator(gameDir, ClientToken),
                gameDir,
                () => HasUsableLocalMinecraftInstall(gameDir, versionJson, versionJar),
                $"Minecraft {mcVer}",
                ct);
            ReportProgress(48);
            return;
        }

        ReportStatus($"正在获取 Minecraft {mcVer} 版本信息……");
        ReportProgress(40);
        Directory.CreateDirectory(Path.GetDirectoryName(versionJson)!);

        var manifestJson = await GetStringWithFallbackAsync(
            [BmclMojangManifestUrl, MojangManifestUrl],
            ct);
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
        var content = await GetStringWithFallbackAsync(GetMetadataUrlCandidates(versionUrl), ct);
        await File.WriteAllTextAsync(versionJson, content, ct);
        await RewriteMinecraftDownloadUrlsForMirrorAsync(versionJson, ct);
        _logger.LogInformation("Minecraft {Ver} 版本 JSON 已保存", mcVer);

        // 下载客户端 JAR（ProjBobcat 的 Resolver 不包含此步骤，需手动处理）
        ReportStatus($"正在下载 Minecraft {mcVer} 客户端……");
        ReportProgress(43);
        await DownloadClientJarAsync(mcVer, versionJson, versionJar, ct);

        ReportStatus($"正在补全 Minecraft {mcVer} 游戏文件……");
        ReportProgress(44);
        // 用新建的 locator，确保它能看到刚写入的 version JSON
        await CompleteResourcesWithFriendlyErrorAsync(
            mcVer,
            new DefaultVersionLocator(gameDir, ClientToken),
            gameDir,
            () => HasUsableLocalMinecraftInstall(gameDir, versionJson, versionJar),
            $"Minecraft {mcVer}",
            ct);
        ReportProgress(48);
    }

    private async Task DownloadClientJarAsync(
        string mcVer,
        string versionJsonPath,
        string jarDestPath,
        CancellationToken ct)
    {
        if (File.Exists(jarDestPath)) return;

        var json = await File.ReadAllTextAsync(versionJsonPath, ct);
        using var doc = JsonDocument.Parse(json);

        var clientEl = doc.RootElement
            .GetProperty("downloads")
            .GetProperty("client");

        var url  = clientEl.GetProperty("url").GetString()
            ?? throw new InvalidOperationException("Version JSON 缺少 downloads.client.url");
        var sha1 = clientEl.TryGetProperty("sha1", out var sha1El) ? sha1El.GetString() : null;

        _logger.LogInformation("下载 Minecraft 客户端 JAR：{Url}", url);
        await DownloadFileWithFallbackAsync(
            GetDownloadUrlCandidates(url, $"{BmclApiBaseUrl}/version/{Uri.EscapeDataString(mcVer)}/client"),
            jarDestPath,
            sha1,
            null,
            ct);
        _logger.LogInformation("客户端 JAR 下载完成：{Path}", jarDestPath);
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
            TimeoutPerFile = TimeSpan.FromSeconds(30),
            TotalRetry = 2,
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
                    VersionInfo    = versionInfo,
                    LibraryUriRoot = BmclApiBaseUrl + "/maven/",
                    FabricMavenUriRoot = BmclApiBaseUrl + "/maven",
                    ForgeMavenUriRoot = BmclApiBaseUrl + "/maven/",
                    ForgeMavenOldUriRoot = BmclApiBaseUrl + "/maven/",
                    QuiltMavenUriRoot = BmclApiBaseUrl + "/maven/"
                },
                new AssetInfoResolver
                {
                    BasePath       = gameDir,
                    CheckLocalFiles = true,
                    VersionInfo    = versionInfo,
                    AssetUriRoot = BmclApiBaseUrl + "/assets/"
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

    private async Task CompleteResourcesWithFriendlyErrorAsync(
        string versionId,
        DefaultVersionLocator versionLocator,
        string gameDir,
        Func<bool> isLocalInstallUsable,
        string displayName,
        CancellationToken ct)
    {
        try
        {
            await CompleteResourcesAsync(versionId, versionLocator, gameDir, ct);
        }
        catch (Exception ex) when (IsNetworkDownloadFailure(ex) && isLocalInstallUsable())
        {
            _logger.LogWarning(ex, "{Name} 资源补全联网失败，但本地资源已可启动，继续启动流程", displayName);
            ReportStatus($"{displayName} 本地资源已可用，跳过联网补全");
        }
        catch (Exception ex) when (IsNetworkDownloadFailure(ex))
        {
            throw new InvalidOperationException(
                $"{displayName} 缺少必要游戏文件，当前网络无法自动补全。请检查网络，或使用带完整 .minecraft 资源的发布包。",
                ex);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // Forge 安装
    // ─────────────────────────────────────────────────────────────────────

    private async Task<string> EnsureForgeAsync(
        string javaExe,
        string mcVer,
        string forgeVer,
        DefaultVersionLocator versionLocator,
        string gameDir,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(forgeVer))
            throw new InvalidOperationException("Forge loader.version 不能为空");

        var forgeVersionId = $"{mcVer}-forge-{forgeVer}";
        var forgeVersionJson = Path.Combine(
            gameDir, "versions", forgeVersionId, $"{forgeVersionId}.json");
        var forgeClientJar = Path.Combine(
            gameDir,
            "libraries",
            "net",
            "minecraftforge",
            "forge",
            $"{mcVer}-{forgeVer}",
            $"forge-{mcVer}-{forgeVer}-client.jar");

        if (File.Exists(forgeVersionJson))
        {
            await EnsureForgeClientLibraryAsync(forgeVersionJson, forgeClientJar, mcVer, forgeVer, ct);
            ReportStatus($"Forge {forgeVer} 已就绪");
            ReportProgress(55);
            return forgeVersionId;
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
        await EnsureForgeClientLibraryAsync(forgeVersionJson, forgeClientJar, mcVer, forgeVer, ct);
        ReportStatus($"Forge {forgeVer} 安装完成");
        ReportProgress(55);
        return forgeVersionId;
    }

    private async Task EnsureForgeClientLibraryAsync(
        string forgeVersionJson,
        string forgeClientJar,
        string mcVer,
        string forgeVer,
        CancellationToken ct)
    {
        var mavenPath =
            $"net/minecraftforge/forge/{mcVer}-{forgeVer}/forge-{mcVer}-{forgeVer}-client.jar";
        var mavenUrl =
            $"https://maven.minecraftforge.net/{mavenPath}";
        var forgeClientName = $"net.minecraftforge:forge:{mcVer}-{forgeVer}:client";
        var projBobcatAliasName = $"net.minecraftforge:forge-client:{mcVer}-{forgeVer}";

        string? expectedSha1 = null;

        if (File.Exists(forgeVersionJson))
        {
            var json = await File.ReadAllTextAsync(forgeVersionJson, ct);
            var root = JsonNode.Parse(json)?.AsObject()
                ?? throw new InvalidOperationException($"Forge 版本 JSON 无法解析：{forgeVersionJson}");
            var libraries = root["libraries"]?.AsArray()
                ?? throw new InvalidOperationException($"Forge 版本 JSON 缺少 libraries：{forgeVersionJson}");

            var changed = false;
            JsonObject? clientLibrary = null;
            JsonObject? aliasLibrary = null;
            JsonObject? clientArtifact = null;

            foreach (var item in libraries)
            {
                if (item is not JsonObject library)
                    continue;

                var artifact = library["downloads"]?["artifact"]?.AsObject();
                var path = artifact?["path"]?.GetValue<string>();
                var name = library["name"]?.GetValue<string>();

                if (string.Equals(path, mavenPath, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, forgeClientName, StringComparison.OrdinalIgnoreCase))
                {
                    clientLibrary = library;
                    clientArtifact = artifact;
                    expectedSha1 = artifact?["sha1"]?.GetValue<string>();

                    if (artifact is not null &&
                        string.IsNullOrWhiteSpace(artifact["url"]?.GetValue<string>()))
                    {
                        artifact["url"] = mavenUrl;
                        changed = true;
                    }

                    continue;
                }

                if (string.Equals(name, projBobcatAliasName, StringComparison.OrdinalIgnoreCase))
                {
                    aliasLibrary = library;
                    expectedSha1 ??= artifact?["sha1"]?.GetValue<string>();

                    if (artifact is not null &&
                        string.IsNullOrWhiteSpace(artifact["url"]?.GetValue<string>()))
                    {
                        artifact["url"] = mavenUrl;
                        changed = true;
                    }
                }
            }

            if (clientLibrary is null)
            {
                libraries.Insert(0, new JsonObject
                {
                    ["downloads"] = new JsonObject
                    {
                        ["artifact"] = new JsonObject
                        {
                            ["path"] = mavenPath,
                            ["url"] = mavenUrl
                        }
                    },
                    ["name"] = $"net.minecraftforge:forge:{mcVer}-{forgeVer}:client",
                    ["serverreq"] = false,
                    ["clientreq"] = true
                });
                changed = true;
            }

            if (aliasLibrary is null)
            {
                var aliasArtifact = new JsonObject
                {
                    ["path"] = mavenPath,
                    ["url"] = mavenUrl
                };

                if (!string.IsNullOrWhiteSpace(expectedSha1))
                    aliasArtifact["sha1"] = expectedSha1;

                if (clientArtifact?["size"] is JsonNode sizeNode)
                    aliasArtifact["size"] = sizeNode.DeepClone();

                libraries.Insert(0, new JsonObject
                {
                    ["downloads"] = new JsonObject
                    {
                        ["artifact"] = aliasArtifact
                    },
                    ["name"] = projBobcatAliasName,
                    ["serverreq"] = false,
                    ["clientreq"] = true
                });
                changed = true;
            }

            if (changed)
            {
                await File.WriteAllTextAsync(
                    forgeVersionJson,
                    root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                    ct);
                _logger.LogInformation("已修复 Forge client library 元数据：{Path}", forgeVersionJson);
            }
        }

        if (File.Exists(forgeClientJar))
            return;

        ReportStatus($"正在补全 Forge {forgeVer} 客户端核心……");
        _logger.LogWarning("Forge client jar 缺失，正在下载：{Path}", forgeClientJar);
        await DownloadFileWithFallbackAsync(
            GetDownloadUrlCandidates(mavenUrl),
            forgeClientJar,
            expectedSha1,
            null,
            ct);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Fabric 安装
    // ─────────────────────────────────────────────────────────────────────

    private async Task<string> EnsureFabricAsync(
        string mcVer,
        string loaderVer,
        string gameDir,
        CancellationToken ct)
    {
        var localLoaderVer = ResolveLocalFabricLoaderVersion(gameDir, mcVer, loaderVer);
        if (!string.IsNullOrWhiteSpace(localLoaderVer))
            ReportStatus($"使用本地 Fabric Loader {localLoaderVer}");

        var resolvedLoaderVer = localLoaderVer
            ?? await ResolveFabricLoaderVersionAsync(mcVer, loaderVer, ct);
        var fabricVersionId = $"{mcVer}-fabric-{resolvedLoaderVer}";
        var fabricVersionJson = Path.Combine(
            gameDir, "versions", fabricVersionId, $"{fabricVersionId}.json");

        var needsRewrite = !File.Exists(fabricVersionJson) ||
            await FabricVersionJsonNeedsRewriteAsync(fabricVersionJson, ct);

        if (!needsRewrite)
        {
            ReportStatus($"Fabric {resolvedLoaderVer} 已就绪");
            ReportProgress(52);
            await RewriteMinecraftDownloadUrlsForMirrorAsync(fabricVersionJson, ct);
            await EnsureFabricLibrariesAsync(fabricVersionJson, gameDir, ct);
            ReportProgress(55);

            if (HasUsableFabricInstall(gameDir, mcVer, fabricVersionJson))
            {
                ReportStatus($"Fabric {resolvedLoaderVer} 本地资源已就绪");
                return fabricVersionId;
            }

            await CompleteResourcesWithFriendlyErrorAsync(
                fabricVersionId,
                new DefaultVersionLocator(gameDir, ClientToken),
                gameDir,
                () => HasUsableFabricInstall(gameDir, mcVer, fabricVersionJson),
                $"Fabric {resolvedLoaderVer}",
                ct);
            return fabricVersionId;
        }

        ReportStatus($"正在安装 Fabric {resolvedLoaderVer}……");
        ReportProgress(50);

        Directory.CreateDirectory(Path.GetDirectoryName(fabricVersionJson)!);

        var profileUrl =
            $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mcVer)}/" +
            $"{Uri.EscapeDataString(resolvedLoaderVer)}/profile/json";

        string profileJson;
        try
        {
            profileJson = await GetStringWithFallbackAsync(GetFabricMetaUrlCandidates(profileUrl), ct);
        }
        catch (Exception ex) when (IsNetworkDownloadFailure(ex))
        {
            throw new InvalidOperationException(
                $"Fabric 暂不支持 Minecraft {mcVer} / Loader {resolvedLoaderVer}，请换一个 Minecraft 或 Fabric 版本。",
                ex);
        }

        var root = await BuildFlattenedFabricVersionJsonAsync(
            gameDir,
            mcVer,
            fabricVersionId,
            profileJson,
            ct);

        await File.WriteAllTextAsync(
            fabricVersionJson,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            ct);
        await RewriteMinecraftDownloadUrlsForMirrorAsync(fabricVersionJson, ct);

        _logger.LogInformation("Fabric 版本 JSON 已保存：{Path}", fabricVersionJson);

        ReportStatus($"正在下载 Fabric {resolvedLoaderVer} 运行库……");
        ReportProgress(52);
        await EnsureFabricLibrariesAsync(fabricVersionJson, gameDir, ct);

        ReportStatus($"正在补全 Fabric {resolvedLoaderVer} 运行库……");
        ReportProgress(53);
        await CompleteResourcesWithFriendlyErrorAsync(
            fabricVersionId,
            new DefaultVersionLocator(gameDir, ClientToken),
            gameDir,
            () => HasUsableFabricInstall(gameDir, mcVer, fabricVersionJson),
            $"Fabric {resolvedLoaderVer}",
            ct);

        ReportStatus($"Fabric {resolvedLoaderVer} 安装完成");
        ReportProgress(55);
        return fabricVersionId;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Quilt 安装
    // ─────────────────────────────────────────────────────────────────────

    private const string QuiltMetaBaseUrl = "https://meta.quiltmc.org";
    private const string QuiltMainClass = "org.quiltmc.loader.impl.launch.knot.KnotClient";

    private async Task<string> EnsureQuiltAsync(
        string mcVer,
        string loaderVer,
        string gameDir,
        CancellationToken ct)
    {
        var resolvedLoaderVer = await ResolveQuiltLoaderVersionAsync(mcVer, loaderVer, ct);
        var quiltVersionId = $"{mcVer}-quilt-{resolvedLoaderVer}";
        var quiltVersionJson = Path.Combine(
            gameDir, "versions", quiltVersionId, $"{quiltVersionId}.json");

        var needsRewrite = !File.Exists(quiltVersionJson) ||
            await QuiltVersionJsonNeedsRewriteAsync(quiltVersionJson, ct);

        if (!needsRewrite)
        {
            ReportStatus($"Quilt {resolvedLoaderVer} 已就绪");
            ReportProgress(52);
            await RewriteMinecraftDownloadUrlsForMirrorAsync(quiltVersionJson, ct);
            await EnsureFabricLibrariesAsync(quiltVersionJson, gameDir, ct);
            ReportProgress(55);

            if (HasUsableQuiltInstall(gameDir, mcVer, quiltVersionJson))
            {
                ReportStatus($"Quilt {resolvedLoaderVer} 本地资源已就绪");
                return quiltVersionId;
            }

            await CompleteResourcesWithFriendlyErrorAsync(
                quiltVersionId,
                new DefaultVersionLocator(gameDir, ClientToken),
                gameDir,
                () => HasUsableQuiltInstall(gameDir, mcVer, quiltVersionJson),
                $"Quilt {resolvedLoaderVer}",
                ct);
            return quiltVersionId;
        }

        ReportStatus($"正在安装 Quilt {resolvedLoaderVer}……");
        ReportProgress(50);

        Directory.CreateDirectory(Path.GetDirectoryName(quiltVersionJson)!);

        var profileUrl =
            $"{QuiltMetaBaseUrl}/v3/versions/loader/{Uri.EscapeDataString(mcVer)}/" +
            $"{Uri.EscapeDataString(resolvedLoaderVer)}/profile/json";

        string profileJson;
        try
        {
            profileJson = await GetStringWithFallbackAsync(
                [MapDownloadUrlToMirror(profileUrl), profileUrl], ct);
        }
        catch (Exception ex) when (IsNetworkDownloadFailure(ex))
        {
            throw new InvalidOperationException(
                $"Quilt 暂不支持 Minecraft {mcVer} / Loader {resolvedLoaderVer}，请换一个 Minecraft 或 Quilt 版本。",
                ex);
        }

        var root = await BuildFlattenedQuiltVersionJsonAsync(
            gameDir, mcVer, quiltVersionId, profileJson, ct);

        await File.WriteAllTextAsync(
            quiltVersionJson,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            ct);
        await RewriteMinecraftDownloadUrlsForMirrorAsync(quiltVersionJson, ct);

        _logger.LogInformation("Quilt 版本 JSON 已保存：{Path}", quiltVersionJson);

        ReportStatus($"正在下载 Quilt {resolvedLoaderVer} 运行库……");
        ReportProgress(52);
        await EnsureFabricLibrariesAsync(quiltVersionJson, gameDir, ct);

        ReportStatus($"正在补全 Quilt {resolvedLoaderVer} 运行库……");
        ReportProgress(53);
        await CompleteResourcesWithFriendlyErrorAsync(
            quiltVersionId,
            new DefaultVersionLocator(gameDir, ClientToken),
            gameDir,
            () => HasUsableQuiltInstall(gameDir, mcVer, quiltVersionJson),
            $"Quilt {resolvedLoaderVer}",
            ct);

        ReportStatus($"Quilt {resolvedLoaderVer} 安装完成");
        ReportProgress(55);
        return quiltVersionId;
    }

    private async Task<JsonObject> BuildFlattenedQuiltVersionJsonAsync(
        string gameDir, string mcVer, string quiltVersionId,
        string quiltProfileJson, CancellationToken ct)
    {
        var vanillaVersionJson = Path.Combine(gameDir, "versions", mcVer, $"{mcVer}.json");
        if (!File.Exists(vanillaVersionJson))
            throw new InvalidOperationException($"缺少 Minecraft {mcVer} 版本 JSON：{vanillaVersionJson}");

        var vanillaRoot = JsonNode.Parse(await File.ReadAllTextAsync(vanillaVersionJson, ct))?.AsObject()
            ?? throw new InvalidOperationException($"Minecraft {mcVer} 版本 JSON 无法解析");
        var quiltRoot = JsonNode.Parse(quiltProfileJson)?.AsObject()
            ?? throw new InvalidOperationException("Quilt 版本 JSON 无法解析");

        var root = vanillaRoot.DeepClone().AsObject();
        root["id"] = quiltVersionId;
        root["jar"] = mcVer;
        root.Remove("inheritsFrom");

        if (quiltRoot["type"] is JsonNode type)
            root["type"] = type.DeepClone();

        root["mainClass"] = quiltRoot["mainClass"]?.DeepClone() ?? QuiltMainClass;

        var arguments = root["arguments"] as JsonObject ?? [];
        var jvmArgs = arguments["jvm"] as JsonArray ?? [];
        if (quiltRoot["arguments"]?["jvm"] is JsonArray quiltJvmArgs)
        {
            foreach (var arg in quiltJvmArgs)
            {
                if (arg is JsonValue value && value.TryGetValue<string>(out var text))
                    jvmArgs.Add(NormalizeFabricJvmArgument(text));
                else
                    jvmArgs.Add(arg?.DeepClone());
            }
        }
        arguments["jvm"] = jvmArgs;
        root["arguments"] = arguments;

        var libraries = root["libraries"] as JsonArray ?? [];
        var existingLibraryNames = libraries
            .OfType<JsonObject>()
            .Select(lib => lib["name"]?.GetValue<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (quiltRoot["libraries"] is JsonArray quiltLibraries)
        {
            foreach (var item in quiltLibraries)
            {
                if (item is not JsonObject library)
                    continue;
                var name = library["name"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(name) && existingLibraryNames.Add(name))
                    libraries.Add(library.DeepClone());
            }
        }
        root["libraries"] = libraries;

        return root;
    }

    private async Task<bool> QuiltVersionJsonNeedsRewriteAsync(
        string quiltVersionJson, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(quiltVersionJson, ct);
            var root = JsonNode.Parse(json)?.AsObject();
            if (root is null) return true;
            if (root["inheritsFrom"] is not null) return true;

            if (!string.Equals(
                    root["mainClass"]?.GetValue<string>(),
                    QuiltMainClass,
                    StringComparison.Ordinal))
                return true;

            if (HasNonIsoOffset(root["releaseTime"]?.GetValue<string>()) ||
                HasNonIsoOffset(root["time"]?.GetValue<string>()))
                return true;

            if (HasUnsafeFabricJvmArgument(root["arguments"]?["jvm"] as JsonArray))
                return true;

            var gameArgs = root["arguments"]?["game"] as JsonArray;
            return gameArgs is null || !ContainsStringArgument(gameArgs, "--username");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quilt 版本 JSON 需要重建：{Path}", quiltVersionJson);
            return true;
        }
    }

    private async Task<string> ResolveQuiltLoaderVersionAsync(
        string mcVer, string loaderVer, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(loaderVer) &&
            !loaderVer.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return loaderVer;

        ReportStatus($"正在获取 Minecraft {mcVer} 可用的 Quilt Loader……");

        var versionsUrl = $"{QuiltMetaBaseUrl}/v3/versions/loader/{Uri.EscapeDataString(mcVer)}";

        string json;
        try
        {
            json = await GetStringWithFallbackAsync(
                [MapDownloadUrlToMirror(versionsUrl), versionsUrl], ct);
        }
        catch (Exception ex) when (IsNetworkDownloadFailure(ex))
        {
            throw new InvalidOperationException(
                $"Quilt 暂未提供 Minecraft {mcVer} 的 Loader。若这是快照版，请确认 Quilt Meta 已支持该版本。",
                ex);
        }

        using var doc = JsonDocument.Parse(json);
        var loaders = doc.RootElement.EnumerateArray().ToList();

        var stable = loaders.FirstOrDefault(item =>
            item.TryGetProperty("loader", out var loader) &&
            loader.TryGetProperty("stable", out var stableEl) &&
            stableEl.ValueKind == JsonValueKind.True);

        var selected = stable.ValueKind == JsonValueKind.Undefined
            ? loaders.FirstOrDefault()
            : stable;

        if (selected.ValueKind == JsonValueKind.Undefined ||
            !selected.TryGetProperty("loader", out var selectedLoader) ||
            !selectedLoader.TryGetProperty("version", out var versionEl))
        {
            throw new InvalidOperationException($"没有找到 Minecraft {mcVer} 可用的 Quilt Loader");
        }

        var version = versionEl.GetString();
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException($"Quilt Loader 版本解析失败：Minecraft {mcVer}");

        _logger.LogInformation("Quilt Loader 自动选择 {Version} for Minecraft {McVer}", version, mcVer);
        return version;
    }

    private static bool HasUsableQuiltInstall(string gameDir, string mcVer, string quiltVersionJson)
    {
        var versionJar = Path.Combine(gameDir, "versions", mcVer, $"{mcVer}.jar");
        return HasRequiredVersionFiles(gameDir, quiltVersionJson, versionJar);
    }

    private async Task<bool> FabricVersionJsonNeedsRewriteAsync(
        string fabricVersionJson,
        CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(fabricVersionJson, ct);
            var root = JsonNode.Parse(json)?.AsObject();
            if (root is null)
                return true;

            if (root["inheritsFrom"] is not null)
                return true;

            if (!string.Equals(
                    root["mainClass"]?.GetValue<string>(),
                    "net.fabricmc.loader.impl.launch.knot.KnotClient",
                    StringComparison.Ordinal))
            {
                return true;
            }

            if (HasNonIsoOffset(root["releaseTime"]?.GetValue<string>()) ||
                HasNonIsoOffset(root["time"]?.GetValue<string>()))
            {
                return true;
            }

            if (HasUnsafeFabricJvmArgument(root["arguments"]?["jvm"] as JsonArray))
                return true;

            var gameArgs = root["arguments"]?["game"] as JsonArray;
            return gameArgs is null || !ContainsStringArgument(gameArgs, "--username");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fabric 版本 JSON 需要重建：{Path}", fabricVersionJson);
            return true;
        }
    }

    private async Task<JsonObject> BuildFlattenedFabricVersionJsonAsync(
        string gameDir,
        string mcVer,
        string fabricVersionId,
        string fabricProfileJson,
        CancellationToken ct)
    {
        var vanillaVersionJson = Path.Combine(gameDir, "versions", mcVer, $"{mcVer}.json");
        if (!File.Exists(vanillaVersionJson))
            throw new InvalidOperationException($"缺少 Minecraft {mcVer} 版本 JSON：{vanillaVersionJson}");

        var vanillaRoot = JsonNode.Parse(await File.ReadAllTextAsync(vanillaVersionJson, ct))?.AsObject()
            ?? throw new InvalidOperationException($"Minecraft {mcVer} 版本 JSON 无法解析");
        var fabricRoot = JsonNode.Parse(fabricProfileJson)?.AsObject()
            ?? throw new InvalidOperationException("Fabric 版本 JSON 无法解析");

        var root = vanillaRoot.DeepClone().AsObject();
        root["id"] = fabricVersionId;
        root["jar"] = mcVer;
        root.Remove("inheritsFrom");

        if (fabricRoot["type"] is JsonNode type)
            root["type"] = type.DeepClone();

        root["mainClass"] = fabricRoot["mainClass"]?.DeepClone()
            ?? "net.fabricmc.loader.impl.launch.knot.KnotClient";

        var arguments = root["arguments"] as JsonObject ?? [];
        var jvmArgs = arguments["jvm"] as JsonArray ?? [];
        if (fabricRoot["arguments"]?["jvm"] is JsonArray fabricJvmArgs)
        {
            foreach (var arg in fabricJvmArgs)
            {
                if (arg is JsonValue value &&
                    value.TryGetValue<string>(out var text))
                {
                    jvmArgs.Add(NormalizeFabricJvmArgument(text));
                }
                else
                {
                    jvmArgs.Add(arg?.DeepClone());
                }
            }
        }
        arguments["jvm"] = jvmArgs;
        root["arguments"] = arguments;

        var libraries = root["libraries"] as JsonArray ?? [];
        var existingLibraryNames = libraries
            .OfType<JsonObject>()
            .Select(library => library["name"]?.GetValue<string>())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (fabricRoot["libraries"] is JsonArray fabricLibraries)
        {
            foreach (var item in fabricLibraries)
            {
                if (item is not JsonObject library)
                    continue;

                var name = library["name"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(name) && existingLibraryNames.Add(name))
                    libraries.Add(library.DeepClone());
            }
        }
        root["libraries"] = libraries;

        return root;
    }

    private async Task EnsureFabricLibrariesAsync(
        string fabricVersionJson,
        string gameDir,
        CancellationToken ct)
    {
        var json = await File.ReadAllTextAsync(fabricVersionJson, ct);
        var root = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException($"Fabric 版本 JSON 无法解析：{fabricVersionJson}");
        var libraries = root["libraries"] as JsonArray
            ?? throw new InvalidOperationException($"Fabric 版本 JSON 缺少 libraries：{fabricVersionJson}");

        var changed = false;
        foreach (var item in libraries)
        {
            if (item is not JsonObject library)
                continue;

            var name = library["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!TryBuildMavenArtifact(name, out var artifact))
                continue;

            var artifactNode = library["downloads"]?["artifact"] as JsonObject;
            var path = artifactNode?["path"]?.GetValue<string>();
            var url = artifactNode?["url"]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(url))
            {
                var baseUrl = library["url"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(baseUrl))
                    continue;

                path = artifact.Path;
                url = CombineMavenUrl(baseUrl, artifact.Path);

                artifactNode ??= [];
                artifactNode["path"] = path;
                artifactNode["url"] = url;

                if (library["sha1"] is JsonNode sha1)
                    artifactNode["sha1"] = sha1.DeepClone();
                if (library["size"] is JsonNode size)
                    artifactNode["size"] = size.DeepClone();

                library["downloads"] = new JsonObject { ["artifact"] = artifactNode };
                changed = true;
            }

            var destination = Path.Combine(gameDir, "libraries", path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(destination))
                continue;

            var expectedSha1 = artifactNode?["sha1"]?.GetValue<string>();
            ReportStatus($"正在下载 Fabric 运行库：{artifact.FileName}");
            _logger.LogInformation("下载 Fabric 运行库：{Name} -> {Path}", name, destination);
            await DownloadFileWithFallbackAsync(
                GetDownloadUrlCandidates(url!),
                destination,
                expectedSha1,
                null,
                ct);
        }

        if (changed)
        {
            await File.WriteAllTextAsync(
                fabricVersionJson,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                ct);
        }
    }

    private static bool TryBuildMavenArtifact(
        string coordinate,
        out (string Path, string FileName) artifact)
    {
        artifact = default;

        var extension = "jar";
        var coordinatePart = coordinate;
        var extensionIndex = coordinate.LastIndexOf('@');
        if (extensionIndex >= 0)
        {
            coordinatePart = coordinate[..extensionIndex];
            extension = coordinate[(extensionIndex + 1)..];
        }

        var parts = coordinatePart.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
            return false;

        var group = parts[0];
        var name = parts[1];
        var version = parts[2];
        var classifier = parts.Length >= 4 ? "-" + parts[3] : string.Empty;
        var fileName = $"{name}-{version}{classifier}.{extension}";
        var path = $"{group.Replace('.', '/')}/{name}/{version}/{fileName}";

        artifact = (path, fileName);
        return true;
    }

    private static string CombineMavenUrl(string baseUrl, string path)
        => baseUrl.TrimEnd('/') + "/" + path;

    private static bool ContainsStringArgument(JsonArray args, string expected)
    {
        foreach (var arg in args)
        {
            if (arg is JsonValue value &&
                value.TryGetValue<string>(out var text) &&
                string.Equals(text, expected, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasUnsafeFabricJvmArgument(JsonArray? args)
    {
        if (args is null)
            return false;

        foreach (var arg in args)
        {
            if (arg is JsonValue value &&
                value.TryGetValue<string>(out var text) &&
                IsUnsafeFabricJvmArgument(text))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnsafeFabricJvmArgument(string text)
        => text.StartsWith("-DFabricMcEmu=", StringComparison.Ordinal) &&
           text.Any(char.IsWhiteSpace);

    private static string NormalizeFabricJvmArgument(string text)
    {
        const string fabricMainProperty = "-DFabricMcEmu=";
        if (!text.StartsWith(fabricMainProperty, StringComparison.Ordinal))
            return text;

        var mainClass = text[fabricMainProperty.Length..].Trim();
        return string.IsNullOrWhiteSpace(mainClass)
            ? text
            : fabricMainProperty + mainClass;
    }

    private static bool HasNonIsoOffset(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 5)
            return false;

        var signIndex = value.Length - 5;
        return (value[signIndex] == '+' || value[signIndex] == '-') &&
               value[^3] != ':';
    }

    private async Task<string> ResolveFabricLoaderVersionAsync(
        string mcVer,
        string loaderVer,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(loaderVer) &&
            !loaderVer.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            return loaderVer;
        }

        ReportStatus($"正在获取 Minecraft {mcVer} 可用的 Fabric Loader……");

        var versionsUrl =
            $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mcVer)}";

        string json;
        try
        {
            json = await GetStringWithFallbackAsync(GetFabricMetaUrlCandidates(versionsUrl), ct);
        }
        catch (Exception ex) when (IsNetworkDownloadFailure(ex))
        {
            throw new InvalidOperationException(
                $"Fabric 暂未提供 Minecraft {mcVer} 的 Loader。若这是快照版，请确认 Fabric Meta 已支持该版本。",
                ex);
        }

        using var doc = JsonDocument.Parse(json);
        var loaders = doc.RootElement.EnumerateArray().ToList();

        var stable = loaders.FirstOrDefault(item =>
            item.TryGetProperty("loader", out var loader) &&
            loader.TryGetProperty("stable", out var stableEl) &&
            stableEl.ValueKind == JsonValueKind.True);

        var selected = stable.ValueKind == JsonValueKind.Undefined
            ? loaders.FirstOrDefault()
            : stable;

        if (selected.ValueKind == JsonValueKind.Undefined ||
            !selected.TryGetProperty("loader", out var selectedLoader) ||
            !selectedLoader.TryGetProperty("version", out var versionEl))
        {
            throw new InvalidOperationException($"没有找到 Minecraft {mcVer} 可用的 Fabric Loader");
        }

        var version = versionEl.GetString();
        if (string.IsNullOrWhiteSpace(version))
            throw new InvalidOperationException($"Fabric Loader 版本解析失败：Minecraft {mcVer}");

        _logger.LogInformation("Fabric Loader 自动选择 {Version} for Minecraft {McVer}", version, mcVer);
        return version;
    }

    // ─────────────────────────────────────────────────────────────────────
    // 启动游戏
    // ─────────────────────────────────────────────────────────────────────

    private async Task LaunchGameAsync(
        Account account,
        string javaExe,
        ServerManifest manifest,
        string loaderVersionId,
        LaunchMode mode,
        string? customServerAddress,
        int customServerPort,
        DefaultVersionLocator versionLocator,
        string gameDir,
        int maxMemoryMb,
        string jvmArgs,
        CancellationToken ct)
    {
        ReportStatus("正在启动游戏……");
        ReportProgress(95);

        await EnsureChineseLanguageAsync(gameDir, ct);
        var gameJavaExe = ResolveGameJavaExecutable(javaExe);

        var accountParser = new DefaultLauncherAccountParser(gameDir, ClientToken);
        var auth = CreateAuthenticator(account, accountParser);

        // 根据模式确定服务器连接参数
        (string Address, int Port)? serverTarget = mode switch
        {
            LaunchMode.OfficialServer =>
                string.IsNullOrEmpty(manifest.Server.Address)
                    ? null
                    : (Address: manifest.Server.Address, Port: Math.Clamp(manifest.Server.Port, 1, 65535)),
            LaunchMode.CustomServer when !string.IsNullOrEmpty(customServerAddress) =>
                (Address: customServerAddress, Port: Math.Clamp(customServerPort, 1, 65535)),
            _ => null   // Singleplayer 和 CustomServer 未填 IP
        };

        var useQuickPlay = serverTarget != null && ShouldUseQuickPlayMultiplayer(manifest.MinecraftVersion);
        var serverSettings = serverTarget != null && !useQuickPlay
            ? new ServerSettings
            {
                Address = serverTarget.Value.Address,
                Port    = (ushort)serverTarget.Value.Port
            }
            : null;

        if (serverTarget != null)
            _logger.LogInformation(
                "将自动连接服务器 {Address}:{Port}，参数模式：{Mode}",
                serverTarget.Value.Address,
                serverTarget.Value.Port,
                useQuickPlay ? "quickPlayMultiplayer" : "server");

        await ApplyQuickPlayServerArgumentsAsync(
            gameDir,
            loaderVersionId,
            useQuickPlay ? serverTarget : null,
            ct);

        // ProjBobcat 1.40 会在构造 VersionInfo 时读取 JSON；quickPlay 写入后刷新 locator。
        versionLocator = new DefaultVersionLocator(gameDir, ClientToken);

        // 解析用户自定义 JVM 参数（空白分割，支持 -D/-X/-XX 参数）
        var extraArgs = jvmArgs
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .ToList();

        // 始终追加 Bootstrap 调试标志，帮助诊断启动失败
        if (!extraArgs.Contains("-Dbsl.debug=true"))
            extraArgs.Add("-Dbsl.debug=true");

        var launchSettings = new LaunchSettings
        {
            GameName          = loaderVersionId,
            GamePath          = gameDir,
            GameResourcePath  = gameDir,
            Version           = loaderVersionId,
            VersionLocator    = versionLocator,
            Authenticator     = auth,
            GameArguments     = new GameArguments
            {
                JavaExecutable     = gameJavaExe,
                MaxMemory          = (uint)maxMemoryMb,
                GcType             = GcType.G1Gc,
                ServerSettings     = serverSettings,
                AdditionalJvmArguments = extraArgs.Count > 0 ? extraArgs : null
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

        // 捕获游戏进程的标准输出（含 Bootstrap 诊断日志）
        gameCore.GameLogEventDelegate += (_, log) =>
            _logger.LogDebug("[GameOut] {Line}", log.RawContent);

        var launchResult = await gameCore.LaunchTaskAsync(launchSettings);

        if (launchResult.Error != null)
            throw new InvalidOperationException($"游戏启动失败：{launchResult.Error.Error}");

        ReportProgress(100);
        ReportStatus("游戏已启动 🎮");
        _logger.LogInformation("游戏进程已启动");
    }

    private IAuthenticator CreateAuthenticator(Account account, DefaultLauncherAccountParser accountParser)
    {
        if (account.IsMicrosoft)
        {
            _logger.LogInformation("使用 Microsoft 正版账号启动：{Username}", account.Username);
            return _microsoftAuthService.CreateAuthenticator(account, accountParser);
        }

        return new OfflineAuthenticator
        {
            Username = account.Username,
            LauncherAccountParser = accountParser
        };
    }

    private async Task EnsureChineseLanguageAsync(string gameDir, CancellationToken ct)
    {
        var optionsPath = Path.Combine(gameDir, "options.txt");
        Directory.CreateDirectory(gameDir);

        var lines = File.Exists(optionsPath)
            ? (await File.ReadAllLinesAsync(optionsPath, ct)).ToList()
            : [];

        var langIndex = lines.FindIndex(line =>
            line.StartsWith("lang:", StringComparison.OrdinalIgnoreCase));

        if (langIndex >= 0)
        {
            if (string.Equals(lines[langIndex], "lang:zh_cn", StringComparison.OrdinalIgnoreCase))
                return;

            lines[langIndex] = "lang:zh_cn";
        }
        else
        {
            lines.Add("lang:zh_cn");
        }

        await File.WriteAllLinesAsync(optionsPath, lines, ct);
        _logger.LogInformation("已设置 Minecraft 启动语言为简体中文：{Path}", optionsPath);
    }

    private string ResolveGameJavaExecutable(string javaExe)
    {
        if (!string.Equals(Path.GetFileName(javaExe), "java.exe", StringComparison.OrdinalIgnoreCase))
            return javaExe;

        var javaDir = Path.GetDirectoryName(javaExe);
        if (string.IsNullOrEmpty(javaDir))
            return javaExe;

        var javawExe = Path.Combine(javaDir, "javaw.exe");
        if (!File.Exists(javawExe))
            return javaExe;

        _logger.LogInformation("使用无控制台 Java 启动游戏：{Path}", javawExe);
        return javawExe;
    }

    private async Task ApplyQuickPlayServerArgumentsAsync(
        string gameDir,
        string versionId,
        (string Address, int Port)? serverTarget,
        CancellationToken ct)
    {
        var versionJson = Path.Combine(gameDir, "versions", versionId, $"{versionId}.json");
        if (!File.Exists(versionJson))
            return;

        var json = await File.ReadAllTextAsync(versionJson, ct);
        var root = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException($"版本 JSON 无法解析：{versionJson}");

        var arguments = root["arguments"] as JsonObject;
        if (arguments is null)
        {
            arguments = [];
            root["arguments"] = arguments;
        }

        var gameArguments = arguments["game"] as JsonArray;
        if (gameArguments is null)
        {
            gameArguments = [];
            arguments["game"] = gameArguments;
        }

        var changed = false;
        var cleanedArguments = new JsonArray();

        for (var i = 0; i < gameArguments.Count; i++)
        {
            var node = gameArguments[i];
            if (node is JsonValue value &&
                value.TryGetValue<string>(out var arg) &&
                IsQuickPlayMultiplayerArgument(arg))
            {
                changed = true;

                if (string.Equals(arg, "--quickPlayMultiplayer", StringComparison.Ordinal) &&
                    i + 1 < gameArguments.Count &&
                    gameArguments[i + 1] is JsonValue nextValue &&
                    nextValue.TryGetValue<string>(out var nextArg) &&
                    !nextArg.StartsWith("--", StringComparison.Ordinal))
                {
                    i++;
                }

                continue;
            }

            cleanedArguments.Add(node?.DeepClone());
        }

        if (serverTarget is { } target)
        {
            cleanedArguments.Add("--quickPlayMultiplayer");
            cleanedArguments.Add($"{target.Address}:{target.Port}");
            changed = true;
        }

        if (!changed)
            return;

        arguments["game"] = cleanedArguments;

        await File.WriteAllTextAsync(
            versionJson,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            ct);

        if (serverTarget is { } appliedTarget)
            _logger.LogInformation(
                "已写入 quickPlayMultiplayer 启动参数：{Address}:{Port}",
                appliedTarget.Address,
                appliedTarget.Port);
        else
            _logger.LogInformation("已清理 quickPlayMultiplayer 启动参数");
    }

    private static bool IsQuickPlayMultiplayerArgument(string arg)
        => string.Equals(arg, "--quickPlayMultiplayer", StringComparison.Ordinal) ||
           arg.StartsWith("--quickPlayMultiplayer ", StringComparison.Ordinal);

    private static bool ShouldUseQuickPlayMultiplayer(string minecraftVersion)
    {
        var parts = minecraftVersion
            .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length < 2)
            return false;

        if (!int.TryParse(parts[0], out var major) ||
            !int.TryParse(parts[1], out var minor))
            return false;

        return major > 1 || major == 1 && minor >= 20;
    }

    private async Task<string> GetStringWithFallbackAsync(
        IEnumerable<string> urls,
        CancellationToken ct)
    {
        var candidates = urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Exception? lastError = null;
        foreach (var url in candidates)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(MetadataRequestTimeout);

                var client = _httpFactory.CreateClient();
                _logger.LogInformation("读取元数据：{Url}", url);
                return await client.GetStringAsync(url, timeoutCts.Token);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsNetworkDownloadFailure(ex))
            {
                lastError = ex;
                _logger.LogWarning(ex, "读取元数据失败，尝试下一个下载源：{Url}", url);
            }
        }

        throw new InvalidOperationException(
            $"无法连接下载源：{string.Join(" / ", candidates)}",
            lastError);
    }

    private async Task DownloadFileWithFallbackAsync(
        IEnumerable<string> urls,
        string destPath,
        string? expectedSha1,
        Action<long, long>? onProgress,
        CancellationToken ct)
    {
        var candidates = urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Exception? lastError = null;
        foreach (var url in candidates)
        {
            try
            {
                await _downloadService.DownloadFileAsync(url, destPath, expectedSha1, onProgress, ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && IsNetworkDownloadFailure(ex))
            {
                lastError = ex;
                _logger.LogWarning(ex, "下载失败，尝试下一个下载源：{Url}", url);
            }
        }

        throw new InvalidOperationException(
            $"文件下载失败：{Path.GetFileName(destPath)}",
            lastError);
    }

    private static IReadOnlyList<string> GetMetadataUrlCandidates(string url)
        => OrderedDistinct([MapMetadataUrlToMirror(url), url]);

    private static IReadOnlyList<string> GetFabricMetaUrlCandidates(string url)
        => OrderedDistinct([MapDownloadUrlToMirror(url), url]);

    private static IReadOnlyList<string> GetDownloadUrlCandidates(
        string url,
        string? extraMirrorUrl = null)
        => OrderedDistinct([extraMirrorUrl, MapDownloadUrlToMirror(url), url]);

    private static IReadOnlyList<string> OrderedDistinct(IEnumerable<string?> urls)
        => urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string MapMetadataUrlToMirror(string url)
    {
        var mapped = ReplacePrefix(
            url,
            "https://piston-meta.mojang.com/",
            BmclApiBaseUrl + "/");

        mapped = ReplacePrefix(
            mapped,
            "https://launchermeta.mojang.com/",
            BmclApiBaseUrl + "/");

        return mapped;
    }

    private static string MapDownloadUrlToMirror(string url)
    {
        var mapped = MapMetadataUrlToMirror(url);

        mapped = ReplacePrefix(
            mapped,
            "https://resources.download.minecraft.net/",
            BmclApiBaseUrl + "/assets/");
        mapped = ReplacePrefix(
            mapped,
            "http://resources.download.minecraft.net/",
            BmclApiBaseUrl + "/assets/");
        mapped = ReplacePrefix(
            mapped,
            "https://libraries.minecraft.net/",
            BmclApiBaseUrl + "/maven/");
        mapped = ReplacePrefix(
            mapped,
            "https://maven.fabricmc.net/",
            BmclApiBaseUrl + "/maven/");
        mapped = ReplacePrefix(
            mapped,
            "https://maven.minecraftforge.net/",
            BmclApiBaseUrl + "/maven/");
        mapped = ReplacePrefix(
            mapped,
            "https://files.minecraftforge.net/maven/",
            BmclApiBaseUrl + "/maven/");
        mapped = ReplacePrefix(
            mapped,
            "https://meta.fabricmc.net/",
            BmclApiBaseUrl + "/fabric-meta/");

        return mapped;
    }

    private static string ReplacePrefix(string value, string prefix, string replacement)
        => value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? replacement + value[prefix.Length..]
            : value;

    private async Task RewriteMinecraftDownloadUrlsForMirrorAsync(
        string versionJsonPath,
        CancellationToken ct)
    {
        if (!File.Exists(versionJsonPath))
            return;

        var json = await File.ReadAllTextAsync(versionJsonPath, ct);
        var root = JsonNode.Parse(json);
        if (root is null || !RewriteDownloadUrls(root))
            return;

        await File.WriteAllTextAsync(
            versionJsonPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            ct);
        _logger.LogInformation("已将版本 JSON 下载地址切换为国内镜像：{Path}", versionJsonPath);
    }

    private static bool RewriteDownloadUrls(JsonNode? node)
    {
        var changed = false;

        switch (node)
        {
            case JsonObject obj:
                foreach (var property in obj.ToList())
                {
                    if (property.Value is JsonValue value &&
                        value.TryGetValue<string>(out var text))
                    {
                        var mapped = MapDownloadUrlToMirror(text);
                        if (!string.Equals(mapped, text, StringComparison.Ordinal))
                        {
                            obj[property.Key] = mapped;
                            changed = true;
                        }

                        continue;
                    }

                    changed |= RewriteDownloadUrls(property.Value);
                }
                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value &&
                        value.TryGetValue<string>(out var text))
                    {
                        var mapped = MapDownloadUrlToMirror(text);
                        if (!string.Equals(mapped, text, StringComparison.Ordinal))
                        {
                            array[i] = mapped;
                            changed = true;
                        }

                        continue;
                    }

                    changed |= RewriteDownloadUrls(array[i]);
                }
                break;
        }

        return changed;
    }

    private static bool HasUsableLocalMinecraftInstall(
        string gameDir,
        string versionJson,
        string versionJar)
    {
        return HasRequiredVersionFiles(gameDir, versionJson, versionJar);
    }

    private static bool HasUsableFabricInstall(
        string gameDir,
        string mcVer,
        string fabricVersionJson)
    {
        var versionJar = Path.Combine(gameDir, "versions", mcVer, $"{mcVer}.jar");
        return HasRequiredVersionFiles(gameDir, fabricVersionJson, versionJar);
    }

    private static bool HasRequiredVersionFiles(
        string gameDir,
        string versionJson,
        string fallbackVersionJar)
    {
        if (!File.Exists(versionJson))
            return false;

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(versionJson))?.AsObject();
            if (root is null)
                return false;

            if (!HasVersionClientJar(gameDir, root, fallbackVersionJar))
                return false;

            if (!HasRequiredLibraries(gameDir, root))
                return false;

            return HasRequiredAssets(gameDir, root);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasVersionClientJar(
        string gameDir,
        JsonObject versionRoot,
        string fallbackVersionJar)
    {
        if (File.Exists(fallbackVersionJar))
            return true;

        var jarVersion = versionRoot["jar"]?.GetValue<string>() ??
                         versionRoot["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(jarVersion))
            return false;

        var jarPath = Path.Combine(gameDir, "versions", jarVersion, $"{jarVersion}.jar");
        return File.Exists(jarPath);
    }

    private static bool HasRequiredLibraries(string gameDir, JsonObject versionRoot)
    {
        if (versionRoot["libraries"] is not JsonArray libraries)
            return false;

        foreach (var item in libraries)
        {
            if (item is not JsonObject library)
                continue;

            var artifactPath = library["downloads"]?["artifact"]?["path"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(artifactPath))
                continue;

            var localPath = Path.Combine(
                gameDir,
                "libraries",
                artifactPath.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(localPath))
                return false;
        }

        return true;
    }

    private static bool HasRequiredAssets(string gameDir, JsonObject versionRoot)
    {
        var assetIndexId = versionRoot["assetIndex"]?["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(assetIndexId))
            return false;

        var assetIndexPath = Path.Combine(gameDir, "assets", "indexes", $"{assetIndexId}.json");
        if (!File.Exists(assetIndexPath))
            return false;

        var indexRoot = JsonNode.Parse(File.ReadAllText(assetIndexPath))?.AsObject();
        if (indexRoot?["objects"] is not JsonObject objects)
            return false;

        foreach (var (_, node) in objects)
        {
            var hash = node?["hash"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2)
                return false;

            var objectPath = Path.Combine(
                gameDir,
                "assets",
                "objects",
                hash[..2],
                hash);

            if (!File.Exists(objectPath))
                return false;
        }

        return true;
    }

    private static string? ResolveLocalFabricLoaderVersion(
        string gameDir,
        string mcVer,
        string loaderVer)
    {
        if (!string.IsNullOrWhiteSpace(loaderVer) &&
            !loaderVer.Equals("latest", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var versionsDir = Path.Combine(gameDir, "versions");
        if (!Directory.Exists(versionsDir))
            return null;

        var prefix = $"{mcVer}-fabric-";
        try
        {
            return Directory.EnumerateDirectories(versionsDir)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrWhiteSpace(name) &&
                               name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                               File.Exists(Path.Combine(versionsDir, name, $"{name}.json")))
                .OrderByDescending(name => Directory.GetLastWriteTimeUtc(Path.Combine(versionsDir, name!)))
                .Select(name => name![prefix.Length..])
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsNetworkDownloadFailure(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException or IOException or TaskCanceledException or TimeoutException)
                return true;
        }

        return false;
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
