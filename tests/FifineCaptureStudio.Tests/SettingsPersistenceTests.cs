using System.IO;
using System.Text.Json;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.Enhancement;
using Xunit;

namespace FifineCaptureStudio.Tests;

/// <summary>
/// Settings must survive restarts and app updates. These tests exercise the real save/load path in a
/// temporary folder: round-trip, unknown fields, corruption recovery and migration from schema 5.
/// </summary>
public class SettingsPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "fifine-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }
        catch
        {
            // best effort
        }
    }

    [Fact]
    public void Every_user_change_survives_a_restart()
    {
        var first = new SettingsService(_root);
        first.Current.General.Language = "English";
        first.Current.General.Notifications = false;
        first.Current.Recording.Codec = VideoCodecKind.HEVC;
        first.Current.Recording.BitrateKbps = 60000;
        first.Current.Capture.Platform = PlatformKind.Xbox;
        first.Current.Audio.MonitoringVolume = 0.55;
        first.Current.Hotkeys.ToggleRecording = "Ctrl+Shift+R";
        first.Current.Enhancement.Enabled = true;
        first.Current.Enhancement.Target = SuperResolutionTarget.P1440;
        first.Current.Ui.WindowLeft = 120;
        first.Current.Ui.WindowTop = 80;
        first.Current.Ui.WindowWidth = 1720;
        first.Current.Ui.WindowHeight = 980;
        first.Current.Ui.WindowMaximized = true;
        first.Current.Ui.LastPage = "enhance";
        first.Current.Ui.PreviewScale = 0.75;
        first.Current.Ui.KeepAspect = false;
        first.Current.Ui.MinimalPreview = true;
        first.Current.Ui.QuickPanelOpen = true;
        first.Current.Ui.SettingsCategory = "Recording";
        first.Save();

        var second = new SettingsService(_root);

        Assert.False(second.Current.General.Notifications);
        Assert.Equal(VideoCodecKind.HEVC, second.Current.Recording.Codec);
        Assert.Equal(60000, second.Current.Recording.BitrateKbps);
        Assert.Equal(PlatformKind.Xbox, second.Current.Capture.Platform);
        Assert.Equal(0.55, second.Current.Audio.MonitoringVolume);
        Assert.Equal("Ctrl+Shift+R", second.Current.Hotkeys.ToggleRecording);
        Assert.True(second.Current.Enhancement.Enabled);
        Assert.Equal(SuperResolutionTarget.P1440, second.Current.Enhancement.Target);

        Assert.Equal(120, second.Current.Ui.WindowLeft);
        Assert.Equal(80, second.Current.Ui.WindowTop);
        Assert.Equal(1720, second.Current.Ui.WindowWidth);
        Assert.Equal(980, second.Current.Ui.WindowHeight);
        Assert.True(second.Current.Ui.WindowMaximized);
        Assert.Equal("enhance", second.Current.Ui.LastPage);
        Assert.Equal(0.75, second.Current.Ui.PreviewScale);
        Assert.False(second.Current.Ui.KeepAspect);
        Assert.True(second.Current.Ui.MinimalPreview);
        Assert.True(second.Current.Ui.QuickPanelOpen);
        Assert.Equal("Recording", second.Current.Ui.SettingsCategory);
    }

    [Fact]
    public void Fields_written_by_a_newer_build_are_not_deleted()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "settings.json"), """
            {
              "schemaVersion": 5,
              "general": { "language": "English", "notifications": true },
              "futureOnlySetting": { "enabled": true, "mode": "hdr" }
            }
            """);

        var service = new SettingsService(_root);
        service.Save();

        var json = File.ReadAllText(Path.Combine(_root, "settings.json"));
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("futureOnlySetting", out var future));
        Assert.True(future.GetProperty("enabled").GetBoolean());
        Assert.Equal("hdr", future.GetProperty("mode").GetString());
    }

    [Fact]
    public void A_damaged_settings_file_falls_back_to_the_last_good_copy()
    {
        var first = new SettingsService(_root);
        first.Current.Ui.LastPage = "audio";
        first.Current.Recording.BitrateKbps = 44000;
        first.Save();

        // Simulate a torn write / disk corruption in the live file.
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{ \"schemaVersion\": 6, \"general\": ");

        var recovered = new SettingsService(_root);

        Assert.Equal("audio", recovered.Current.Ui.LastPage);
        Assert.Equal(44000, recovered.Current.Recording.BitrateKbps);
        Assert.True(File.Exists(Path.Combine(_root, "settings.broken.json")));
    }

    [Fact]
    public void A_file_from_schema_5_migrates_without_losing_anything()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "settings.json"), """
            {
              "schemaVersion": 5,
              "general": { "language": "English", "showWizardOnStartup": false },
              "recording": { "bitrateKbps": 50000, "codec": "AV1" },
              "enhancement": { "enabled": true, "target": "P2160", "profile": "Cinematic" },
              "wizardCompleted": true
            }
            """);

        var service = new SettingsService(_root);
        var settings = service.Current;

        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal(50000, settings.Recording.BitrateKbps);
        Assert.Equal(VideoCodecKind.AV1, settings.Recording.Codec);
        Assert.True(settings.Enhancement.Enabled);
        Assert.Equal(SuperResolutionTarget.P2160, settings.Enhancement.Target);
        Assert.Equal(EnhancementProfile.Cinematic, settings.Enhancement.Profile);
        Assert.True(settings.WizardCompleted);

        // Schema 6 UI state is added with usable defaults, not nulls.
        Assert.Equal("dashboard", settings.Ui.LastPage);
        Assert.Equal(1.0, settings.Ui.PreviewScale);
        Assert.True(settings.Ui.KeepAspect);
        Assert.Equal(1600, settings.Ui.WindowWidth);
        Assert.Equal(920, settings.Ui.WindowHeight);
    }

    [Fact]
    public void Flush_writes_a_coalesced_change_immediately()
    {
        var service = new SettingsService(_root);
        service.Current.Ui.LastPage = "performance";
        service.SaveSoon();
        service.Flush();

        var reloaded = new SettingsService(_root);
        Assert.Equal("performance", reloaded.Current.Ui.LastPage);
    }

    [Fact]
    public void Deleting_the_settings_file_does_not_leave_the_next_run_without_a_backup()
    {
        var service = new SettingsService(_root);
        service.Current.Capture.Platform = PlatformKind.PS5;
        service.Save();

        File.Delete(Path.Combine(_root, "settings.json"));

        var reloaded = new SettingsService(_root);
        Assert.Equal(PlatformKind.PS5, reloaded.Current.Capture.Platform);
    }
}
