using System.IO;

namespace FifineCaptureStudio.Core;

/// <summary>
/// Canonical file-system locations used by FIFINE Capture Studio.
/// Everything the application writes lives under %LOCALAPPDATA%\FifineCaptureStudioData,
/// deliberately outside the Velopack installation folder, so an update can replace the
/// application without ever touching recordings, settings or captures.
/// </summary>
public sealed record LegacyMigrationResult(bool Complete, IReadOnlyList<string> Report);

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
    /// new root, without deleting anything we do not recognise. A partial run records pending and
    /// is retried on the next start; a complete run is not repeated.
    /// </summary>
    public static void MigrateLegacyData() => MigrateLegacyData(LegacyRoot, Root);

    /// <summary>Testable migration with explicit roots. Existing destination data is never replaced.</summary>
    public static LegacyMigrationResult MigrateLegacyData(string legacyRoot, string root)
    {
        var report = new List<string>();
        try
        {
            if (!Directory.Exists(legacyRoot) || string.Equals(legacyRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                return new LegacyMigrationResult(true, report);
            }

            var marker = Path.Combine(root, "legacy-migration.txt");
            if (IsMigrationComplete(marker))
            {
                return new LegacyMigrationResult(true, report);
            }

            Directory.CreateDirectory(root);
            var entries = new[]
            {
                "settings.json", "settings.backup.json", "settings.broken.json", "trusted-devices.json",
                "logs", "cache", "replaybuffer", "recording-parts", "ffmpeg", "Recordings"
            };

            var failures = new List<string>();
            foreach (var name in entries)
            {
                var source = Path.Combine(legacyRoot, name);
                var target = Path.Combine(root, name);
                try
                {
                    if (File.Exists(source))
                    {
                        if (File.Exists(target))
                        {
                            report.Add("kept existing " + name);
                        }
                        else
                        {
                            if (Directory.Exists(target))
                            {
                                throw new IOException("the destination path is a directory");
                            }

                            File.Move(source, target);
                            report.Add("moved file " + name);
                        }
                    }
                    else if (Directory.Exists(source))
                    {
                        if (File.Exists(target))
                        {
                            throw new IOException("the destination path is a file");
                        }

                        Merge(source, target, name, report, failures);
                    }
                }
                catch (Exception ex)
                {
                    failures.Add($"could not move {name}: {ex.Message}");
                }
            }

            var complete = failures.Count == 0;
            var lines = new List<string>
            {
                "status=" + (complete ? "complete" : "pending"),
                "updatedUtc=" + DateTime.UtcNow.ToString("O")
            };
            lines.AddRange(report);
            lines.AddRange(failures);
            WriteMigrationMarker(marker, lines);

            var summary = string.Join("; ", lines.Skip(2));
            Log.Info($"Legacy data migration [{(complete ? "complete" : "pending")}]: " +
                     (string.IsNullOrEmpty(summary) ? "nothing to migrate" : summary));
            return new LegacyMigrationResult(complete, lines.AsReadOnly());
        }
        catch (Exception ex)
        {
            report.Add("migration failed: " + ex.Message);
            Log.Warn("Legacy data migration failed: " + ex.Message);
            return new LegacyMigrationResult(false, report.AsReadOnly());
        }
    }

    private static bool IsMigrationComplete(string marker)
    {
        try
        {
            return File.Exists(marker) &&
                   File.ReadLines(marker).FirstOrDefault()?.Trim()
                       .Equals("status=complete", StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception ex)
        {
            Log.Warn("Legacy migration marker could not be read: " + ex.Message);
            return false;
        }
    }

    private static void WriteMigrationMarker(string marker, IReadOnlyList<string> lines)
    {
        var temporary = marker + ".tmp";
        try
        {
            File.WriteAllLines(temporary, lines);
            File.Move(temporary, marker, true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch
            {
                // Best effort; a missing marker simply makes the next start retry safely.
            }

            throw;
        }
    }

    /// <summary>
    /// Moves everything from source into target without overwriting existing files. A single
    /// locked file is recorded as a failure but does not stop the rest of the migration.
    /// </summary>
    private static void Merge(
        string source,
        string target,
        string relative,
        List<string> report,
        List<string> failures)
    {
        try
        {
            Directory.CreateDirectory(target);
        }
        catch (Exception ex)
        {
            failures.Add($"could not create {relative}: {ex.Message}");
            return;
        }

        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            var destination = Path.Combine(target, name);
            try
            {
                if (File.Exists(destination))
                {
                    report.Add("kept existing " + Path.Combine(relative, name));
                }
                else if (Directory.Exists(destination))
                {
                    failures.Add($"could not move {Path.Combine(relative, name)}: the destination path is a directory");
                }
                else
                {
                    File.Move(file, destination);
                    report.Add("moved " + Path.Combine(relative, name));
                }
            }
            catch (Exception ex)
            {
                failures.Add($"could not move {Path.Combine(relative, name)}: {ex.Message}");
            }
        }

        foreach (var folder in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(folder);
            var destination = Path.Combine(target, name);
            if (File.Exists(destination))
            {
                failures.Add($"could not merge {Path.Combine(relative, name)}: the destination path is a file");
                continue;
            }

            Merge(folder, destination, Path.Combine(relative, name), report, failures);
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
