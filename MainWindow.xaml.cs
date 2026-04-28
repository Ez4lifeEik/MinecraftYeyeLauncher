using System.Windows;
using ArclightLauncher.ViewModels;

namespace ArclightLauncher;

/// <summary>
/// 主窗口，ViewModel 通过 DI 注入
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}