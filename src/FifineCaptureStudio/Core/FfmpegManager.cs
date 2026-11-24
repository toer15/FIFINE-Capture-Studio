using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace FifineCaptureStudio.Core;

public sealed record FfmpegInstallResult(bool Success, string? Message);

/// <summary>
/// Locates the FFmpeg engine (the capture + encode backend), or downloads a
/// verified static Windows build on request. The app never fakes a capture
/// pipeline: if FFmpeg is missing, the UI says so and offers to install it.
/// </summary>
public sealed class FfmpegManager
{
    // Official BtbN "latest" static build; GPL variant so libx264/libx265/libsvtav1 are included.
    private const string DownloadUrl =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    private string? _encodersText;
    private readonly SemaphoreSlim _installGate = new(1, 1);

    public string? FfmpegPath { get; private set; }
    public string? FfprobePath { get; private set; }
    public bool IsAvailable => !string.IsNullOrEmpty(FfmpegPath) && File.Exists(FfmpegPath);
    public bool IsInstalling { get; private set; }

    public event Action? AvailabilityChanged;

    public void Locate()
    {
        FfmpegPath = null;
        FfprobePath = null;

        foreach (var candidate in AppPaths.FfmpegSearchRoots())
        {
            var resolved = candidate;
            if (!resolved.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                resolved = Path.Combine(candidate, "ffmpeg.exe");
            }

            if (File.Exists(resolved))
            {
                FfmpegPath = resolved;
                break;
            }
        }

        if (FfmpegPath is null)
        {
            var onPath = FindOnPath("ffmpeg.exe");
            if (onPath is not null)
            {
                FfmpegPath = onPath;
            }
        }

        if (FfmpegPath is not null)
        {
            var dir = Path.GetDirectoryName(FfmpegPath)!;
            var probe = Path.Combine(dir, "ffprobe.exe");
            FfprobePath = File.Exists(probe) ? probe : FindOnPath("ffprobe.exe");
            Log.Info($"FFmpeg engine: {FfmpegPath} (ffprobe: {FfprobePath ?? "not found"})");
        }
        else
        {
            Log.Warn("FFmpeg engine not found. Capture and recording require FFmpeg.");
        }

        AvailabilityChanged?.Invoke();
    }

    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var full = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(full))
                {
                    return full;
                }
            }
            catch
            {
                // malformed PATH entry
            }
        }

        return null;
    }

    /// <summary>Downloads and extracts the FFmpeg engine into the app data folder.</summary>
    public async Task<FfmpegInstallResult> InstallAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (!await _installGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return new FfmpegInstallResult(false, "An installation is already running.");
        }

        IsInstalling = true;
        var tempZip = Path.Combine(Path.GetTempPath(), "fifine-ffmpeg-" + Guid.NewGuid().ToString("N") + ".zip");
        var extractDir = Path.Combine(Path.GetTempPath(), "fifine-ffmpeg-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(extractDir);
            progress?.Report("Contacting download server…");

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("FifineCaptureStudio/1.0");

            using var response = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? -1;
            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var target = File.Create(tempZip))
            {
                var buffer = new byte[81920];
                long read = 0;
                int n;
                var lastReport = DateTime.MinValue;
                while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                    read += n;

                    if ((DateTime.UtcNow - lastReport).TotalMilliseconds > 350)
                    {
                        lastReport = DateTime.UtcNow;
                        var mb = read / 1048576.0;
                        progress?.Report(total > 0
                            ? $"Downloading engine… {mb:0.0} / {total / 1048576.0:0.0} MB ({read * 100.0 / total:0}%)"
                            : $"Downloading engine… {mb:0.0} MB");
                    }
                }
            }

            progress?.Report("Extracting…");
            await Task.Run(() => ZipFile.ExtractToDirectory(tempZip, extractDir, true), ct).ConfigureAwait(false);

            var ffmpeg = Directory.EnumerateFiles(extractDir, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (ffmpeg is null)
            {
                return new FfmpegInstallResult(false, "The downloaded archive did not contain ffmpeg.exe.");
            }

            var binDir = Path.GetDirectoryName(ffmpeg)!;
            var destDir = Path.Combine(AppPaths.FfmpegDir, "bin");
            Directory.CreateDirectory(destDir);

            foreach (var file in Directory.EnumerateFiles(binDir))
            {
                var dest = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, dest, true);
            }

            // Licences travel with the binaries.
            var licence = Directory.EnumerateFiles(extractDir, "LICENSE*", SearchOption.AllDirectories).FirstOrDefault();
            if (licence is not null)
            {
                File.Copy(licence, Path.Combine(AppPaths.FfmpegDir, Path.GetFileName(licence)), true);
            }

            File.WriteAllText(Path.Combine(AppPaths.FfmpegDir, "BUILD.txt"),
                "FFmpeg static build supplied by BtbN (github.com/BtbN/FFmpeg-Builds), GPL variant." +
                Environment.NewLine + "Downloaded " + DateTime.Now.ToString("u") + Environment.NewLine + DownloadUrl);

            Locate();
            if (!IsAvailable)
            {
                return new FfmpegInstallResult(false, "FFmpeg could not be located after extraction.");
            }

            progress?.Report("Engine ready.");
            Log.Info("FFmpeg engine installed to " + destDir);
            return new FfmpegInstallResult(true, null);
        }
        catch (OperationCanceledException)
        {
            return new FfmpegInstallResult(false, "Installation cancelled.");
        }
        catch (Exception ex)
        {
            Log.Error("FFmpeg installation failed", ex);
            return new FfmpegInstallResult(false, ex.Message);
        }
        finally
        {
            IsInstalling = false;
            try { File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true); } catch { }
            _installGate.Release();
        }
    }

    public async Task<string> GetEncodersTextAsync(CancellationToken ct = default)
    {
        if (_encodersText is not null)
        {
            return _encodersText;
        }

        if (!IsAvailable)
        {
            return string.Empty;
        }

        var result = await Proc.RunAsync(FfmpegPath!, new[] { "-hide_banner", "-encoders" }, TimeSpan.FromSeconds(20), ct)
            .ConfigureAwait(false);
        _encodersText = result.StdOut + result.StdErr;
        return _encodersText;
    }

    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        if (!IsAvailable)
        {
            return "not installed";
        }

        var result = await Proc.RunAsync(FfmpegPath!, new[] { "-hide_banner", "-version" }, TimeSpan.FromSeconds(10), ct)
            .ConfigureAwait(false);
        var first = (result.StdOut + result.StdErr).Split('\n').FirstOrDefault();
        return first?.Trim() ?? "unknown";
    }
}
