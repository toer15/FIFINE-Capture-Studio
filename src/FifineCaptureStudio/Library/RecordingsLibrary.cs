using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using FifineCaptureStudio.Core;

namespace FifineCaptureStudio.Library;

public sealed class RecordingItem : INotifyPropertyChanged
{
    public string Path { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public TimeSpan Duration { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public string Codec { get; set; } = string.Empty;
    public string Platform { get; set; } = "PC";
    public DateTime CreatedAt { get; set; }
    public string Source { get; set; } = string.Empty;
    public string? Flag { get; set; }

    private BitmapSource? _thumbnail;
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            Raise();
        }
    }

    public string SizeLabel => SizeBytes >= 1_073_741_824
        ? $"{SizeBytes / 1073741824.0:0.00} GB"
        : SizeBytes >= 1048576 ? $"{SizeBytes / 1048576.0:0.0} MB" : $"{SizeBytes / 1024.0:0} KB";

    public string DurationLabel => Duration > TimeSpan.Zero
        ? Duration.TotalHours >= 1
            ? Duration.ToString(@"h\:mm\:ss")
            : Duration.ToString(@"m\:ss")
        : "--:--";

    public string ResolutionLabel => Width > 0 ? $"{Width}×{Height}" : "Unknown";

    public string FpsLabel => Fps > 0 ? $"{Fps:0.##} fps" : "—";

    public string DateLabel => CreatedAt.ToString("MMM d, yyyy · HH:mm", CultureInfo.CurrentCulture);

    public string CodecLabel => string.IsNullOrEmpty(Codec) ? "—" : Codec.ToUpperInvariant();

