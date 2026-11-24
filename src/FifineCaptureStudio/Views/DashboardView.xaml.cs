using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FifineCaptureStudio.ViewModels;

namespace FifineCaptureStudio.Views;

public partial class DashboardView : UserControl
{
    private StudioViewModel? _vm;
    private readonly DispatcherTimer _fullscreenHideTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private bool _fullscreenShown;

    public DashboardView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        _vm = DataContext as StudioViewModel;
        if (_vm is null)
        {
            return;
        }

        _vm.PropertyChanged += OnViewModelChanged;
        _fullscreenHideTimer.Tick += OnFullscreenHideTick;
        ApplyScale();
    }

    private void OnUnloaded(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm = null;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
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

    private void VolumeCommit(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is Slider { DataContext: AudioChannelViewModel channel } slider)
        {
            channel.Volume = slider.Value;
        }
    }

    private void ApplyScale()
    {
        if (_vm is null)
        {
            return;
        }

        PreviewImageElement.RenderTransform = new ScaleTransform(_vm.PreviewScale, _vm.PreviewScale);
    }

    private void OnFullscreenHideTick(object? sender, EventArgs e)
    {
        _fullscreenHideTimer.Stop();
        RevealFullscreenButton(false);
    }
}
