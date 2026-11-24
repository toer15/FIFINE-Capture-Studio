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

        Loaded += OnLoaded;
        Closing += OnClosing;
        StateChanged += (_, _) => UpdateChrome();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private StudioViewModel ViewModel => App.ViewModel;

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
}
