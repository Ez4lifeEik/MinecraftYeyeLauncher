using System.Collections.ObjectModel;
using ArclightLauncher.Models;
using ArclightLauncher.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ArclightLauncher.ViewModels.Dialogs;

/// <summary>
/// 单个可选 mod 的行 ViewModel
/// </summary>
public partial class ModEntryViewModel : ObservableObject
{
    public PackFile File { get; }

    [ObservableProperty]
    private bool _isEnabled;

    public string DisplayName =>
        string.IsNullOrEmpty(File.DisplayName) ? File.Filename : File.DisplayName;

    public string Category => File.Category;

    public ModEntryViewModel(PackFile file, bool isEnabled)
    {
        File       = file;
        _isEnabled = isEnabled;
    }
}

/// <summary>
/// Mod 管理对话框 ViewModel
/// </summary>
public partial class ModManagerViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;

    public ObservableCollection<ModEntryViewModel> Mods { get; }

    /// <summary>VM 请求关闭对话框时触发，参数为 DialogResult</summary>
    public event Action<bool>? CloseRequested;

    public ModManagerViewModel(
        IEnumerable<PackFile> removableMods,
        SettingsService settingsService)
    {
        _settingsService = settingsService;

        var disabled = new HashSet<string>(
            settingsService.Current.DisabledMods,
            StringComparer.OrdinalIgnoreCase);

        Mods = new ObservableCollection<ModEntryViewModel>(
            removableMods.Select(m =>
                new ModEntryViewModel(m, !disabled.Contains(m.Filename))));
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var m in Mods) m.IsEnabled = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var m in Mods) m.IsEnabled = false;
    }

    [RelayCommand]
    private async Task ConfirmAsync()
    {
        _settingsService.Current.DisabledMods = Mods
            .Where(m => !m.IsEnabled)
            .Select(m => m.File.Filename)
            .ToList();

        await _settingsService.SaveAsync();
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);
}
