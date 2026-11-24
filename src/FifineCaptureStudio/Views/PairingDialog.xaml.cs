using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FifineCaptureStudio.ViewModels;

namespace FifineCaptureStudio.Views;

public partial class PairingDialog : UserControl
{
    private readonly DispatcherTimer _metricsTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };

    public PairingDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _metricsTimer.Stop();
        DataContextChanged += (_, _) => StartAnimations();
        _metricsTimer.Tick += (_, _) =>
        {
            if (DataContext is PairingViewModel vm)
            {
                vm.RefreshLiveMetrics();
            }
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        StartAnimations();
        _metricsTimer.Start();
    }

    private void StartAnimations()
    {
        // Card fades and scales in like a device-pairing sheet.
        Card.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        // Breathing ring so the dialog never looks frozen while it works.
        var pulse = new DoubleAnimation(0.65, 0.15, TimeSpan.FromMilliseconds(1100))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        PulseRing.BeginAnimation(OpacityProperty, pulse);
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is PairingViewModel vm)
        {
            vm.CancelCommand.Execute(null);
        }
    }
}
