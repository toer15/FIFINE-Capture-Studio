using System.IO;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.Pairing;
using Xunit;

namespace FifineCaptureStudio.Tests;

public class SettingsMigrationTests
{
    [Fact]
    public void SchemaOneSettingsGetUpdateDefaults()
    {
        var legacy = new AppSettings { SchemaVersion = 1, Updates = null! };

        AppSettings.Normalize(legacy);

        Assert.Equal(AppSettings.CurrentSchemaVersion, legacy.SchemaVersion);
        Assert.NotNull(legacy.Updates);
        Assert.True(legacy.Updates.AutoCheckEnabled);
        Assert.True(legacy.Updates.AutoDownload);
        Assert.Equal(UpdateChannel.Stable, legacy.Updates.Channel);
        Assert.Equal(360, legacy.Updates.CheckIntervalMinutes);
    }

    [Fact]
    public void FreshInstallStillGetsOnboardingOnce()
    {
        var fresh = new AppSettings();

        AppSettings.Normalize(fresh);

        Assert.False(fresh.WizardCompleted);
        Assert.True(fresh.General.ShowWizardOnStartup);
    }

    [Fact]
    public void DismissedOnboardingFromOlderBuildStaysDismissed()
    {
        var legacy = new AppSettings { SchemaVersion = 3, WizardCompleted = false };
        legacy.General.ShowWizardOnStartup = false;

        AppSettings.Normalize(legacy);

        Assert.Equal(AppSettings.CurrentSchemaVersion, legacy.SchemaVersion);
        Assert.True(legacy.WizardCompleted);
    }

    [Fact]
    public void CompletedOnboardingSurvivesNormalize()
    {
        var settings = new AppSettings { SchemaVersion = 4, WizardCompleted = true };
        settings.General.ShowWizardOnStartup = true;

        AppSettings.Normalize(settings);

        Assert.True(settings.WizardCompleted);
    }

    [Fact]
    public void ExistingChoicesSurviveNormalize()
    {
        var settings = new AppSettings
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            Updates = new UpdateSettings
            {
                AutoCheckEnabled = false,
                AutoDownload = false,
                Channel = UpdateChannel.Beta,
                CheckIntervalMinutes = 1440,
                Repository = "  toer15/FIFINE-Capture-Studio  "
            }
        };

        AppSettings.Normalize(settings);

        Assert.False(settings.Updates.AutoCheckEnabled);
        Assert.False(settings.Updates.AutoDownload);
        Assert.Equal(UpdateChannel.Beta, settings.Updates.Channel);
        Assert.Equal(1440, settings.Updates.CheckIntervalMinutes);
        Assert.Equal("toer15/FIFINE-Capture-Studio", settings.Updates.Repository);
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(0, 30)]
    [InlineData(999999, 10080)]
    [InlineData(720, 720)]
    public void CheckIntervalIsClamped(int input, int expected)
    {
        var settings = new AppSettings
        {
            Updates = new UpdateSettings { CheckIntervalMinutes = input }
        };

        AppSettings.Normalize(settings);

        Assert.Equal(expected, settings.Updates.CheckIntervalMinutes);
    }

    [Fact]
    public void RecordingsDirectoryDefaultsWhenEmpty()
    {
        var settings = new AppSettings { Recording = new RecordingSettings { Directory = "  " } };

        AppSettings.Normalize(settings);

        Assert.False(string.IsNullOrWhiteSpace(settings.Recording.Directory));
    }

    [Fact]
    public void AppDataRecordingDirectoryIsNotMigratedRepeatedly()
    {
        var expected = Path.Combine(AppPaths.Root, "Recordings");
        var settings = new AppSettings
        {
            Recording = new RecordingSettings { Directory = expected }
        };

        AppSettings.Normalize(settings);
        AppSettings.Normalize(settings);

        Assert.Equal(expected, settings.Recording.Directory);
    }

    [Fact]
    public void RepeatedAppDataRootFromOldMigrationIsRepaired()
    {
        var corrupted = AppPaths.Root + "DataDataDataData" + Path.DirectorySeparatorChar + "Recordings";
        var expected = Path.Combine(AppPaths.Root, "Recordings");
        var settings = new AppSettings
        {
            Recording = new RecordingSettings { Directory = corrupted }
        };

        AppSettings.Normalize(settings);

        Assert.Equal(expected, settings.Recording.Directory);
    }

    [Fact]
    public void LegacyInstallRecordingDirectoryMovesToAppDataOnce()
    {
        if (string.Equals(AppPaths.LegacyRoot, AppPaths.Root, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var legacy = Path.Combine(AppPaths.LegacyRoot, "Recordings");
        var expected = Path.Combine(AppPaths.Root, "Recordings");
        var settings = new AppSettings
        {
            Recording = new RecordingSettings { Directory = legacy }
        };

        AppSettings.Normalize(settings);
        AppSettings.Normalize(settings);

        Assert.Equal(expected, settings.Recording.Directory);
    }
}

public class PairingCodeTests
{
    [Theory]
    [InlineData("12345678", "12345678")]
    [InlineData(" 12ab34 56 ", "123456")]
    [InlineData("", "")]
    public void CodesAreDigitsOnly(string input, string expected) =>
        Assert.Equal(expected, PairingCode.Normalize(input));

    [Fact]
    public void EmptyCodeIsNotUsable() => Assert.False(PairingCode.IsUsable(""));
}

public class PlatformCatalogTests
{
    [Fact]
    public void EveryPlatformEntryHasActionableData()
    {
        foreach (var entry in PlatformCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Methods));
            Assert.False(string.IsNullOrWhiteSpace(entry.Notes));
            Assert.False(string.IsNullOrWhiteSpace(entry.StateLabel));
            Assert.StartsWith("#FF", entry.StateColor);
        }
    }

    [Fact]
    public void ConsolesNeverClaimToBeFullySupported()
    {
        var consoles = PlatformCatalog.All.Where(p => p.Family != PlatformCatalog.Pc);

        Assert.All(consoles, entry => Assert.NotEqual(CompatibilityState.FullySupported, entry.State));
    }
}
