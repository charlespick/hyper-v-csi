using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Tulpep.ActiveDirectoryObjectPicker;

namespace HyperVCsiAgent.Installer.Bootstrapper.Pages;

public partial class ServiceAccountPage : UserControl
{
    public ServiceAccountPage()
    {
        InitializeComponent();
    }

    // PasswordBox.Password is deliberately not a dependency property (WPF
    // avoids leaving plaintext passwords sitting in the binding/undo
    // infrastructure), so it has to be pushed to the view model by hand.
    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is WizardViewModel viewModel)
        {
            viewModel.ServicePassword = PasswordBox.Password;
        }
    }

    // The same "Select User, Computer, Service Account, or Group" dialog
    // ADUC itself uses (objsel.dll's IDsObjectPicker, wrapped by this
    // package rather than hand-rolled COM interop). WinNT provider forces
    // Path back as WinNT://DOMAIN/user, which is what SERVICEACCOUNT (and
    // ServiceInstall's own Account attribute in the MSI) both expect as
    // DOMAIN\user.
    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WizardViewModel viewModel)
        {
            return;
        }

        // Same object types ADUC's own "Select User, Computer, Service
        // Account, or Group" dialog offers - a service can run as any of
        // these, not just an ordinary user account.
        const ObjectTypes AllowedTypes =
            ObjectTypes.Users | ObjectTypes.Computers | ObjectTypes.ServiceAccounts | ObjectTypes.Groups;

        using var picker = new DirectoryObjectPickerDialog
        {
            AllowedObjectTypes = AllowedTypes,
            DefaultObjectTypes = AllowedTypes,
            AllowedLocations = Locations.All,
            DefaultLocations = Locations.JoinedDomain,
            MultiSelect = false,
            Providers = ADsPathsProviders.WinNT,
        };

        // WindowInteropHelper's own constructor throws on a null Window
        // rather than tolerating one, so this page not being parented (it
        // always is in practice - MainWindow is the only host - but
        // nothing enforces that) falls back to no owner instead of crashing
        // the click handler.
        var ownerHandle = Window.GetWindow(this) is { } window ? new WindowInteropHelper(window).Handle : IntPtr.Zero;
        var owner = new Win32Window(ownerHandle);
        if (picker.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK || picker.SelectedObject is not { } selected)
        {
            return;
        }

        const string WinNtPrefix = "WinNT://";
        var path = selected.Path.StartsWith(WinNtPrefix, StringComparison.OrdinalIgnoreCase)
            ? selected.Path[WinNtPrefix.Length..]
            : selected.Path;
        viewModel.ServiceAccount = path.Replace('/', '\\');
    }

    private sealed class Win32Window(IntPtr handle) : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }
}
