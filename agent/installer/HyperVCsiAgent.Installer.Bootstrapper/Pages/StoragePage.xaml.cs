using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace HyperVCsiAgent.Installer.Bootstrapper.Pages;

public partial class StoragePage : UserControl
{
    public StoragePage()
    {
        InitializeComponent();
    }

    private void BrowseVolumes_Click(object sender, RoutedEventArgs e) => Browse(vm => vm.CsvVolumesRoot, (vm, path) => vm.CsvVolumesRoot = path);

    private void BrowseSnapshots_Click(object sender, RoutedEventArgs e) => Browse(vm => vm.CsvSnapshotsRoot, (vm, path) => vm.CsvSnapshotsRoot = path);

    // Microsoft.Win32.OpenFolderDialog - WPF's own folder picker since
    // .NET 8, no Windows Forms dependency needed for this one.
    private void Browse(System.Func<WizardViewModel, string> getCurrent, System.Action<WizardViewModel, string> setResult)
    {
        if (DataContext is not WizardViewModel viewModel)
        {
            return;
        }

        var dialog = new OpenFolderDialog
        {
            InitialDirectory = getCurrent(viewModel),
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            setResult(viewModel, dialog.FolderName);
        }
    }
}
