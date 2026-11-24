using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.ViewModels;
using FifineCaptureStudio.Views;

namespace FifineCaptureStudio;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, UserControl> _pages = new();
    private bool _closingHandled;

    public MainWindow()
    {
        InitializeComponent();

        var vm = App.ViewModel;
        DataContext = vm;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.FullScreenRequested += ToggleFullScreen;

        RestoreWindowPlacement();
        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += (_, _) => UpdateChrome();
        LocationChanged += (_, _) => PersistWindowPlacement();
        SizeChanged += (_, _) => PersistWindowPlacement();
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
    }

    private StudioViewModel ViewModel => App.ViewModel;

    /// <summary>
    /// Puts the window back where the user left it. Saved coordinates are ignored when the monitor
    /// they referred to is gone, so the window can never open off-screen after a display change.
    /// </summary>
    private void RestoreWindowPlacement()
    {
        var ui = ViewModel.Config.Ui;

        if (ui.WindowWidth >= MinWidth && ui.WindowHeight >= MinHeight)
        {
            Width = ui.WindowWidth;
            Height = ui.WindowHeight;
        }

        if (ui.WindowLeft is { } left && ui.WindowTop is { } top && IsReachable(left, top))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        if (ui.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }
    }

    private static bool IsReachable(double left, double top)
    {
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

        // At least the title area and a slice of the window must be on a real screen.
        return left + 120 > screenLeft && left < screenRight - 120
            && top + 40 > screenTop && top < screenBottom - 40;
    }

    /// <summary>Stores the current placement; called on close and when the window is moved or resized.</summary>
    private void SaveWindowPlacement()
    {
        var ui = ViewModel.Config.Ui;
        ui.WindowMaximized = WindowState == WindowState.Maximized;

        // RestoreBounds tracks the real window rectangle even after native resizes (where Width and
        // Height keep their XAML values) and also reports the normal bounds while maximised.
        var bounds = RestoreBounds;
        if (bounds.IsEmpty || bounds.Width < MinWidth || bounds.Height < MinHeight)
        {
            if (WindowState != WindowState.Normal)
            {
                return;
            }

            bounds = new Rect(Left, Top, ActualWidth, ActualHeight);
        }

        ui.WindowLeft = bounds.Left;
        ui.WindowTop = bounds.Top;
        ui.WindowWidth = bounds.Width;
        ui.WindowHeight = bounds.Height;
    }

    private void PersistWindowPlacement()
    {
        // Destroying the window resets its bounds to the startup values; recording that would
        // overwrite the placement the user actually left behind.
        if (_closingHandled)
        {
            return;
        }

        SaveWindowPlacement();
        ViewModel.Services.Settings.SaveSoon();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ShowPageFor(ViewModel.CurrentPage);
        ViewModel.RegisterHotkeys();
        App.Services.Hotkeys.Attach(this);
        ViewModel.RegisterHotkeys();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StudioViewModel.CurrentPage))
        {
            ShowPageFor(ViewModel.CurrentPage);
        }
        else if (e.PropertyName == nameof(StudioViewModel.ShowWizard) && ViewModel.ShowWizard)
        {
            WizardOverlay.Visibility = Visibility.Visible;
        }
    }

    private void ShowPageFor(string page)
    {
        if (!_pages.TryGetValue(page, out var view))
        {
            view = CreatePage(page);
            _pages[page] = view;
        }

        PageHost.Content = view;
    }

    private UserControl CreatePage(string page) => page switch
    {
        "capture" => new LiveCaptureView(),
        "recording" => new RecordingStudioView(),
        "audio" => new AudioStudioView(),
        "sources" => new GamingSourcesView(),
        "enhance" => new EnhancementView(),
        "performance" => new PerformanceView(),
        "library" => new LibraryView(),
        "settings" => new SettingsView(),
        _ => new DashboardView()
    };

    // ---------------------------------------------------------------- chrome

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximise();
            return;
        }

        if (e.OriginalSource is DependencyObject source && FindInteractive(source))
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // drag was interrupted
        }
    }

    private static bool FindInteractive(DependencyObject source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is ButtonBase || current is ToggleButton || current is ListBoxItem)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private void Minimise_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximise_Click(object sender, RoutedEventArgs e) => ToggleMaximise();

    private void ToggleMaximise() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateChrome()
    {
        // Remove the resize gutter when maximised so the window fills the screen.
        BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
    }

    private void ToggleFullScreen()
    {
        if (PageHost.Content is not LiveCaptureView && PageHost.Content is not DashboardView)
        {
            ViewModel.CurrentPage = "capture";
            ShowPageFor("capture");
        }

        var window = new FullScreenPreviewWindow
        {
            Owner = this,
            DataContext = ViewModel
        };
        window.ShowDialog();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && ViewModel.EnhancementBadgeVisible)
        {
            // Hold to compare against the untouched signal; releasing restores the enhanced view.
            ViewModel.ShowOriginal = true;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape && ViewModel.ShowWizard)
        {
            ViewModel.CloseWizard();
            e.Handled = true;
        }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closingHandled)
        {
            return;
        }

        e.Cancel = true;
        _closingHandled = true;

        // Capture window state and any coalesced edits before shutdown tears the services down.
        try
        {
            SaveWindowPlacement();
            App.Services.Settings.Save();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not persist window placement: " + ex.Message);
        }

        try
        {
            if (App.Services.Session.IsRecording)
            {
                var path = await App.Services.Session.StopRecordingAsync();
                if (path is not null)
                {
                    MessageBox.Show(
                        "The in-progress recording was finalised before closing:\n" + path,
                        "FIFINE Capture Studio",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
            }

            await ViewModel.ShutdownAsync();
            await App.Services.DisposeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown failed", ex);
        }

        Application.Current.Shutdown();
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && ViewModel.ShowOriginal)
        {
            ViewModel.ShowOriginal = false;
            e.Handled = true;
        }
    }
}
