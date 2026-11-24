using System.Windows;
using System.Windows.Threading;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.ViewModels;

namespace FifineCaptureStudio;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;
    public static StudioViewModel ViewModel { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        Log.Init();
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled domain exception", args.ExceptionObject as Exception);

        Services = new AppServices();
        ViewModel = new StudioViewModel(Services);

        if (e.Args.Contains("--smoke"))
        {
            RunSmokeTest();
            return;
        }

        if (e.Args.Contains("--pairing-test"))
        {
            // Protocol self-test against a mock agent on loopback (no console, no credentials).
            Core.Pairing.PairingSelfTest.RunAsync();
            Shutdown(0);
            return;
        }

        base.OnStartup(e);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        _ = InitialiseAsync();
    }

    /// <summary>Loads every page in isolation so XAML problems surface with the failing view named.</summary>
    private void RunSmokeTest()
    {
        var results = new System.Text.StringBuilder();
        foreach (var factory in new (string Name, Func<FrameworkElement> Create)[]
                 {
                     ("Dashboard", () => new Views.DashboardView()),
                     ("LiveCapture", () => new Views.LiveCaptureView()),
                     ("RecordingStudio", () => new Views.RecordingStudioView()),
                     ("AudioStudio", () => new Views.AudioStudioView()),
                     ("GamingSources", () => new Views.GamingSourcesView()),
                     ("GamingEnhancement", () => new Views.EnhancementView()),
                     ("Performance", () => new Views.PerformanceView()),
                     ("Library", () => new Views.LibraryView()),
                     ("Settings", () => new Views.SettingsView()),
                     ("Wizard", () => new Views.OnboardingWizard()),
                     ("PairingDialog", () =>
                     {
                         ViewModel.OpenPairing(Core.Pairing.PairingPlatform.PlayStation);
                         var dialog = new Views.PairingDialog();
                         ViewModel.ClosePairing();
                         return dialog;
                     })
                 })
        {
            try
            {
                var view = factory.Create();
                view.DataContext = ViewModel;
                view.Measure(new Size(1400, 800));
                view.Arrange(new Rect(0, 0, 1400, 800));
                results.AppendLine("OK   " + factory.Name);
            }
            catch (Exception ex)
            {
                results.AppendLine("FAIL " + factory.Name + " :: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        var path = System.IO.Path.Combine(AppPaths.Root, "smoke.txt");
        System.IO.Directory.CreateDirectory(AppPaths.Root);
        System.IO.File.WriteAllText(path, results.ToString());
        Shutdown(0);
    }

    private async Task InitialiseAsync()
    {
        try
        {
            await Services.InitializeAsync();
            await ViewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed", ex);
            MessageBox.Show(
                "FIFINE Capture Studio could not complete start-up:\n\n" + ex.Message,
                "FIFINE Capture Studio",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        e.Handled = true;
        try
        {
            ViewModel?.Notify(e.Exception.Message, true);
        }
        catch
        {
            // UI may already be gone
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Services?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(12));
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown failed", ex);
        }

        base.OnExit(e);
    }
}
