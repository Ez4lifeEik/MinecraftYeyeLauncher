using CommunityToolkit.Mvvm.ComponentModel;

namespace ArclightLauncher.ViewModels;

/// <summary>
/// 主窗口 ViewModel —— TabControl 容器（启动 / 设置 / 关于）
/// </summary>
public partial class MainViewModel : ObservableObject
{
    public HomeViewModel     HomeVm     { get; }
    public SettingsViewModel SettingsVm { get; }
    public AboutViewModel    AboutVm    { get; }

    [ObservableProperty]
    private int _selectedTab = 0;

    public MainViewModel(
        HomeViewModel homeVm,
        SettingsViewModel settingsVm,
        AboutViewModel aboutVm)
    {
        HomeVm     = homeVm;
        SettingsVm = settingsVm;
        AboutVm    = aboutVm;
    }
}
