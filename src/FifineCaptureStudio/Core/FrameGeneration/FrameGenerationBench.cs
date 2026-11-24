using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using FifineCaptureStudio.Core.Enhancement;

namespace FifineCaptureStudio.Core.FrameGeneration;

/// <summary>
/// Headless RIFE benchmark. It runs the real ONNX model on the real GPU with synthetic motion that
/// contains edges, a HUD-like rectangle and fine checker detail, and verifies that the generated
/// frame differs from both source frames and from a 50/50 cross-fade.
/// </summary>
public static class FrameGenerationBench
{
    public static void Run(string[] args)
    {
        AppPaths.EnsureCreated();
        var frames = ArgInt(args, "--iters", 30);
        var width = ArgInt(args, "--width", 1024);
        var height = ArgInt(args, "--height", 576);
        var model = Arg(args, "--model", string.Empty);
        if (string.IsNullOrWhiteSpace(model))
        {
            model = RifeModel.ResolvePath() ?? string.Empty;
        }

        var report = new StringBuilder();
        report.AppendLine("FIFINE frame-generation benchmark");
        report.AppendLine("=================================");
        report.AppendLine("date        = " + DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture));
        report.AppendLine("model       = " + RifeModel.Describe());
        report.AppendLine("source      = " + RifeModel.SourceUrl);
        report.AppendLine("licence     = " + RifeModel.License);

        try
        {
            if (string.IsNullOrWhiteSpace(model) || !File.Exists(model))
            {
                report.AppendLine("result      = FAIL: model missing. Open Gaming Enhancement and install the model.");
                Write(report);
                return;
            }

            if (!RifeModel.Verify(model, out var verification))
            {
                report.AppendLine("result      = FAIL: " + verification);
                Write(report);
                return;
            }

            report.AppendLine("verify      = " + verification);
            report.AppendLine("file        = " + model);
            report.AppendLine("input       = " + width + "x" + height);

            using var engine = new RifeOnnxEngine(model, width, height);
            report.AppendLine("provider    = " + engine.Provider);
            report.AppendLine("gpu         = " + GpuDetection.Primary(GpuDetection.Detect())?.Info.Name);
            report.AppendLine("placement   = " + engine.PlacementReport);
            report.AppendLine("first run   = " + engine.FirstRunMs.ToString("0.0") + " ms (DirectML compile)");

            var bytes = width * height * 4;
            var frame0 = new byte[bytes];
            var frame1 = new byte[bytes];
            var output = new byte[bytes];
            var crossfade = new byte[bytes];

            var inference = new List<double>();
            var total = new List<double>();
            var diff0 = 0.0;
            var diff1 = 0.0;
            var diffFade = 0.0;

            for (var i = 0; i < frames + 2; i++)
            {
                DrawScene(frame0, width, height, i * 3);
                DrawScene(frame1, width, height, i * 3 + 9);

                if (!engine.TryInterpolate(frame0, frame1, output, out var run))
                {
                    report.AppendLine("result      = FAIL: interpolation rejected the buffers.");
                    Write(report);
                    return;
                }

                if (i < 2)
                {
                    continue;
                }

                inference.Add(run.InferenceMs);
                total.Add(run.TotalMs);
                diff0 += MeanDifference(output, frame0);
                diff1 += MeanDifference(output, frame1);
                for (var p = 0; p < bytes; p++)
                {
                    crossfade[p] = (byte)((frame0[p] + frame1[p]) / 2);
                }

                diffFade += MeanDifference(output, crossfade);
                if (i == frames + 1)
                {
                    SavePng(frame0, width, height, "fg-bench-frame0.png");
                    SavePng(frame1, width, height, "fg-bench-frame1.png");
                    SavePng(output, width, height, "fg-bench-generated.png");
                }
            }

            inference.Sort();
            total.Sort();
            diff0 /= frames;
            diff1 /= frames;
            diffFade /= frames;

            report.AppendLine("frames      = " + frames);
            report.AppendLine("inference   = avg " + Avg(inference).ToString("0.00") + " ms  p50 " +
                              Pct(inference, 0.50).ToString("0.00") + "  p95 " +
                              Pct(inference, 0.95).ToString("0.00") + "  max " +
                              inference[^1].ToString("0.00"));
            report.AppendLine("total       = avg " + Avg(total).ToString("0.00") + " ms  p95 " +
                              Pct(total, 0.95).ToString("0.00"));
            report.AppendLine("ceiling     = " + (1000.0 / Avg(total)).ToString("0.0") + " generated fps");
            report.AppendLine("preprocess  = " + engine.LastPreprocessMs.ToString("0.00") + " ms");
            report.AppendLine("postprocess = " + engine.LastPostprocessMs.ToString("0.00") + " ms");
            report.AppendLine("diff f0     = " + diff0.ToString("0.00") + " levels (must be > 0)");
            report.AppendLine("diff f1     = " + diff1.ToString("0.00") + " levels (must be > 0)");
            report.AppendLine("diff fade   = " + diffFade.ToString("0.00") + " levels (must be > 0, proves not a cross-fade)");
            report.AppendLine("placement   = " + engine.PlacementReport);

            var ok = diff0 > 0.5 && diff1 > 0.5 && diffFade > 0.5 && engine.GpuVerified;
            report.AppendLine("result      = " + (ok ? "PASS" : "FAIL"));
        }
        catch (Exception ex)
        {
            report.AppendLine("result      = FAIL: " + ex.GetType().Name + ": " + ex.Message);
            report.AppendLine(ex.StackTrace);
        }

        Write(report);
    }

    private static void DrawScene(byte[] bgra, int width, int height, int shift)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var sx = (x + shift) % width;
                byte b = (byte)((sx * 255) / width);
                byte g = (byte)((y * 255) / height);
                byte r = (byte)(128 + 100 * Math.Sin((sx + y) * 0.02));

                // Fine checker detail (text-like high contrast) and a static HUD block.
                if (((sx / 4) + (y / 4)) % 2 == 0 && y > height / 3 && y < height / 2)
                {
                    b = 235;
                    g = 235;
                    r = 235;
                }

                if (x > width * 3 / 4 && y < height / 6)
                {
                    b = 30;
                    g = 210;
                    r = 60;
                }

                bgra[offset] = b;
                bgra[offset + 1] = g;
                bgra[offset + 2] = r;
                bgra[offset + 3] = 255;
            }
        }
    }

    private static double MeanDifference(byte[] a, byte[] b)
    {
        long sum = 0;
        var count = 0;
        for (var i = 0; i < a.Length; i += 4)
        {
            sum += Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);
            count += 3;
        }

        return sum / (double)Math.Max(1, count);
    }

    private static void SavePng(byte[] bgra, int width, int height, string name)
    {
        try
        {
            var bitmap = System.Windows.Media.Imaging.BitmapSource.Create(
                width, height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, bgra, width * 4);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(AppPaths.Root, name));
            encoder.Save(stream);
        }
        catch
        {
            // Diagnostic images only.
        }
    }

    private static void Write(StringBuilder report)
    {
        try
        {
            Debug.WriteLine(report.ToString());
            File.WriteAllText(Path.Combine(AppPaths.Root, "fg-bench.txt"), report.ToString());
        }
        catch
        {
            // Diagnostics only.
        }
    }

    private static double Avg(List<double> values) => values.Count == 0 ? 0 : values.Average();

    private static double Pct(List<double> sorted, double fraction)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var index = (int)Math.Round((sorted.Count - 1) * fraction);
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
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
}
