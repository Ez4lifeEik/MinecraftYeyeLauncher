using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ArclightLauncher.Services;

/// <summary>
/// Java 检测服务（v0.1：检测系统已安装的 Java，不自动下载）
/// </summary>
public class JavaService
{
    private readonly ILogger<JavaService> _logger;

    public const string JavaDownloadUrl = "https://adoptium.net/temurin/releases/";

    public JavaService(ILogger<JavaService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 在系统中寻找版本 ≥ <paramref name="minVersion"/> 的 Java 可执行文件。
    /// </summary>
    public async Task<string?> FindJavaAsync(int minVersion = 17)
    {
        var candidates = CollectCandidates();

        foreach (var candidate in candidates)
        {
            var version = await GetMajorVersionAsync(candidate);
            if (version >= minVersion)
            {
                _logger.LogInformation("找到满足要求的 Java {Version}：{Path}", version, candidate);
                return candidate;
            }
            if (version > 0)
                _logger.LogDebug("跳过 Java {Version}（需要 >={Min}）：{Path}", version, minVersion, candidate);
        }

        _logger.LogWarning("未找到 Java {Min}+，请手动安装：{Url}", minVersion, JavaDownloadUrl);
        return null;
    }

    /// <summary>向后兼容旧调用</summary>
    public Task<string?> FindJava17Async() => FindJavaAsync(17);

    // ── 候选路径收集 ─────────────────────────────────────────────────────

    private List<string> CollectCandidates()
    {
        var list = new List<string>();

        // 1. JAVA_HOME 环境变量
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            var exe = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(exe)) list.Add(exe);
        }

        // 2. PATH 中第一个 java.exe（通过直接调用名称探测）
        var fromPath = FindInPath("java.exe");
        if (fromPath != null) list.Add(fromPath);

        // 3. 注册表（Oracle / Eclipse Adoptium / Microsoft OpenJDK / Azul）
        list.AddRange(FindFromRegistry());

        // 4. 常见安装目录兜底扫描
        list.AddRange(ScanCommonDirs());

        // 去重（大小写不敏感）
        return list
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? FindInPath(string exeName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var full = Path.Combine(dir.Trim(), exeName);
            if (File.Exists(full)) return full;
        }
        return null;
    }

    private IEnumerable<string> FindFromRegistry()
    {
        // 注册表根键列表（32/64 位都扫）
        var roots = new[]
        {
            @"SOFTWARE\JavaSoft\JDK",                          // Oracle JDK 9+
            @"SOFTWARE\JavaSoft\Java Development Kit",         // Oracle JDK 8
            @"SOFTWARE\Eclipse Adoptium\JDK",                  // Adoptium / Temurin
            @"SOFTWARE\Eclipse Foundation\JDK",
            @"SOFTWARE\Microsoft\JDK",                         // Microsoft OpenJDK
            @"SOFTWARE\Azul Systems\Zulu",                     // Azul Zulu
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
                        _logger.LogDebug("注册表发现 Java：{Path}", exe);
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
            @"C:\Program Files\BellSoft",       // Liberica
            @"C:\Program Files (x86)\Java",
        };

        foreach (var root in searchRoots)
        {
            if (!Directory.Exists(root)) continue;

            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var exe = Path.Combine(dir, "bin", "java.exe");
                if (File.Exists(exe)) yield return exe;
            }
        }
    }

    // ── 版本解析 ─────────────────────────────────────────────────────────

    /// <summary>
    /// 调用 java.exe --version，解析主版本号。失败返回 0。
    /// </summary>
    private async Task<int> GetMajorVersionAsync(string javaExe)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = javaExe,
                Arguments = "--version",          // Java 9+ 输出到 stdout
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return 0;

            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            // 优先 stdout，Java 8 及以下只写 stderr
            var text = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            return ParseMajorVersion(text);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("无法执行 {JavaExe}：{Msg}", javaExe, ex.Message);
            return 0;
        }
    }

    private static readonly Regex VersionPattern =
        new(@"(?:openjdk|java)\s+(\d+)(?:\.(\d+))?", RegexOptions.IgnoreCase);

    private static int ParseMajorVersion(string text)
    {
        // 新格式：openjdk 17.0.2 / openjdk 21
        // 旧格式：java version "1.8.0_301"
        var m = VersionPattern.Match(text);
        if (!m.Success) return 0;

        if (!int.TryParse(m.Groups[1].Value, out var major)) return 0;

        // 旧格式 1.x → 实际版本是 x
        if (major == 1 && m.Groups[2].Success &&
            int.TryParse(m.Groups[2].Value, out var minor))
            return minor;

        return major;
    }
}
