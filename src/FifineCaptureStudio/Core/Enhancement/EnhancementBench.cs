using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FifineCaptureStudio.Core.Enhancement;

/// <summary>
/// Headless benchmark used to prove the GPU pipeline actually runs on this machine.
/// It processes synthetic frames through the same engine the preview uses and reports
/// measured per-stage times instead of an estimate.
/// </summary>
public static class EnhancementBench
{
    public static void Run(string[] args)
    {
        var frames = ArgInt(args, "--enhance-bench", 180);
        var sourceWidth = ArgInt(args, "--width", 1600);
        var sourceHeight = ArgInt(args, "--height", 900);
        var target = ParseTarget(Arg(args, "--target", "1440"));

        var report = new StringBuilder();
        report.AppendLine("FIFINE enhancement benchmark");
        report.AppendLine("============================");
        report.AppendLine("date        = " + DateTime.Now.ToString("u", CultureInfo.InvariantCulture));
        report.AppendLine($"source      = {sourceWidth}x{sourceHeight}");
        report.AppendLine($"target      = {target.Width}x{target.Height}");
        report.AppendLine($"frames      = {frames}");

        try
        {
            var adapters = GpuDetection.Detect();
            if (!string.IsNullOrEmpty(GpuDetection.LastError))
            {
                report.AppendLine("detect error = " + GpuDetection.LastError);
            }
            foreach (var adapter in adapters)
            {
                report.AppendLine($"adapter[{adapter.Index}] = {adapter.Info.Name} · {adapter.Info.VramText} · AMD={adapter.Info.IsAmd}");
            }

            var primary = GpuDetection.Primary(adapters);
            if (primary is null)
            {
                report.AppendLine("result      = FAILED: no DXGI adapter found");
                Write(report);
                return;
            }

            using var engine = new EnhancementEngine(primary);
            report.AppendLine("device      = " + engine.AdapterName);

            var imagePath = Arg(args, "--image", string.Empty);
            BitmapSource source;
            if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
            {
                var loaded = new BitmapImage();
                loaded.BeginInit();
                loaded.CacheOption = BitmapCacheOption.OnLoad;
                loaded.UriSource = new Uri(imagePath, UriKind.Absolute);
                loaded.EndInit();
                loaded.Freeze();
                source = loaded.Format == PixelFormats.Bgra32
                    ? loaded
                    : new FormatConvertedBitmap(loaded, PixelFormats.Bgra32, null, 0);
                report.AppendLine($"image       = {imagePath} ({source.PixelWidth}x{source.PixelHeight} {source.Format})");
            }
            else
            {
                source = CreateSource(sourceWidth, sourceHeight);
            }
            var config = new EnhancementConfig
            {
                Enabled = true,
                Target = target.Target,
                Upscale = UpscaleStrength.High,
                Denoise = true,
                Deblock = true,
                DetailRecovery = 0.5,
                Sharpening = 0.45,
                EdgeClarity = 0.3,
                TextureEnhancement = 0.25,
                CompressionReduction = 0.4,
                Contrast = 0.12,
                Saturation = 0.1,
                Vibrance = 0.15
            };

            // Warm-up: shader compilation and first-use allocations must not pollute the numbers.
            for (var i = 0; i < 10; i++)
            {
                engine.Process(source, config);
            }

            var times = new List<double>(frames);
            var stages = new List<EnhancementTimings>(frames);
            EnhancedFrame? last = null;
            var total = Stopwatch.StartNew();
            for (var i = 0; i < frames; i++)
            {
                var frameStart = Stopwatch.GetTimestamp();
                last = engine.Process(source, config);
                times.Add((Stopwatch.GetTimestamp() - frameStart) * 1000.0 / Stopwatch.Frequency);
                if (last is not null)
                {
                    stages.Add(last.Timings);
                }
            }

            total.Stop();
            if (last is null)
            {
                report.AppendLine("result      = FAILED: engine returned no frames");
                Write(report);
                return;
            }

            times.Sort();
            var average = times.Average();
            var p50 = times[(int)(times.Count * 0.50)];
            var p95 = times[(int)(times.Count * 0.95)];
            var max = times[^1];

            report.AppendLine($"output      = {last.OutputWidth}x{last.OutputHeight}");
            report.AppendLine($"avg         = {average:0.00} ms  ({1000.0 / average:0.0} fps ceiling)");
            report.AppendLine($"p50/p95/max = {p50:0.00} / {p95:0.00} / {max:0.00} ms");
            report.AppendLine($"wall time   = {total.Elapsed.TotalSeconds:0.00} s for {frames} frames");
            report.AppendLine($"avg upload  = {stages.Average(s => s.UploadMs):0.00} ms");
            report.AppendLine($"avg denoise = {stages.Average(s => s.DenoiseMs):0.00} ms");
            report.AppendLine($"avg grade   = {stages.Average(s => s.ColourMs):0.00} ms");
            report.AppendLine($"avg upscale = {stages.Average(s => s.UpscaleMs):0.00} ms");
            report.AppendLine($"avg readback= {stages.Average(s => s.ReadbackMs):0.00} ms");
            report.AppendLine($"vram used   = {engine.QueryVramUsedMb():0.0} MB");

            // Content check: require real colour in a large share of pixels. Checking alpha alone
            // would happily pass a fully black frame, which is how a broken read went unnoticed once.
            var samples = 0L;
            var colourSamples = 0L;
            var min = 255;
            var maxPixel = 0;
            for (var i = 0; i + 3 < last.Pixels.Length; i += 61 * 4)
            {
                samples++;
                var b = last.Pixels[i];
                var g = last.Pixels[i + 1];
                var r = last.Pixels[i + 2];
                var high = Math.Max(r, Math.Max(g, b));
                var low = Math.Min(r, Math.Min(g, b));
                if (high > 16)
                {
                    colourSamples++;
                }

                min = Math.Min(min, low);
                maxPixel = Math.Max(maxPixel, high);
            }

            var colourRatio = samples == 0 ? 0 : (double)colourSamples / samples;
            report.AppendLine($"range       = {min}..{maxPixel}");
            report.AppendLine($"colour px   = {colourRatio:P0} of samples");
            var passed = colourRatio > 0.5 && maxPixel > 32;
            report.AppendLine(passed ? "result      = PASS" : "result      = FAILED: output looks empty or flat");

            if (!string.IsNullOrEmpty(imagePath))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(last.ToBitmap()));
                var outputPath = Path.Combine(AppPaths.Root, "enhance-bench-output.png");
                using (var stream = File.Create(outputPath))
                {
                    encoder.Save(stream);
                }

                report.AppendLine("saved       = " + outputPath);
            }

