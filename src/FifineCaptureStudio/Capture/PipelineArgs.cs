using System.Globalization;
using FifineCaptureStudio.Core.Enhancement;
using System.IO;
using FifineCaptureStudio.Core;

namespace FifineCaptureStudio.Capture;

public sealed class PipelineOptions
{
    public required SourceSpec Source { get; init; }

    /// <summary>Named pipe carrying compressed console video (ConsoleStream sources only).</summary>
    public string ConsoleVideoPipe { get; init; } = string.Empty;

    /// <summary>"h264" or "hevc" — the codec the console backend negotiated.</summary>
    public string ConsoleCodec { get; init; } = "h264";
    public int PreviewWidth { get; init; } = 1600;
    public bool PreviewEnabled { get; init; } = true;
    public bool MeasureLatency { get; init; } = true;
    public string? RecordPath { get; init; }
    public string? RingDirectory { get; init; }
    public int RingSegmentSeconds { get; init; } = 2;
    public int RingSegmentCount { get; init; } = 18;
    public int EncodeWidth { get; init; } = 1920;
    public int EncodeHeight { get; init; } = 1080;
    public int EncodeFps { get; init; } = 60;
    public int FrameGeneration { get; init; } = 1;
    public required EncoderInfo Encoder { get; init; }
    public int BitrateKbps { get; init; } = 20000;
    public string QualityPreset { get; init; } = "Balanced";
    public ContainerKind Container { get; init; } = ContainerKind.Mp4;
    public int AudioBitrateKbps { get; init; } = 192;
    public IReadOnlyList<string> AudioPipes { get; init; } = Array.Empty<string>();
    public bool SeparateAudioTracks { get; init; }
    public int AudioSampleRate { get; init; } = 48000;
    public LowLatencyMode Latency { get; init; } = LowLatencyMode.Balanced;
    public int VideoBufferMs { get; init; } = 120;

    /// <summary>Real Vulkan/libplacebo filter chain applied to the encoder input when recording enhanced.</summary>
    public string EnhancementFilter { get; init; } = string.Empty;

    public bool HasRecordOutput => !string.IsNullOrEmpty(RecordPath);
    public bool HasRingOutput => !string.IsNullOrEmpty(RingDirectory) && RingSegmentCount > 0;
}

public static class PipelineArgs
{
    public const string PreviewPipe = "pipe:1";
    public const int LightweightPreviewWidth = 1600;
    public const int MaxEnhancementPreviewWidth = 3840;

    /// <summary>
    /// Chooses the MJPEG preview width. Enhancement asks for the native frame (capped for 8K
    /// desktops); everything else keeps the cheap 1600 px stream.
    /// </summary>
    public static int ResolvePreviewWidth(int sourceWidth, bool enhancementPreview, bool fullResolution)
    {
        if (!enhancementPreview || !fullResolution)
        {
            return LightweightPreviewWidth;
        }

        var width = sourceWidth > 0 ? sourceWidth : 1920;
        return Math.Min(width, MaxEnhancementPreviewWidth);
    }

    /// <summary>The size the preview MJPEG stream is encoded at for this option set.</summary>
    public static (int Width, int Height) EstimatePreviewSize(PipelineOptions options) => PreviewSize(options, 1);

    /// <summary>
    /// Builds the real GPU filter chain used for enhanced recordings. Frame generation is
    /// deliberately excluded: generated frames are a presentation feature and never written to disk.
    /// </summary>
    public static string BuildRecordingEnhancementFilter(EnhancementSettings e, int width, int height)
    {
        if (!e.Enabled || !e.RecordingEnhanced)
        {
            return string.Empty;
        }

        static string F(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        var builder = new System.Text.StringBuilder();
        builder.Append("libplacebo=w=").Append(width)
            .Append(":h=").Append(height)
            .Append(":upscaler=lanczos:antiringing=0.4")
            .Append(e.Denoise ? ":deband=true:deband_iterations=2" : ":deband=false")
            .Append(":brightness=").Append(F(Math.Clamp(e.Brightness, -1, 1)))
            .Append(":contrast=").Append(F(Math.Clamp(1 + e.Contrast, 0.1, 3)))
            .Append(":saturation=").Append(F(Math.Clamp(1 + e.Saturation + e.Vibrance * 0.5, 0, 3)))
            .Append(":gamma=").Append(F(Math.Clamp(1 + e.Gamma, 0.2, 4)));

        if (e.Sharpening > 0.01)
        {
            // CAS on the encoder input; the live compute pipeline applies its own sharpen pass.
            builder.Append(",cas=strength=").Append(F(Math.Clamp(e.Sharpening, 0, 1)));
        }

        return builder.ToString();
    }

    public static List<string> Build(PipelineOptions options, int previewDivisor = 1)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "info",
            "-stats_period", "0.5"
        };

