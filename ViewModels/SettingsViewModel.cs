using System.Diagnostics;
using System.IO;
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
    private readonly ILogger<SettingsViewModel> _logger;

    // ── 路径 ──────────────────────────────────────────────────────────────
    [ObservableProperty] private string _gameDir;
    [ObservableProperty] private string _javaExe;

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

    // ── 反馈文字 ──────────────────────────────────────────────────────────
    [ObservableProperty] private string _statusMessage = string.Empty;

    // ── 构造 ──────────────────────────────────────────────────────────────

    public SettingsViewModel(
        SettingsService settingsService,
        JavaService javaService,
        ManifestService manifestService,
        ILogger<SettingsViewModel> logger)
    {
        _settingsService = settingsService;
        _javaService     = javaService;
        _manifestService = manifestService;
        _logger          = logger;

        var s    = settingsService.Current;
        _gameDir = s.GameDir;
        _javaExe = s.JavaExe;
        _maxMemory = s.MaxMemory;
        _jvmArgs = s.JvmArgs;
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
        StatusMessage = string.Empty;
        var prev = JavaExe;
        JavaExe = "正在检测……";

        var result = await _javaService.FindJava17Async();
        if (result != null)
        {
            JavaExe = result;
            StatusMessage = "检测成功";
        }
        else
        {
            JavaExe = prev;
            StatusMessage = $"未找到合适的 Java，请手动指定路径或安装：{JavaService.JavaDownloadUrl}";
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

    // ─────────────────────────────────────────────────────────────────────
    // 主题切换
    // ─────────────────────────────────────────────────────────────────────

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