    public string PlatformBadge => Platform switch
    {
        "PS5" => "PlayStation 5",
        "Xbox" => "Xbox",
        _ => "PC"
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Scans the recordings folder and reports only measured metadata: file facts
/// from the file system, stream facts from ffprobe, and platform/source from the
/// sidecar written by the recorder.
/// </summary>
public sealed class RecordingsLibrary
{
    private readonly FfmpegManager _ffmpeg;
    private readonly Dictionary<string, MediaInfo> _cache = new(StringComparer.OrdinalIgnoreCase);

    public RecordingsLibrary(FfmpegManager ffmpeg)
    {
        _ffmpeg = ffmpeg;
    }

    public string Directory { get; private set; } = AppPaths.DefaultRecordingsDir;

    public async Task<IReadOnlyList<RecordingItem>> ScanAsync(string directory, CancellationToken ct = default)
    {
        Directory = string.IsNullOrWhiteSpace(directory) ? AppPaths.DefaultRecordingsDir : directory;
        if (!System.IO.Directory.Exists(Directory))
        {
            return Array.Empty<RecordingItem>();
        }

        var files = new List<FileInfo>();
        foreach (var ext in new[] { "*.mp4", "*.mkv", "*.mov" })
        {
            try
            {
                files.AddRange(new DirectoryInfo(Directory).GetFiles(ext, SearchOption.TopDirectoryOnly));
            }
            catch (Exception ex)
            {
                Log.Warn("Could not list recordings: " + ex.Message);
            }
        }

        var items = new List<RecordingItem>();
        foreach (var file in files.DistinctBy(f => f.FullName))
        {
            var item = new RecordingItem
            {
                Path = file.FullName,
                FileName = file.Name,
                DisplayName = System.IO.Path.GetFileNameWithoutExtension(file.Name),
                SizeBytes = file.Length,
                CreatedAt = file.LastWriteTime
            };

            var meta = ReadSidecar(file.FullName);
            if (meta is not null)
            {
                item.Platform = meta.Value.Platform;
                item.Source = meta.Value.Source;
                item.Width = meta.Value.Width;
                item.Height = meta.Value.Height;
                item.Fps = meta.Value.Fps;
                item.Duration = meta.Value.Duration;
                item.Flag = meta.Value.Encoder;
            }

            var info = await MediaProbe.ProbeAsync(_ffmpeg, file.FullName, ct).ConfigureAwait(false);
            if (info is not null)
            {
                if (info.Duration > TimeSpan.Zero)
                {
                    item.Duration = info.Duration;
                }

                if (info.Width > 0)
                {
                    item.Width = info.Width;
                    item.Height = info.Height;
                }

                if (info.Fps > 0)
                {
                    item.Fps = info.Fps;
                }

                if (!string.IsNullOrEmpty(info.VideoCodec))
                {
                    item.Codec = info.VideoCodec;
                }
            }

            items.Add(item);
        }

        return items
            .OrderByDescending(i => i.CreatedAt)
            .ToList();
    }

    private static (string Platform, string Source, int Width, int Height, int Fps, TimeSpan Duration, string Encoder)? ReadSidecar(string path)
    {
        try
        {
            var sidecar = path + ".fifine.json";
            if (!File.Exists(sidecar))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(sidecar));
            var root = doc.RootElement;
            string Get(string name) => root.TryGetProperty(name, out var v)
                ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : v.ToString())
                : string.Empty;

            var duration = double.TryParse(Get("durationSeconds"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? TimeSpan.FromSeconds(d)
                : TimeSpan.Zero;

            return (
                Get("platform") is { Length: > 0 } p ? p : "PC",
                Get("source"),
                int.TryParse(Get("width"), out var w) ? w : 0,
                int.TryParse(Get("height"), out var h) ? h : 0,
                int.TryParse(Get("fps"), out var f) ? f : 0,
                duration,
                Get("encoder"));
        }
        catch
        {
            return null;
        }
    }

    public string ThumbnailPath(RecordingItem item)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(item.Path)))[..16].ToLowerInvariant();
        return System.IO.Path.Combine(AppPaths.ThumbCacheDir, hash + ".jpg");
    }

    public async Task<BitmapSource?> LoadThumbnailAsync(RecordingItem item, CancellationToken ct = default)
    {
        try
        {
            var path = ThumbnailPath(item);
            if (!File.Exists(path))
            {
                var position = item.Duration > TimeSpan.FromSeconds(4)
                    ? TimeSpan.FromSeconds(Math.Min(item.Duration.TotalSeconds * 0.2, 20))
                    : TimeSpan.Zero;
                var ok = await MediaProbe.ExtractThumbnailAsync(_ffmpeg, item.Path, path, position, ct).ConfigureAwait(false);
                if (!ok)
                {
                    return null;
                }
            }

            return LoadBitmap(path);
        }
        catch (Exception ex)
        {
            Log.Debug("Thumbnail load failed: " + ex.Message);
            return null;
        }
    }

    private static BitmapSource LoadBitmap(string path)
    {
        using var stream = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 480;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    public bool Delete(RecordingItem item)
    {
        try
        {
            if (File.Exists(item.Path))
            {
                File.Delete(item.Path);
            }

            var sidecar = item.Path + ".fifine.json";
            if (File.Exists(sidecar))
            {
                File.Delete(sidecar);
            }

            var thumb = ThumbnailPath(item);
            if (File.Exists(thumb))
            {
                File.Delete(thumb);
            }

            Log.Info("Deleted recording " + item.Path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Delete failed", ex);
            return false;
        }
    }

    public string? Rename(RecordingItem item, string newName)
    {
        try
        {
            var clean = string.Join("_", newName.Split(System.IO.Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
            if (string.IsNullOrWhiteSpace(clean))
            {
                return null;
            }

            var dir = System.IO.Path.GetDirectoryName(item.Path)!;
            var ext = System.IO.Path.GetExtension(item.Path);
            var target = System.IO.Path.Combine(dir, clean + ext);
            if (File.Exists(target))
            {
                target = System.IO.Path.Combine(dir, clean + "_" + DateTime.Now.ToString("HHmmss") + ext);
            }

            File.Move(item.Path, target);

            var oldSidecar = item.Path + ".fifine.json";
            if (File.Exists(oldSidecar))
            {
                File.Move(oldSidecar, target + ".fifine.json");
            }

            return target;
        }
        catch (Exception ex)
        {
            Log.Error("Rename failed", ex);
            return null;
        }
    }

    public void Play(RecordingItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Playback failed", ex);
        }
    }

    public void Reveal(RecordingItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Reveal failed", ex);
        }
    }
}
