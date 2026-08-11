using System.Windows.Controls;

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
    private void PasswordBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is WizardViewModel viewModel)
        {
            viewModel.ServicePassword = PasswordBox.Password;
        }
    }
}
