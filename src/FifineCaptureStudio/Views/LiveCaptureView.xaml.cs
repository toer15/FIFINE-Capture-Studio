using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FifineCaptureStudio.ViewModels;

namespace FifineCaptureStudio.Views;

public partial class LiveCaptureView : UserControl
{
    private StudioViewModel? _vm;
    private readonly DispatcherTimer _fullscreenHideTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private bool _fullscreenShown;

    public LiveCaptureView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm = DataContext as StudioViewModel;
        if (_vm is null)
        {
            return;
        }

        _vm.PropertyChanged += OnChanged;
        _fullscreenHideTimer.Tick += (_, _) =>
        {
            _fullscreenHideTimer.Stop();
            RevealFullscreenButton(false);
        };
        ApplyScale();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnChanged;
            _vm = null;
        }
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StudioViewModel.PreviewScale))
        {
            ApplyScale();
        }
    }

    private void PreviewSurface_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _fullscreenHideTimer.Stop();
        RevealFullscreenButton(true);
    }

    private void PreviewSurface_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _fullscreenHideTimer.Stop();
        _fullscreenHideTimer.Start();
    }

    private void RevealFullscreenButton(bool show)
    {
        if (_fullscreenShown == show)
        {
            return;
        }

        _fullscreenShown = show;
        FullscreenButton.IsHitTestVisible = show;
        var duration = TimeSpan.FromMilliseconds(show ? 170 : 240);
        var ease = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn };
        FullscreenButton.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, duration) { EasingFunction = ease });
        FullscreenScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(show ? 1 : 0.82, duration) { EasingFunction = ease });
        FullscreenScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(show ? 1 : 0.82, duration) { EasingFunction = ease });
    }

    private void VolumeCommit(object sender, RoutedEventArgs e)
    {
        if (sender is Slider { DataContext: AudioChannelViewModel channel } slider)
        {
            channel.Volume = slider.Value;
        }
    }

    private void ApplyScale()
    {
        if (_vm is not null)
        {
            PreviewImageElement.RenderTransform = new ScaleTransform(_vm.PreviewScale, _vm.PreviewScale);
        }
    }
}
