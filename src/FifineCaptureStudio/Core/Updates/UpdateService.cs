using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using Velopack;
using Velopack.Sources;

namespace FifineCaptureStudio.Core.Updates;

public enum UpdateStage
{
    Idle,
    NotInstalled,
    NotConfigured,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToInstall,
    Recording,
    Failed
}

/// <summary>
/// Wraps Velopack. Checks, downloads and applies real update packages from the project's
/// GitHub Releases. It never runs outside an installed Velopack build, never installs an
/// unverified file, and refuses to restart while a recording is running.
/// </summary>
public sealed class UpdateService
{
    private readonly SettingsService _settings;
    private readonly object _gate = new();
    private UpdateManager? _manager;
    private UpdateInfo? _pending;
    private DateTime _lastProgressAt = DateTime.MinValue;
    private int _lastProgressPercent;
    private double _speedBytesPerSecond;

    public UpdateService(SettingsService settings)
    {
        _settings = settings;
    }

    public event Action? Changed;

    public UpdateStage Stage { get; private set; } = UpdateStage.Idle;
    public string Message { get; private set; } = "Update status unknown.";
    public UpdateInfo? Pending => _pending;
    public string? LatestVersion => _pending?.TargetFullRelease.Version.ToString();
    public string? ReleaseNotes => _pending?.TargetFullRelease.NotesMarkdown;
    public long? PackageSize => _pending?.TargetFullRelease.Size;
    public int DownloadPercent { get; private set; }
    public double DownloadSpeedBytesPerSecond => _speedBytesPerSecond;
    public bool IsInstalled => SafeManager()?.IsInstalled ?? false;
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Repository);

    public string InstalledVersion
    {
        get
        {
            var manager = SafeManager();
            if (manager?.CurrentVersion is { } version)
            {
                return version.ToString();
            }

            var informational = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return string.IsNullOrWhiteSpace(informational)
                ? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0"
                : informational.Split('+')[0];
        }
    }

    public string BuildNumber
    {
        get
        {
            var location = Assembly.GetExecutingAssembly().Location;
            var stamp = string.IsNullOrEmpty(location) ? DateTime.MinValue : File.GetLastWriteTimeUtc(location);
            return $"{InstalledVersion}+{stamp:yyyyMMdd.HHmm}";
        }
    }

    /// <summary>owner/repo resolved from settings first, then the build-time assembly metadata.</summary>
    public string Repository
    {
        get
        {
            var configured = _settings.Current.Updates.Repository;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return configured.Trim();
            }

            return Assembly.GetExecutingAssembly()
                       .GetCustomAttributes<AssemblyMetadataAttribute>()
                       .FirstOrDefault(a => a.Key == "GitHubRepository")?.Value
                   ?? string.Empty;
        }
    }

    public string RepositoryUrl => Repository.Contains('/') ? "https://github.com/" + Repository : string.Empty;

    public UpdateChannel Channel => _settings.Current.Updates.Channel;

    private UpdateManager? SafeManager()
    {
        if (_manager is not null)
        {
            return _manager;
        }

        if (!IsConfigured)
        {
            return null;
        }

        try
        {
            var source = new GithubSource(
                $"https://github.com/{Repository}",
                accessToken: null,
                prerelease: Channel == UpdateChannel.Beta);

            _manager = new UpdateManager(source);
            return _manager;
        }
        catch (Exception ex)
        {
            Log.Warn("Update manager could not be created: " + ex.Message);
            return null;
        }
    }

    public void Rebuild()
    {
        _manager = null;
        _pending = null;
        DownloadPercent = 0;
        _speedBytesPerSecond = 0;
    }

    public async Task CheckAsync(bool userInitiated)
    {
        if (!IsConfigured)
        {
            SetState(UpdateStage.NotConfigured,
                "No update repository is configured for this build, so there is nothing to check.");
            return;
        }

        var manager = SafeManager();
        if (manager is null)
        {
            SetState(UpdateStage.Failed, "The update engine could not start. See the log for details.");
            return;
        }

        if (!manager.IsInstalled)
        {
            SetState(UpdateStage.NotInstalled,
                "This is a development build. Automatic updates run only in the installed Velopack build " +
                "(install FIFINE Capture Studio with the Setup.exe from a release).");
            return;
        }

        SetState(UpdateStage.Checking, "Checking GitHub Releases for a newer version…");

        try
        {
            var info = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            _settings.Current.Updates.LastCheckUtc = DateTime.UtcNow;
            _settings.Save();

            if (info is null)
            {
                _pending = null;
                SetState(UpdateStage.UpToDate, $"You are on the latest {ChannelLabel} build ({InstalledVersion}).");
                return;
            }

            _pending = info;
            SetState(UpdateStage.Available,
                $"Version {info.TargetFullRelease.Version} is available (installed {InstalledVersion})." +
                (info.IsDowngrade ? " It is older than the installed build." : string.Empty));

            if (_settings.Current.Updates.AutoDownload)
            {
                await DownloadAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Update check failed: " + ex.Message);
            SetState(UpdateStage.Failed, "Update check failed: " + Describe(ex) +
                                         " Check your connection and the repository settings, then try again.");
        }
    }

    public async Task DownloadAsync()
    {
        var manager = SafeManager();
        var info = _pending;
        if (manager is null || info is null)
        {
            SetState(UpdateStage.Failed, "There is no update to download yet. Check for updates first.");
            return;
        }

        DownloadPercent = 0;
        _speedBytesPerSecond = 0;
        _lastProgressPercent = 0;
        _lastProgressAt = DateTime.UtcNow;
        SetState(UpdateStage.Downloading, $"Downloading {info.TargetFullRelease.Version}…");

        try
        {
            await manager.DownloadUpdatesAsync(info, percent =>
            {
                var now = DateTime.UtcNow;
                var elapsed = (now - _lastProgressAt).TotalSeconds;
                if (elapsed > 0.4 && PackageSize is { } size && size > 0)
                {
                    var deltaFraction = (percent - _lastProgressPercent) / 100.0;
                    _speedBytesPerSecond = deltaFraction * size / elapsed;
                    _lastProgressAt = now;
                    _lastProgressPercent = percent;
                }

                DownloadPercent = Math.Clamp(percent, 0, 100);
                Changed?.Invoke();
            }).ConfigureAwait(false);

            DownloadPercent = 100;
            SetState(UpdateStage.ReadyToInstall,
                $"{info.TargetFullRelease.Version} is downloaded and verified. Restart FIFINE Capture Studio to install it.");
        }
        catch (Exception ex)
        {
            Log.Warn("Update download failed: " + ex.Message);
            DownloadPercent = 0;
            SetState(UpdateStage.Failed, "Download failed: " + Describe(ex) + " Nothing was installed.");
        }
    }

    /// <summary>
    /// Applies the downloaded update and restarts. Refuses while recording so gameplay is never cut.
    /// </summary>
    public bool ApplyAndRestart(bool isRecording, out string refusal)
    {
        refusal = string.Empty;

        if (isRecording)
        {
            SetState(UpdateStage.Recording,
                "A recording is in progress. Stop the recording first — the update will not interrupt it.");
            refusal = Message;
            return false;
        }

        var manager = SafeManager();
        var info = _pending;
        if (manager is null || info is null)
        {
            refusal = "There is no downloaded update to install.";
            return false;
        }

        try
        {
            // The update restarts the process without a normal window close, so persist every
            // pending change first; otherwise the last edits made before updating would be lost.
            _settings.Flush();

            Log.Info($"Applying update to {info.TargetFullRelease.Version} and restarting.");
            manager.ApplyUpdatesAndRestart(info.TargetFullRelease);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Applying the update failed: " + ex.Message);
            SetState(UpdateStage.Failed, "The update could not be applied: " + Describe(ex));
            refusal = Message;
            return false;
        }
    }

    public void MarkReminded(string version)
    {
        Message = $"Update {version} is available. You chose to install it later.";
        Changed?.Invoke();
    }

    public string ChannelLabel => Channel == UpdateChannel.Beta ? "Beta" : "Stable";

    private void SetState(UpdateStage stage, string message)
    {
        lock (_gate)
        {
            Stage = stage;
            Message = message;
        }

        Log.Info($"Update [{stage}] {message}");
        Changed?.Invoke();
    }

    private static string Describe(Exception ex)
    {
        var text = ex is AggregateException { InnerException: { } inner } ? inner.Message : ex.Message;
        return text.Length > 240 ? text[..240] + "…" : text;
    }
}
