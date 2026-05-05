using System.Diagnostics;
using System.Drawing;
using System.Windows;
using System.Windows.Input;
using ArclightLauncher.ViewModels;

namespace ArclightLauncher;

/// <summary>
/// Main shell for the launcher. The game launch flow remains in HomeViewModel.
/// </summary>
public partial class MainWindow : Window
{
    private Window? _settingsWindow;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private bool _exiting;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        SetupTrayIcon();
        StateChanged += OnStateChanged;
        Closing += OnClosing;
    }

    private void SetupTrayIcon()
    {
        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "ArclightLauncher",
            Visible = false
        };

        // 使用程序内嵌图标，回退到系统默认
        try
        {
            using var stream = System.Reflection.Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("ArclightLauncher.app.ico");
            if (stream is not null)
                _trayIcon.Icon = new Icon(stream);
        }
        catch
        {
            // 使用系统默认图标
        }

        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("显示窗口", null, (_, _) => RestoreFromTray());
        menu.Items.Add("-");
        menu.Items.Add("退出", null, (_, _) => ExitApp());
        _trayIcon.ContextMenuStrip = menu;
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            if (_trayIcon is not null) _trayIcon.Visible = true;
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting) return;

        // 关闭窗口时最小化到托盘，不退出程序
        e.Cancel = true;
        WindowState = WindowState.Minimized;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        if (_trayIcon is not null) _trayIcon.Visible = false;
    }

    private void ExitApp()
    {
        _exiting = true;
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayIcon = null;
        }
        Application.Current.Shutdown();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => ToggleWindowState();

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void LanguageButton_Click(object sender, RoutedEventArgs e)
        => MainTabs.SelectedIndex = 2;

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow?.IsVisible == true)
        {
            _settingsWindow.Activate();
            return;
        }

        if (DataContext is not MainViewModel vm)
        {
            MainTabs.SelectedIndex = 1;
            return;
        }

        var view = new Views.SettingsView
        {
            DataContext = vm.SettingsVm
        };

        _settingsWindow = new Window
        {
            Title = "设置",
            Owner = this,
            Content = view,
            Width = 980,
            Height = 720,
            MinWidth = 860,
            MinHeight = 560,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize,
            Background = System.Windows.Media.Brushes.Transparent
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void WebsiteButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://www.minecraft.net/",
            UseShellExecute = true
        });
    }

    private void ToggleWindowState()
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
}
