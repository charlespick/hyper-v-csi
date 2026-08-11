using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace HyperVCsiAgent.Installer.Bootstrapper.Pages;

public partial class WelcomePage : UserControl
{
    public WelcomePage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadLicenseText();
    }

    private void LoadLicenseText()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("License.rtf")
            ?? throw new InvalidOperationException("License.rtf was not embedded in this assembly.");
        var range = new TextRange(LicenseText.Document.ContentStart, LicenseText.Document.ContentEnd);
        range.Load(stream, DataFormats.Rtf);
    }

    private void LicenseChoice_Changed(object sender, RoutedEventArgs e)
    {
        if (DataContext is WizardViewModel viewModel)
        {
            viewModel.LicenseAccepted = AcceptButton.IsChecked == true;
        }
    }
}
