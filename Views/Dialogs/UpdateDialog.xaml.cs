using System.Windows;
using ArclightLauncher.ViewModels.Dialogs;

namespace ArclightLauncher.Views.Dialogs;

public partial class UpdateDialog : Window
{
    public UpdateDialog(UpdateViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.CloseRequested += _ => Close();
    }
}
