using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using ArclightLauncher.Models;
using ArclightLauncher.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace ArclightLauncher.ViewModels;

/// <summary>
/// 启动页 ViewModel：账户、启动模式、状态卡片与启动流程。
/// </summary>
public partial class HomeViewModel : ObservableObject
{
    private readonly ManifestService _manifestService;
    private readonly JavaService     _javaService;
    private readonly LaunchService   _launchService;
    private readonly SettingsService _settingsService;
    private readonly AccountService  _accountService;
    private readonly MicrosoftAuthService _microsoftAuthService;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<HomeViewModel> _logger;

    private const string OfficialMinecraftVersion = "1.21.11";
    private const string OfficialLoaderType = "fabric";
    private const string OfficialLoaderVersion = "latest";
    private const int OfficialJavaVersion = 21;
    private const string MojangManifestUrl =
        "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    private bool _suppressVersionChangedRefresh;

    private string _mcVersionStr = string.Empty;
    private string _loaderStr    = string.Empty;
    private Account? _activeAccount;
    private List<Account> _accounts = [];

    public IEnumerable<string> AccountNames => _accounts.Select(a =>
        a.IsMicrosoft ? $"[正版] {a.Username}" : $"[离线] {a.Username}");

    public IEnumerable<string> AccountUuids => _accounts.Select(a => a.Uuid);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UsernameInitial))]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    private string _username = string.Empty;

    public string UsernameInitial
        => string.IsNullOrEmpty(Username) ? "?" : Username[0].ToString().ToUpperInvariant();

    [ObservableProperty] private LaunchMode _selectedMode = LaunchMode.OfficialServer;

    public bool IsOfficialServerMode => SelectedMode == LaunchMode.OfficialServer;
    public bool IsSingleplayerMode   => SelectedMode == LaunchMode.Singleplayer;
    public bool IsCustomServerMode   => SelectedMode == LaunchMode.CustomServer;

    partial void OnSelectedAccountIndexChanged(int value)
    {
        if (value < 0 || value >= _accounts.Count)
            return;

        _activeAccount = _accounts[value];
        Username = _activeAccount.Username;
        ApplyAccountState(_activeAccount);
    }

    partial void OnSelectedModeChanged(LaunchMode value)
    {
        OnPropertyChanged(nameof(IsOfficialServerMode));
        OnPropertyChanged(nameof(IsSingleplayerMode));
        OnPropertyChanged(nameof(IsCustomServerMode));
        UpdateSubtitle();
        if (value != LaunchMode.OfficialServer)
            _settingsService.Current.LastLaunchMode = value;
        _ = _settingsService.SaveAsync();
        UpdatePlayerLaunchSubtitle();
    }

    partial void OnIncludeSnapshotsChanged(bool value)
    {
        _settingsService.Current.IncludeSnapshotsInVersionList = value;
        _ = _settingsService.SaveAsync();
        _ = RefreshGameVersionsAsync();
    }

    partial void OnSelectedMinecraftVersionChanged(MinecraftVersionOption? value)
    {
        if (value is null)
            return;

        _settingsService.Current.PlayerMinecraftVersion = value.Id;
        _ = _settingsService.SaveAsync();
        UpdatePlayerLaunchSubtitle();

        if (!_suppressVersionChangedRefresh)
            _ = RefreshFabricLoadersAsync();
    }

    partial void OnSelectedPlayerLoaderTypeChanged(string value)
    {
        _ = RefreshFabricLoadersAsync();
    }

    partial void OnSelectedFabricLoaderVersionChanged(string value)
    {
        _settingsService.Current.PlayerFabricLoaderVersion = string.IsNullOrWhiteSpace(value)
            ? "latest"
            : value;
        _ = _settingsService.SaveAsync();
        UpdatePlayerLaunchSubtitle();
    }

    public IReadOnlyList<string> PlayerLoaderTypeOptions { get; } = ["Fabric", "Quilt"];

    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private double _progress;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    [NotifyCanExecuteChangedFor(nameof(LaunchSelectedVersionCommand))]
    [NotifyCanExecuteChangedFor(nameof(LoginMicrosoftCommand))]
    private bool _isLaunching;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoginMicrosoftCommand))]
    private bool _isLoggingIn;

    [ObservableProperty] private string _launchButtonSubtitle = string.Empty;
    [ObservableProperty] private string _serverAddress = "读取中";
    [ObservableProperty] private string _packVersion = "读取中";
    [ObservableProperty] private string _announcement = string.Empty;
    [ObservableProperty] private string _serverName = "烨夜服";
    [ObservableProperty] private string _serverStatusText = "检测中";
    [ObservableProperty] private string _serverPlayersText = "在线人数读取中";
    [ObservableProperty] private string _javaStatusText = "自动检测";
    [ObservableProperty] private string _versionStatusText = "读取中";
    [ObservableProperty] private string _modeText = "烨夜服";
    [ObservableProperty] private bool _includeSnapshots = true;
    [ObservableProperty] private bool _isVersionListBusy;
    [ObservableProperty] private MinecraftVersionOption? _selectedMinecraftVersion;
    [ObservableProperty] private string _selectedFabricLoaderVersion = "latest";
    [ObservableProperty] private string _versionPickerStatus = "正在加载版本列表";
    [ObservableProperty] private int _selectedAccountIndex = -1;
    [ObservableProperty] private string _selectedPlayerLoaderType = "Fabric";
    [ObservableProperty] private string _playerLaunchSubtitle = "请选择版本";
    [ObservableProperty] private string _accountModeText = "离线账号";
    [ObservableProperty] private string _accountStatusText = "输入昵称即可启动；正版账号可点击右侧登录";
    [ObservableProperty] private string _microsoftLoginButtonText = "正版登录";

    public ObservableCollection<MinecraftVersionOption> MinecraftVersionOptions { get; } = [];
    public ObservableCollection<string> FabricLoaderVersionOptions { get; } = ["latest"];

    public string CustomBackgroundPath => _settingsService.Current.BackgroundImagePath;

    public string ModsFolderPath
    {
        get
        {
            var gameDir = string.IsNullOrWhiteSpace(_settingsService.Current.GameDir)
                ? LauncherSettings.DefaultGameDir
                : _settingsService.Current.GameDir;
            return Path.Combine(gameDir, "mods");
        }
    }

    public HomeViewModel(
        ManifestService manifestService,
        JavaService     javaService,
        LaunchService   launchService,
        SettingsService settingsService,
        AccountService  accountService,
        MicrosoftAuthService microsoftAuthService,
        IHttpClientFactory httpFactory,
        ILogger<HomeViewModel> logger)
    {
        _manifestService = manifestService;
        _javaService     = javaService;
        _launchService   = launchService;
        _settingsService = settingsService;
        _accountService  = accountService;
        _microsoftAuthService = microsoftAuthService;
        _httpFactory     = httpFactory;
        _logger          = logger;

        _username = settingsService.Current.Username;
        _selectedMode = settingsService.Current.LastLaunchMode == LaunchMode.OfficialServer
            ? LaunchMode.Singleplayer
            : settingsService.Current.LastLaunchMode;
        _includeSnapshots = settingsService.Current.IncludeSnapshotsInVersionList;
        _selectedFabricLoaderVersion = string.IsNullOrWhiteSpace(settingsService.Current.PlayerFabricLoaderVersion)
            ? "latest"
            : settingsService.Current.PlayerFabricLoaderVersion;
        RefreshJavaStatusText();
        UpdateSubtitle();
        UpdatePlayerLaunchSubtitle();

        _launchService.StatusChanged += (_, msg) =>
            Application.Current.Dispatcher.Invoke(() => StatusText = msg);
        _launchService.ProgressChanged += (_, pct) =>
            Application.Current.Dispatcher.Invoke(() => Progress = pct);
        _javaService.StatusChanged += (_, msg) =>
            Application.Current.Dispatcher.Invoke(() => StatusText = msg);
        _javaService.ProgressChanged += (_, pct) =>
            Application.Current.Dispatcher.Invoke(() => Progress = 25 + pct * 0.05);

        _ = LoadManifestInfoAsync();
        _ = LoadAnnouncementAsync();
        _ = LoadSavedAccountAsync();
        _ = RefreshGameVersionsAsync();
    }

    [RelayCommand]
    private void SelectOfficialServer() => SelectedMode = LaunchMode.OfficialServer;

    [RelayCommand]
    private void SelectSingleplayer() => SelectedMode = LaunchMode.Singleplayer;

    [RelayCommand]
    private void SelectCustomServer()
    {
        var vm = new Dialogs.CustomServerViewModel(_settingsService);
        var dialog = new Views.Dialogs.CustomServerDialog(vm)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true)
            SelectedMode = LaunchMode.CustomServer;
    }

    public string CrashReportsPath
    {
        get
        {
            var gameDir = string.IsNullOrWhiteSpace(_settingsService.Current.GameDir)
                ? LauncherSettings.DefaultGameDir
                : _settingsService.Current.GameDir;
            return Path.Combine(gameDir, "crash-reports");
        }
    }

    public string GameLogsPath
    {
        get
        {
            var gameDir = string.IsNullOrWhiteSpace(_settingsService.Current.GameDir)
                ? LauncherSettings.DefaultGameDir
                : _settingsService.Current.GameDir;
            return Path.Combine(gameDir, "logs");
        }
    }

    [RelayCommand]
    private void OpenModsFolder()
    {
        Directory.CreateDirectory(ModsFolderPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = ModsFolderPath,
            UseShellExecute = true
        });

        StatusText = "已打开 mods 文件夹；请只添加 Fabric 客户端 mod，添加后重启游戏生效。";
    }

    [RelayCommand]
    private void OpenCrashReports()
    {
        Directory.CreateDirectory(CrashReportsPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = CrashReportsPath,
            UseShellExecute = true
        });
        StatusText = "已打开崩溃报告文件夹";
    }

    [RelayCommand]
    private void OpenGameLogs()
    {
        Directory.CreateDirectory(GameLogsPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = GameLogsPath,
            UseShellExecute = true
        });
        StatusText = "已打开游戏日志文件夹";
    }

    [RelayCommand(CanExecute = nameof(CanLoginMicrosoft))]
    private async Task LoginMicrosoftAsync()
    {
        if (!_microsoftAuthService.IsConfigured)
        {
            StatusText = _microsoftAuthService.ConfigurationHint;
            MessageBox.Show(
                _microsoftAuthService.ConfigurationHint,
                "Microsoft 正版登录未配置",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        IsLoggingIn = true;
        StatusText = "正在打开 Microsoft 正版登录窗口...";

        var dialog = new Views.Dialogs.MicrosoftWebLoginDialog
        {
            Owner = Application.Current.MainWindow
        };
        dialog.Show();

        try
        {
            var gameDir = string.IsNullOrWhiteSpace(_settingsService.Current.GameDir)
                ? LauncherSettings.DefaultGameDir
                : _settingsService.Current.GameDir;

            var account = await _microsoftAuthService.LoginInteractiveAsync(
                gameDir,
                (authorizeUri, loginCt) => dialog.SignInAsync(authorizeUri, loginCt));

            _activeAccount = account;
            Username = account.Username;
            _settingsService.Current.Username = account.Username;
            await _settingsService.SaveAsync();
            _accounts = await _accountService.AddOrUpdateAsync(_accounts, account);
            SelectedAccountIndex = _accounts.Count - 1;
            OnPropertyChanged(nameof(AccountNames));
            OnPropertyChanged(nameof(AccountUuids));
            ApplyAccountState(account);

            StatusText = $"正版登录成功：{account.Username}";
        }
        catch (Exception ex)
        {
            StatusText = $"正版登录失败：{ex.Message}";
            if (dialog.IsVisible)
                dialog.Close();
            MessageBox.Show(
                ex.Message,
                "Microsoft 正版登录失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            _logger.LogWarning(ex, "Microsoft 正版登录失败");
        }
        finally
        {
            IsLoggingIn = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanLoginMicrosoft))]
    private async Task LoginMicrosoftDeviceCodeAsync()
    {
        if (!_microsoftAuthService.IsConfigured)
        {
            StatusText = _microsoftAuthService.ConfigurationHint;
            MessageBox.Show(
                _microsoftAuthService.ConfigurationHint,
                "Microsoft 正版登录未配置",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        IsLoggingIn = true;
        StatusText = "正在获取 Microsoft 设备验证码...";

        var dialog = new Views.Dialogs.MicrosoftLoginDialog
        {
            Owner = Application.Current.MainWindow
        };

        try
        {
            var gameDir = string.IsNullOrWhiteSpace(_settingsService.Current.GameDir)
                ? LauncherSettings.DefaultGameDir
                : _settingsService.Current.GameDir;

            var loginTask = _microsoftAuthService.LoginAsync(
                gameDir,
                deviceCode =>
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        dialog.SetDeviceCode(deviceCode);
                        dialog.Show();
                    }));

            var account = await loginTask;

            Application.Current.Dispatcher.Invoke(() =>
            {
                dialog.SetCompleted("登录成功！");
                dialog.Close();
            });

            _activeAccount = account;
            Username = account.Username;
            _settingsService.Current.Username = account.Username;
            await _settingsService.SaveAsync();
            _accounts = await _accountService.AddOrUpdateAsync(_accounts, account);
            SelectedAccountIndex = _accounts.Count - 1;
            OnPropertyChanged(nameof(AccountNames));
            OnPropertyChanged(nameof(AccountUuids));
            ApplyAccountState(account);

            StatusText = $"正版登录成功：{account.Username}";
        }
        catch (Exception ex)
        {
            var message = ex is OperationCanceledException
                ? "登录已取消"
                : ex.Message;

            Application.Current.Dispatcher.Invoke(() =>
            {
                if (dialog.IsVisible)
                {
                    dialog.SetFailed(message);
                    dialog.Close();
                }
            });

            StatusText = $"正版登录失败：{message}";
            if (ex is not OperationCanceledException)
            {
                MessageBox.Show(message, "Microsoft 正版登录失败",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            _logger.LogWarning(ex, "Microsoft 设备码登录失败");
        }
        finally
        {
            IsLoggingIn = false;
        }
    }

    [RelayCommand]
    private async Task UseOfflineAccountAsync()
    {
        if (string.IsNullOrWhiteSpace(Username))
        {
            StatusText = "请先输入离线游戏昵称";
            return;
        }

        var account = _accountService.CreateOfflineAccount(Username.Trim());
        _activeAccount = account;
        _accounts = await _accountService.AddOrUpdateAsync(_accounts, account);
        SelectedAccountIndex = _accounts.Count - 1;
        OnPropertyChanged(nameof(AccountNames));
        OnPropertyChanged(nameof(AccountUuids));
        ApplyAccountState(account);
        StatusText = $"已切换为离线账号：{account.Username}";
    }

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchAsync()
        => await ExecuteLaunchAsync(async () =>
        {
            StatusText = "正在拉取官方整合包信息...";
            Progress = 10;

            var remoteManifest = await _manifestService.FetchAsync();
            var manifest = BuildOfficialManifest(remoteManifest);
            Progress = 20;

            ApplyManifestSummary(manifest);
            StatusText = $"官方指定版本：Fabric {OfficialMinecraftVersion}";

            return new PreparedLaunch(
                manifest,
                LaunchMode.OfficialServer,
                null,
                25565,
                UseDisabledMods: false,
                ConsumeForceRevalidate: true);
        });

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private async Task LaunchSelectedVersionAsync()
        => await ExecuteLaunchAsync(() =>
        {
            if (SelectedMinecraftVersion is null)
                throw new InvalidOperationException("请先选择一个游戏版本");

            var settings = _settingsService.Current;
            var mode = SelectedMode == LaunchMode.CustomServer
                ? LaunchMode.CustomServer
                : LaunchMode.Singleplayer;

            string? customIp = null;
            var customPort = 25565;
            if (mode == LaunchMode.CustomServer)
            {
                customIp = settings.CustomServerAddress.Trim();
                customPort = settings.CustomServerPort;

                if (string.IsNullOrWhiteSpace(customIp))
                    throw new InvalidOperationException("请先填写自定义服务器地址");
            }

            var loaderVersion = string.IsNullOrWhiteSpace(SelectedFabricLoaderVersion)
                ? "latest"
                : SelectedFabricLoaderVersion.Trim();
            var loaderType = SelectedPlayerLoaderType.ToLowerInvariant();
            var manifest = BuildPlayerManifest(
                SelectedMinecraftVersion.Id,
                loaderVersion,
                loaderType,
                mode,
                customIp,
                customPort);

            ApplyPlayerVersionSummary(manifest, mode);
            StatusText = $"自选版本：{SelectedPlayerLoaderType} {SelectedMinecraftVersion.Id}";
            Progress = 20;

            return Task.FromResult(new PreparedLaunch(
                manifest,
                mode,
                customIp,
                customPort,
                UseDisabledMods: true,
                ConsumeForceRevalidate: false));
        });

    private async Task ExecuteLaunchAsync(Func<Task<PreparedLaunch>> prepareLaunchAsync)
    {
        var account = ResolveAccountForLaunch();
        if (account is null)
            return;

        IsLaunching = true;
        Progress = 0;
        StatusText = string.Empty;

        try
        {
            Username = account.Username;
            _settingsService.Current.Username = account.Username;
            await _settingsService.SaveAsync();
            if (!account.IsMicrosoft)
            {
                _accounts = await _accountService.AddOrUpdateAsync(_accounts, account);
                SelectedAccountIndex = _accounts.Count - 1;
                OnPropertyChanged(nameof(AccountNames));
                OnPropertyChanged(nameof(AccountUuids));
            }

            var launch = await prepareLaunchAsync();
            var manifest = launch.Manifest;

            StatusText = $"正在检测 Java {manifest.JavaVersion}+...";
            Progress = 25;

            var settings = _settingsService.Current;
            var resolvedJavaExe = await _javaService.ResolveJavaAsync(
                manifest.JavaVersion,
                settings.JavaExe,
                settings.AutoDownloadJava);

            if (resolvedJavaExe is null)
            {
                StatusText = $"未找到 Java {manifest.JavaVersion}+，请在设置页启用自动下载或手动指定 Java。";
                Progress = 0;
                return;
            }

            if (!string.Equals(settings.JavaExe, resolvedJavaExe, StringComparison.OrdinalIgnoreCase))
            {
                settings.JavaExe = resolvedJavaExe;
                await _settingsService.SaveAsync();
            }
            await RefreshJavaStatusTextAsync(resolvedJavaExe);

            if (!string.IsNullOrWhiteSpace(settings.JavaExe) && File.Exists(settings.JavaExe))
            {
                var configuredJavaVersion = await _javaService.GetMajorVersionAsync(settings.JavaExe);
                if (configuredJavaVersion < manifest.JavaVersion)
                {
                    var currentVersionText = configuredJavaVersion > 0
                        ? configuredJavaVersion.ToString()
                        : "未知";
                    StatusText = $"已配置 Java 不符合要求（当前 {currentVersionText}，需要 {manifest.JavaVersion}+），正在重新检测...";
                    Progress = 27;
                    _logger.LogWarning(
                        "Configured Java is incompatible. Required {Required}, actual {Actual}, path {Path}",
                        manifest.JavaVersion,
                        configuredJavaVersion,
                        settings.JavaExe);
                    settings.JavaExe = string.Empty;
                    await _settingsService.SaveAsync();
                }
            }
            else if (!string.IsNullOrWhiteSpace(settings.JavaExe))
            {
                _logger.LogWarning("Configured Java path does not exist: {Path}", settings.JavaExe);
                settings.JavaExe = string.Empty;
                await _settingsService.SaveAsync();
            }

            string? javaExe = (!string.IsNullOrEmpty(settings.JavaExe) && File.Exists(settings.JavaExe))
                ? settings.JavaExe
                : await _javaService.FindJavaAsync(manifest.JavaVersion);

            if (javaExe is null)
            {
                StatusText = $"未找到 Java {manifest.JavaVersion}+，请在设置页手动指定。";
                Progress = 0;
                return;
            }

            if (!string.Equals(settings.JavaExe, javaExe, StringComparison.OrdinalIgnoreCase))
            {
                settings.JavaExe = javaExe;
                await _settingsService.SaveAsync();
            }
            await RefreshJavaStatusTextAsync(javaExe);

            Progress = 30;

            var disabledMods = launch.UseDisabledMods
                ? (IReadOnlySet<string>?)new HashSet<string>(
                    settings.DisabledMods, StringComparer.OrdinalIgnoreCase)
                : null;

            bool forceRevalidate = launch.ConsumeForceRevalidate && settings.ForceRevalidate;
            if (forceRevalidate)
            {
                settings.ForceRevalidate = false;
                await _settingsService.SaveAsync();
            }

            await _launchService.RunAsync(
                account:             account,
                javaExe:             javaExe,
                manifest:            manifest,
                mode:                launch.Mode,
                gameDir:             settings.GameDir,
                maxMemoryMb:         settings.MaxMemory,
                jvmArgs:             settings.JvmArgs,
                customServerAddress: launch.CustomIp,
                customServerPort:    launch.CustomPort,
                disabledMods:        disabledMods,
                forceRevalidate:     forceRevalidate);

            HandlePostLaunch(settings.PostLaunchBehavior);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("ManifestUrl"))
        {
            StatusText = "请先配置 ManifestUrl";
            Progress = 0;
        }
        catch (HttpRequestException ex)
        {
            StatusText = $"网络错误：{ex.Message}";
            Progress = 0;
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
            Progress = 0;
        }
        catch (Exception ex)
        {
            StatusText = $"错误：{ex.Message}";
            Progress = 0;
            _logger.LogError(ex, "LaunchAsync 异常");
        }
        finally
        {
            IsLaunching = false;
        }
    }

    private async Task LoadSavedAccountAsync()
    {
        try
        {
            _accounts = await _accountService.LoadAllAsync();
            var account = _accounts.FirstOrDefault();

            Application.Current.Dispatcher.Invoke(() =>
            {
                OnPropertyChanged(nameof(AccountNames));
                OnPropertyChanged(nameof(AccountUuids));

                if (account is not null)
                {
                    _activeAccount = account;
                    SelectedAccountIndex = 0;
                    Username = account.Username;
                    ApplyAccountState(account);
                }
                else
                {
                    ApplyAccountState(null);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取本地账号失败");
            ApplyAccountState(null);
        }
    }

    [RelayCommand]
    private async Task RemoveAccountAsync()
    {
        if (_activeAccount is null || SelectedAccountIndex < 0 || SelectedAccountIndex >= _accounts.Count)
            return;

        var uuid = _activeAccount.Uuid;
        _accounts = await _accountService.RemoveAsync(_accounts, uuid);
        Application.Current.Dispatcher.Invoke(() =>
        {
            OnPropertyChanged(nameof(AccountNames));
            OnPropertyChanged(nameof(AccountUuids));

            if (_accounts.Count > 0)
            {
                _activeAccount = _accounts[0];
                SelectedAccountIndex = 0;
                Username = _activeAccount.Username;
                ApplyAccountState(_activeAccount);
            }
            else
            {
                _activeAccount = null;
                SelectedAccountIndex = -1;
                Username = string.Empty;
                ApplyAccountState(null);
            }
        });
        StatusText = "账户已删除";
    }

    private Account? ResolveAccountForLaunch()
    {
        if (_activeAccount?.IsMicrosoft == true)
            return _activeAccount;

        if (string.IsNullOrWhiteSpace(Username))
        {
            StatusText = "请先输入用户名，或点击正版登录";
            return null;
        }

        var account = _accountService.CreateOfflineAccount(Username.Trim());
        _activeAccount = account;
        ApplyAccountState(account);
        return account;
    }

    private void ApplyAccountState(Account? account)
    {
        if (account?.IsMicrosoft == true)
        {
            AccountModeText = "正版账号";
            AccountStatusText = $"已登录：{account.Username}";
            MicrosoftLoginButtonText = "切换正版";
            return;
        }

        AccountModeText = "离线账号";
        AccountStatusText = "输入昵称即可启动；正版账号可点击右侧登录";
        MicrosoftLoginButtonText = "正版登录";
    }

    private bool CanLaunch() => !IsLaunching;

    private bool CanLoginMicrosoft() => !IsLaunching && !IsLoggingIn;

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

    [RelayCommand]
    private async Task RefreshGameVersionsAsync()
    {
        IsVersionListBusy = true;
        try
        {
            VersionPickerStatus = "正在刷新游戏版本列表...";

            using var refreshCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var client = _httpFactory.CreateClient();
            var json = await client.GetStringAsync(MojangManifestUrl, refreshCts.Token);
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

            var preferred = _settingsService.Current.PlayerMinecraftVersion;
            _suppressVersionChangedRefresh = true;
            try
            {
                SelectedMinecraftVersion =
                    versions.FirstOrDefault(v => string.Equals(v.Id, preferred, StringComparison.OrdinalIgnoreCase)) ??
                    versions.FirstOrDefault(v => string.Equals(v.Id, "1.21.10", StringComparison.OrdinalIgnoreCase)) ??
                    versions.FirstOrDefault(v => string.Equals(v.Type, "release", StringComparison.OrdinalIgnoreCase)) ??
                    versions.FirstOrDefault();
            }
            finally
            {
                _suppressVersionChangedRefresh = false;
            }

            VersionPickerStatus = $"已加载 {MinecraftVersionOptions.Count} 个版本";

            if (SelectedMinecraftVersion is not null)
                await RefreshFabricLoadersForAsync(SelectedMinecraftVersion.Id);

            UpdatePlayerLaunchSubtitle();
        }
        catch (Exception ex)
        {
            VersionPickerStatus = $"版本列表刷新失败：{ex.Message}";
            _logger.LogWarning(ex, "刷新游戏版本列表失败");
        }
        finally
        {
            IsVersionListBusy = false;
        }
    }

    [RelayCommand]
    private async Task RefreshFabricLoadersAsync()
    {
        if (SelectedMinecraftVersion is null)
        {
            VersionPickerStatus = "请先选择游戏版本";
            return;
        }

        IsVersionListBusy = true;
        try
        {
            await RefreshFabricLoadersForAsync(SelectedMinecraftVersion.Id);
        }
        finally
        {
            IsVersionListBusy = false;
        }
    }

    private async Task RefreshFabricLoadersForAsync(string mcVer)
    {
        var loaderLabel = SelectedPlayerLoaderType;
        VersionPickerStatus = $"正在获取 {mcVer} 的 {loaderLabel} Loader...";

        var previous = string.IsNullOrWhiteSpace(_settingsService.Current.PlayerFabricLoaderVersion)
            ? "latest"
            : _settingsService.Current.PlayerFabricLoaderVersion;

        FabricLoaderVersionOptions.Clear();
        FabricLoaderVersionOptions.Add("latest");

        using var loaderCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = _httpFactory.CreateClient();
        var url = loaderLabel.Equals("Quilt", StringComparison.OrdinalIgnoreCase)
            ? $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(mcVer)}"
            : $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(mcVer)}";

        try
        {
            var json = await client.GetStringAsync(url, loaderCts.Token);
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

            SelectedFabricLoaderVersion = FabricLoaderVersionOptions.Contains(previous)
                ? previous
                : "latest";

            VersionPickerStatus = loaderVersions.Count == 0
                ? $"{loaderLabel} 暂未返回 {mcVer} 的 Loader"
                : $"{loaderLabel} 支持 {mcVer}，可选 Loader {loaderVersions.Count} 个";
        }
        catch (Exception ex)
        {
            SelectedFabricLoaderVersion = "latest";
            VersionPickerStatus = $"{loaderLabel} 暂未支持 {mcVer}，或网络不可用：{ex.Message}";
            _logger.LogWarning(ex, "刷新 {Loader} Loader 列表失败：{McVer}", loaderLabel, mcVer);
        }

        UpdatePlayerLaunchSubtitle();
    }

    private void UpdatePlayerLaunchSubtitle()
    {
        var version = SelectedMinecraftVersion?.Id ?? _settingsService.Current.PlayerMinecraftVersion;
        if (string.IsNullOrWhiteSpace(version))
        {
            PlayerLaunchSubtitle = "请选择版本";
            return;
        }

        var loader = string.IsNullOrWhiteSpace(SelectedFabricLoaderVersion)
            ? "latest"
            : SelectedFabricLoaderVersion;
        var target = SelectedMode == LaunchMode.CustomServer
            ? "自定义服务器"
            : "单人游戏";

        PlayerLaunchSubtitle = $"{version} / Fabric {loader} | {target}";
    }

    private void UpdateSubtitle()
    {
        ModeText = ServerName;
        LaunchButtonSubtitle = $"官方指定：{OfficialMinecraftVersion} / Fabric {OfficialLoaderVersion}";
    }

    private async Task LoadManifestInfoAsync()
    {
        try
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                StatusText = "正在获取服务器整合包信息...";
                Progress = 5;
            });

            var manifest = BuildOfficialManifest(await _manifestService.FetchAsync());
            Application.Current.Dispatcher.Invoke(() =>
            {
                ApplyManifestSummary(manifest);
                StatusText = "整合包信息加载完成";
                Progress = 0;
            });
        }
        catch (Exception ex)
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                ServerStatusText = "离线";
                ServerPlayersText = "无法连接更新服务器，请检查网络";
                StatusText = "网络连接失败，使用缓存数据";
                Progress = 0;
            });
            _logger.LogWarning(ex, "预加载 manifest 失败");
        }
    }

    private async Task LoadAnnouncementAsync()
    {
        var text = await _manifestService.FetchAnnouncementAsync();
        if (!string.IsNullOrEmpty(text))
            Application.Current.Dispatcher.Invoke(() => Announcement = text);
    }

    private static ServerManifest BuildOfficialManifest(ServerManifest source)
        => new()
        {
            PackVersion = source.PackVersion,
            MinecraftVersion = OfficialMinecraftVersion,
            Loader = new LoaderInfo
            {
                Type = OfficialLoaderType,
                Version = OfficialLoaderVersion
            },
            JavaVersion = OfficialJavaVersion,
            Server = new ServerInfo
            {
                Address = source.Server.Address,
                Port = source.Server.Port
            },
            Mods = source.Mods,
            Resourcepacks = source.Resourcepacks,
            Shaderpacks = source.Shaderpacks
        };

    private static ServerManifest BuildPlayerManifest(
        string minecraftVersion,
        string loaderVersion,
        string loaderType,
        LaunchMode mode,
        string? customIp,
        int customPort)
        => new()
        {
            PackVersion = "自选版本",
            MinecraftVersion = minecraftVersion,
            Loader = new LoaderInfo
            {
                Type = loaderType,
                Version = string.IsNullOrWhiteSpace(loaderVersion)
                    ? "latest"
                    : loaderVersion
            },
            JavaVersion = GuessRequiredJavaMajor(minecraftVersion),
            Server = mode == LaunchMode.CustomServer
                ? new ServerInfo
                {
                    Address = customIp ?? string.Empty,
                    Port = Math.Clamp(customPort, 1, 65535)
                }
                : new ServerInfo(),
            Mods = [],
            Resourcepacks = [],
            Shaderpacks = []
        };

    private void ApplyPlayerVersionSummary(ServerManifest manifest, LaunchMode mode)
    {
        _mcVersionStr = manifest.MinecraftVersion;
        _loaderStr = $"{manifest.Loader.Type} {manifest.Loader.Version}";
        VersionStatusText = $"{manifest.MinecraftVersion} / Fabric {manifest.Loader.Version}";
        PackVersion = "自选版本";
        ServerAddress = mode == LaunchMode.CustomServer
            ? FormatServerAddress(manifest.Server.Address, manifest.Server.Port)
            : "单人游戏";
        ModeText = mode == LaunchMode.CustomServer ? "自定义服务器" : "单人游戏";
        UpdatePlayerLaunchSubtitle();
    }

    private void ApplyManifestSummary(ServerManifest manifest)
    {
        _mcVersionStr = manifest.MinecraftVersion;
        _loaderStr = $"{manifest.Loader.Type} {manifest.Loader.Version}";
        ServerAddress = FormatServerAddress(manifest.Server.Address, manifest.Server.Port);
        PackVersion = manifest.PackVersion;
        VersionStatusText = $"{manifest.MinecraftVersion} / {manifest.Loader.Type} {manifest.Loader.Version}";
        UpdateSubtitle();
        _ = RefreshServerStatusAsync(manifest.Server.Address, manifest.Server.Port);
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

    private void RefreshJavaStatusText()
    {
        var javaExe = _settingsService.Current.JavaExe;
        JavaStatusText = string.IsNullOrWhiteSpace(javaExe) || !File.Exists(javaExe)
            ? "自动检测"
            : "已配置";
    }

    private async Task RefreshJavaStatusTextAsync(string javaExe)
    {
        var version = await _javaService.GetMajorVersionAsync(javaExe);
        Application.Current.Dispatcher.Invoke(() =>
        {
            JavaStatusText = version > 0 ? $"Java {version}" : "Java 已配置";
        });
    }

    private async Task RefreshServerStatusAsync(string address, int port)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                ServerStatusText = "未配置";
                ServerPlayersText = "无服务器地址";
            });
            return;
        }

        var normalizedPort = Math.Clamp(port, 1, 65535);

        try
        {
            var status = await QueryMinecraftStatusAsync(address, normalizedPort, TimeSpan.FromSeconds(5));

            Application.Current.Dispatcher.Invoke(() =>
            {
                ServerStatusText = "在线";
                ServerPlayersText = $"{status.OnlinePlayers} / {status.MaxPlayers} 人在线";
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取服务器状态失败，尝试 TCP 连接检测");

            var tcpOnline = await CanOpenTcpAsync(address, normalizedPort);
            Application.Current.Dispatcher.Invoke(() =>
            {
                ServerStatusText = tcpOnline ? "在线" : "离线";
                ServerPlayersText = tcpOnline ? "人数读取失败" : "无法连接服务器";
            });
        }
    }

    private static async Task<bool> CanOpenTcpAsync(string address, int port)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var client = new TcpClient();
            await client.ConnectAsync(address, port, cts.Token);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<MinecraftServerStatus> QueryMinecraftStatusAsync(
        string address,
        int port,
        TimeSpan timeout)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var client = new TcpClient();
        await client.ConnectAsync(address, port, timeoutCts.Token);

        await using var stream = client.GetStream();

        var addressBytes = Encoding.UTF8.GetBytes(address);
        using var body = new MemoryStream();
        WriteVarInt(body, 0);
        WriteVarInt(body, -1);
        WriteVarInt(body, addressBytes.Length);
        body.Write(addressBytes);
        body.WriteByte((byte)(port >> 8));
        body.WriteByte((byte)(port & 0xFF));
        WriteVarInt(body, 1);

        var bodyBytes = body.ToArray();
        using var packet = new MemoryStream();
        WriteVarInt(packet, bodyBytes.Length);
        packet.Write(bodyBytes);

        await stream.WriteAsync(packet.ToArray(), timeoutCts.Token);
        await stream.WriteAsync(new byte[] { 1, 0 }, timeoutCts.Token);

        _ = await ReadVarIntAsync(stream, timeoutCts.Token);
        _ = await ReadVarIntAsync(stream, timeoutCts.Token);
        var jsonLength = await ReadVarIntAsync(stream, timeoutCts.Token);

        var jsonBytes = new byte[jsonLength];
        await stream.ReadExactlyAsync(jsonBytes, timeoutCts.Token);

        using var doc = JsonDocument.Parse(jsonBytes);
        var root = doc.RootElement;
        var players = root.GetProperty("players");
        var version = root.GetProperty("version");

        return new MinecraftServerStatus(
            players.GetProperty("online").GetInt32(),
            players.GetProperty("max").GetInt32(),
            version.GetProperty("name").GetString() ?? string.Empty);
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        var unsignedValue = unchecked((uint)value);
        do
        {
            var temp = (byte)(unsignedValue & 0x7F);
            unsignedValue >>= 7;
            if (unsignedValue != 0)
                temp |= 0x80;

            stream.WriteByte(temp);
        }
        while (unsignedValue != 0);
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken ct)
    {
        var numRead = 0;
        var result = 0;
        var buffer = new byte[1];
        byte read;

        do
        {
            var count = await stream.ReadAsync(buffer, ct);
            if (count == 0)
                throw new EndOfStreamException();

            read = buffer[0];
            var value = read & 0x7F;
            result |= value << (7 * numRead);

            numRead++;
            if (numRead > 5)
                throw new InvalidDataException("服务器状态包 VarInt 过长");
        }
        while ((read & 0x80) != 0);

        return result;
    }

    private sealed record MinecraftServerStatus(
        int OnlinePlayers,
        int MaxPlayers,
        string VersionName);

    private sealed record PreparedLaunch(
        ServerManifest Manifest,
        LaunchMode Mode,
        string? CustomIp,
        int CustomPort,
        bool UseDisabledMods,
        bool ConsumeForceRevalidate);

    private static string FormatServerAddress(string address, int port)
        => string.IsNullOrWhiteSpace(address)
            ? "未配置服务器"
            : $"{address}:{Math.Clamp(port, 1, 65535)}";
}
