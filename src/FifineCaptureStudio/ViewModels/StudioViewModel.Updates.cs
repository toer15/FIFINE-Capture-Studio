using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.Updates;

namespace FifineCaptureStudio.ViewModels;

public sealed partial class StudioViewModel
{
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromMinutes(30) };
    private bool _updatesInitialised;
    private bool _bannerDismissed;

    // ---------------------------------------------------------------- state

    public string InstalledVersion => _services.Updates.InstalledVersion;
    public string BuildNumber => _services.Updates.BuildNumber;
    public string UpdateRepositoryUrl => _services.Updates.RepositoryUrl;
    public string UpdateRepositoryLabel => string.IsNullOrEmpty(UpdateRepositoryUrl) ? "Not configured" : UpdateRepositoryUrl;

    public UpdateStage UpdateStage => _services.Updates.Stage;

    public string UpdateStatusText => UpdateStage switch
    {
        UpdateStage.Checking => "Checking for updates",
        UpdateStage.Downloading => "Downloading update",
        UpdateStage.Available => "Update available",
        UpdateStage.ReadyToInstall => "Update ready to install",
        UpdateStage.UpToDate => "Up to date",
        UpdateStage.Recording => "Restart required after recording",
        UpdateStage.NotInstalled => "Install the packaged build to update",
        UpdateStage.NotConfigured => "Update source not configured",
        UpdateStage.Failed => "Update failed",
        _ => "Not checked yet"
    };

    public string UpdateStatusColor => UpdateStage switch
    {
        UpdateStage.UpToDate => "#FF45D69A",
        UpdateStage.ReadyToInstall => "#FF7CF0BE",
        UpdateStage.Available => "#FF35C9FF",
        UpdateStage.Checking or UpdateStage.Downloading => "#FF8B7BFF",
        UpdateStage.Failed or UpdateStage.Recording => "#FFFFC46B",
        _ => "#FF8E9AB2"
    };

    public string UpdateMessage => _services.Updates.Message;
    public string? LatestVersion => _services.Updates.LatestVersion;
    public string? ReleaseNotes => _services.Updates.ReleaseNotes;
    public bool HasReleaseNotes => !string.IsNullOrWhiteSpace(ReleaseNotes);

    public bool IsCheckingUpdates => UpdateStage == UpdateStage.Checking;
    public bool IsDownloadingUpdate => UpdateStage == UpdateStage.Downloading;
    public bool IsUpdateAvailable => UpdateStage is UpdateStage.Available or UpdateStage.Downloading or UpdateStage.ReadyToInstall;
    public bool IsUpdateReady => UpdateStage == UpdateStage.ReadyToInstall;
    public bool CanDownloadUpdate => UpdateStage == UpdateStage.Available;
    public bool IsUpdateBusy => IsCheckingUpdates || IsDownloadingUpdate;

    public double UpdateProgress => _services.Updates.DownloadPercent;
    public string UpdateProgressText => $"{_services.Updates.DownloadPercent:0}%";

    public string DownloadSpeedText
    {
        get
        {
            var speed = _services.Updates.DownloadSpeedBytesPerSecond;
            return speed > 0 ? $"{speed / 1024 / 1024:0.0} MB/s" : "—";
        }
    }

    public string DownloadSizeText
    {
        get
        {
            var size = _services.Updates.PackageSize;
            if (size is null or <= 0)
            {
                return "Size unknown";
            }

            var downloaded = size.Value * _services.Updates.DownloadPercent / 100.0;
            var total = size.Value / 1024.0 / 1024.0;
            var done = downloaded / 1024.0 / 1024.0;
            return $"{done:0.0} / {total:0.0} MB  ·  {total - done:0.0} MB left";
        }
    }

    public string UpdateVersionHeadline => IsUpdateAvailable && LatestVersion is not null
        ? $"Version {LatestVersion} is available"
        : "FIFINE Capture Studio is up to date";

    public string UpdateVersionsLine => LatestVersion is null
        ? $"Installed {InstalledVersion}"
        : $"Installed {InstalledVersion}  →  {LatestVersion}";

    public string LastCheckText => Config.Updates.LastCheckUtc is { } last
        ? $"Last checked {last.ToLocalTime():dd MMM yyyy HH:mm}"
        : "Never checked";

    public IReadOnlyList<string> UpdateChannels { get; } = new[] { "Stable", "Beta" };

    public IReadOnlyList<int> UpdateIntervalOptions { get; } = new[] { 30, 60, 180, 360, 720, 1440 };

    public IReadOnlyList<string> UpdateIntervalLabels { get; } = new[]
    {
        "30 minutes", "1 hour", "3 hours", "6 hours", "12 hours", "24 hours"
    };

    public string SelectedUpdateIntervalLabel
    {
        get
        {
            var index = UpdateIntervalOptions.ToList().IndexOf(UpdateIntervalMinutes);
            return index < 0 ? $"{UpdateIntervalMinutes} min" : UpdateIntervalLabels[index];
        }
        set
        {
            var index = UpdateIntervalLabels.ToList().IndexOf(value ?? string.Empty);
            if (index >= 0)
            {
                UpdateIntervalMinutes = UpdateIntervalOptions[index];
            }
        }
    }

    public string SelectedUpdateChannel
    {
        get => Config.Updates.Channel == UpdateChannel.Beta ? "Beta" : "Stable";
        set
        {
            var channel = string.Equals(value, "Beta", StringComparison.OrdinalIgnoreCase)
                ? UpdateChannel.Beta
                : UpdateChannel.Stable;
            if (channel == Config.Updates.Channel)
            {
                return;
            }

            Config.Updates.Channel = channel;
            _services.Settings.Save();
            _services.Updates.Rebuild();
            Raise();
            Raise(nameof(SelectedUpdateChannel));
            Raise(nameof(UpdateChannelNote));
            _ = CheckForUpdatesAsync(true);
        }
    }

    public string UpdateChannelNote => Config.Updates.Channel == UpdateChannel.Beta
        ? "Beta follows GitHub pre-releases (tags such as v1.1.0-beta.1). It is configured by the release workflow."
        : "Stable follows full GitHub releases only. Beta pre-releases are ignored.";

    public bool AutoUpdateCheck
    {
        get => Config.Updates.AutoCheckEnabled;
        set
        {
            Config.Updates.AutoCheckEnabled = value;
            _services.Settings.Save();
            ApplyUpdateSchedule();
            Raise();
        }
    }

    public bool AutoDownloadUpdates
    {
        get => Config.Updates.AutoDownload;
        set
        {
            Config.Updates.AutoDownload = value;
            _services.Settings.Save();
            Raise();
        }
    }

    public int UpdateIntervalMinutes
    {
        get => Config.Updates.CheckIntervalMinutes;
        set
        {
            Config.Updates.CheckIntervalMinutes = Math.Clamp(value, 30, 10080);
            _services.Settings.Save();
            ApplyUpdateSchedule();
            Raise();
            Raise(nameof(UpdateIntervalLabel));
        }
    }

    public string UpdateIntervalLabel => Config.Updates.CheckIntervalMinutes >= 60
        ? $"{Config.Updates.CheckIntervalMinutes / 60} h"
        : $"{Config.Updates.CheckIntervalMinutes} min";

    // ---------------------------------------------------------------- commands

    private bool _showReleaseNotes;

    public bool ShowReleaseNotes
    {
        get => _showReleaseNotes;
        private set
        {
            _showReleaseNotes = value;
            Raise();
        }
    }

    public ICommand ToggleReleaseNotesCommand => new RelayCommand(() => ShowReleaseNotes = !ShowReleaseNotes);

    public ICommand CheckUpdatesCommand => new AsyncRelayCommand(() => CheckForUpdatesAsync(true));
    public ICommand DownloadUpdateCommand => new AsyncRelayCommand(() => _services.Updates.DownloadAsync());
    public ICommand RemindLaterCommand => new RelayCommand(() =>
    {
        _bannerDismissed = true;
        if (LatestVersion is { } version)
        {
            _services.Updates.MarkReminded(version);
        }

        Raise(nameof(ShowUpdateBanner));
    });

    public ICommand InstallUpdateCommand => new RelayCommand(() =>
    {
        if (!_services.Updates.ApplyAndRestart(_services.Session.IsRecording, out var refusal))
        {
            if (!string.IsNullOrEmpty(refusal))
            {
                Notify(refusal, true);
            }
        }
    });

    public ICommand OpenUpdateRepositoryCommand => new RelayCommand(() =>
    {
        var url = UpdateRepositoryUrl;
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify("Could not open the repository: " + ex.Message, true);
        }
    });

    public bool ShowUpdateBanner =>
        !_bannerDismissed && UpdateStage is UpdateStage.Available or UpdateStage.Downloading or UpdateStage.ReadyToInstall;

    // ---------------------------------------------------------------- wiring

    private void InitializeUpdates()
    {
        if (_updatesInitialised)
        {
            return;
        }

        _updatesInitialised = true;
        _services.Updates.Changed += OnUpdateServiceChanged;
        _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(false);
        ApplyUpdateSchedule();
        RaiseUpdateState();

        if (Config.Updates.AutoCheckEnabled)
        {
            // Background check a few seconds after start so the UI is already responsive.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                await CheckForUpdatesAsync(false);
            };
            timer.Start();
        }
    }

    private void ApplyUpdateSchedule()
    {
        _updateTimer.Stop();
        if (!Config.Updates.AutoCheckEnabled)
        {
            return;
        }

        _updateTimer.Interval = TimeSpan.FromMinutes(Math.Clamp(Config.Updates.CheckIntervalMinutes, 30, 10080));
        _updateTimer.Start();
    }

    private async Task CheckForUpdatesAsync(bool userInitiated)
    {
        _bannerDismissed = false;
        if (userInitiated && !_services.Updates.IsConfigured)
        {
            Notify("This build has no update repository configured.", true);
        }

        await _services.Updates.CheckAsync(userInitiated);
        RaiseUpdateState();
    }

    private void OnUpdateServiceChanged()
    {
        Dispatch(() =>
        {
            RaiseUpdateState();
            if (_services.Updates.Stage == UpdateStage.NotInstalled && !_updatesInitialised)
            {
                return;
            }

            if (_services.Updates.Stage == UpdateStage.ReadyToInstall)
            {
                Notify($"Update {LatestVersion} is ready. Restart to install it.", false);
            }
        });
    }

    private void RaiseUpdateState()
    {
        Raise(nameof(UpdateStage));
        Raise(nameof(UpdateStatusText));
        Raise(nameof(UpdateStatusColor));
        Raise(nameof(UpdateMessage));
        Raise(nameof(LatestVersion));
        Raise(nameof(ReleaseNotes));
        Raise(nameof(HasReleaseNotes));
        Raise(nameof(ShowReleaseNotes));
        Raise(nameof(IsCheckingUpdates));
        Raise(nameof(IsDownloadingUpdate));
        Raise(nameof(IsUpdateAvailable));
        Raise(nameof(IsUpdateReady));
        Raise(nameof(CanDownloadUpdate));
        Raise(nameof(IsUpdateBusy));
        Raise(nameof(UpdateProgress));
        Raise(nameof(UpdateProgressText));
        Raise(nameof(DownloadSpeedText));
        Raise(nameof(DownloadSizeText));
        Raise(nameof(UpdateVersionHeadline));
        Raise(nameof(UpdateVersionsLine));
        Raise(nameof(LastCheckText));
        Raise(nameof(ShowUpdateBanner));
        Raise(nameof(InstalledVersion));
        Raise(nameof(UpdateIntervalMinutes));
        Raise(nameof(UpdateIntervalLabel));
        Raise(nameof(SelectedUpdateIntervalLabel));
    }
}
