using System.Windows;
using System.Windows.Controls;
using FifineCaptureStudio.ViewModels;

namespace FifineCaptureStudio.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void ReopenWizard_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is StudioViewModel vm)
        {
            vm.ReopenWizard();
        }
    }
}
