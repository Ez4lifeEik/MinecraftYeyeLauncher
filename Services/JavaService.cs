using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ArclightLauncher.Services;

public sealed record ManagedJavaRuntime(int MajorVersion, string JavaExe, string HomeDir);

/// <summary>
/// Java 检测、自动下载与托管运行时管理。
/// </summary>
public class JavaService
{
    private const int MaxDownloadProgress = 80;

    private readonly DownloadService _downloadService;
    private readonly ILogger<JavaService> _logger;

    public const string JavaDownloadUrl = "https://adoptium.net/temurin/releases/";

    public static string ManagedJavaRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ArclightLauncher",
        "runtime",
        "java");

    public static string BundledJavaRoot => Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "JavaCache");

    public event EventHandler<string>? StatusChanged;
    public event EventHandler<double>? ProgressChanged;

    public JavaService(DownloadService downloadService, ILogger<JavaService> logger)
    {
        _downloadService = downloadService;
        _logger = logger;
    }

    /// <summary>
    /// 优先使用用户指定路径；否则使用托管 Java / 系统 Java；允许时自动安装所需大版本。
    /// </summary>
    public async Task<string?> ResolveJavaAsync(
        int requiredVersion,
        string? configuredJavaExe,
        bool autoDownload,
        CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(configuredJavaExe) && File.Exists(configuredJavaExe))
        {
            var configuredVersion = await GetMajorVersionAsync(configuredJavaExe);
            if (configuredVersion >= requiredVersion)
            {
                _logger.LogInformation("使用已配置 Java {Version}: {Path}", configuredVersion, configuredJavaExe);
                return configuredJavaExe;
            }

            _logger.LogWarning(
                "已配置 Java 版本不足。Required={Required}, Actual={Actual}, Path={Path}",
                requiredVersion,
                configuredVersion,
                configuredJavaExe);
        }

        var managedJava = await FindManagedJavaAsync(requiredVersion);
        if (managedJava != null)
            return managedJava;

        var javaExe = await FindJavaAsync(requiredVersion);
        if (javaExe != null)
            return javaExe;

        if (!autoDownload)
            return null;

        return await InstallManagedJavaAsync(requiredVersion, ct);
    }

    /// <summary>
    /// 在启动器托管目录中安装指定大版本的 Eclipse Temurin JRE。
    /// </summary>
    public async Task<string> InstallManagedJavaAsync(int majorVersion, CancellationToken ct = default)
    {
        var existing = await FindManagedJavaAsync(majorVersion);
        if (existing != null)
        {
            ReportStatus($"Java {majorVersion} 已安装");
            ReportProgress(100);
            return existing;
        }

        Directory.CreateDirectory(ManagedJavaRoot);
        var installDir = Path.Combine(ManagedJavaRoot, $"temurin-{majorVersion}");

        var bundledJava = await TryInstallBundledJavaAsync(majorVersion, installDir, ct);
        if (bundledJava != null)
            return bundledJava;

        var arch = GetAdoptiumArch();
        var apiUrl =
            $"https://api.adoptium.net/v3/binary/latest/{majorVersion}/ga/windows/{arch}/jre/hotspot/normal/eclipse";
        var zipPath = Path.Combine(Path.GetTempPath(), $"ArclightLauncher-java-{majorVersion}-{Guid.NewGuid():N}.zip");
        var extractRoot = Path.Combine(Path.GetTempPath(), $"ArclightLauncher-java-{Guid.NewGuid():N}");

        try
        {
            ReportStatus($"正在下载 Java {majorVersion} 运行环境……");
            ReportProgress(5);
            await _downloadService.DownloadFileAsync(
                apiUrl,
                zipPath,
                null,
                (downloaded, total) =>
                {
                    if (total <= 0) return;
                    var pct = 5 + downloaded * (MaxDownloadProgress - 5) / (double)total;
                    ReportProgress(pct);
                },
                ct);

            ReportStatus($"正在安装 Java {majorVersion}……");
            ReportProgress(85);

            Directory.CreateDirectory(extractRoot);
            ZipFile.ExtractToDirectory(zipPath, extractRoot);

            var extractedJavaExe = Directory
                .EnumerateFiles(extractRoot, "java.exe", SearchOption.AllDirectories)
                .FirstOrDefault(path => path.EndsWith($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}java.exe",
                    StringComparison.OrdinalIgnoreCase));

            if (extractedJavaExe == null)
                throw new InvalidOperationException("下载的 Java 压缩包中未找到 bin\\java.exe");

            var javaHome = Directory.GetParent(Path.GetDirectoryName(extractedJavaExe)!)?.FullName
                ?? throw new InvalidOperationException("无法解析 Java 安装目录");

            DeleteManagedInstallDir(installDir);
            Directory.Move(javaHome, installDir);

            var installedJavaExe = Path.Combine(installDir, "bin", "java.exe");
            var installedVersion = await GetMajorVersionAsync(installedJavaExe);
            if (installedVersion < majorVersion)
                throw new InvalidOperationException($"Java 安装校验失败：需要 {majorVersion}+，实际 {installedVersion}");

            ReportStatus($"Java {installedVersion} 已安装");
            ReportProgress(100);
            _logger.LogInformation("托管 Java {Version} 安装完成：{Path}", installedVersion, installedJavaExe);

            return installedJavaExe;
        }
        finally
        {
            TryDeleteFile(zipPath);
            TryDeleteDirectory(extractRoot);
        }
    }

    public async Task<IReadOnlyList<ManagedJavaRuntime>> GetManagedRuntimesAsync()
    {
        if (!Directory.Exists(ManagedJavaRoot))
            return [];

        var result = new List<ManagedJavaRuntime>();

        foreach (var dir in Directory.EnumerateDirectories(ManagedJavaRoot))
        {
            var javaExe = Path.Combine(dir, "bin", "java.exe");
            if (!File.Exists(javaExe)) continue;

            var version = await GetMajorVersionAsync(javaExe);
            if (version <= 0) continue;

            result.Add(new ManagedJavaRuntime(version, javaExe, dir));
        }

        return result
            .OrderByDescending(runtime => runtime.MajorVersion)
            .ThenBy(runtime => runtime.HomeDir, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 在系统和启动器托管目录中寻找版本大于等于 minVersion 的 Java。
    /// </summary>
    public async Task<string?> FindJavaAsync(int minVersion = 17)
    {
        var candidates = CollectCandidates();

        foreach (var candidate in candidates)
        {
            var version = await GetMajorVersionAsync(candidate);
            if (version >= minVersion)
            {
                _logger.LogInformation("找到满足要求的 Java {Version}: {Path}", version, candidate);
                return candidate;
            }

            if (version > 0)
                _logger.LogDebug("跳过 Java {Version}（需要 >= {Min}）：{Path}", version, minVersion, candidate);
        }

        _logger.LogWarning("未找到 Java {Min}+，可自动安装或手动安装：{Url}", minVersion, JavaDownloadUrl);
        return null;
    }

    /// <summary>向后兼容旧调用。</summary>
    public Task<string?> FindJava17Async() => FindJavaAsync(17);

    public async Task<int> GetMajorVersionAsync(string javaExe)
    {
        try
        {
            var text = await ReadJavaVersionAsync(javaExe, "--version");
            var version = ParseMajorVersion(text);
            if (version > 0) return version;

            text = await ReadJavaVersionAsync(javaExe, "-version");
            return ParseMajorVersion(text);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("无法执行 {JavaExe}: {Msg}", javaExe, ex.Message);
            return 0;
        }
    }

    private async Task<string?> FindManagedJavaAsync(int majorVersion)
    {
        foreach (var runtime in await GetManagedRuntimesAsync())
        {
            if (runtime.MajorVersion == majorVersion)
                return runtime.JavaExe;
        }

        return null;
    }

    private List<string> CollectCandidates()
    {
        var list = new List<string>();

        list.AddRange(FindManagedCandidates());

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var exe = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(exe)) list.Add(exe);
        }

        var fromPath = FindInPath("java.exe");
        if (fromPath != null) list.Add(fromPath);

        list.AddRange(FindFromRegistry());
        list.AddRange(ScanCommonDirs());

        return list
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> FindManagedCandidates()
    {
        if (!Directory.Exists(ManagedJavaRoot))
            yield break;

        foreach (var dir in Directory.EnumerateDirectories(ManagedJavaRoot))
        {
            var exe = Path.Combine(dir, "bin", "java.exe");
            if (File.Exists(exe))
                yield return exe;
        }
    }

    private static string? FindInPath(string exeName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;

            var full = Path.Combine(dir.Trim(), exeName);
            if (File.Exists(full)) return full;
        }

        return null;
    }

    private IEnumerable<string> FindFromRegistry()
    {
        var roots = new[]
        {
            @"SOFTWARE\JavaSoft\JDK",
            @"SOFTWARE\JavaSoft\Java Development Kit",
            @"SOFTWARE\JavaSoft\Java Runtime Environment",
            @"SOFTWARE\Eclipse Adoptium\JDK",
            @"SOFTWARE\Eclipse Adoptium\JRE",
            @"SOFTWARE\Eclipse Foundation\JDK",
            @"SOFTWARE\Eclipse Foundation\JRE",
            @"SOFTWARE\Microsoft\JDK",
            @"SOFTWARE\Azul Systems\Zulu",
        };

        var hives = new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser };
        var views = new[] { RegistryView.Registry64, RegistryView.Registry32 };

        foreach (var hive in hives)
        foreach (var view in views)
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            foreach (var root in roots)
            {
                using var key = baseKey.OpenSubKey(root);
                if (key == null) continue;

                foreach (var versionName in key.GetSubKeyNames())
                {
                    using var vKey = key.OpenSubKey(versionName);
                    var javaHome = vKey?.GetValue("JavaHome") as string
                                ?? vKey?.GetValue("InstallDir") as string;
                    if (string.IsNullOrEmpty(javaHome)) continue;

                    var exe = Path.Combine(javaHome, "bin", "java.exe");
                    if (File.Exists(exe))
                    {
                        _logger.LogDebug("注册表发现 Java: {Path}", exe);
                        yield return exe;
                    }
                }
            }
        }
    }

    private static IEnumerable<string> ScanCommonDirs()
    {
        var searchRoots = new[]
        {
            @"C:\Program Files\Java",
            @"C:\Program Files\Eclipse Adoptium",
            @"C:\Program Files\Microsoft",
            @"C:\Program Files\Zulu",
            @"C:\Program Files\BellSoft",
            @"C:\Program Files (x86)\Java",
        };

        foreach (var root in searchRoots)
        {
            if (!Directory.Exists(root)) continue;

            foreach (var exe in Directory.EnumerateFiles(root, "java.exe", SearchOption.AllDirectories))
            {
                if (exe.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                    yield return exe;
            }
        }
    }

    private static async Task<string> ReadJavaVersionAsync(string javaExe, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = javaExe,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return string.Empty;

        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        return string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
    }

    private static readonly Regex VersionPattern =
        new(@"(?:openjdk|java)\s+(?:version\s+)?""?(\d+)(?:\.(\d+))?", RegexOptions.IgnoreCase);

    private static int ParseMajorVersion(string text)
    {
        var m = VersionPattern.Match(text);
        if (!m.Success) return 0;

        if (!int.TryParse(m.Groups[1].Value, out var major)) return 0;

        if (major == 1 && m.Groups[2].Success &&
            int.TryParse(m.Groups[2].Value, out var minor))
            return minor;

        return major;
    }

    private static string GetAdoptiumArch()
        => RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "aarch64",
            _ => throw new PlatformNotSupportedException("暂不支持当前 CPU 架构的 Java 自动下载")
        };

    private async Task<string?> TryInstallBundledJavaAsync(
        int majorVersion,
        string installDir,
        CancellationToken ct)
    {
        var bundledZip = Path.Combine(BundledJavaRoot, $"temurin-{majorVersion}.zip");
        if (File.Exists(bundledZip))
            return await InstallBundledJavaZipAsync(bundledZip, majorVersion, installDir, ct);

        var bundledDir = Path.Combine(BundledJavaRoot, $"temurin-{majorVersion}");
        var bundledJavaExe = Path.Combine(bundledDir, "bin", "java.exe");
        if (!File.Exists(bundledJavaExe))
            return null;

        ReportStatus($"正在安装内置 Java {majorVersion}…");
        ReportProgress(10);

        return await InstallBundledJavaHomeAsync(bundledDir, majorVersion, installDir, ct);
    }

    private async Task<string> InstallBundledJavaZipAsync(
        string bundledZip,
        int majorVersion,
        string installDir,
        CancellationToken ct)
    {
        var extractRoot = Path.Combine(Path.GetTempPath(), $"ArclightLauncher-bundled-java-{Guid.NewGuid():N}");

        try
        {
            ReportStatus($"正在安装内置 Java {majorVersion}…");
            ReportProgress(10);

            Directory.CreateDirectory(extractRoot);
            ZipFile.ExtractToDirectory(bundledZip, extractRoot);

            var extractedJavaExe = Directory
                .EnumerateFiles(extractRoot, "java.exe", SearchOption.AllDirectories)
                .FirstOrDefault(path => path.EndsWith($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}java.exe",
                    StringComparison.OrdinalIgnoreCase));

            if (extractedJavaExe == null)
                throw new InvalidOperationException("内置 Java 压缩包中未找到 bin\\java.exe");

            var javaHome = Directory.GetParent(Path.GetDirectoryName(extractedJavaExe)!)?.FullName
                ?? throw new InvalidOperationException("无法解析内置 Java 目录");

            return await InstallBundledJavaHomeAsync(javaHome, majorVersion, installDir, ct);
        }
        finally
        {
            TryDeleteDirectory(extractRoot);
        }
    }

    private async Task<string> InstallBundledJavaHomeAsync(
        string javaHome,
        int majorVersion,
        string installDir,
        CancellationToken ct)
    {
        var stagingDir = Path.Combine(ManagedJavaRoot, $".temurin-{majorVersion}-{Guid.NewGuid():N}");

        try
        {
            await CopyDirectoryAsync(javaHome, stagingDir, ct);

            var stagedJavaExe = Path.Combine(stagingDir, "bin", "java.exe");
            var installedVersion = await GetMajorVersionAsync(stagedJavaExe);
            if (installedVersion < majorVersion)
            {
                throw new InvalidOperationException(
                    $"内置 Java 安装校验失败：需要 {majorVersion}+，实际 {installedVersion}");
            }

            DeleteManagedInstallDir(installDir);
            Directory.Move(stagingDir, installDir);

            var installedJavaExe = Path.Combine(installDir, "bin", "java.exe");
            ReportStatus($"Java {installedVersion} 已安装");
            ReportProgress(100);
            _logger.LogInformation("内置 Java {Version} 已安装：{Path}", installedVersion, installedJavaExe);
            return installedJavaExe;
        }
        finally
        {
            TryDeleteDirectory(stagingDir);
        }
    }

    private static async Task CopyDirectoryAsync(
        string sourceDir,
        string targetDir,
        CancellationToken ct)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var dir in Directory.EnumerateDirectories(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourceDir, dir);
            Directory.CreateDirectory(Path.Combine(targetDir, relative));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            var relative = Path.GetRelativePath(sourceDir, file);
            var destination = Path.Combine(targetDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            await using var source = new FileStream(
                file, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);
            await using var target = new FileStream(
                destination, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true);

            await source.CopyToAsync(target, ct);
        }
    }

    private static void DeleteManagedInstallDir(string installDir)
    {
        var fullRoot = Path.GetFullPath(ManagedJavaRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullInstallDir = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        if (!fullInstallDir.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"拒绝删除托管 Java 目录之外的路径：{installDir}");

        if (Directory.Exists(installDir))
            Directory.Delete(installDir, recursive: true);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 临时文件清理失败不影响启动器使用。
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响启动器使用。
        }
    }

    private void ReportStatus(string message)
    {
        StatusChanged?.Invoke(this, message);
        _logger.LogInformation("[Java] {Message}", message);
    }

    private void ReportProgress(double progress)
        => ProgressChanged?.Invoke(this, Math.Clamp(progress, 0, 100));
}
