using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FifineCaptureStudio.ViewModels;

namespace FifineCaptureStudio.Views;

public partial class FullScreenPreviewWindow : Window
{
    private readonly DispatcherTimer _hideTimer;

    public FullScreenPreviewWindow()
    {
        InitializeComponent();

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            AnimateOverlay(show: false);
        };

        Loaded += (_, _) =>
        {
            Focus();
            Keyboard.Focus(this);
            // Show the controls once so the user knows they exist, then autohide.
            AnimateOverlay(show: true);
            _hideTimer.Start();
        };

        MouseMove += OnMouseMove;
        Cursor = Cursors.None;
    }

    private void AnimateOverlay(bool show)
    {
        var duration = TimeSpan.FromMilliseconds(show ? 160 : 260);
        var easing = new CubicEase { EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn };

        Overlay.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, duration) { EasingFunction = easing });
        Overlay.IsHitTestVisible = show;
        Cursor = show ? Cursors.Arrow : Cursors.None;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        AnimateOverlay(show: true);
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
            case Key.F:
            case Key.F11:
                Close();
                e.Handled = true;
                break;

            case Key.M:
                ToggleMute();
                e.Handled = true;
                break;
        }
    }

    private void OnRootClick(object sender, MouseButtonEventArgs e)
    {
        // A click on empty space toggles the overlay instead of exiting, like a video player.
        if (e.OriginalSource is System.Windows.Controls.Image)
        {
            AnimateOverlay(Overlay.Opacity < 0.5);
            _hideTimer.Stop();
            if (Overlay.Opacity >= 0.5)
            {
                _hideTimer.Start();
            }

            return;
        }

        if (e.ClickCount == 2)
        {
            Close();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMute_Click(object sender, RoutedEventArgs e) => ToggleMute();

    private void ToggleMute()
    {
        if (DataContext is StudioViewModel vm)
        {
            vm.ToggleMuteMicCommand.Execute(null);
        }
    }
}
