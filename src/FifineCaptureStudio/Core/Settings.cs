using FifineCaptureStudio.Core.Enhancement;
using FifineCaptureStudio.Core.FrameGeneration;
using FifineCaptureStudio.Core.Monitoring;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FifineCaptureStudio.Core;

public enum CaptureSourceKind
{
    CaptureCard,
    Display,
    Window,
    TestPattern,

    /// <summary>Video arrives from a console streaming backend instead of a capture device.</summary>
    ConsoleStream
}

public enum PlatformKind
{
    PC,
    PS5,
    Xbox
}

public enum LowLatencyMode
{
    Off,
    Balanced,
    UltraLow
}

public enum BufferMode
{
    Automatic,
    Low,
    Custom
}

public enum SyncMode
{
    Automatic,
    LowLatency,
    Stable
}

public enum VideoCodecKind
{
    H264,
    HEVC,
    AV1
}

public enum ContainerKind
{
    Mp4,
    Mkv
}

public enum RateControlMode
{
    Preset,
    Custom,
    Quality
}

public sealed class HotkeySettings
{
    public string ToggleRecording { get; set; } = "Ctrl+F9";
    public string PauseRecording { get; set; } = "Ctrl+F10";
    public string SaveReplay { get; set; } = "Ctrl+F8";
    public string MuteMicrophone { get; set; } = "Ctrl+M";
    public string TogglePreview { get; set; } = "Ctrl+Shift+P";
}

public sealed class GeneralSettings
{
    public bool LaunchOnStartup { get; set; }
    public string Language { get; set; } = "English";
    public string Theme { get; set; } = "Midnight";
    public bool Notifications { get; set; } = true;
    public bool MinimizeToTray { get; set; }
    public bool StartMinimized { get; set; }
    public bool ShowWizardOnStartup { get; set; } = true;
}

public sealed class CaptureSettings
{
    public CaptureSourceKind SourceKind { get; set; } = CaptureSourceKind.CaptureCard;
    public string VideoDeviceName { get; set; } = string.Empty;
    public string VideoDeviceId { get; set; } = string.Empty;
    public int DisplayIndex { get; set; }
    public string WindowTitle { get; set; } = string.Empty;
    public string InputPixelFormat { get; set; } = "Auto";
    public int ResolutionWidth { get; set; } = 1920;
    public int ResolutionHeight { get; set; } = 1080;
    public int Fps { get; set; } = 60;
    public bool HdrEnabled { get; set; }
    public PlatformKind Platform { get; set; } = PlatformKind.PS5;
}

/// <summary>
/// PlayStation Remote Play settings. The registration keys are console credentials and are stored
/// DPAPI-protected (per Windows user), never in plain text and never in the repository.
/// </summary>
public sealed class PlayStationSettings
{
    public string EnginePath { get; set; } = string.Empty;
    public string HostId { get; set; } = string.Empty;
    public string ConsoleName { get; set; } = string.Empty;
    public string ConsoleAddress { get; set; } = string.Empty;
    public bool IsPs5 { get; set; } = true;
    public int Resolution { get; set; } = 1080;
    public int Fps { get; set; } = 60;
    public int BitrateKbps { get; set; }
    public bool PreferHevc { get; set; }
    public bool DualSenseHaptics { get; set; } = true;
    public bool ForwardController { get; set; } = true;
    public uint ConsolePin { get; set; }
    public string AccountId { get; set; } = string.Empty;
    public string RegistKey { get; set; } = string.Empty;
    public string RpKey { get; set; } = string.Empty;

    [JsonIgnore]
    public bool HasRegistration => !string.IsNullOrEmpty(RegistKey) && !string.IsNullOrEmpty(RpKey);

    [JsonIgnore]
    public int Width => Resolution >= 1080 ? 1920 : Resolution >= 720 ? 1280 : 960;

    [JsonIgnore]
    public int Height => Resolution >= 1080 ? 1080 : Resolution >= 720 ? 720 : Resolution >= 540 ? 540 : 360;
}

/// <summary>Xbox account and console selection. The token is DPAPI-protected.</summary>
public sealed class XboxSettings
{
    public string Gamertag { get; set; } = string.Empty;
    public string Xuid { get; set; } = string.Empty;
    public string ConsoleId { get; set; } = string.Empty;
    public string ConsoleName { get; set; } = string.Empty;
    public string Region { get; set; } = "en-US";
    public string Token { get; set; } = string.Empty;
    public bool ForwardController { get; set; } = true;

    [JsonIgnore]
    public bool HasToken => !string.IsNullOrEmpty(Token);
}

