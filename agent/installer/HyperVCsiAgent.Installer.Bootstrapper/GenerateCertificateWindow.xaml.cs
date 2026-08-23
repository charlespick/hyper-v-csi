using System;
using System.ComponentModel;
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
        Closing += GenerateCertificateWindow_Closing;
    }

    // Catches the Cancel button (IsCancel="True"), Escape, and the native
    // title-bar close button in one place, rather than a Cancel_Click
    // handler that only covers the button - DialogResult is already true by
    // the time this runs for a successful Generate_Click, so only the
    // discard paths ever prompt.
    private void GenerateCertificateWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (DialogResult == true)
        {
            return;
        }

        var result = MessageBox.Show(this, "Discard this certificate request?", "Generate Self-Signed Certificate",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes)
        {
            e.Cancel = true;
        }
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
