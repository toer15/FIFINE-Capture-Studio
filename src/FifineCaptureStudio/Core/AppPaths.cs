using System.IO;

namespace FifineCaptureStudio.Core;

/// <summary>
/// Canonical file-system locations used by FIFINE Capture Studio.
/// Everything the application writes lives under %LOCALAPPDATA%\FifineCaptureStudioData,
/// deliberately outside the Velopack installation folder, so an update can replace the
/// application without ever touching recordings, settings or captures.
/// </summary>
public static class AppPaths
{
    /// <summary>Velopack installs into this folder; it must never hold user data.</summary>
    public static string LegacyRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FifineCaptureStudio");

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FifineCaptureStudioData");

    public static string FfmpegDir { get; } = Path.Combine(Root, "ffmpeg");
    public static string LogsDir { get; } = Path.Combine(Root, "logs");
    public static string CacheDir { get; } = Path.Combine(Root, "cache");
    public static string ThumbCacheDir { get; } = Path.Combine(CacheDir, "thumbs");
    public static string ReplayDir { get; } = Path.Combine(Root, "replaybuffer");
    public static string RecordPartsDir { get; } = Path.Combine(Root, "recording-parts");
    public static string SettingsFile { get; } = Path.Combine(Root, "settings.json");

    /// <summary>Last known-good copy of <see cref="SettingsFile"/>, used when the main file is damaged.</summary>
    public static string SettingsBackupFile { get; } = Path.Combine(Root, "settings.backup.json");
    public static string AppDir { get; } = AppContext.BaseDirectory;

    /// <summary>
    /// Preferred recordings location. Some machines (managed or redirected profiles)
    /// deny writes to the Videos known folder, so this falls back to app data instead
    /// of failing at start-up.
    /// </summary>
    public static string DefaultRecordingsDir { get; } = ResolveRecordingsDir();

    private static string ResolveRecordingsDir()
    {
        try
        {
            var videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (!string.IsNullOrWhiteSpace(videos))
            {
                var candidate = Path.Combine(videos, "FIFINE Capture Studio");
                Directory.CreateDirectory(candidate);
                return candidate;
            }
        }
        catch
        {
            // fall through to the app-data location
        }

        var fallback = Path.Combine(Root, "Recordings");
        try
        {
            Directory.CreateDirectory(fallback);
        }
        catch
        {
            // last resort: the app data root itself
        }

        return fallback;
    }

    /// <summary>
    /// Earlier builds stored user data inside the install directory. Move what is ours into the
    /// new root once, without deleting anything we do not recognise.
    /// </summary>
    public static void MigrateLegacyData()
    {
        try
        {
            if (!Directory.Exists(LegacyRoot) || string.Equals(LegacyRoot, Root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var moved = Path.Combine(Root, "legacy-migration.txt");
            if (File.Exists(moved))
            {
                return;
            }

            Directory.CreateDirectory(Root);
            var entries = new[]
            {
                "settings.json", "trusted-devices.json", "logs", "cache", "replaybuffer",
                "recording-parts", "ffmpeg", "Recordings"
            };

            var report = new List<string>();
            foreach (var name in entries)
            {
                var source = Path.Combine(LegacyRoot, name);
                var target = Path.Combine(Root, name);
                try
                {
                    if (File.Exists(source) && !File.Exists(target))
                    {
                        File.Move(source, target);
                        report.Add("moved file " + name);
                    }
                    else if (Directory.Exists(source))
                    {
                        Merge(source, target);
                        report.Add("merged folder " + name);
                    }
                }
                catch (Exception ex)
                {
                    report.Add($"could not move {name}: {ex.Message}");
                }
            }

            File.WriteAllLines(moved, report.DefaultIfEmpty("nothing to migrate"));
            Log.Info("Legacy data migration: " + string.Join("; ", report));
        }
        catch (Exception ex)
        {
            Log.Warn("Legacy data migration failed: " + ex.Message);
        }
    }

    /// <summary>Moves everything from source into target without overwriting existing files.</summary>
    private static void Merge(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (!File.Exists(destination))
            {
                File.Move(file, destination);
            }
        }

        foreach (var folder in Directory.GetDirectories(source))
        {
            Merge(folder, Path.Combine(target, Path.GetFileName(folder)));
        }
    }

    public static void EnsureCreated()
    {
        MigrateLegacyData();

        foreach (var dir in new[] { Root, LogsDir, CacheDir, ThumbCacheDir, ReplayDir, RecordPartsDir, DefaultRecordingsDir })
        {
            try
            {
                Directory.CreateDirectory(dir);
            }
            catch
            {
                // A single unavailable folder must never stop the application.
            }
        }
    }

    /// <summary>Locations searched (in order) for a user-supplied FFmpeg build.</summary>
    public static IEnumerable<string> FfmpegSearchRoots()
    {
        var env = Environment.GetEnvironmentVariable("FIFINE_FFMPEG");
        if (!string.IsNullOrWhiteSpace(env))
        {
            yield return env;
        }

        yield return Path.Combine(AppDir, "ffmpeg.exe");
        yield return Path.Combine(AppDir, "tools", "ffmpeg.exe");
        yield return Path.Combine(AppDir, "ffmpeg", "bin", "ffmpeg.exe");
        yield return Path.Combine(FfmpegDir, "bin", "ffmpeg.exe");
    }
}
