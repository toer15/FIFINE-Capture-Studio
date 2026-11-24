using System.Text.RegularExpressions;

namespace FifineCaptureStudio.Core;

public sealed record VideoModeInfo(int Width, int Height, int Fps, string Codec);

/// <summary>
/// What the connected capture hardware actually reports, read from the driver
/// through FFmpeg's dshow option listing. Nothing here is guessed: if the card
/// cannot deliver 1440p60 the mode simply never appears.
/// </summary>
public sealed class DeviceCapabilities
{
    public string DeviceName { get; init; } = string.Empty;
    public IReadOnlyList<VideoModeInfo> Modes { get; init; } = Array.Empty<VideoModeInfo>();
    public IReadOnlyList<string> Codecs { get; init; } = Array.Empty<string>();
    public string RawOutput { get; init; } = string.Empty;
    public string? Error { get; init; }
    public bool ProbeSucceeded => Error is null && Modes.Count > 0;

    public bool Supports(int width, int height, int fps) =>
        Modes.Any(m => m.Width == width && m.Height == height && m.Fps >= fps);

    public int MaxFps => Modes.Count == 0 ? 0 : Modes.Max(m => m.Fps);

    public IEnumerable<VideoModeInfo> DistinctResolutions =>
        Modes.GroupBy(m => (m.Width, m.Height))
            .Select(g => g.OrderByDescending(m => m.Fps).First())
            .OrderByDescending(m => (long)m.Width * m.Height)
            .ThenByDescending(m => m.Fps);

    public static DeviceCapabilities Unknown(string device, string? error = null) =>
        new() { DeviceName = device, Error = error ?? "No capture modes reported by the driver." };
}

public static class CapabilityProbe
{
    private static readonly Regex ModeLine = new(
        @"vcodec=(?<codec>\S+)\s+min s=(?<w1>\d+)x(?<h1>\d+) fps=(?<f1>[\d.]+)\s+max s=(?<w2>\d+)x(?<h2>\d+) fps=(?<f2>[\d.]+)",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, DeviceCapabilities> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<DeviceCapabilities> ProbeVideoAsync(
        FfmpegManager ffmpeg,
        string deviceName,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return DeviceCapabilities.Unknown(deviceName, "No capture device selected.");
        }

        if (Cache.TryGetValue(deviceName, out var cached))
        {
            return cached;
        }

        if (!ffmpeg.IsAvailable)
        {
            return DeviceCapabilities.Unknown(deviceName, "FFmpeg engine not installed.");
        }

        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Cache.TryGetValue(deviceName, out cached))
            {
                return cached;
            }

            var result = await Proc.RunAsync(
                ffmpeg.FfmpegPath!,
                new[] { "-hide_banner", "-f", "dshow", "-list_options", "true", "-i", "video=" + deviceName },
                TimeSpan.FromSeconds(15),
                ct).ConfigureAwait(false);

            var text = result.StdOut + "\n" + result.StdErr;
            var modes = new List<VideoModeInfo>();
            var codecs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in text.Split('\n'))
            {
                var m = ModeLine.Match(line);
                if (!m.Success)
                {
                    continue;
                }

                var codec = m.Groups["codec"].Value;
                codecs.Add(codec);

                var minW = int.Parse(m.Groups["w1"].Value);
                var minH = int.Parse(m.Groups["h1"].Value);
                var minF = (int)Math.Round(double.Parse(m.Groups["f1"].Value, System.Globalization.CultureInfo.InvariantCulture));
                var maxW = int.Parse(m.Groups["w2"].Value);
                var maxH = int.Parse(m.Groups["h2"].Value);
                var maxF = (int)Math.Round(double.Parse(m.Groups["f2"].Value, System.Globalization.CultureInfo.InvariantCulture));

                foreach (var (w, h, f) in new[] { (minW, minH, minF), (maxW, maxH, maxF) })
                {
                    if (w < 160 || h < 120 || f < 1)
                    {
                        continue;
                    }

                    if (!modes.Any(x => x.Width == w && x.Height == h && x.Fps == f))
                    {
                        modes.Add(new VideoModeInfo(w, h, f, codec));
                    }
                }
            }

            string? error = null;
            if (modes.Count == 0)
            {
                var relevant = text
                    .Split('\n')
                    .FirstOrDefault(l => l.Contains("Error", StringComparison.OrdinalIgnoreCase)
                                         || l.Contains("Could not", StringComparison.OrdinalIgnoreCase)
                                         || l.Contains("Cannot", StringComparison.OrdinalIgnoreCase));
                error = relevant?.Trim() ?? "The driver did not report any capture formats.";
            }

            var caps = new DeviceCapabilities
            {
                DeviceName = deviceName,
                Modes = modes
                    .OrderByDescending(m => (long)m.Width * m.Height)
                    .ThenByDescending(m => m.Fps)
                    .ToList(),
                Codecs = codecs.OrderBy(c => c).ToList(),
                RawOutput = text,
                Error = error
            };

            if (caps.ProbeSucceeded)
            {
                Cache[deviceName] = caps;
                Log.Info($"Probed '{deviceName}': {caps.Modes.Count} modes, codecs={string.Join('/', caps.Codecs)}");
            }

            return caps;
        }
        catch (Exception ex)
        {
            Log.Error("Capability probe failed for " + deviceName, ex);
            return DeviceCapabilities.Unknown(deviceName, ex.Message);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static void Invalidate(string? deviceName = null)
    {
        if (deviceName is null)
        {
            Cache.Clear();
        }
        else
        {
            Cache.Remove(deviceName);
        }
    }
}
