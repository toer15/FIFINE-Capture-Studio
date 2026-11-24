using System.Globalization;

namespace FifineCaptureStudio.Core;

public sealed record EncoderInfo(
    string Id,
    string DisplayName,
    string Vendor,
    VideoCodecKind Codec,
    bool Hardware);

/// <summary>
/// Discovers which encoders this machine's FFmpeg build exposes, then verifies
/// each one by actually encoding a few frames. A hardware encoder that is listed
/// but cannot initialise (common with stale drivers) is reported as unavailable
/// instead of failing a recording later.
/// </summary>
public sealed class EncoderCatalog
{
    private static readonly (VideoCodecKind Codec, string Id, string Name, string Vendor, bool Hardware)[] Candidates =
    {
        (VideoCodecKind.H264, "h264_nvenc", "NVIDIA NVENC H.264", "NVIDIA", true),
        (VideoCodecKind.H264, "h264_amf", "AMD AMF H.264", "AMD", true),
        (VideoCodecKind.H264, "h264_qsv", "Intel Quick Sync H.264", "Intel", true),
        (VideoCodecKind.H264, "h264_mf", "Windows Media Foundation H.264", "Windows", true),
        (VideoCodecKind.H264, "libx264", "CPU x264 (software)", "CPU", false),

        (VideoCodecKind.HEVC, "hevc_nvenc", "NVIDIA NVENC HEVC", "NVIDIA", true),
        (VideoCodecKind.HEVC, "hevc_amf", "AMD AMF HEVC", "AMD", true),
        (VideoCodecKind.HEVC, "hevc_qsv", "Intel Quick Sync HEVC", "Intel", true),
        (VideoCodecKind.HEVC, "hevc_mf", "Windows Media Foundation HEVC", "Windows", true),
        (VideoCodecKind.HEVC, "libx265", "CPU x265 (software)", "CPU", false),

        (VideoCodecKind.AV1, "av1_nvenc", "NVIDIA NVENC AV1", "NVIDIA", true),
        (VideoCodecKind.AV1, "av1_amf", "AMD AMF AV1", "AMD", true),
        (VideoCodecKind.AV1, "av1_qsv", "Intel Quick Sync AV1", "Intel", true),
        (VideoCodecKind.AV1, "libsvtav1", "CPU SVT-AV1 (software)", "CPU", false)
    };

    private readonly FfmpegManager _ffmpeg;
    private readonly List<EncoderInfo> _available = new();
    private readonly Dictionary<string, bool> _verified = new(StringComparer.OrdinalIgnoreCase);

    public EncoderCatalog(FfmpegManager ffmpeg)
    {
        _ffmpeg = ffmpeg;
    }

    public IReadOnlyList<EncoderInfo> Available => _available;
    public DateTime LastScan { get; private set; } = DateTime.MinValue;

    /// <summary>True when a real encode succeeded, false when it failed, null when untested.</summary>
    public bool? IsVerified(string id) => _verified.TryGetValue(id, out var ok) ? ok : null;

    /// <summary>Raised after each encoder test so the UI can drop encoders that failed.</summary>
    public event Action? VerificationChanged;

