using System.IO;
using System.Windows;
using ArclightLauncher.Services;
using ArclightLauncher.ViewModels;
using ArclightLauncher.ViewModels.Dialogs;
using ArclightLauncher.Views.Dialogs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace ArclightLauncher;

/// <summary>
/// 应用程序入口：构建 DI 容器、初始化日志、加载设置、启动主窗口
/// </summary>
public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 防止 FirstRunDialog 关闭时 WPF 误判"无窗口"而自动退出
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // ── 1. 日志初始化 ──────────────────────────────────────────────
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ArclightLauncher", "logs");
        Directory.CreateDirectory(logDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File(
                path: Path.Combine(logDir, "launcher-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.3.5";
        Log.Information("ArclightLauncher v{Version} 启动", version);


        // ── 2. 构建通用主机（DI 容器）────────────────────────────────
        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureAppConfiguration(cfg =>
            {
                var builtInConfig = new Dictionary<string, string?>
                {
                    ["Launcher:ManifestUrl"] =
                        "https://raw.githubusercontent.com/Ez4lifeEik/arclight-modpack/main/manifest.json",
                    ["Launcher:ManifestMirrorUrls:0"] =
                        "https://cdn.jsdelivr.net/gh/Ez4lifeEik/arclight-modpack@main/manifest.json",
                    ["Launcher:ManifestMirrorUrls:1"] =
                        "https://ghproxy.net/https://raw.githubusercontent.com/Ez4lifeEik/arclight-modpack/main/manifest.json",
                    ["Launcher:AnnouncementUrl"] = "",
                    ["Launcher:GitHubRepo"] = "Ez4lifeEik/MinecraftYeyeLauncher",
                    ["Launcher:MicrosoftClientId"] = "",
                    ["Launcher:MicrosoftTenantId"] = "consumers",
                    ["Launcher:MicrosoftRedirectUri"] = "http://localhost",
                    ["Launcher:MicrosoftScopes:0"] = "XboxLive.signin",
                    ["Launcher:MicrosoftScopes:1"] = "offline_access",
                    ["Launcher:MicrosoftScopes:2"] = "openid",
                    ["Launcher:MicrosoftScopes:3"] = "email",
                    ["Launcher:MicrosoftScopes:4"] = "profile"
                };

                cfg.SetBasePath(AppContext.BaseDirectory)
                   .AddInMemoryCollection(builtInConfig)
                   .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
            })
            .ConfigureServices((_, services) =>
            {
                // 基础设施
                services.AddHttpClient();

                // 服务层
                services.AddSingleton<SettingsService>();
                services.AddSingleton<GameDirectoryService>();
                services.AddSingleton<ManifestService>();
                services.AddSingleton<AccountService>();
                services.AddSingleton<MicrosoftAuthService>();
                services.AddSingleton<JavaService>();
                services.AddSingleton<DownloadService>();
                services.AddSingleton<SyncService>();
                services.AddSingleton<LaunchService>();
                services.AddSingleton<UpdateService>();

                // ViewModels（Singleton：整个生命周期只有一个实例）
                services.AddSingleton<HomeViewModel>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<AboutViewModel>();
                services.AddSingleton<MainViewModel>();

                // 主窗口
                services.AddTransient<MainWindow>();
            })
            .Build();

        await _host.StartAsync();

        // ── 2.5. 清理上次更新残留文件 ──────────────────────────────────
        UpdateService.CleanOldFiles();

        // ── 3. 加载设置，应用初始主题 ─────────────────────────────────
        var settingsService  = _host.Services.GetRequiredService<SettingsService>();
        var gameDirService   = _host.Services.GetRequiredService<GameDirectoryService>();
        await settingsService.LoadAsync();
        ViewModels.SettingsViewModel.ApplyTheme(settingsService.Current.Theme);

        // ── 4. 首次运行：弹出游戏目录向导 ─────────────────────────────
        if (gameDirService.IsFirstRun(settingsService.Current))
        {
            var vm     = new FirstRunViewModel(settingsService, gameDirService);
            var dialog = new FirstRunDialog(vm);
            // 用户关闭而非确认时退出程序，避免以未知路径运行
            if (dialog.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
        }

        // ── 5. 显示主窗口 ──────────────────────────────────────────────
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        // 托盘图标接管退出逻辑（ShutdownMode.OnExplicitShutdown）
        mainWindow.Show();

        // 后台静默检查更新，不阻塞主窗口
        _ = CheckForUpdateInBackgroundAsync(mainWindow);
    }

    private async Task CheckForUpdateInBackgroundAsync(System.Windows.Window owner)
    {
        // 等待主窗口完成渲染后再发起网络请求
        await Task.Delay(TimeSpan.FromSeconds(2));
        try
        {
            var updateService = _host!.Services.GetRequiredService<UpdateService>();
            var info = await updateService.CheckForUpdateAsync();
            if (info is null) return;

            await Dispatcher.InvokeAsync(() =>
            {
                var vm     = new UpdateViewModel(info, updateService);
                var dialog = new UpdateDialog(vm) { Owner = owner };
                dialog.ShowDialog();
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "后台更新检查出现未处理异常");
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
