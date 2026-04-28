using System.Windows;
using System.Windows.Controls;

namespace ArclightLauncher.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    // ▾ 下拉按钮：打开模式 Popup
    private void DropdownBtn_Click(object sender, RoutedEventArgs e)
    {
        ModePopup.IsOpen = true;
    }

    // 任意模式选中后关闭 Popup
    private void ModeItem_Click(object sender, RoutedEventArgs e)
    {
        ModePopup.IsOpen = false;
    }
}
