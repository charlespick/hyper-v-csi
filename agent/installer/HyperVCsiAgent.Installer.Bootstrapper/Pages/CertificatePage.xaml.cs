using System.Windows;
using System.Windows.Controls;

namespace HyperVCsiAgent.Installer.Bootstrapper.Pages;

public partial class CertificatePage : UserControl
{
    public CertificatePage()
    {
        InitializeComponent();
    }

    private void GenerateCertificate_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not WizardViewModel viewModel)
        {
            return;
        }

        var dialog = new GenerateCertificateWindow { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true && dialog.GeneratedThumbprint is { } thumbprint)
        {
            viewModel.RefreshCertificates();
            viewModel.ServerCertThumbprint = thumbprint;
        }
    }
}