    public async Task VerifyAllAsync(CancellationToken ct = default)
    {
        foreach (var encoder in _available.ToList())
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            await VerifyAsync(encoder, ct).ConfigureAwait(false);
            VerificationChanged?.Invoke();
        }
    }

    public async Task ScanAsync(CancellationToken ct = default)
    {
        _available.Clear();
        if (!_ffmpeg.IsAvailable)
        {
            LastScan = DateTime.Now;
            return;
        }

        var encoders = await _ffmpeg.GetEncodersTextAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(encoders))
        {
            return;
        }

        var listed = new HashSet<string>(
            encoders.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("-", StringComparison.Ordinal) && !l.StartsWith("Encoders", StringComparison.OrdinalIgnoreCase))
                .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 2)
                .Select(parts => parts[1]),
            StringComparer.OrdinalIgnoreCase);

        foreach (var c in Candidates)
        {
            if (listed.Contains(c.Id))
            {
                _available.Add(new EncoderInfo(c.Id, c.Name, c.Vendor, c.Codec, c.Hardware));
            }
        }

        LastScan = DateTime.Now;
        Log.Info("Encoders listed by FFmpeg: " + string.Join(", ", _available.Select(e => e.Id)));
    }

    /// <summary>Runs a real 6-frame encode to confirm the encoder initialises.</summary>
    public async Task<bool> VerifyAsync(EncoderInfo encoder, CancellationToken ct = default)
    {
        if (_verified.TryGetValue(encoder.Id, out var known))
        {
            return known;
        }

        if (!_ffmpeg.IsAvailable)
        {
            return false;
        }

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=30",
            "-frames:v", "6",
            "-c:v", encoder.Id,
            "-pix_fmt", "yuv420p",
            "-f", "null", "-"
        };

        var result = await Proc.RunAsync(_ffmpeg.FfmpegPath!, args, TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
        var ok = result.Code == 0;
        _verified[encoder.Id] = ok;
        Log.Info($"Encoder {encoder.Id}: {(ok ? "verified" : "unavailable - " + Summarise(result.StdErr))}");
        return ok;
    }

    private static string Summarise(string stderr)
    {
        var line = stderr.Split('\n')
            .Select(l => l.Trim())
            .LastOrDefault(l => l.Length > 0);
        return line is null ? "no diagnostics" : (line.Length > 180 ? line[..180] : line);
    }

    /// <summary>Picks the best encoder for the requested codec, honouring the hardware preference.</summary>
    public EncoderInfo? Resolve(VideoCodecKind codec, bool preferHardware, string preferredId = "Auto")
    {
        // An encoder that failed its real encode is never chosen, even when listed.
        bool Usable(EncoderInfo e) => !_verified.TryGetValue(e.Id, out var ok) || ok;

        if (!string.Equals(preferredId, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            var exact = _available.FirstOrDefault(e => e.Id.Equals(preferredId, StringComparison.OrdinalIgnoreCase));
            if (exact is not null && exact.Codec == codec && Usable(exact))
            {
                return exact;
            }
        }

        var forCodec = _available.Where(e => e.Codec == codec).ToList();
        var usable = forCodec.Where(Usable).ToList();
        if (usable.Count == 0)
        {
            usable = forCodec;
        }

        var hardware = usable.Where(e => e.Hardware).ToList();
        if (preferHardware && hardware.Count > 0)
        {
            return hardware[0];
        }

        return usable.FirstOrDefault(e => !e.Hardware) ?? usable.FirstOrDefault();
    }

    /// <summary>
    /// Builds encoder-specific arguments (rate control, preset, quality flags).
    /// Values are deliberately conservative so recordings stay stable.
    /// </summary>
    public static IEnumerable<string> BuildVideoArgs(
        EncoderInfo encoder,
        int width,
        int height,
        int fps,
        int bitrateKbps,
        string tune = "Balanced")
    {
        var args = new List<string> { "-c:v", encoder.Id };

        switch (encoder.Id)
        {
            case "h264_nvenc":
            case "hevc_nvenc":
            case "av1_nvenc":
                args.AddRange(new[]
                {
                    "-preset", tune == "Quality" ? "p6" : tune == "Speed" ? "p3" : "p5",
                    "-tune", "ll",
                    "-rc", "cbr",
                    "-b:v", bitrateKbps + "k",
                    "-maxrate", bitrateKbps + "k",
                    "-bufsize", (bitrateKbps * 2) + "k",
                    "-spatial-aq", "1",
                    "-zerolatency", "1"
                });
                break;

            case "h264_amf":
            case "hevc_amf":
            case "av1_amf":
                args.AddRange(new[]
                {
                    "-quality", tune == "Quality" ? "quality" : "balanced",
                    "-rc", "cbr",
                    "-b:v", bitrateKbps + "k",
                    "-maxrate", bitrateKbps + "k",
                    "-bufsize", (bitrateKbps * 2) + "k"
                });
                break;

            case "h264_qsv":
            case "hevc_qsv":
            case "av1_qsv":
                args.AddRange(new[]
                {
                    "-preset", tune == "Quality" ? "veryslow" : "medium",
                    "-b:v", bitrateKbps + "k",
                    "-maxrate", bitrateKbps + "k",
                    "-bufsize", (bitrateKbps * 2) + "k"
                });
                break;

            case "h264_mf":
            case "hevc_mf":
                args.AddRange(new[]
                {
                    "-b:v", bitrateKbps + "k",
                    "-scenario", "video_conference",
                    "-hw_encoding", "1"
                });
                break;

            case "libx264":
            case "libx265":
                args.AddRange(new[]
                {
                    "-preset", tune == "Quality" ? "slow" : tune == "Speed" ? "veryfast" : "medium",
                    "-b:v", bitrateKbps + "k",
                    "-maxrate", bitrateKbps + "k",
                    "-bufsize", (bitrateKbps * 2) + "k"
                });
                if (encoder.Id == "libx265")
                {
                    args.AddRange(new[] { "-x265-params", "log-level=error" });
                }

                break;

            case "libsvtav1":
                args.AddRange(new[]
                {
                    "-preset", tune == "Quality" ? "6" : "10",
                    "-b:v", bitrateKbps + "k"
                });
                break;

            default:
                args.AddRange(new[] { "-b:v", bitrateKbps + "k" });
                break;
        }

        args.AddRange(new[] { "-pix_fmt", "yuv420p", "-g", Math.Max(30, fps * 2).ToString(CultureInfo.InvariantCulture) });
        return args;
    }
}
