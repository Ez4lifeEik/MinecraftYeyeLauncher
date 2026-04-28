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

        Log.Information("ArclightLauncher v0.3 启动");

        // ── 2. 构建通用主机（DI 容器）────────────────────────────────
        _host = Host.CreateDefaultBuilder()
            .UseSerilog()
            .ConfigureAppConfiguration(cfg =>
            {
                cfg.SetBasePath(AppContext.BaseDirectory)
                   .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);
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
                services.AddSingleton<JavaService>();
                services.AddSingleton<DownloadService>();
                services.AddSingleton<SyncService>();
                services.AddSingleton<LaunchService>();

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
        // 主窗口关闭时才真正退出程序
        mainWindow.Closed += (_, _) => Shutdown();
        mainWindow.Show();
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
