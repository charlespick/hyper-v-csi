using System.Windows.Controls;
using System.Windows.Input;

namespace HyperVCsiAgent.Installer.Bootstrapper.Pages;

public partial class TrustedClientsPage : UserControl
{
    public TrustedClientsPage()
    {
        InitializeComponent();
    }

    private void Add_Click(object sender, System.Windows.RoutedEventArgs e) => AddFromTextBox();

    private void NewThumbprintBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddFromTextBox();
        }
    }

    private void AddFromTextBox()
    {
        if (DataContext is not WizardViewModel viewModel)
        {
            return;
        }

        var value = NewThumbprintBox.Text.Trim();
        if (value.Length == 0)
        {
            return;
        }

        viewModel.ClientThumbprintList.Add(value);
        NewThumbprintBox.Clear();
    }

    private void Remove_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not WizardViewModel viewModel || ThumbprintList.SelectedItem is not string selected)
        {
            return;
        }

        viewModel.ClientThumbprintList.Remove(selected);
    }
}
