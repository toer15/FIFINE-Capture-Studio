using System.Diagnostics;
using System.IO;
using System.Text;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.FrameGeneration;

namespace FifineCaptureStudio.Rendering;

/// <summary>
/// Opens the real native presenter on the primary monitor and submits 120 synthetic frames per
/// second for a few seconds. Used to verify that the flip-model swapchain works and that the
/// presentation cadence is genuine rather than inferred from a counter.
/// </summary>
public static class PresenterSelfTest
{
    public static void Run(string[] args)
    {
        AppPaths.EnsureCreated();
        var seconds = 4;
        var index = Array.IndexOf(args, "--seconds");
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var parsed))
        {
            seconds = Math.Clamp(parsed, 2, 15);
        }

        var report = new StringBuilder();
        report.AppendLine("FIFINE native presenter self-test");
        report.AppendLine("================================");
        try
        {
            using var presenter = new NativeFramePresenter(896, 512);
            if (!presenter.Start(IntPtr.Zero))
            {
                report.AppendLine("result = FAIL: " + (presenter.Error ?? "presenter did not open"));
                Write(report);
                return;
            }

            report.AppendLine("display refresh = " + presenter.DisplayRefreshHz + " Hz (" + presenter.DisplayName + ")");
            report.AppendLine("buffer = 896x512, target cadence = 120 fps");

            var width = 896;
            var height = 512;
            var intervalTicks = Stopwatch.Frequency / 120.0;
            var start = Stopwatch.GetTimestamp();
            var frames = (int)(seconds * 120);
            for (var i = 0; i < frames; i++)
            {
                var pixels = new byte[width * height * 4];
                var shift = i * 4;
                for (var y = 0; y < height; y++)
                {
                    for (var x = 0; x < width; x++)
                    {
                        var o = (y * width + x) * 4;
                        pixels[o] = (byte)((x + shift) & 0xFF);
                        pixels[o + 1] = (byte)(y & 0xFF);
                        pixels[o + 2] = (byte)((x + y + shift) & 0xFF);
                        pixels[o + 3] = 255;
                    }
                }

                presenter.Enqueue(new ScheduledFrame
                {
                    Bgra = pixels,
                    Width = width,
                    Height = height,
                    DueTicks = start + (long)(i * intervalTicks),
                    Generated = i % 2 == 1,
                    SourceSequence = i / 2
                });

                var waitMs = (presenter.QueueAgeMs > 200 ? 0 : 2);
                if (waitMs > 0) Thread.Sleep(waitMs);
            }

            Thread.Sleep((seconds + 1) * 1000);
            var elapsed = (Stopwatch.GetTimestamp() - start) * 1.0 / Stopwatch.Frequency;
            report.AppendLine("submitted = " + frames);
            report.AppendLine("presented = " + presenter.PresentedFrames);
            report.AppendLine("missed    = " + presenter.MissedDeadlines);
            report.AppendLine("cadence   = " + presenter.PresentedFps.ToString("0.0") + " fps (windowed)");
            report.AppendLine("queue age = " + presenter.QueueAgeMs.ToString("0.0") + " ms");
            var cadence = presenter.PresentedFps;
            report.AppendLine("result = " + (presenter.PresentedFrames > seconds * 90 && cadence is > 100 and < 140 ? "PASS" : "FAIL"));
            presenter.Stop();
        }
        catch (Exception ex)
        {
            report.AppendLine("result = FAIL: " + ex.GetType().Name + ": " + ex.Message);
        }

        Write(report);
    }

    private static void Write(StringBuilder report)
    {
        Console.WriteLine(report.ToString());
        try
        {
            File.WriteAllText(Path.Combine(AppPaths.Root, "fg-present.txt"), report.ToString());
        }
        catch
        {
            // diagnostics only
        }
    }
}