public sealed class LowLatencySettings
{
    public LowLatencyMode Mode { get; set; } = LowLatencyMode.UltraLow;
    public BufferMode VideoBuffer { get; set; } = BufferMode.Low;
    public int VideoBufferMs { get; set; } = 120;
    public SyncMode Sync { get; set; } = SyncMode.Automatic;
    public BufferMode AudioBuffer { get; set; } = BufferMode.Automatic;
    public int AudioBufferMs { get; set; } = 60;
}

public sealed class RecordingSettings
{
    public string Directory { get; set; } = AppPaths.DefaultRecordingsDir;
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int Fps { get; set; } = 60;
    public VideoCodecKind Codec { get; set; } = VideoCodecKind.H264;
    public ContainerKind Container { get; set; } = ContainerKind.Mp4;
    public RateControlMode RateControl { get; set; } = RateControlMode.Preset;
    public int BitrateKbps { get; set; } = 20000;
    public string QualityPreset { get; set; } = "Balanced";
    public string Encoder { get; set; } = "Auto";
    public bool UseHardwareEncoding { get; set; } = true;
    public bool SeparateAudioTracks { get; set; }
    public string FilenameTemplate { get; set; } = "{platform}_{date}_{time}";
    public int AudioBitrateKbps { get; set; } = 192;

    /// <summary>Recording-only frame interpolation multiplier: 1 = off, 2 or 4.</summary>
    public int FrameGeneration { get; set; } = 1;
}

public sealed class AudioChannelSettings
{
    public string Role { get; set; } = "game";
    public string DisplayName { get; set; } = "Audio";
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public bool UseSystemLoopback { get; set; }
    public double Volume { get; set; } = 1.0;
    public bool Muted { get; set; }
    public bool Monitor { get; set; } = true;
    public int DelayMs { get; set; }
}

public sealed class AudioSettings
{
    public int SampleRate { get; set; } = 48000;
    public int ChannelCount { get; set; } = 2;
    public bool MonitoringEnabled { get; set; } = true;
    public double MonitoringVolume { get; set; } = 0.9;
    public string MonitoringDeviceId { get; set; } = string.Empty;
    public int AvOffsetMs { get; set; }
    public List<AudioChannelSettings> Channels { get; set; } = new()
    {
        new AudioChannelSettings { Role = "game", DisplayName = "Game Audio", UseSystemLoopback = true },
        new AudioChannelSettings { Role = "capture", DisplayName = "Capture Card Audio" },
        new AudioChannelSettings { Role = "mic", DisplayName = "Microphone" }
    };

    public AudioChannelSettings Channel(string role)
    {
        var ch = Channels.FirstOrDefault(c => string.Equals(c.Role, role, StringComparison.OrdinalIgnoreCase));
        if (ch is null)
        {
            ch = new AudioChannelSettings { Role = role, DisplayName = role };
            Channels.Add(ch);
        }

        return ch;
    }
}

public sealed class PerformanceSettings
{
    public bool GpuAcceleration { get; set; } = true;
    public bool HardwareEncoding { get; set; } = true;
    public bool PowerSavingWhenIdle { get; set; } = true;
    public bool ProcessAboveNormalPriority { get; set; } = true;
    public LowLatencyMode PreferredMode { get; set; } = LowLatencyMode.UltraLow;
}

public sealed class ReplaySettings
{
    public bool Enabled { get; set; }
    public int DurationSeconds { get; set; } = 30;
}

public enum UpdateChannel
{
    Stable,
    Beta
}

/// <summary>
/// Desktop and navigation state the user changes by using the app. Kept with the other settings so
/// the window, the page they were on and their preview layout come back exactly as they left them.
/// </summary>
public sealed class UiSettings
{
    /// <summary>Null means "let Windows centre the window" (first run, or the monitor is gone).</summary>
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1600;
    public double WindowHeight { get; set; } = 920;
    public bool WindowMaximized { get; set; }
    public string LastPage { get; set; } = "dashboard";
    public double PreviewScale { get; set; } = 1.0;
    public bool KeepAspect { get; set; } = true;
    public bool MinimalPreview { get; set; }
    public bool QuickPanelOpen { get; set; }
    public string SettingsCategory { get; set; } = "General";
}

public sealed class UpdateSettings
{
    public bool AutoCheckEnabled { get; set; } = true;
    public bool AutoDownload { get; set; } = true;
    public int CheckIntervalMinutes { get; set; } = 360;
    public UpdateChannel Channel { get; set; } = UpdateChannel.Stable;
    public DateTime? LastCheckUtc { get; set; }

