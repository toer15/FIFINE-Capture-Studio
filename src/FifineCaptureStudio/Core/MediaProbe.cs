using System.Globalization;
using System.IO;
using System.Text.Json;

namespace FifineCaptureStudio.Core;

public sealed record MediaInfo(
    TimeSpan Duration,
    int Width,
    int Height,
    double Fps,
    string VideoCodec,
    string AudioCodec,
    long SizeBytes,
    long BitrateKbps);

/// <summary>Reads real media metadata and thumbnails with ffprobe/ffmpeg.</summary>
public static class MediaProbe
{
    public static async Task<MediaInfo?> ProbeAsync(FfmpegManager ffmpeg, string file, CancellationToken ct = default)
    {
        if (!File.Exists(file) || string.IsNullOrEmpty(ffmpeg.FfprobePath))
        {
            return Fallback(file);
        }

        try
        {
            var result = await Proc.RunAsync(
                ffmpeg.FfprobePath!,
                new[]
                {
                    "-v", "error", "-print_format", "json",
                    "-show_format", "-show_streams", file
                },
                TimeSpan.FromSeconds(20),
                ct).ConfigureAwait(false);

            if (result.Code != 0 || string.IsNullOrWhiteSpace(result.StdOut))
            {
                return Fallback(file);
            }

            using var doc = JsonDocument.Parse(result.StdOut);
            var root = doc.RootElement;
            var streams = root.GetProperty("streams").EnumerateArray().ToList();
            var video = streams.FirstOrDefault(s => GetString(s, "codec_type") == "video");
            var audio = streams.FirstOrDefault(s => GetString(s, "codec_type") == "audio");
            var format = root.TryGetProperty("format", out var f) ? f : default;

            var duration = 0.0;
            if (format.ValueKind == JsonValueKind.Object && format.TryGetProperty("duration", out var d)
                && double.TryParse(GetString(f, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                duration = parsed;
            }
            else if (video.ValueKind == JsonValueKind.Object && video.TryGetProperty("duration", out _))
            {
                double.TryParse(GetString(video, "duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);
            }

            var width = 0;
            var height = 0;
            var fps = 0.0;
            if (video.ValueKind == JsonValueKind.Object)
            {
                int.TryParse(GetString(video, "width"), out width);
                int.TryParse(GetString(video, "height"), out height);
                fps = ParseRate(GetString(video, "avg_frame_rate"));
                if (fps <= 0)
                {
                    fps = ParseRate(GetString(video, "r_frame_rate"));
                }
            }

            var size = new FileInfo(file).Length;
            if (long.TryParse(GetString(format, "size"), out var formatSize) && formatSize > 0)
            {
                size = formatSize;
            }

            long.TryParse(GetString(format, "bit_rate"), out var bitrate);

            return new MediaInfo(
                TimeSpan.FromSeconds(Math.Max(0, duration)),
                width,
                height,
                fps,
                video.ValueKind == JsonValueKind.Object ? GetString(video, "codec_name") ?? string.Empty : string.Empty,
                audio.ValueKind == JsonValueKind.Object ? GetString(audio, "codec_name") ?? string.Empty : string.Empty,
                size,
                bitrate / 1000);
        }
        catch (Exception ex)
        {
            Log.Debug("ffprobe failed for " + file + ": " + ex.Message);
            return Fallback(file);
        }
    }

    private static string? GetString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static double ParseRate(string? rate)
    {
        if (string.IsNullOrEmpty(rate))
        {
            return 0;
        }

        var parts = rate.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
            && den > 0)
        {
            return Math.Round(num / den, 2);
        }

        return double.TryParse(rate, NumberStyles.Float, CultureInfo.InvariantCulture, out var single) ? single : 0;
    }

    private static MediaInfo Fallback(string file)
    {
        var size = File.Exists(file) ? new FileInfo(file).Length : 0;
        return new MediaInfo(TimeSpan.Zero, 0, 0, 0, string.Empty, string.Empty, size, 0);
    }

    public static async Task<bool> ExtractThumbnailAsync(
        FfmpegManager ffmpeg,
        string file,
        string output,
        TimeSpan position,
        CancellationToken ct = default)
    {
        if (!ffmpeg.IsAvailable)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var args = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-ss", position.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture),
                "-i", file,
                "-frames:v", "1",
                "-vf", "scale=480:-2",
                "-q:v", "4",
                output
            };

            var result = await Proc.RunAsync(ffmpeg.FfmpegPath!, args, TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);
            return result.Code == 0 && File.Exists(output);
        }
        catch (Exception ex)
        {
            Log.Debug("Thumbnail extraction failed: " + ex.Message);
            return false;
        }
    }
}
