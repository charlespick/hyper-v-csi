using System;
using System.Windows;

namespace HyperVCsiAgent.Installer.Bootstrapper;

public partial class GenerateCertificateWindow : Window
{
    /// <summary>
    /// The subject name to generate a certificate for - collected here, but
    /// not acted on here. This process runs asInvoker for its whole
    /// lifetime (see the Bootstrapper csproj's own remarks), so importing
    /// into LocalMachine\My needs to happen later, during the MSI's own
    /// elevated execute sequence - see WizardViewModel.AddPendingCertificate
    /// and HyperVCsiAgent.Installer.Actions' GenerateServerCertificateCommand.
    /// </summary>
    public string? SubjectName { get; private set; }

    public GenerateCertificateWindow()
    {
        InitializeComponent();
        SubjectNameBox.Text = Environment.MachineName;
    }

    private void Generate_Click(object sender, RoutedEventArgs e)
    {
        var subjectName = SubjectNameBox.Text.Trim();
        if (subjectName.Length == 0)
        {
            MessageBox.Show(this, "Enter a subject name.", "Generate Self-Signed Certificate",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SubjectName = subjectName;
        DialogResult = true;
    }
}