    /// <summary>Owner/repo override. Empty means "use the value baked in at build time".</summary>
    public string Repository { get; set; } = string.Empty;
}

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 6;

    /// <summary>Pages that can be restored at startup, in the same order as the sidebar.</summary>
    public static readonly string[] KnownPages =
    {
        "dashboard", "capture", "recording", "audio", "sources", "enhance", "performance", "library", "settings"
    };

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public GeneralSettings General { get; set; } = new();
    public CaptureSettings Capture { get; set; } = new();
    public LowLatencySettings LowLatency { get; set; } = new();
    public RecordingSettings Recording { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public PerformanceSettings Performance { get; set; } = new();
    public ReplaySettings Replay { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
    public UpdateSettings Updates { get; set; } = new();
    public PlayStationSettings PlayStation { get; set; } = new();
    public XboxSettings Xbox { get; set; } = new();
    public EnhancementSettings Enhancement { get; set; } = new();
    public FrameGenerationSettings FrameGeneration { get; set; } = new();
    public FpsMonitorSettings FpsMonitor { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public bool WizardCompleted { get; set; }

    /// <summary>
    /// Unknown top-level properties from a newer build. Round-tripping them means installing an
    /// older version (or a build that does not know a field yet) cannot silently delete settings.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    public static void Normalize(AppSettings s)
    {
        var loadedVersion = s.SchemaVersion;
        s.Updates ??= new UpdateSettings();

        // Schema 1 had no update settings; the defaults above are the migration.
        if (s.SchemaVersion < 2)
        {
            s.Updates.AutoCheckEnabled = true;
            s.Updates.AutoDownload = true;
            s.Updates.Channel = UpdateChannel.Stable;
            s.SchemaVersion = CurrentSchemaVersion;
        }

        // Schema 2 added the console streaming settings; defaults above are the migration.
        // Schema 6 added UI state (window placement, last page, preview layout). Defaults apply
        // when an older file is loaded; nothing existing is reset.
        s.Ui ??= new UiSettings();
        s.Ui.WindowWidth = double.IsFinite(s.Ui.WindowWidth) ? Math.Clamp(s.Ui.WindowWidth, 900, 10000) : 1600;
        s.Ui.WindowHeight = double.IsFinite(s.Ui.WindowHeight) ? Math.Clamp(s.Ui.WindowHeight, 600, 10000) : 920;
        s.Ui.PreviewScale = double.IsFinite(s.Ui.PreviewScale) ? Math.Clamp(s.Ui.PreviewScale, 0.3, 1.0) : 1.0;
        s.Ui.LastPage = KnownPages.Contains(s.Ui.LastPage, StringComparer.OrdinalIgnoreCase)
            ? s.Ui.LastPage.ToLowerInvariant()
            : "dashboard";
        s.Ui.SettingsCategory = string.IsNullOrWhiteSpace(s.Ui.SettingsCategory)
            ? "General"
            : s.Ui.SettingsCategory.Trim();

        s.PlayStation ??= new PlayStationSettings();
        s.Xbox ??= new XboxSettings();
        s.Enhancement ??= new EnhancementSettings();
        s.Enhancement.Normalize();
        s.FrameGeneration ??= new FrameGenerationSettings();
        s.FrameGeneration.Normalize();
        s.FpsMonitor ??= new FpsMonitorSettings();
        s.FpsMonitor.Normalize();

        // Schema 4: onboarding completion is tracked explicitly. Builds before it only cleared the
        // "show wizard on startup" flag when the wizard was dismissed, so honour that as completion
        // and never show onboarding to those users again.
        if (loadedVersion < 4 && !s.WizardCompleted && s.General is { ShowWizardOnStartup: false })
        {
            s.WizardCompleted = true;
        }
        if (s.SchemaVersion < CurrentSchemaVersion)
        {
            s.SchemaVersion = CurrentSchemaVersion;
        }

        s.PlayStation.Resolution = s.PlayStation.Resolution is 360 or 540 or 720 or 1080 ? s.PlayStation.Resolution : 1080;
        s.PlayStation.Fps = s.PlayStation.Fps >= 60 ? 60 : 30;
        s.PlayStation.BitrateKbps = Math.Clamp(s.PlayStation.BitrateKbps, 0, 100000);
        s.PlayStation.EnginePath = (s.PlayStation.EnginePath ?? string.Empty).Trim();

        s.Updates.CheckIntervalMinutes = Math.Clamp(s.Updates.CheckIntervalMinutes, 30, 10080);
        s.Updates.Repository = (s.Updates.Repository ?? string.Empty).Trim();
        s.General ??= new GeneralSettings();
        s.Capture ??= new CaptureSettings();
        s.LowLatency ??= new LowLatencySettings();
        s.Recording ??= new RecordingSettings();
        s.Audio ??= new AudioSettings();
        s.Performance ??= new PerformanceSettings();
        s.Replay ??= new ReplaySettings();
        s.Hotkeys ??= new HotkeySettings();
        s.Audio.Channels ??= new List<AudioChannelSettings>();
        s.Audio.Channel("game");
        s.Audio.Channel("capture");
        s.Audio.Channel("mic");

        if (string.IsNullOrWhiteSpace(s.Recording.Directory))
        {
            s.Recording.Directory = AppPaths.DefaultRecordingsDir;
        }

        // Settings written before the data folder moved still point at the install directory.
        if (!string.IsNullOrWhiteSpace(s.Recording.Directory) &&
            s.Recording.Directory.StartsWith(AppPaths.LegacyRoot, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(AppPaths.LegacyRoot, AppPaths.Root, StringComparison.OrdinalIgnoreCase))
        {
            s.Recording.Directory = AppPaths.Root +
                                    s.Recording.Directory[AppPaths.LegacyRoot.Length..];
        }

        s.Capture.Fps = Math.Clamp(s.Capture.Fps, 24, 240);
        s.Recording.Fps = Math.Clamp(s.Recording.Fps, 24, 240);
        s.Recording.BitrateKbps = Math.Clamp(s.Recording.BitrateKbps, 2000, 150000);
        s.LowLatency.VideoBufferMs = Math.Clamp(s.LowLatency.VideoBufferMs, 20, 1000);
        s.LowLatency.AudioBufferMs = Math.Clamp(s.LowLatency.AudioBufferMs, 10, 500);
        s.Replay.DurationSeconds = Math.Clamp(s.Replay.DurationSeconds, 5, 300);
        s.Audio.SampleRate = s.Audio.SampleRate is 44100 or 48000 or 96000 ? s.Audio.SampleRate : 48000;
    }
}

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private readonly string _root;
    private readonly string _file;
    private readonly string _backup;
    private readonly string _broken;
    private Timer? _debounce;

    public SettingsService(string? rootDirectory = null)
    {
        _root = rootDirectory ?? AppPaths.Root;
        _file = Path.Combine(_root, "settings.json");
        _backup = Path.Combine(_root, "settings.backup.json");
        _broken = Path.Combine(_root, "settings.broken.json");
        Current = Load();
    }

    public AppSettings Current { get; private set; }

    /// <summary>Location actually used; tests can point this at a temporary folder.</summary>
    public string SettingsPath => _file;

    public event Action<AppSettings>? Saved;

    private AppSettings Load()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_root) == false)
            {
                Directory.CreateDirectory(_root);
            }

            if (File.Exists(_file))
            {
                var loaded = TryRead(_file);
                if (loaded is not null)
                {
                    return loaded;
                }

                Log.Warn("settings.json could not be read; trying the last good backup");
                TryBackupBrokenFile();
            }
            else if (File.Exists(_backup))
            {
                Log.Warn("settings.json is missing; restoring the last good backup");
            }

            if (File.Exists(_backup))
            {
                var restored = TryRead(_backup);
                if (restored is not null)
                {
                    Log.Info("Settings restored from " + _backup);
                    return restored;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to load settings, falling back to defaults", ex);
            TryBackupBrokenFile();
        }

        var fresh = new AppSettings();
        AppSettings.Normalize(fresh);
        return fresh;
    }

    private static AppSettings? TryRead(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
            if (loaded is null)
            {
                return null;
            }

            AppSettings.Normalize(loaded);
            return loaded;
        }
        catch (Exception ex)
        {
            Log.Warn($"Settings file {Path.GetFileName(path)} is unreadable: {ex.Message}");
            return null;
        }
    }

    private void TryBackupBrokenFile()
    {
        try
        {
            if (File.Exists(_file))
            {
                File.Copy(_file, _broken, true);
            }
        }
        catch
        {
            // best effort
        }
    }

    /// <summary>
    /// Writes the whole settings document atomically: a temporary file is flushed next to the real
    /// one, then swapped in, and a last-good copy is kept so a crash or bad disk cannot reset the
    /// user's configuration to defaults.
    /// </summary>
    public void Save()
    {
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = null;

            try
            {
                Directory.CreateDirectory(_root);
                AppSettings.Normalize(Current);
                var json = JsonSerializer.Serialize(Current, Options);
                var tmp = _file + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _file, true);

                try
                {
                    File.Copy(_file, _backup, true);
                }
                catch (Exception ex)
                {
                    Log.Warn("Could not refresh the settings backup: " + ex.Message);
                }

                Saved?.Invoke(Current);
            }
            catch (Exception ex)
            {
                Log.Error("Failed to save settings", ex);
            }
        }
    }

    /// <summary>Coalesced save used by slider drags, window moves and other high-frequency edits.</summary>
    public void SaveSoon()
    {
        lock (_gate)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ => Save(), null, 700, Timeout.Infinite);
        }
    }

    /// <summary>Writes any coalesced change immediately. Used before updates restart the app.</summary>
    public void Flush() => Save();
}
