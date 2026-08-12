using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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
    // .NET 8, no Windows Forms dependency needed for this one - but it's
    // the Vista-style IFileOpenDialog under the hood, which is hosted by
    // Explorer and crashes the whole process on Server Core (no Explorer
    // shell there). Server Core falls back to Windows Forms'
    // FolderBrowserDialog with AutoUpgradeEnabled=false, which forces the
    // old SHBrowseForFolder tree-view dialog that doesn't need Explorer.
    private void Browse(System.Func<WizardViewModel, string> getCurrent, System.Action<WizardViewModel, string> setResult)
    {
        if (DataContext is not WizardViewModel viewModel)
        {
            return;
        }

        var current = getCurrent(viewModel);

        if (IsServerCore())
        {
            var ownerHandle = Window.GetWindow(this) is { } window ? new WindowInteropHelper(window).Handle : IntPtr.Zero;
            using var legacyDialog = new System.Windows.Forms.FolderBrowserDialog
            {
                AutoUpgradeEnabled = false,
                SelectedPath = current,
            };

            if (legacyDialog.ShowDialog(new Win32Window(ownerHandle)) == System.Windows.Forms.DialogResult.OK)
            {
                setResult(viewModel, legacyDialog.SelectedPath);
            }

            return;
        }

        var dialog = new OpenFolderDialog
        {
            InitialDirectory = current,
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            setResult(viewModel, dialog.FolderName);
        }
    }

    // Registry, not a try/catch around the modern dialog: by the time
    // OpenFolderDialog crashes on Server Core it has already taken the
    // process down with it, so there's nothing left to catch. "InstallationType"
    // is the same value Microsoft's own admin tooling checks to distinguish
    // Server Core from a full-shell install.
    private static bool IsServerCore()
    {
        const string KeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        return key?.GetValue("InstallationType") is string installationType
            && installationType.Equals("Server Core", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Win32Window(IntPtr handle) : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }
}
