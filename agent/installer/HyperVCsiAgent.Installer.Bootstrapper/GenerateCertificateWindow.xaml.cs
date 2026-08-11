using System;
using System.Windows;

namespace HyperVCsiAgent.Installer.Bootstrapper;

public partial class GenerateCertificateWindow : Window
{
    public string? GeneratedThumbprint { get; private set; }

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

        try
        {
            var certificate = SelfSignedCertificateGenerator.CreateAndImport(subjectName);
            GeneratedThumbprint = certificate.Thumbprint;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not generate the certificate: {ex.Message}", "Generate Self-Signed Certificate",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
