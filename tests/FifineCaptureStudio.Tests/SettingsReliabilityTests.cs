using System.IO;
using System.Text.Json;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.Updates;
using Xunit;

namespace FifineCaptureStudio.Tests;

/// <summary>
/// Reliability tests for the real settings file paths. Each test owns a unique temporary folder so
/// failure injection cannot affect another test.
/// </summary>
public class SettingsReliabilityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fifine-reliability-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            File.SetAttributes(_root, FileAttributes.Normal);
        }
        catch
        {
            // The path may be a directory or may already be gone.
        }

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
            else if (File.Exists(_root))
            {
                File.Delete(_root);
            }
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public void A_failed_primary_save_is_reported_to_the_caller()
    {
        File.WriteAllText(_root, "this path deliberately blocks directory creation");
        var service = new SettingsService(_root);

        var result = service.TrySave();

        Assert.False(result.Success);
        Assert.False(string.IsNullOrWhiteSpace(result.ErrorMessage));
        Assert.Contains("settings.json", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(service.TryFlush().Success);
    }

    [Fact]
    public void A_flush_failure_prevents_the_update_gate_from_proceeding()
    {
        File.WriteAllText(_root, "this path deliberately blocks directory creation");
        var settings = new SettingsService(_root);
        var updates = new UpdateService(settings);

        var canUpdate = updates.TryPersistBeforeUpdate(out var refusal);

        Assert.False(canUpdate);
        Assert.Equal(UpdateStage.Failed, updates.Stage);
        Assert.Contains("not applied", refusal, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("settings", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_failed_backup_refresh_keeps_the_previous_valid_backup()
    {
        var service = new SettingsService(_root);
        service.Current.Ui.LastPage = "audio";
        service.Current.Recording.BitrateKbps = 44000;
        Assert.True(service.TrySave().Success);

        var backup = Path.Combine(_root, "settings.backup.json");
        var previousBackup = File.ReadAllText(backup);

        // Holding the backup open without delete sharing makes the atomic replace fail, just like a
        // scanner or another process holding the file while the app refreshes it.
        using (var backupLock = new FileStream(backup, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            service.Current.Ui.LastPage = "performance";
            var result = service.TrySave();

            Assert.True(result.Success);
            Assert.False(string.IsNullOrWhiteSpace(result.Warning));
            Assert.Contains("backup", result.Warning, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(previousBackup, File.ReadAllText(backup));
        }

        var reloaded = new SettingsService(_root);
        Assert.Equal("performance", reloaded.Current.Ui.LastPage);
    }

    [Fact]
    public void A_damaged_main_file_is_recovered_and_settings_json_is_recreated()
    {
        var first = new SettingsService(_root);
        first.Current.Ui.LastPage = "audio";
        first.Current.Recording.BitrateKbps = 44000;
        Assert.True(first.TrySave().Success);

        var primary = Path.Combine(_root, "settings.json");
        var backup = Path.Combine(_root, "settings.backup.json");
        var backupBeforeRecovery = File.ReadAllText(backup);
        File.WriteAllText(primary, "{ \"schemaVersion\": 6, \"general\": ");

        var recovered = new SettingsService(_root);

        Assert.Equal("audio", recovered.Current.Ui.LastPage);
        Assert.Equal(44000, recovered.Current.Recording.BitrateKbps);
        Assert.True(File.Exists(Path.Combine(_root, "settings.broken.json")));
        Assert.True(File.Exists(primary));
        Assert.Equal(backupBeforeRecovery, File.ReadAllText(backup));
        using var document = JsonDocument.Parse(File.ReadAllText(primary));
        Assert.Equal(44000, document.RootElement.GetProperty("recording").GetProperty("bitrateKbps").GetInt32());
    }

    [Fact]
    public void A_missing_main_file_is_recovered_from_the_backup_and_recreated()
    {
        var first = new SettingsService(_root);
        first.Current.Capture.Platform = PlatformKind.Xbox;
        first.Current.Ui.LastPage = "library";
        Assert.True(first.TrySave().Success);

        var primary = Path.Combine(_root, "settings.json");
        var backup = Path.Combine(_root, "settings.backup.json");
        var backupBeforeRecovery = File.ReadAllText(backup);
        File.Delete(primary);

        var recovered = new SettingsService(_root);

        Assert.Equal(PlatformKind.Xbox, recovered.Current.Capture.Platform);
        Assert.Equal("library", recovered.Current.Ui.LastPage);
        Assert.True(File.Exists(primary));
        Assert.Equal(backupBeforeRecovery, File.ReadAllText(backup));
        using var document = JsonDocument.Parse(File.ReadAllText(primary));
        Assert.Equal("Xbox", document.RootElement.GetProperty("capture").GetProperty("platform").GetString());
    }

    [Fact]
    public void Unknown_properties_inside_nested_settings_survive_load_and_save()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "settings.json"), """
            {
              "schemaVersion": 6,
              "general": { "futureGeneral": { "enabled": true } },
              "capture": { "futureCapture": "new-source-mode" },
              "recording": { "futureRecording": [1, 2, 3] },
              "audio": {
                "futureAudio": { "voiceMix": 0.75 },
                "channels": [
                  { "role": "game", "futureChannel": { "gain": 2 } }
                ]
              },
              "enhancement": { "futureEnhancement": { "model": "v2" } },
              "ui": { "futureUi": { "layout": "compact" } },
              "hotkeys": { "futureHotkey": "Ctrl+Alt+X" },
              "updates": { "futureUpdate": { "channel": "nightly" } },
              "playStation": { "futurePlayStation": { "codec": "av1" } },
              "xbox": { "futureXbox": { "streamMode": "low-latency" } },
              "frameGeneration": { "futureFrameGeneration": 2 },
              "fpsMonitor": { "futureFpsMonitor": true },
              "futureTopLevel": { "keep": "me" }
            }
            """);

        var service = new SettingsService(_root);
        Assert.True(service.TrySave().Success);

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "settings.json")));
        var root = document.RootElement;
        Assert.True(root.GetProperty("general").GetProperty("futureGeneral").GetProperty("enabled").GetBoolean());
        Assert.Equal("new-source-mode", root.GetProperty("capture").GetProperty("futureCapture").GetString());
        Assert.Equal(3, root.GetProperty("recording").GetProperty("futureRecording").GetArrayLength());
        Assert.Equal(0.75, root.GetProperty("audio").GetProperty("futureAudio").GetProperty("voiceMix").GetDouble());
        Assert.Equal(2, root.GetProperty("audio").GetProperty("channels")[0].GetProperty("futureChannel").GetProperty("gain").GetInt32());
        Assert.Equal("v2", root.GetProperty("enhancement").GetProperty("futureEnhancement").GetProperty("model").GetString());
        Assert.Equal("compact", root.GetProperty("ui").GetProperty("futureUi").GetProperty("layout").GetString());
        Assert.Equal("Ctrl+Alt+X", root.GetProperty("hotkeys").GetProperty("futureHotkey").GetString());
        Assert.Equal("nightly", root.GetProperty("updates").GetProperty("futureUpdate").GetProperty("channel").GetString());
        Assert.Equal("av1", root.GetProperty("playStation").GetProperty("futurePlayStation").GetProperty("codec").GetString());
        Assert.Equal("low-latency", root.GetProperty("xbox").GetProperty("futureXbox").GetProperty("streamMode").GetString());
        Assert.Equal(2, root.GetProperty("frameGeneration").GetProperty("futureFrameGeneration").GetInt32());
        Assert.True(root.GetProperty("fpsMonitor").GetProperty("futureFpsMonitor").GetBoolean());
        Assert.Equal("me", root.GetProperty("futureTopLevel").GetProperty("keep").GetString());
    }

    [Fact]
    public void Failed_legacy_migration_is_retried_without_overwriting_newer_data()
    {
        var legacy = Path.Combine(_root, "legacy");
        var root = Path.Combine(_root, "new");
        Directory.CreateDirectory(legacy);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"schemaVersion\":6}");
        File.WriteAllText(Path.Combine(root, "settings.backup.json"), "newer backup");
        Directory.CreateDirectory(Path.Combine(root, "settings.json"));

        var failed = AppPaths.MigrateLegacyData(legacy, root);

        Assert.False(failed.Complete);
        Assert.True(File.Exists(Path.Combine(legacy, "settings.json")));
        var marker = Path.Combine(root, "legacy-migration.txt");
        Assert.Equal("status=pending", File.ReadLines(marker).First());
        Assert.Equal("newer backup", File.ReadAllText(Path.Combine(root, "settings.backup.json")));

        Directory.Delete(Path.Combine(root, "settings.json"));
        var retried = AppPaths.MigrateLegacyData(legacy, root);

        Assert.True(retried.Complete);
        Assert.False(File.Exists(Path.Combine(legacy, "settings.json")));
        Assert.True(File.Exists(Path.Combine(root, "settings.json")));
        Assert.Equal("status=complete", File.ReadLines(marker).First());
        Assert.Equal("newer backup", File.ReadAllText(Path.Combine(root, "settings.backup.json")));
    }

    [Fact]
    public void A_successful_restart_and_update_gate_preserve_settings()
    {
        var first = new SettingsService(_root);
        first.Current.General.Notifications = false;
        first.Current.Recording.BitrateKbps = 50000;
        first.Current.Ui.LastPage = "recording";
        Assert.True(first.TrySave().Success);

        var updates = new UpdateService(first);
        Assert.True(updates.TryPersistBeforeUpdate(out var refusal));
        Assert.Equal(string.Empty, refusal);

        var restarted = new SettingsService(_root);
        Assert.False(restarted.Current.General.Notifications);
        Assert.Equal(50000, restarted.Current.Recording.BitrateKbps);
        Assert.Equal("recording", restarted.Current.Ui.LastPage);
    }

    [Fact]
    public void Stale_temporary_files_are_cleaned_up_without_touching_new_ones()
    {
        Directory.CreateDirectory(_root);
        var stale = Path.Combine(_root, "settings.json.tmp");
        var fresh = Path.Combine(_root, "settings.backup.json.tmp");
        File.WriteAllText(stale, "partial");
        File.WriteAllText(fresh, "active");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-1));

        _ = new SettingsService(_root);

        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(fresh));
    }
}
