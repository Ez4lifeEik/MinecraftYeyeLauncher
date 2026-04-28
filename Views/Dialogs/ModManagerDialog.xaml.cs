using System.Windows;
using ArclightLauncher.ViewModels.Dialogs;

namespace ArclightLauncher.Views.Dialogs;

public partial class ModManagerDialog : Window
{
    public ModManagerDialog(ModManagerViewModel vm)
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
