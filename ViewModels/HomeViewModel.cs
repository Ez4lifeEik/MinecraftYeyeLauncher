using System.IO;
using System.Net.Http;
using System.Windows;
using ArclightLauncher.Models;
using ArclightLauncher.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ArclightLauncher.ViewModels;

/// <summary>
/// 启动页 ViewModel：账户、分割启动按钮、三模式切换、进度
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    private readonly ManifestService _manifestService;
    private readonly JavaService     _javaService;
    private readonly LaunchService   _launchService;
    private readonly SettingsService _settingsService;
    private readonly AccountService  _accountService;
    private readonly ILogger<HomeViewModel> _logger;

    // manifest 缓存（用于组装副标题）
    private string _mcVersionStr  = string.Empty;
    private string _loaderStr     = string.Empty;

    // ── 账户 ──────────────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsernameInitial))]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    private string _username = string.Empty;

    public string UsernameInitial
        => string.IsNullOrEmpty(Username) ? "?" : Username[0].ToString().ToUpperInvariant();

    // ── 启动模式 ──────────────────────────────────────────────────────────
    [ObservableProperty]
    private LaunchMode _selectedMode = LaunchMode.OfficialServer;

    // 供 Popup 按钮高亮使用
    public bool IsOfficialServerMode => SelectedMode == LaunchMode.OfficialServer;
    public bool IsSingleplayerMode   => SelectedMode == LaunchMode.Singleplayer;
    public bool IsCustomServerMode   => SelectedMode == LaunchMode.CustomServer;

    partial void OnSelectedModeChanged(LaunchMode value)
    {
        OnPropertyChanged(nameof(IsOfficialServerMode));
        OnPropertyChanged(nameof(IsSingleplayerMode));
        OnPropertyChanged(nameof(IsCustomServerMode));
        UpdateSubtitle();
        _settingsService.Current.LastLaunchMode = value;
        _ = _settingsService.SaveAsync();
    }

    // ── 상태 ──────────────────────────────────────────────────────────────
    [ObservableProperty] private string _statusText  = string.Empty;
    [ObservableProperty] private double _progress;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    private bool _isLaunching;

    // ── 버튼 부제목 ───────────────────────────────────────────────────────
    [ObservableProperty] private string _launchButtonSubtitle = string.Empty;

    // ── 우측 패널 ─────────────────────────────────────────────────────────
    [ObservableProperty] private string _serverAddress = "—";
    [ObservableProperty] private string _packVersion   = "—";
    [ObservableProperty] private string _announcement  = string.Empty;

    // ── 构造 ──────────────────────────────────────────────────────────────
    public HomeViewModel(
        ManifestService manifestService,
        JavaService     javaService,
        LaunchService   launchService,
        SettingsService settingsService,
        AccountService  accountService,
        ILogger<HomeViewModel> logger)
    {
        _manifestService = manifestService;
        _javaService     = javaService;
        _launchService   = launchService;
        _settingsService = settingsService;
        _accountService  = accountService;
        _logger          = logger;

        // 从设置恢复
        _username     = settingsService.Current.Username;
        _selectedMode = settingsService.Current.LastLaunchMode;

        UpdateSubtitle();

        _launchService.StatusChanged   += (_, msg) =>
            Application.Current.Dispatcher.Invoke(() => StatusText = msg);
        _launchService.ProgressChanged += (_, pct) =>
            Application.Current.Dispatcher.Invoke(() => Progress = pct);

        _ = LoadManifestInfoAsync();
        _ = LoadAnnouncementAsync();
    }

    // ─────────────────────────────────────────────────────────────────────
    // 模式选择命令（供 Popup 调用）
    // ─────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private void SelectOfficialServer() => SelectedMode = LaunchMode.OfficialServer;

    [RelayCommand]
    private void SelectSingleplayer() => SelectedMode = LaunchMode.Singleplayer;

    /// <summary>
    /// 打开"连接其他服务器"对话框；确认后切换到 CustomServer 模式
    /// </summary>
    [RelayCommand]
    private void SelectCustomServer()
    {
        var vm     = new Dialogs.CustomServerViewModel(_settingsService);
        var dialog = new Views.Dialogs.CustomServerDialog(vm)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true)
        {
            SelectedMode = LaunchMode.CustomServer; // 触发 OnSelectedModeChanged → UpdateSubtitle
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // 启动命令
    // ─────────────────────────────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchAsync()
    {
        if (string.IsNullOrWhiteSpace(Username))
        {
            StatusText = "请先输入用户名";
            return;
        }

        IsLaunching = true;
        Progress    = 0;
        StatusText  = string.Empty;

        try
        {
            _settingsService.Current.Username = Username;
            await _settingsService.SaveAsync();

            StatusText = "正在拉取整合包信息……";
            Progress   = 10;
            var manifest = await _manifestService.FetchAsync();
            Progress = 20;

            _mcVersionStr = manifest.MinecraftVersion;
            _loaderStr    = $"{manifest.Loader.Type} {manifest.Loader.Version}";
            Application.Current.Dispatcher.Invoke(() =>
            {
                ServerAddress = manifest.Server.Address;
                PackVersion   = manifest.PackVersion;
                UpdateSubtitle();
            });

            StatusText = $"正在检测 Java {manifest.JavaVersion}+……";
            Progress   = 25;

            var settings = _settingsService.Current;
            string? javaExe = (!string.IsNullOrEmpty(settings.JavaExe) && File.Exists(settings.JavaExe))
                ? settings.JavaExe
                : await _javaService.FindJavaAsync(manifest.JavaVersion);

            if (javaExe is null)
            {
                StatusText = $"未找到 Java {manifest.JavaVersion}+！请在设置页手动指定。";
                Progress   = 0;
                return;
            }

            Progress = 30;

            var disabledMods = SelectedMode == LaunchMode.OfficialServer
                ? null
                : (IReadOnlySet<string>?)new HashSet<string>(
                    settings.DisabledMods, StringComparer.OrdinalIgnoreCase);

            string? customIp = null;
            int customPort   = 25565;
            if (SelectedMode == LaunchMode.CustomServer)
            {
                customIp   = settings.CustomServerAddress.Trim();
                customPort = settings.CustomServerPort;
            }

            var account = _accountService.CreateOfflineAccount(Username);

            bool forceRevalidate = settings.ForceRevalidate;
            if (forceRevalidate)
            {
                settings.ForceRevalidate = false;
                await _settingsService.SaveAsync();
            }

            await _launchService.RunAsync(
                account:             account,
                javaExe:             javaExe,
                manifest:            manifest,
                mode:                SelectedMode,
                gameDir:             settings.GameDir,
                maxMemoryMb:         settings.MaxMemory,
                jvmArgs:             settings.JvmArgs,
                customServerAddress: customIp,
                customServerPort:    customPort,
                disabledMods:        disabledMods,
                forceRevalidate:     forceRevalidate);

            HandlePostLaunch(settings.PostLaunchBehavior);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("ManifestUrl"))
        {
            StatusText = "请先在 appsettings.json 中填写真实的 ManifestUrl";
            Progress   = 0;
        }
        catch (HttpRequestException ex)
        {
            StatusText = $"网络错误：{ex.Message}";
            Progress   = 0;
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
            Progress   = 0;
        }
        catch (Exception ex)
        {
            StatusText = $"错误：{ex.Message}";
            Progress   = 0;
            _logger.LogError(ex, "LaunchAsync 异常");
        }
        finally
        {
            IsLaunching = false;
        }
    }

    private bool CanLaunch() => !IsLaunching;

    private static void HandlePostLaunch(string behavior)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            switch (behavior)
            {
                case "Close":
                    Application.Current.Shutdown();
                    break;
                case "Minimize":
                    if (Application.Current.MainWindow is { } w)
                        w.WindowState = WindowState.Minimized;
                    break;
            }
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // 副标题拼装
    // ─────────────────────────────────────────────────────────────────────

    private void UpdateSubtitle()
    {
        var ver = string.IsNullOrEmpty(_mcVersionStr)
            ? string.Empty
            : $"{_mcVersionStr}  ·  {_loaderStr}";

        var mode = SelectedMode switch
        {
            LaunchMode.OfficialServer => "朝夕服",
            LaunchMode.Singleplayer   => "单机",
            LaunchMode.CustomServer
                when !string.IsNullOrEmpty(_settingsService.Current.CustomServerAddress)
                => _settingsService.Current.CustomServerAddress,
            _ => "自定义服务器"
        };

        LaunchButtonSubtitle = string.IsNullOrEmpty(ver) ? mode : $"{ver}  |  {mode}";
    }

    // ─────────────────────────────────────────────────────────────────────
    // 后台加载
    // ─────────────────────────────────────────────────────────────────────

    private async Task LoadManifestInfoAsync()
    {
        try
        {
            var m = await _manifestService.FetchAsync();
            _mcVersionStr = m.MinecraftVersion;
            _loaderStr    = $"{m.Loader.Type} {m.Loader.Version}";
            Application.Current.Dispatcher.Invoke(() =>
            {
                ServerAddress = m.Server.Address;
                PackVersion   = m.PackVersion;
                UpdateSubtitle();
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "预加载 manifest 失败");
        }
    }

    private async Task LoadAnnouncementAsync()
    {
        var text = await _manifestService.FetchAnnouncementAsync();
        if (!string.IsNullOrEmpty(text))
            Application.Current.Dispatcher.Invoke(() => Announcement = text);
    }
}
