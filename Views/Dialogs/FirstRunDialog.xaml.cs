using System.Windows;
using ArclightLauncher.ViewModels.Dialogs;

namespace ArclightLauncher.Views.Dialogs;

public partial class FirstRunDialog : Window
{
    public FirstRunDialog(FirstRunViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += result =>
        {
            DialogResult = result;
            Close();
        };
    }
}
