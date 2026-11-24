using System.Windows;
using System.Windows.Controls;
using FifineCaptureStudio.ViewModels;

namespace FifineCaptureStudio.Views;

public partial class AudioStudioView : UserControl
{
    public AudioStudioView()
    {
        InitializeComponent();
    }

    /// <summary>Pushes the slider value only when the user finished a gesture, so layout never zeroes a stored level.</summary>
    private void VolumeCommit(object sender, RoutedEventArgs e)
    {
        if (sender is Slider { DataContext: AudioChannelViewModel channel } slider)
        {
            channel.Volume = slider.Value;
        }
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AudioChannelViewModel channel })
        {
            channel.Muted = !channel.Muted;
        }
    }
}
