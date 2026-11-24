using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace FifineCaptureStudio.Core.FrameGeneration;

public sealed record RifeRunResult(
    float[] Pixels,          // RGB float, planar NCHW
    int Width,
    int Height,
    double InferenceMs,
    double PreprocessMs,
    double PostprocessMs,
    double TotalMs);

/// <summary>
/// Persistent local RIFE inference through ONNX Runtime's DirectML execution provider.
/// One session and one timestep tensor live for the lifetime of the engine; frames are passed as
/// tensors and never written to disk. DirectML dispatch is asynchronous, so every timed run waits
/// for the output before it returns — otherwise the benchmark would report impossible numbers.
/// </summary>
public sealed class RifeOnnxEngine : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName0;
    private readonly string _inputName1;
    private readonly string _inputNameT;
    private readonly string _outputName;
    private readonly DenseTensor<float> _timestep;
    private readonly DenseTensor<float> _img0;
    private readonly DenseTensor<float> _img1;
    private readonly float[] _rgb0;
    private readonly float[] _rgb1;

    public int Width { get; }
    public int Height { get; }
    public string Provider { get; }
    public string ModelPath { get; }
    public string ModelName => RifeModel.DisplayName;
    public bool GpuVerified { get; private set; }
    public string PlacementReport { get; private set; } = "not checked";
    public long InferenceCount { get; private set; }
    public double LastInferenceMs { get; private set; }
    public double LastPreprocessMs { get; private set; }
    public double LastPostprocessMs { get; private set; }

    public RifeOnnxEngine(string modelPath, int width, int height, int deviceId = 0, bool profileOnFirstRun = true)
    {
        if (width % RifeModel.InputMultiple != 0 || height % RifeModel.InputMultiple != 0)
        {
            throw new ArgumentException($"RIFE needs dimensions that are multiples of {RifeModel.InputMultiple}.");
        }

        if (!RifeModel.Verify(modelPath, out var verification))
        {
            throw new InvalidOperationException(verification);
        }

        ModelPath = modelPath;
        Width = width;
        Height = height;

        var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            // DirectML keeps its own allocations; ORT's arena planner is counter-productive here.
            EnableMemoryPattern = false,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_WARNING,
            EnableProfiling = profileOnFirstRun
        };

        if (profileOnFirstRun)
        {
            options.ProfileOutputPathPrefix = Path.Combine(AppPaths.CacheDir, "rife-profile");
        }

        options.AppendExecutionProvider_DML(deviceId);
        Provider = "DmlExecutionProvider";

        _session = new InferenceSession(modelPath, options);
        var inputs = _session.InputMetadata.Keys.ToArray();
        _outputName = _session.OutputMetadata.Keys.First();
        _inputName0 = inputs.Contains("img0") ? "img0" : inputs[0];
        _inputName1 = inputs.Contains("img1") ? "img1" : inputs[1];
        _inputNameT = inputs.Contains("timestep") ? "timestep" : inputs[2];

        _img0 = new DenseTensor<float>(new[] { 1, 3, Height, Width });
        _img1 = new DenseTensor<float>(new[] { 1, 3, Height, Width });
        _timestep = new DenseTensor<float>(new[] { 1, 1, 1, 1 });
        _timestep.Buffer.Span.Fill(0.5f);
        _rgb0 = new float[3 * Width * Height];
        _rgb1 = new float[3 * Width * Height];

        // One warm-up run also triggers DirectML's per-shape shader/metacommand compilation.
        var sw = Stopwatch.StartNew();
        Warmup();
        sw.Stop();
        FirstRunMs = sw.Elapsed.TotalMilliseconds;

        if (profileOnFirstRun)
        {
            CheckPlacement();
        }
    }

    public double FirstRunMs { get; }

    /// <summary>
    /// Runs one interpolation. Inputs are packed BGRA rows at the engine size; output is packed BGRA.
    /// Returns false when the buffers do not match the engine geometry.
    /// </summary>
    public bool TryInterpolate(ReadOnlySpan<byte> bgra0, ReadOnlySpan<byte> bgra1, Span<byte> destinationBgra, out RifeRunResult result)
    {
        result = default!;
        var expected = Width * Height * 4;
        if (bgra0.Length < expected || bgra1.Length < expected || destinationBgra.Length < expected)
        {
            return false;
        }

        var preprocess = Stopwatch.StartNew();
        PackBgraToRgbPlanar(bgra0, _rgb0);
        PackBgraToRgbPlanar(bgra1, _rgb1);
        _rgb0.CopyTo(_img0.Buffer);
        _rgb1.CopyTo(_img1.Buffer);
        preprocess.Stop();

        var inference = Stopwatch.StartNew();
        float[] output;
        using (var results = _session.Run(
                   new[]
                   {
                       NamedOnnxValue.CreateFromTensor(_inputName0, _img0),
                       NamedOnnxValue.CreateFromTensor(_inputName1, _img1),
                       NamedOnnxValue.CreateFromTensor(_inputNameT, _timestep)
                   },
                   new[] { _outputName }))
        {
            output = results.First().AsTensor<float>().ToArray();
        }

        inference.Stop();

        var postprocess = Stopwatch.StartNew();
        UnpackRgbPlanarToBgra(output, destinationBgra);
        postprocess.Stop();

        LastInferenceMs = inference.Elapsed.TotalMilliseconds;
        LastPreprocessMs = preprocess.Elapsed.TotalMilliseconds;
        LastPostprocessMs = postprocess.Elapsed.TotalMilliseconds;
        InferenceCount++;
        result = new RifeRunResult(output, Width, Height, LastInferenceMs, LastPreprocessMs, LastPostprocessMs,
            LastInferenceMs + LastPreprocessMs + LastPostprocessMs);
        return true;
    }

    private void Warmup()
    {
        // Warm-up with neutral content: the network needs real structure for meaningful output, but
        // the warm-up only exists to force DirectML to compile the graph for this exact shape.
        for (var i = 0; i < _rgb0.Length; i++)
        {
            _rgb0[i] = 0.25f;
            _rgb1[i] = 0.25f;
        }

        _rgb0.CopyTo(_img0.Buffer);
        _rgb1.CopyTo(_img1.Buffer);
        using var _warmupResult = _session.Run(
            new[]
            {
                NamedOnnxValue.CreateFromTensor(_inputName0, _img0),
                NamedOnnxValue.CreateFromTensor(_inputName1, _img1),
                NamedOnnxValue.CreateFromTensor(_inputNameT, _timestep)
            },
            new[] { _outputName });
    }

    private void CheckPlacement()
    {
        try
        {
            var path = _session.EndProfiling();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                PlacementReport = "profile unavailable";
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var cpuNodes = new List<string>();
            var dmlNodes = 0;
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (!element.TryGetProperty("cat", out var category) || category.GetString() != "Node")
                {
                    continue;
                }

                if (!element.TryGetProperty("args", out var args))
                {
                    continue;
                }

                var provider = args.TryGetProperty("provider", out var providerElement) ? providerElement.GetString() : null;
                var op = args.TryGetProperty("op_name", out var opElement) ? opElement.GetString() : null;
                if (provider is null)
                {
                    continue;
                }

                if (provider.Contains("Dml", StringComparison.OrdinalIgnoreCase))
                {
                    dmlNodes++;
                }
                else if (provider.Contains("CPU", StringComparison.OrdinalIgnoreCase))
                {
                    cpuNodes.Add(op ?? "unknown");
                }
            }

            // DirectML runs the whole sampling/convolution graph. A handful of shape, cast and
            // concat nodes have no DirectML kernel and stay on CPU; they are microseconds, but the
            // report says so instead of pretending the graph is literally CPU-free. GridSample on
            // CPU would be the real failure: that is the optical-flow warp and it would kill real-time
            // performance.
            GpuVerified = dmlNodes > 0 && !cpuNodes.Contains("GridSample");
            var cpuSummary = cpuNodes.Count == 0
                ? "no CPU fallback"
                : $"{cpuNodes.Count} shape/cast ops on CPU; GridSample on GPU";
            PlacementReport = GpuVerified
                ? $"{dmlNodes} nodes on DirectML; {cpuSummary}"
                : cpuNodes.Count > 0
                    ? $"CPU fallback: {string.Join(", ", cpuNodes.Distinct().Take(6))}"
                    : "no DirectML node events found";
            File.Delete(path);
        }
        catch (Exception ex)
        {
            PlacementReport = "profile check failed: " + ex.Message;
        }
    }

    private void PackBgraToRgbPlanar(ReadOnlySpan<byte> bgra, float[] planar)
    {
        var pixels = Width * Height;
        for (var i = 0; i < pixels; i++)
        {
            var offset = i * 4;
            planar[i] = bgra[offset + 2] * (1f / 255f);
            planar[pixels + i] = bgra[offset + 1] * (1f / 255f);
            planar[2 * pixels + i] = bgra[offset] * (1f / 255f);
        }
    }

    private void UnpackRgbPlanarToBgra(float[] planar, Span<byte> bgra)
    {
        var pixels = Width * Height;
        for (var i = 0; i < pixels; i++)
        {
            var offset = i * 4;
            bgra[offset] = ToByte(planar[2 * pixels + i]);
            bgra[offset + 1] = ToByte(planar[pixels + i]);
            bgra[offset + 2] = ToByte(planar[i]);
            bgra[offset + 3] = 255;
        }
    }

    private static byte ToByte(float value) =>
        (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

    public void Dispose() => _session.Dispose();
}
