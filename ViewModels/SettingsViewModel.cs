using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using ArclightLauncher.Models;
using ArclightLauncher.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace ArclightLauncher.ViewModels;

/// <summary>
/// 设置页 ViewModel
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService  _settingsService;
    private readonly JavaService      _javaService;
    private readonly ManifestService  _manifestService;
    private readonly LaunchService    _launchService;
    private readonly UpdateService    _updateService;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<SettingsViewModel> _logger;
    private bool _suppressVersionChangedRefresh;

    private const string MojangManifestUrl =
        "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    // ── 路径 ──────────────────────────────────────────────────────────────
    [ObservableProperty] private string _gameDir;
    [ObservableProperty] private string _javaExe;
    [ObservableProperty] private bool _autoDownloadJava;
    [ObservableProperty] private bool _isJavaBusy;
    [ObservableProperty] private int _selectedJavaVersion = 21;
    [ObservableProperty] private string _managedJavaSummary = string.Empty;
    [ObservableProperty] private bool _isVersionBusy;
    [ObservableProperty] private bool _includeSnapshots = true;
    [ObservableProperty] private MinecraftVersionOption? _selectedMinecraftVersion;
    [ObservableProperty] private string _selectedLoaderType = "Fabric";
    [ObservableProperty] private string _selectedFabricLoaderVersion = "latest";
    [ObservableProperty] private string _versionInstallSummary = "尚未刷新游戏版本列表";

    public IReadOnlyList<int> JavaVersionOptions { get; } = [8, 17, 21];
    public IReadOnlyList<string> LoaderTypeOptions { get; } = ["Fabric", "Quilt"];
    public ObservableCollection<MinecraftVersionOption> MinecraftVersionOptions { get; } = [];
    public ObservableCollection<string> FabricLoaderVersionOptions { get; } = ["latest"];

    // ── 性能 ──────────────────────────────────────────────────────────────
    [ObservableProperty] private int _maxMemory;
    [ObservableProperty] private string _jvmArgs;
    [ObservableProperty] private bool _isAdvancedJvmVisible;

    /// <summary>滑块 Maximum：物理内存 75%，至少 4096</summary>
    public int MaxMemorySliderMax { get; } =
        Math.Max(4096, LauncherSettings.PhysicalMemory75Pct());

    // ── 启动后行为（三个 bool 互斥） ─────────────────────────────────────
    public bool PostLaunchKeep
    {
        get => _settingsService.Current.PostLaunchBehavior == "Keep";
        set { if (value) SetPostLaunch("Keep"); }
    }
    public bool PostLaunchMinimize
    {
        get => _settingsService.Current.PostLaunchBehavior == "Minimize";
        set { if (value) SetPostLaunch("Minimize"); }
    }
    public bool PostLaunchClose
    {
        get => _settingsService.Current.PostLaunchBehavior == "Close";
        set { if (value) SetPostLaunch("Close"); }
    }

    private void SetPostLaunch(string val)
    {
        _settingsService.Current.PostLaunchBehavior = val;
        OnPropertyChanged(nameof(PostLaunchKeep));
        OnPropertyChanged(nameof(PostLaunchMinimize));
        OnPropertyChanged(nameof(PostLaunchClose));
        _ = _settingsService.SaveAsync();
    }

    // ── 主题（三个 bool 互斥） ────────────────────────────────────────────
    public bool ThemeLight
    {
        get => _settingsService.Current.Theme == "Light";
        set { if (value) SetTheme("Light"); }
    }
    public bool ThemeDark
    {
        get => _settingsService.Current.Theme == "Dark";
        set { if (value) SetTheme("Dark"); }
    }
    public bool ThemeSystem
    {
        get => _settingsService.Current.Theme == "System";
        set { if (value) SetTheme("System"); }
    }

    private void SetTheme(string theme)
    {
        _settingsService.Current.Theme = theme;
        OnPropertyChanged(nameof(ThemeLight));
        OnPropertyChanged(nameof(ThemeDark));
        OnPropertyChanged(nameof(ThemeSystem));
        ApplyTheme(theme);
        _ = _settingsService.SaveAsync();
    }

    // ── 更新 ──────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isCheckingForUpdates;
    [ObservableProperty] private double _updateCheckProgress;
    [ObservableProperty] private string _backgroundImagePath = string.Empty;
    [ObservableProperty] private string _updateStatusText = "启动器版本";
    [ObservableProperty] private string _updateSubText = "点击检查是否有新版本";

    // ── 反馈文字 ──────────────────────────────────────────────────────────
    [ObservableProperty] private string _statusMessage = string.Empty;

    // ── 构造 ──────────────────────────────────────────────────────────────

    public SettingsViewModel(
        SettingsService settingsService,
        JavaService javaService,
        ManifestService manifestService,
        LaunchService launchService,
        UpdateService updateService,
        IHttpClientFactory httpFactory,
        ILogger<SettingsViewModel> logger)
    {
        _settingsService = settingsService;
        _javaService     = javaService;
        _manifestService = manifestService;
        _launchService   = launchService;
        _updateService   = updateService;
        _httpFactory     = httpFactory;
        _logger          = logger;

        var currentVer = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "未知";
        UpdateStatusText = $"当前版本 v{currentVer}";
        UpdateSubText = "点击检查是否有新版本";

        var s    = settingsService.Current;
        _gameDir = s.GameDir;
        _javaExe = s.JavaExe;
        _autoDownloadJava = s.AutoDownloadJava;
        _maxMemory = s.MaxMemory;
        _jvmArgs = s.JvmArgs;
        _backgroundImagePath = s.BackgroundImagePath;

        _javaService.StatusChanged += (_, msg) =>
            Application.Current.Dispatcher.Invoke(() => StatusMessage = msg);
        _javaService.ProgressChanged += (_, pct) =>
            Application.Current.Dispatcher.Invoke(() => StatusMessage = $"Java 处理进度：{pct:0}%");

        _ = RefreshManagedJavaSummaryAsync();
        _ = RefreshGameVersionsAsync();
    }

    // ─────────────────────────────────────────────────────────────────────
    // 属性变更 → 自动保存
    // ─────────────────────────────────────────────────────────────────────

    partial void OnGameDirChanged(string value)
    {
        _settingsService.Current.GameDir = value;
        _ = _settingsService.SaveAsync();
    }

    partial void OnJavaExeChanged(string value)
    {
        _settingsService.Current.JavaExe = value;
        _ = _settingsService.SaveAsync();
    }

    partial void OnAutoDownloadJavaChanged(bool value)
    {
        _settingsService.Current.AutoDownloadJava = value;
        _ = _settingsService.SaveAsync();
    }

    partial void OnIncludeSnapshotsChanged(bool value)
    {
        _ = RefreshGameVersionsAsync();
    }

    partial void OnSelectedMinecraftVersionChanged(MinecraftVersionOption? value)
    {
        if (!_suppressVersionChangedRefresh && value is not null)
            _ = RefreshFabricLoadersAsync();
    }

    partial void OnMaxMemoryChanged(int value)
    {
        _settingsService.Current.MaxMemory = value;
        _ = _settingsService.SaveAsync();
    }

    partial void OnJvmArgsChanged(string value)
    {
        _settingsService.Current.JvmArgs = value;
        _ = _settingsService.SaveAsync();
    }

    // ─────────────────────────────────────────────────────────────────────
    // 命令
    // ─────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void BrowseGameDir()
    {
        var dlg = new OpenFolderDialog
        {
            Title = "选择游戏目录（.minecraft 所在文件夹）",
            InitialDirectory = Directory.Exists(GameDir) ? GameDir : null
        };
        if (dlg.ShowDialog() == true)
            GameDir = dlg.FolderName;
    }

    [RelayCommand]
    private void OpenGameDir()
    {
        var dir = Directory.Exists(GameDir) ? GameDir : LauncherSettings.DefaultGameDir;
        if (Directory.Exists(dir))
            Process.Start("explorer.exe", dir);
    }

    [RelayCommand]
    private async Task DetectJavaAsync()
    {
        var minVersion = 17;
        StatusMessage = "正在读取整合包所需的 Java 版本…";

        try
        {
            var manifestInfo = _manifestService.CachedManifest ?? await _manifestService.FetchAsync();
            if (manifestInfo.JavaVersion > 0)
                minVersion = manifestInfo.JavaVersion;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve manifest Java version, fallback to Java {Version}", minVersion);
        }

        StatusMessage = $"正在检测 Java {minVersion}+…";

        if (AutoDownloadJava)
        {
            IsJavaBusy = true;
            try
            {
                var resolvedJava = await _javaService.ResolveJavaAsync(minVersion, JavaExe, true);
                if (resolvedJava != null)
                {
                    JavaExe = resolvedJava;
                    var resolvedVersion = await _javaService.GetMajorVersionAsync(resolvedJava);
                    StatusMessage = $"Java {resolvedVersion} 已就绪";
                    await RefreshManagedJavaSummaryAsync();
                    return;
                }
            }
            finally
            {
                IsJavaBusy = false;
            }
        }

        var detectedJava = await _javaService.FindJavaAsync(minVersion);
        if (detectedJava != null)
        {
            JavaExe = detectedJava;
            var detectedVersion = await _javaService.GetMajorVersionAsync(detectedJava);
            StatusMessage = detectedVersion > 0
                ? $"检测成功：Java {detectedVersion}"
                : "检测成功";
        }
        else
        {
            StatusMessage = $"未找到可用的 Java {minVersion}+，请手动指定路径或安装：{JavaService.JavaDownloadUrl}";
        }

    }

    [RelayCommand]
    private void BrowseJava()
    {
        var dlg = new OpenFileDialog
        {
            Title  = "选择 Java 可执行文件",
            Filter = "java.exe|java.exe|所有文件|*.*"
        };
        if (dlg.ShowDialog() == true)
            JavaExe = dlg.FileName;
    }

    [RelayCommand]
    private async Task InstallManagedJavaAsync()
    {
        var minVersion = 17;

        try
        {
            var manifestInfo = _manifestService.CachedManifest ?? await _manifestService.FetchAsync();
            if (manifestInfo.JavaVersion > 0)
                minVersion = manifestInfo.JavaVersion;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve manifest Java version, fallback to Java {Version}", minVersion);
        }

        IsJavaBusy = true;
        try
        {
            JavaExe = await _javaService.InstallManagedJavaAsync(minVersion);
            AutoDownloadJava = true;
            await RefreshManagedJavaSummaryAsync();
        }
        finally
        {
            IsJavaBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallSelectedJavaAsync()
    {
        IsJavaBusy = true;
        try
        {
            StatusMessage = $"正在准备 Java {SelectedJavaVersion}…";
            JavaExe = await _javaService.InstallManagedJavaAsync(SelectedJavaVersion);
            AutoDownloadJava = true;

            var installedVersion = await _javaService.GetMajorVersionAsync(JavaExe);
            StatusMessage = installedVersion > 0
                ? $"已切换到 Java {installedVersion}"
                : $"已切换到 Java {SelectedJavaVersion}";

            await RefreshManagedJavaSummaryAsync();
        }
        finally
        {
            IsJavaBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshGameVersionsAsync()
    {
        IsVersionBusy = true;
        try
        {
            VersionInstallSummary = "正在刷新游戏版本列表……";

            var client = _httpFactory.CreateClient();
            var json = await client.GetStringAsync(MojangManifestUrl);
            using var doc = JsonDocument.Parse(json);

            var versions = doc.RootElement.GetProperty("versions")
                .EnumerateArray()
                .Select(item => new MinecraftVersionOption(
                    item.GetProperty("id").GetString() ?? string.Empty,
                    item.GetProperty("type").GetString() ?? string.Empty))
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .Where(item => IncludeSnapshots ||
                    !string.Equals(item.Type, "snapshot", StringComparison.OrdinalIgnoreCase))
                .ToList();

            MinecraftVersionOptions.Clear();
            foreach (var version in versions)
                MinecraftVersionOptions.Add(version);

            _suppressVersionChangedRefresh = true;
            try
            {
                SelectedMinecraftVersion =
                    versions.FirstOrDefault(v => string.Equals(v.Id, "1.21.11", StringComparison.OrdinalIgnoreCase)) ??
                    versions.FirstOrDefault(v => string.Equals(v.Id, "1.21.10", StringComparison.OrdinalIgnoreCase)) ??
                    versions.FirstOrDefault(v => string.Equals(v.Type, "release", StringComparison.OrdinalIgnoreCase)) ??
                    versions.FirstOrDefault();
            }
            finally
            {
                _suppressVersionChangedRefresh = false;
            }

            VersionInstallSummary = $"已加载 {MinecraftVersionOptions.Count} 个游戏版本";
            if (SelectedMinecraftVersion is not null)
                await RefreshFabricLoadersForAsync(SelectedMinecraftVersion.Id);
        }
        catch (Exception ex)
        {
            VersionInstallSummary = $"版本列表刷新失败：{ex.Message}";
            _logger.LogWarning(ex, "刷新 Minecraft 版本列表失败");
        }
        finally
        {
            IsVersionBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshFabricLoadersAsync()
    {
        if (SelectedMinecraftVersion is null)
        {
            VersionInstallSummary = "请先选择游戏版本";
            return;
        }

        IsVersionBusy = true;
        try
        {
            await RefreshFabricLoadersForAsync(SelectedMinecraftVersion.Id);
        }
        finally
        {
            IsVersionBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallSelectedMinecraftVersionAsync()
    {
        if (SelectedMinecraftVersion is null)
        {
            VersionInstallSummary = "请先刷新并选择游戏版本";
            return;
        }

        IsVersionBusy = true;
        try
        {
            var mcVer = SelectedMinecraftVersion.Id;
            var loaderType = SelectedLoaderType.Equals("Fabric", StringComparison.OrdinalIgnoreCase)
                ? "fabric"
                : SelectedLoaderType.Trim().ToLowerInvariant();
            var loaderVer = string.IsNullOrWhiteSpace(SelectedFabricLoaderVersion)
                ? "latest"
                : SelectedFabricLoaderVersion.Trim();
            var requiredJava = GuessRequiredJavaMajor(mcVer);

            VersionInstallSummary = $"正在准备 Java {requiredJava}+……";
            StatusMessage = VersionInstallSummary;

            var resolvedJava = await _javaService.ResolveJavaAsync(requiredJava, JavaExe, true);
            if (resolvedJava is null)
            {
                VersionInstallSummary = $"未找到 Java {requiredJava}+，自动下载也未完成";
                StatusMessage = VersionInstallSummary;
                return;
            }

            JavaExe = resolvedJava;
            AutoDownloadJava = true;

            VersionInstallSummary = $"正在安装 {mcVer} / Fabric {loaderVer}……";
            StatusMessage = VersionInstallSummary;

            var installedVersionId = await _launchService.InstallVersionAsync(
                resolvedJava,
                mcVer,
                loaderType,
                loaderVer,
                GameDir);

            await RefreshManagedJavaSummaryAsync();

            VersionInstallSummary = $"已安装 {installedVersionId}";
            StatusMessage = VersionInstallSummary;
        }
        catch (Exception ex)
        {
            VersionInstallSummary = $"版本安装失败：{ex.Message}";
            StatusMessage = VersionInstallSummary;
            _logger.LogError(ex, "安装游戏 / Fabric 版本失败");
        }
        finally
        {
            IsVersionBusy = false;
        }
    }

    private async Task RefreshFabricLoadersForAsync(string mcVer)
    {
        VersionInstallSummary = $"正在获取 {mcVer} 可用的 Fabric Loader……";

        FabricLoaderVersionOptions.Clear();
        FabricLoaderVersionOptions.Add("latest");
        SelectedFabricLoaderVersion = "latest";

        var client = _httpFactory.CreateClient();
        var url = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mcVer)}";

        try
        {
            var json = await client.GetStringAsync(url);
            using var doc = JsonDocument.Parse(json);

            var loaderVersions = doc.RootElement.EnumerateArray()
                .Select(item =>
                    item.TryGetProperty("loader", out var loader) &&
                    loader.TryGetProperty("version", out var version)
                        ? version.GetString()
                        : null)
                .Where(version => !string.IsNullOrWhiteSpace(version))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var loaderVersion in loaderVersions)
                FabricLoaderVersionOptions.Add(loaderVersion!);

            VersionInstallSummary = $"Fabric 支持 {mcVer}，可选 Loader {loaderVersions.Count} 个";
        }
        catch (Exception ex)
        {
            VersionInstallSummary = $"Fabric 暂未支持 {mcVer}，或网络不可用：{ex.Message}";
            _logger.LogWarning(ex, "刷新 Fabric Loader 列表失败：{McVer}", mcVer);
        }
    }

    [RelayCommand]
    private void OpenManagedJavaDir()
    {
        Directory.CreateDirectory(JavaService.ManagedJavaRoot);
        Process.Start("explorer.exe", JavaService.ManagedJavaRoot);
    }

    [RelayCommand]
    private void ToggleAdvancedJvm()
        => IsAdvancedJvmVisible = !IsAdvancedJvmVisible;

    [RelayCommand]
    private void OpenModManager()
    {
        var removable = _manifestService.CachedManifest?.Mods
            .Where(m => m.UserRemovable)
            .ToList() ?? [];

        if (removable.Count == 0)
        {
            StatusMessage = "manifest 尚未加载，或没有可选 mod。请先点击启动以拉取 manifest。";
            return;
        }

        var vm     = new Dialogs.ModManagerViewModel(removable, _settingsService);
        var dialog = new Views.Dialogs.ModManagerDialog(vm)
        {
            Owner = Application.Current.MainWindow
        };
        dialog.ShowDialog();
    }

    [RelayCommand]
    private async Task ForceRevalidateAsync()
    {
        _settingsService.Current.ForceRevalidate = true;
        await _settingsService.SaveAsync();
        StatusMessage = "✓ 已设置：下次启动将强制重新校验所有文件";
    }

    [RelayCommand]
    private void OpenLogDir()
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ArclightLauncher", "logs");
        Directory.CreateDirectory(logDir);
        Process.Start("explorer.exe", logDir);
    }

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingForUpdates = true;
        UpdateCheckProgress = 0;
        UpdateSubText = "正在连接更新服务器...";
        StatusMessage = string.Empty;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var info = await _updateService.CheckForUpdateAsync(cts.Token);

            var currentVer = Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);

            if (info is null)
            {
                UpdateStatusText = $"已是最新版本 v{currentVer}";
                UpdateSubText = "启动器已是最新，无需更新";
                UpdateCheckProgress = 100;
            }
            else
            {
                UpdateStatusText = $"发现新版本 v{info.NewVersion.ToString(3)}";
                UpdateSubText = info.Size > 0
                    ? $"安装包 {FormatUpdateSize(info.Size)}，点击\"检查更新\"打开下载对话框"
                    : "点击\"检查更新\"打开下载对话框";
                UpdateCheckProgress = 100;

                var vm     = new Dialogs.UpdateViewModel(info, _updateService);
                var dialog = new Views.Dialogs.UpdateDialog(vm)
                {
                    Owner = Application.Current.MainWindow
                };
                dialog.ShowDialog();
            }
        }
        catch (Exception ex)
        {
            UpdateStatusText = "更新检查失败";
            UpdateSubText = ex.Message;
            StatusMessage = ex.Message;
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    private bool CanCheckForUpdates() => !IsCheckingForUpdates;

    [RelayCommand]
    private void BrowseBackground()
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择背景图片",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.webp|所有文件|*.*",
            InitialDirectory = !string.IsNullOrWhiteSpace(BackgroundImagePath) &&
                               Directory.Exists(Path.GetDirectoryName(BackgroundImagePath))
                ? Path.GetDirectoryName(BackgroundImagePath)
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };

        if (dlg.ShowDialog() == true)
        {
            BackgroundImagePath = dlg.FileName;
            _settingsService.Current.BackgroundImagePath = dlg.FileName;
            _ = _settingsService.SaveAsync();
            StatusMessage = "背景图已保存，重启或切换页面后生效";
        }
    }

    [RelayCommand]
    private void ClearBackground()
    {
        BackgroundImagePath = string.Empty;
        _settingsService.Current.BackgroundImagePath = string.Empty;
        _ = _settingsService.SaveAsync();
        StatusMessage = "已恢复默认背景，重启或切换页面后生效";
    }

    [RelayCommand]
    private void OpenUpdateDir()
    {
        var appDir = AppContext.BaseDirectory;
        Process.Start("explorer.exe", appDir);
    }

    private static string FormatUpdateSize(long bytes)
    {
        if (bytes <= 0) return string.Empty;
        return bytes switch
        {
            >= 1_048_576 => $"{bytes / 1_048_576.0:F1} MB",
            >= 1_024     => $"{bytes / 1_024.0:F0} KB",
            _            => $"{bytes} B"
        };
    }

    // ─────────────────────────────────────────────────────────────────────
    // 主题切换
    // ─────────────────────────────────────────────────────────────────────

    private async Task RefreshManagedJavaSummaryAsync()
    {
        var runtimes = await _javaService.GetManagedRuntimesAsync();
        ManagedJavaSummary = runtimes.Count == 0
            ? "尚未安装启动器托管 Java"
            : "托管 Java：" + string.Join("，", runtimes.Select(r => $"Java {r.MajorVersion}"));
    }

    private static int GuessRequiredJavaMajor(string minecraftVersion)
    {
        var parts = minecraftVersion.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length >= 2 &&
            int.TryParse(parts[0], out var major) &&
            int.TryParse(parts[1], out var minor))
        {
            var patch = parts.Length >= 3 && int.TryParse(parts[2], out var parsedPatch)
                ? parsedPatch
                : 0;

            if (major > 1 || minor > 20 || minor == 20 && patch >= 5)
                return 21;
            if (minor >= 18)
                return 17;
            if (minor == 17)
                return 16;

            return 8;
        }

        if (minecraftVersion.Length >= 2 &&
            int.TryParse(minecraftVersion[..2], out var snapshotYear))
        {
            if (snapshotYear >= 24)
                return 21;
            if (snapshotYear >= 21)
                return 17;
        }

        return 21;
    }

    public static void ApplyTheme(string theme)
    {
        string? effectiveTheme = theme;

        if (theme == "System")
        {
            effectiveTheme = IsSystemDarkMode() ? "Dark" : "Light";
        }

        var skinUri = effectiveTheme == "Dark"
            ? new Uri("pack://application:,,,/HandyControl;component/Themes/SkinDark.xaml")
            : new Uri("pack://application:,,,/HandyControl;component/Themes/SkinDefault.xaml");

        var dicts = Application.Current.Resources.MergedDictionaries;
        var skin  = dicts.FirstOrDefault(d =>
            d.Source?.OriginalString.Contains("Skin", StringComparison.OrdinalIgnoreCase) == true);

        if (skin != null)
            skin.Source = skinUri;
    }

    private static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return (int)(key?.GetValue("AppsUseLightTheme") ?? 1) == 0;
        }
        catch { return false; }
    }
}

public sealed class MinecraftVersionOption
{
    public MinecraftVersionOption(string id, string type)
    {
        Id = id;
        Type = type;
    }

    public string Id { get; }
    public string Type { get; }

    public string DisplayName => string.Equals(Type, "snapshot", StringComparison.OrdinalIgnoreCase)
        ? $"{Id}（快照）"
        : $"{Id}（正式）";
}
