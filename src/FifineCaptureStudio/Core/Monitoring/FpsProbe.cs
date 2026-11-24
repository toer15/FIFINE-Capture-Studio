using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace FifineCaptureStudio.Core.Monitoring;

/// <summary>
/// Diagnostic harness for the real PC measurement path. It starts the same PresentMonSource the UI
/// uses against a chosen PID and writes each snapshot, so chain selection and unavailable states can
/// be checked without a game-specific test harness.
/// </summary>
public static class FpsProbe
{
    public static void Run(string[] args)
    {
        var report = new StringBuilder();
        report.AppendLine("FIFINE FPS probe");
        report.AppendLine("================");
        report.AppendLine("PresentMon path: " + (PresentMonTool.ResolvePath() ?? "not installed"));

        var pidText = args.SkipWhile(a => a != "--fps-probe").Skip(1).FirstOrDefault();
        if (!int.TryParse(pidText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
        {
            report.AppendLine("No target PID. Usage: FifineCaptureStudio.exe --fps-probe <pid>");
            Write(report);
            return;
        }

        string? name = null;
        try
        {
            using var target = Process.GetProcessById(pid);
            name = target.ProcessName;
        }
        catch (Exception ex)
        {
            report.AppendLine("Cannot open target: " + ex.Message);
        }

        report.AppendLine($"Target: {name ?? "unknown"} (PID {pid})");
        using var source = new PresentMonSource();
        var (ok, message) = source.Start(pid, name ?? "process");
        report.AppendLine($"Start: {ok} · {message}");

        for (var i = 0; i < 8; i++)
        {
            Thread.Sleep(1000);
            var reading = source.Snapshot();
            report.AppendLine(reading.Available
                ? $"{i + 1}s available fps={reading.Fps:0.0} presented={reading.PresentedFps:0.0} " +
                  $"displayed={reading.DisplayedFps:0.0} frame={reading.FrameTimeMs:0.00}ms " +
                  $"dropped={reading.DroppedFrames} chains={reading.SwapChainCount} chain={reading.SwapChain} {reading.Detail}"
                : $"{i + 1}s unavailable {reading.Detail}");
            Console.WriteLine(report.ToString().Split('\n')[^2]);
        }

        Write(report);
    }

    private static void Write(StringBuilder report)
    {
        try
        {
            File.WriteAllText(Path.Combine(AppPaths.Root, "fps-probe.txt"), report.ToString());
        }
        catch
        {
            // Diagnostic output is best-effort.
        }
    }
}
