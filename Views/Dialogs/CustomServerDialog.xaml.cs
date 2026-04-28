using System.Windows;
using ArclightLauncher.ViewModels.Dialogs;

namespace ArclightLauncher.Views.Dialogs;

public partial class CustomServerDialog : Window
{
    public CustomServerDialog(CustomServerViewModel vm)
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
