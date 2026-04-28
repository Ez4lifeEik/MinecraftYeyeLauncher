using ArclightLauncher.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArclightLauncher.ViewModels.Dialogs;

/// <summary>
/// "连接其他服务器"对话框 ViewModel
/// </summary>
public partial class CustomServerViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;

    [ObservableProperty] private string _address;
    [ObservableProperty] private string _port;

    /// <summary>确认或取消时触发，参数为 DialogResult</summary>
    public event Action<bool>? CloseRequested;

    public CustomServerViewModel(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _address = settingsService.Current.CustomServerAddress;
        _port    = settingsService.Current.CustomServerPort.ToString();
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        var addr = Address.Trim();
        if (string.IsNullOrEmpty(addr)) return;

        _settingsService.Current.CustomServerAddress = addr;

        if (int.TryParse(Port.Trim(), out var p) && p is > 0 and <= 65535)
            _settingsService.Current.CustomServerPort = p;
        else
            _settingsService.Current.CustomServerPort = 25565;

        await _settingsService.SaveAsync();
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);
}
