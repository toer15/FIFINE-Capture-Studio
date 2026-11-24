using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Velopack;

namespace FifineCaptureStudio;

/// <summary>
/// Velopack requires its hook handler to run before any other application code, so the WPF
/// application is started from here instead of the generated entry point.
/// </summary>
public static class Program
{
    private const string InstanceMutexName = @"Local\FifineCaptureStudio.SingleInstance";
    private const string ActivationEventName = @"Local\FifineCaptureStudio.Activate";

    [STAThread]
    public static void Main(string[] args)
    {
        // Handles install / update / uninstall fast-exit hooks and then returns for a normal launch.
        VelopackApp.Build().Run();

        if (args.Contains("--enhance-bench"))
        {
            Core.Enhancement.EnhancementBench.Run(args);
            return;
        }

        if (args.Contains("--fg-bench"))
        {
            Core.FrameGeneration.FrameGenerationBench.Run(args);
            return;
        }

        if (args.Contains("--fps-probe"))
        {
            Core.Monitoring.FpsProbe.Run(args);
            return;
        }

        if (args.Contains("--fg-present-test"))
        {
            Rendering.PresenterSelfTest.Run(args);
            return;
        }

        if (args.Contains("--update-test"))
        {
            var source = args.SkipWhile(a => a != "--update-test").Skip(1).FirstOrDefault();
            RunUpdateTestAsync(source).GetAwaiter().GetResult();
            return;
        }

        // Keep one normal app process per Windows session. The event is created before the mutex
        // so an early second launch can always wake the first one instead of disappearing silently.
        using var activation = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        using var mutex = new Mutex(true, InstanceMutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            try
            {
                activation.Set();
            }
            catch
            {
                // The first instance is already shutting down.
            }

            return;
        }

        try
        {
            var app = new App();
            app.InitializeComponent();
            StartActivationListener(activation);
            app.Run();
        }
        finally
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch
            {
                // The process is exiting; Windows will release the mutex.
            }
        }
    }

    private static void StartActivationListener(EventWaitHandle activation)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    activation.WaitOne();
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception)
                {
                    return;
                }

                var app = Application.Current;
                if (app is null)
                {
                    continue;
                }

                try
                {
                    app.Dispatcher.BeginInvoke(
                        DispatcherPriority.ApplicationIdle,
                        new Action(() => BringExistingWindowToFront(app.MainWindow)));
                }
                catch (InvalidOperationException)
                {
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "FIFINE Capture Studio activation listener"
        };

        thread.Start();
    }

    private static void BringExistingWindowToFront(Window? window)
    {
        if (window is null)
        {
            return;
        }

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        // Briefly asserting topmost lets Windows bring the existing window forward when another
        // application currently owns the foreground, without leaving the window pinned on top.
        window.Topmost = true;
        window.Topmost = false;
        window.Focus();
    }

    /// <summary>
    /// Diagnostic harness: checks a local or GitHub feed from inside the installed app, downloads,
    /// then applies the update and restarts. Writes update-test.txt so the result can be inspected.
    /// This is the same UpdateManager path the UI uses.
    /// </summary>
    private static async Task RunUpdateTestAsync(string? sourceDirectory)
    {
        var report = new StringBuilder();
        report.AppendLine("FIFINE update self-test");
        report.AppendLine("=======================");

        try
        {
            UpdateManager manager = string.IsNullOrWhiteSpace(sourceDirectory)
                ? new UpdateManager(new Velopack.Sources.GithubSource(
                    "https://github.com/" + (typeof(Program).Assembly
                        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                        .Cast<System.Reflection.AssemblyMetadataAttribute>()
                        .FirstOrDefault(a => a.Key == "GitHubRepository")?.Value ?? string.Empty), null, false))
                : new UpdateManager(sourceDirectory);

            report.AppendLine($"source      = {(string.IsNullOrWhiteSpace(sourceDirectory) ? "GitHub Releases" : sourceDirectory)}");
            report.AppendLine($"installed   = {manager.IsInstalled}");
            report.AppendLine($"current     = {manager.CurrentVersion}");

            var info = await manager.CheckForUpdatesAsync();
            if (info is null)
            {
                report.AppendLine("result      = no update available");
                Write(report);
                return;
            }

            report.AppendLine($"target      = {info.TargetFullRelease.Version}");
            report.AppendLine($"sha256      = {info.TargetFullRelease.SHA256}");

            var last = -1;
            await manager.DownloadUpdatesAsync(info, percent =>
            {
                if (percent / 10 != last / 10)
                {
                    last = percent;
                    report.AppendLine($"download    = {percent}%");
                }
            });

            report.AppendLine("download    = complete");
            Write(report);

            report.AppendLine("applying and restarting");
            Write(report);
            manager.ApplyUpdatesAndRestart(info.TargetFullRelease);
        }
        catch (Exception ex)
        {
            report.AppendLine("FAILED      = " + ex.GetType().Name + ": " + ex.Message);
            Write(report);
        }
    }

    private static void Write(StringBuilder report)
    {
        try
        {
            var dir = Core.AppPaths.Root;
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "update-test.txt"), report.ToString());
        }
        catch
        {
            // Diagnostics only.
        }
    }
}
