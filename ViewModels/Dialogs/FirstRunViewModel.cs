using System.IO;
using System.Windows;
using ArclightLauncher.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace ArclightLauncher.ViewModels.Dialogs;

/// <summary>
/// 首次运行向导 ViewModel：让用户确认游戏数据目录。
/// </summary>
public partial class FirstRunViewModel : ObservableObject
{
    private readonly SettingsService      _settingsService;
    private readonly GameDirectoryService _gameDirService;

    // ── 绑定属性 ──────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriveInfo))]
    [NotifyPropertyChangedFor(nameof(IsSystemDriveWarningVisible))]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _gamePath = string.Empty;

    public string DriveInfo              => _gameDirService.GetDriveInfo(GamePath);
    public bool   IsSystemDriveWarningVisible => _gameDirService.IsOnSystemDrive(GamePath);

    // ── 关闭事件 ──────────────────────────────────────────────────────────
    public event Action<bool>? CloseRequested;

    // ── 构造 ──────────────────────────────────────────────────────────────
    public FirstRunViewModel(SettingsService settingsService, GameDirectoryService gameDirService)
    {
        _settingsService = settingsService;
        _gameDirService  = gameDirService;

        // 用推荐路径初始化（同时会触发派生属性刷新）
        _gamePath = gameDirService.SuggestDefaultPath();
    }

    // ── 浏览文件夹 ────────────────────────────────────────────────────────
    [RelayCommand]
    private void BrowseFolder()
    {
        var dlg = new OpenFolderDialog
        {
            Title            = "选择游戏数据目录（将在其中创建 .minecraft）",
            InitialDirectory = Directory.Exists(GamePath) ? GamePath : string.Empty
        };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            GamePath = dlg.FolderName;
    }

    // ── 确认 ──────────────────────────────────────────────────────────────
    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        _settingsService.Current.GameDir          = GamePath;
        _settingsService.Current.GameDirConfirmed = true;
        await _settingsService.SaveAsync();
        CloseRequested?.Invoke(true);
    }

    private bool CanConfirm() => !string.IsNullOrWhiteSpace(GamePath);

    // ── 使用默认路径 ──────────────────────────────────────────────────────
    [RelayCommand]
    private async Task UseDefaultAsync()
    {
        GamePath = Models.LauncherSettings.DefaultGameDir;
        _settingsService.Current.GameDir          = GamePath;
        _settingsService.Current.GameDirConfirmed = true;
        await _settingsService.SaveAsync();
        CloseRequested?.Invoke(true);
    }
}