        ApplyLatency(args, options);
        AppendVideoInput(args, options);
        AppendAudioInputs(args, options);

        if (options.PreviewEnabled)
        {
            var (previewWidth, previewHeight) = PreviewSize(options, previewDivisor);
            args.AddRange(new[] { "-map", "0:v:0", "-an" });
            var filters = new List<string>();
            if (previewWidth > 0 && (previewWidth < options.Source.Width || previewHeight < options.Source.Height))
            {
                filters.Add($"scale={previewWidth}:{previewHeight}:flags=fast_bilinear");
            }

            if (options.MeasureLatency)
            {
                filters.Add("showinfo");
            }

            if (filters.Count > 0)
            {
                args.AddRange(new[] { "-vf", string.Join(",", filters) });
            }

            args.AddRange(new[]
            {
                "-c:v", "mjpeg",
                "-q:v", "6",
                "-threads", "2",
                "-f", "mjpeg",
                PreviewPipe
            });
        }

        if (options.HasRecordOutput)
        {
            AppendEncodedOutput(args, options, options.RecordPath!, isRing: false);
        }

        if (options.HasRingOutput)
        {
            AppendEncodedOutput(args, options, Path.Combine(options.RingDirectory!, "replay_%03d.mkv"), isRing: true);
        }

        return args;
    }

    private static void ApplyLatency(List<string> args, PipelineOptions options)
    {
        switch (options.Latency)
        {
            case LowLatencyMode.UltraLow:
                args.AddRange(new[]
                {
                    "-fflags", "nobuffer+genpts",
                    "-flags", "low_delay",
                    "-probesize", "32",
                    "-analyzeduration", "0",
                    "-rtbufsize", "64M"
                });
                break;

            case LowLatencyMode.Balanced:
                args.AddRange(new[]
                {
                    "-fflags", "genpts",
                    "-probesize", "1000000",
                    "-analyzeduration", "500000",
                    "-rtbufsize", Math.Max(128, options.VideoBufferMs).ToString(CultureInfo.InvariantCulture) + "M"
                });
                break;

            default:
                args.AddRange(new[]
                {
                    "-probesize", "3000000",
                    "-analyzeduration", "1000000",
                    "-rtbufsize", "256M"
                });
                break;
        }
    }

    private static void AppendVideoInput(List<string> args, PipelineOptions options)
    {
        var source = options.Source;
        switch (source.Kind)
        {
            case CaptureSourceKind.ConsoleStream:
                // The console backend delivers complete Annex B access units through a named pipe.
                args.AddRange(new[]
                {
                    "-f", string.Equals(options.ConsoleCodec, "hevc", StringComparison.OrdinalIgnoreCase) ? "hevc" : "h264",
                    "-framerate", Math.Clamp(source.Fps, 1, 120).ToString(CultureInfo.InvariantCulture),
                    "-i", string.IsNullOrEmpty(options.ConsoleVideoPipe) ? "pipe:0" : options.ConsoleVideoPipe
                });
                break;
            case CaptureSourceKind.TestPattern:
                args.AddRange(new[]
                {
                    "-re",
                    "-f", "lavfi",
                    "-i", $"testsrc2=size={Math.Max(320, source.Width)}x{Math.Max(180, source.Height)}:rate={Math.Clamp(source.Fps, 1, 240)}"
                });
                break;

            case CaptureSourceKind.Display:
                args.AddRange(new[]
                {
                    "-f", "gdigrab",
                    "-framerate", source.Fps.ToString(CultureInfo.InvariantCulture),
                    "-offset_x", source.DisplayX.ToString(CultureInfo.InvariantCulture),
                    "-offset_y", source.DisplayY.ToString(CultureInfo.InvariantCulture),
                    "-video_size", $"{source.Width}x{source.Height}",
                    "-draw_mouse", "1",
                    "-i", "desktop"
                });
                break;

            case CaptureSourceKind.Window:
                args.AddRange(new[]
                {
                    "-f", "gdigrab",
                    "-framerate", source.Fps.ToString(CultureInfo.InvariantCulture),
                    "-i", "title=" + source.WindowTitle
                });
                break;

            default:
                args.AddRange(new[] { "-f", "dshow" });
                if (source.Fps > 0)
                {
                    args.AddRange(new[] { "-framerate", source.Fps.ToString(CultureInfo.InvariantCulture) });
                }

                if (source.Width > 0 && source.Height > 0)
                {
                    args.AddRange(new[] { "-video_size", $"{source.Width}x{source.Height}" });
                }

                var format = source.InputFormat?.ToLowerInvariant();
                if (format is "mjpeg" or "rawvideo")
                {
                    args.AddRange(new[] { "-vcodec", format });
                }

                args.AddRange(new[] { "-i", "video=" + source.DeviceName });
                break;
        }
    }

    private static void AppendAudioInputs(List<string> args, PipelineOptions options)
    {
        if (!options.HasRecordOutput && !options.HasRingOutput)
        {
            return;
        }

        foreach (var pipe in options.AudioPipes)
        {
            args.AddRange(new[]
            {
                "-f", "s16le",
                "-ar", options.AudioSampleRate.ToString(CultureInfo.InvariantCulture),
                "-ac", "2",
                "-i", pipe
            });
        }
    }

    private static void AppendEncodedOutput(List<string> args, PipelineOptions options, string path, bool isRing)
    {
        args.AddRange(new[] { "-map", "0:v:0" });

        if (options.AudioPipes.Count > 0)
        {
            for (var i = 0; i < options.AudioPipes.Count; i++)
            {
                args.AddRange(new[] { "-map", $"{i + 1}:a:0" });
            }
        }
        else
        {
            args.Add("-an");
        }

        var recordFilters = new List<string>();
        if (!string.IsNullOrEmpty(options.EnhancementFilter))
        {
            // libplacebo performs scaling, colour grading and debanding on the GPU (Vulkan).
            recordFilters.Add(options.EnhancementFilter);
        }
        else if (options.EncodeWidth != options.Source.Width || options.EncodeHeight != options.Source.Height)
        {
            recordFilters.Add($"scale={options.EncodeWidth}:{options.EncodeHeight}:flags=bicubic,setsar=1");
        }

        if (options.FrameGeneration > 1)
        {
            // Real motion-compensated interpolation, applied only to the recording.
            recordFilters.Add(
                $"minterpolate=fps={options.EncodeFps.ToString(CultureInfo.InvariantCulture)}:mi_mode=mci:mc_mode=aobmc:me_mode=bidir:vsbmc=1");
        }

        if (recordFilters.Count > 0)
        {
            args.AddRange(new[] { "-vf", string.Join(",", recordFilters) });
        }

        args.AddRange(EncoderCatalog.BuildVideoArgs(
            options.Encoder,
            options.EncodeWidth,
            options.EncodeHeight,
            options.EncodeFps,
            options.BitrateKbps,
            options.QualityPreset));

        args.AddRange(new[] { "-r", options.EncodeFps.ToString(CultureInfo.InvariantCulture) });

        if (options.AudioPipes.Count > 0)
        {
            args.AddRange(new[]
            {
                "-c:a", "aac",
                "-b:a", options.AudioBitrateKbps + "k",
                "-ar", options.AudioSampleRate.ToString(CultureInfo.InvariantCulture),
                "-ac", "2"
            });
        }

        args.AddRange(new[] { "-max_muxing_queue_size", "2048" });

        if (isRing)
        {
            args.AddRange(new[]
            {
                "-f", "segment",
                "-segment_time", Math.Max(1, options.RingSegmentSeconds).ToString(CultureInfo.InvariantCulture),
                "-segment_wrap", Math.Max(2, options.RingSegmentCount).ToString(CultureInfo.InvariantCulture),
                "-reset_timestamps", "1",
                "-segment_format", "matroska",
                "-y", path
            });
            return;
        }

        if (options.Container == ContainerKind.Mkv)
        {
            args.AddRange(new[] { "-f", "matroska", "-y", path });
        }
        else
        {
            args.AddRange(new[] { "-f", "mp4", "-movflags", "+faststart", "-y", path });
        }
    }

    private static (int Width, int Height) PreviewSize(PipelineOptions options, int divisor)
    {
        var width = options.PreviewWidth <= 0 ? options.Source.Width : Math.Min(options.PreviewWidth, options.Source.Width);
        width = Math.Max(320, width / Math.Max(1, divisor));
        var height = (int)Math.Round(width * (double)options.Source.Height / Math.Max(1, options.Source.Width));
        // Keep even dimensions for the MJPEG encoder.
        width -= width % 2;
        height -= height % 2;
        return (width, Math.Max(180, height));
    }
}