            Write(report);
        }
        catch (Exception ex)
        {
            report.AppendLine("result      = FAILED: " + ex.GetType().Name + ": " + ex.Message);
            report.AppendLine(ex.StackTrace ?? string.Empty);
            Write(report);
        }
    }

    private static BitmapSource CreateSource(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        var random = new Random(1234);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = y * stride + x * 4;
                // Gradient base + fine checker detail + noise, so sharpening and denoise have work to do.
                var checker = ((x / 4) + (y / 4)) % 2 == 0 ? 26 : -26;
                pixels[o + 0] = (byte)Math.Clamp(x * 255 / Math.Max(1, width) + checker, 0, 255);
                pixels[o + 1] = (byte)Math.Clamp(y * 255 / Math.Max(1, height) + checker, 0, 255);
                pixels[o + 2] = (byte)Math.Clamp(128 + checker + random.Next(-8, 8), 0, 255);
                pixels[o + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static string Arg(string[] args, string name, string fallback)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
    }

    private static int ArgInt(string[] args, string name, int fallback) =>
        int.TryParse(Arg(args, name, string.Empty), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    private static (int Width, int Height, SuperResolutionTarget Target) ParseTarget(string value) => value switch
    {
        "1080" => (1920, 1080, SuperResolutionTarget.P1080),
        "2160" or "4k" => (3840, 2160, SuperResolutionTarget.P2160),
        _ => (2560, 1440, SuperResolutionTarget.P1440)
    };

    private static void Write(StringBuilder report)
    {
        var text = report.ToString();
        try
        {
            AppPaths.EnsureCreated();
            File.WriteAllText(Path.Combine(AppPaths.Root, "enhancement-bench.txt"), text);
        }
        catch
        {
            // diagnostics only
        }

        Console.WriteLine(text);
    }
}
