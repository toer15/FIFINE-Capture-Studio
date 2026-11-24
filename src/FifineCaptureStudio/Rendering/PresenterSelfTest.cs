using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.Enhancement;
using FifineCaptureStudio.Core.FrameGeneration;

namespace FifineCaptureStudio.Rendering;

/// <summary>
/// Opens the real native presenter on the primary monitor and submits frames at a requested cadence.
/// The default path uses synthetic CPU BGRA frames; --gpu runs the actual D3D11 enhancement engine
/// and hands its keyed-mutex shared texture to the presenter, exercising the readback-free path.
/// Measurements describe what the display presented, not what the producer submitted.
/// </summary>
public static class PresenterSelfTest
{
    private sealed record RunResult(
        int Frames,
        double ElapsedSeconds,
        string Path,
        string GpuName,
        double AverageProcessMs,
        double VramMb,
        long Rejected);

    public static void Run(string[] args)
    {
        AppPaths.EnsureCreated();
        var seconds = 4;
        var targetFps = 120;
        var gpuMode = args.Contains("--gpu");
        var target = SuperResolutionTarget.P1080;
        var bufferWidth = 896;
        var bufferHeight = 512;
        var sourceWidth = 0;
        var sourceHeight = 0;
        var index = Array.IndexOf(args, "--seconds");
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var parsedSeconds))
        {
            seconds = Math.Clamp(parsedSeconds, 2, 15);
        }

        index = Array.IndexOf(args, "--fps");
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var parsedFps))
        {
            targetFps = Math.Clamp(parsedFps, 30, 480);
        }

        index = Array.IndexOf(args, "--width");
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var parsedWidth))
        {
            bufferWidth = Math.Clamp(parsedWidth, 320, 7680);
        }

        index = Array.IndexOf(args, "--height");
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var parsedHeight))
        {
            bufferHeight = Math.Clamp(parsedHeight, 240, 4320);
        }

        index = Array.IndexOf(args, "--source-width");
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var parsedSourceWidth))
        {
            sourceWidth = Math.Clamp(parsedSourceWidth, 320, 7680);
        }

        index = Array.IndexOf(args, "--source-height");
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var parsedSourceHeight))
        {
            sourceHeight = Math.Clamp(parsedSourceHeight, 240, 4320);
        }

        index = Array.IndexOf(args, "--target");
        if (index >= 0 && index + 1 < args.Length)
        {
            target = args[index + 1].ToLowerInvariant() switch
            {
                "1440" or "1440p" => SuperResolutionTarget.P1440,
                "2160" or "4k" => SuperResolutionTarget.P2160,
                _ => SuperResolutionTarget.P1080
            };
        }

        var report = new StringBuilder();
        report.AppendLine("FIFINE native presenter self-test");
        report.AppendLine("================================");
        try
        {
            using var presenter = new NativeFramePresenter(bufferWidth, bufferHeight);
            if (!presenter.Start(IntPtr.Zero))
            {
                report.AppendLine("result = FAIL: " + (presenter.Error ?? "presenter did not open"));
                Write(report);
                return;
            }

            report.AppendLine("display refresh = " + presenter.DisplayRefreshHz + " Hz (" + presenter.DisplayName + ")");
            report.AppendLine("swapchain       = " + presenter.SwapChainMode);
            report.AppendLine("present mode    = " + presenter.PresentMode);
            report.AppendLine("waitable object = " + (presenter.WaitableObjectActive ? "active, latency 1" : "unavailable"));
            report.AppendLine("path            = " + (gpuMode ? "enhancement shared texture (no CPU readback)" : "CPU BGRA compatibility"));
            report.AppendLine("buffer          = " + bufferWidth + "x" + bufferHeight);
            report.AppendLine("requested       = " + targetFps + " FPS source cadence");

            var result = gpuMode
                ? RunGpu(presenter, seconds, targetFps, target)
                : RunCpu(
                    presenter,
                    seconds,
                    targetFps,
                    sourceWidth > 0 ? sourceWidth : bufferWidth,
                    sourceHeight > 0 ? sourceHeight : bufferHeight);

            report.AppendLine("source size     = " + result.Path);
            if (!string.IsNullOrWhiteSpace(result.GpuName))
            {
                report.AppendLine("gpu             = " + result.GpuName);
            }

            var presented = presenter.PresentedFrames;
            var cadence = result.ElapsedSeconds > 0 ? presented / result.ElapsedSeconds : 0;
            report.AppendLine("submitted       = " + result.Frames + " (" + (result.Frames / Math.Max(0.001, result.ElapsedSeconds)).ToString("0.0") + " FPS)");
            report.AppendLine("presented       = " + presented + " (" + cadence.ToString("0.0") + " FPS measured)");
            report.AppendLine("display cadence = " + presenter.PresentedFps.ToString("0.0") + " FPS (0.5 s window)");
            report.AppendLine("queue dropped   = " + presenter.QueueDroppedFrames);
            report.AppendLine("late dropped    = " + presenter.LateDroppedFrames);
            report.AppendLine("dropped total   = " + presenter.DroppedFrames);
            report.AppendLine("missed budget   = " + presenter.MissedDeadlines);
            report.AppendLine("interval        = " + presenter.AveragePresentIntervalMs.ToString("0.00") + " ms avg / " +
                               presenter.P95PresentIntervalMs.ToString("0.00") + " ms p95");
            report.AppendLine("latency         = " + presenter.AveragePresentationLatencyMs.ToString("0.00") + " ms avg / " +
                               presenter.P95PresentationLatencyMs.ToString("0.00") + " ms p95");
            report.AppendLine("queue age       = " + presenter.AverageQueueAgeMs.ToString("0.00") + " ms avg");
            if (result.AverageProcessMs > 0)
            {
                report.AppendLine("process submit  = " + result.AverageProcessMs.ToString("0.00") + " ms avg");
            }

            if (result.VramMb > 0)
            {
                report.AppendLine("process vram    = " + result.VramMb.ToString("0") + " MB");
            }

            if (result.Rejected > 0)
            {
                report.AppendLine("rejected frames = " + result.Rejected);
            }

            var cadenceReasonable = cadence > 0.5
                                    && cadence <= presenter.DisplayRefreshHz + 1.5
                                    && presented > 20;
            report.AppendLine("result = " + (cadenceReasonable ? "PASS" : "FAIL") +
                               " (presented cadence is independent of submitted count)");
            presenter.Stop();
        }
        catch (Exception ex)
        {
            report.AppendLine("result = FAIL: " + ex.GetType().Name + ": " + ex.Message);
        }

        Write(report);
    }

    private static RunResult RunCpu(
        NativeFramePresenter presenter,
        int seconds,
        int targetFps,
        int width,
        int height)
    {
        var intervalTicks = Stopwatch.Frequency / (double)targetFps;
        var start = Stopwatch.GetTimestamp();
        var end = start + (long)(seconds * Stopwatch.Frequency);
        var frames = 0;
        long sequence = 0;

        while (Stopwatch.GetTimestamp() < end)
        {
            var due = start + (long)(frames * intervalTicks);
            WaitUntil(due);

            var pixels = new byte[width * height * 4];
            var shift = frames * 4;
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
                DueTicks = due,
                Generated = false,
                SourceSequence = sequence++
            });
            frames++;
            LimitProducer(presenter, end);
        }

        Drain(presenter);
        return new RunResult(frames, Elapsed(start), $"{width}x{height}", string.Empty, 0, 0, 0);
    }

    private static RunResult RunGpu(
        NativeFramePresenter presenter,
        int seconds,
        int targetFps,
        SuperResolutionTarget target)
    {
        var primary = GpuDetection.Primary(GpuDetection.Detect())
                      ?? throw new InvalidOperationException("No Direct3D 11 adapter found.");
        using var engine = new EnhancementEngine(primary);
        var source = CreateSource(1920, 1080);
        var config = new EnhancementConfig
        {
            Enabled = true,
            Target = target,
            FullscreenEnhanced = true,
            PreviewEnhanced = true
        };

        var (outWidth, outHeight) = EnhancementConfig.Resolution(target);
        var intervalTicks = Stopwatch.Frequency / (double)targetFps;
        var start = Stopwatch.GetTimestamp();
        var end = start + (long)(seconds * Stopwatch.Frequency);
        var frames = 0;
        var rejected = 0;
        var processTicks = 0L;
        long sequence = 0;

        while (Stopwatch.GetTimestamp() < end)
        {
            var due = start + (long)(frames * intervalTicks);
            WaitUntil(due);

            var arrival = Stopwatch.GetTimestamp();
            var before = Stopwatch.GetTimestamp();
            var gpu = engine.ProcessShared(source, config, sequence, arrival, due);
            processTicks += Stopwatch.GetTimestamp() - before;
            if (gpu is not null)
            {
                presenter.EnqueueGpu(new GpuScheduledFrame(
                    gpu.SharedHandle,
                    gpu.OutputWidth,
                    gpu.OutputHeight,
                    due,
                    false,
                    gpu.SourceSequence,
                    gpu.ArrivalTicks));
                frames++;
                sequence++;
            }
            else
            {
                rejected++;
            }

            LimitProducer(presenter, end);
        }

        Drain(presenter);
        var averageProcess = frames > 0
            ? processTicks * 1000.0 / Stopwatch.Frequency / frames
            : 0;
        return new RunResult(
            frames,
            Elapsed(start),
            $"1920x1080 -> {outWidth}x{outHeight} ({target})",
            engine.AdapterName,
            averageProcess,
            engine.QueryVramUsedMb(),
            rejected);
    }

    private static BitmapSource CreateSource(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                pixels[o] = (byte)((x * 255) / width);
                pixels[o + 1] = (byte)((y * 255) / height);
                pixels[o + 2] = (byte)(((x + y) * 255) / Math.Max(1, width + height));
                pixels[o + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static void LimitProducer(NativeFramePresenter presenter, long end)
    {
        while (presenter.SubmittedFrames - presenter.PresentedFrames > 6
               && Stopwatch.GetTimestamp() < end)
        {
            Thread.Sleep(1);
        }
    }

    private static void Drain(NativeFramePresenter presenter)
    {
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 2;
        while (presenter.SubmittedFrames > presenter.PresentedFrames
               && Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Sleep(5);
        }
    }

    private static double Elapsed(long start) =>
        (Stopwatch.GetTimestamp() - start) * 1.0 / Stopwatch.Frequency;

    private static void WaitUntil(long dueTicks)
    {
        while (true)
        {
            var remainingMs = (dueTicks - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
            if (remainingMs <= 0)
            {
                return;
            }

            if (remainingMs > 1.5)
            {
                Thread.Sleep((int)Math.Clamp(remainingMs - 0.5, 1, 4));
            }
            else
            {
                Thread.SpinWait(80);
            }
        }
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
