using System.Windows;
using System.Windows.Controls;
using FifineCaptureStudio.ViewModels;

namespace FifineCaptureStudio.Views;

public partial class OnboardingWizard : UserControl
{
    public OnboardingWizard()
    {
        InitializeComponent();
    }

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is StudioViewModel vm)
        {
            vm.CloseWizard();
        }
    }
}
