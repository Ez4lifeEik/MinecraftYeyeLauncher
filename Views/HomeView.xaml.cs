using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using ArclightLauncher.Models;
using ArclightLauncher.ViewModels;

namespace ArclightLauncher.Views;

/// <summary>
/// Presentation-only interactions for the launcher home page.
/// </summary>
public partial class HomeView : UserControl
{
    private bool _syncingPasswordText;

    public HomeView()
    {
        InitializeComponent();
        Loaded += HomeView_Loaded;
    }

    private void HomeView_Loaded(object sender, RoutedEventArgs e)
        => TryLoadBackgroundImage();

    private void TryLoadBackgroundImage()
    {
        // 优先使用玩家自定义背景图
        if (DataContext is HomeViewModel vm &&
            !string.IsNullOrWhiteSpace(vm.CustomBackgroundPath) &&
            File.Exists(vm.CustomBackgroundPath))
        {
            SetBackgroundImage(vm.CustomBackgroundPath);
            return;
        }

        // 否则随机选择内置背景
        var imagePath = GetBackgroundImagePaths().OrderBy(_ => Random.Shared.Next()).FirstOrDefault();
        if (imagePath is null)
            return;

        SetBackgroundImage(imagePath);
    }

    private void SetBackgroundImage(string imagePath)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(imagePath, UriKind.Absolute);
        image.EndInit();
        image.Freeze();

        BackgroundImage.Source = image;
    }

    private static IReadOnlyList<string> GetBackgroundImagePaths()
    {
        var baseDir = AppContext.BaseDirectory;
        var backgroundDirs = new[]
        {
            Path.Combine(baseDir, "Assets", "Backgrounds"),
            Path.Combine(baseDir, "..", "..", "..", "Assets", "Backgrounds")
        };

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg",
            ".jpeg",
            ".png",
            ".webp"
        };

        var backgrounds = backgroundDirs
            .Select(Path.GetFullPath)
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir)
                .Where(path => extensions.Contains(Path.GetExtension(path))))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (backgrounds.Count > 0)
            return backgrounds;

        return new[]
        {
            Path.Combine(baseDir, "Assets", "launcher-background.jpg"),
            Path.Combine(baseDir, "Assets", "launcher-background.png"),
            Path.Combine(baseDir, "..", "..", "..", "Assets", "launcher-background.jpg"),
            Path.Combine(baseDir, "..", "..", "..", "Assets", "launcher-background.png")
        }
        .Select(Path.GetFullPath)
        .Where(File.Exists)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
    }

    private void TogglePasswordButton_Click(object sender, RoutedEventArgs e)
    {
        var showPassword = TogglePasswordButton.IsChecked == true;
        PasswordHiddenBox.Visibility = showPassword ? Visibility.Collapsed : Visibility.Visible;
        PasswordVisibleBox.Visibility = showPassword ? Visibility.Visible : Visibility.Collapsed;
        PasswordVisibleBox.Text = PasswordHiddenBox.Password;

        if (showPassword)
            PasswordVisibleBox.Focus();
        else
            PasswordHiddenBox.Focus();
    }

    private void PasswordHiddenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPasswordText)
            return;

        _syncingPasswordText = true;
        PasswordVisibleBox.Text = PasswordHiddenBox.Password;
        _syncingPasswordText = false;
    }

    private void PasswordVisibleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingPasswordText)
            return;

        _syncingPasswordText = true;
        PasswordHiddenBox.Password = PasswordVisibleBox.Text;
        _syncingPasswordText = false;
    }

    private void ServerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || DataContext is not HomeViewModel vm)
            return;

        if (ServerCombo.SelectedItem is not ComboBoxItem { Tag: LaunchMode mode })
            return;

        switch (mode)
        {
            case LaunchMode.OfficialServer:
                if (vm.SelectOfficialServerCommand.CanExecute(null))
                    vm.SelectOfficialServerCommand.Execute(null);
                break;
            case LaunchMode.Singleplayer:
                if (vm.SelectSingleplayerCommand.CanExecute(null))
                    vm.SelectSingleplayerCommand.Execute(null);
                break;
            case LaunchMode.CustomServer:
                if (vm.SelectCustomServerCommand.CanExecute(null))
                    vm.SelectCustomServerCommand.Execute(null);
                break;
        }
    }

    private void ModGuideButton_Click(object sender, RoutedEventArgs e)
    {
        var modsDir = DataContext is HomeViewModel vm
            ? vm.ModsFolderPath
            : Path.Combine(LauncherSettings.DefaultGameDir, "mods");

        var dialog = new Dialogs.ModGuideDialog(modsDir)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.ShowDialog();
    }
}
