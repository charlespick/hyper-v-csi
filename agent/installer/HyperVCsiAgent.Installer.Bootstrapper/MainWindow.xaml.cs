using System.ComponentModel;
using System.Windows;

namespace HyperVCsiAgent.Installer.Bootstrapper;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Closing += MainWindow_Closing;
    }

    // The wizard's own Cancel button already confirms before backing out of
    // setup (WizardViewModel.CancelCommand) - the native title-bar close
    // button and Alt+F4 bypass that entirely otherwise, since neither goes
    // through any bound Command.
    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (DataContext is WizardViewModel viewModel && !viewModel.ConfirmCloseFromWindowChrome())
        {
            e.Cancel = true;
        }
    }
}
