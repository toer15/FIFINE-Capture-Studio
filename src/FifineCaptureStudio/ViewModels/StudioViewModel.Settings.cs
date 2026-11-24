using FifineCaptureStudio.Core;

namespace FifineCaptureStudio.ViewModels;

public sealed partial class StudioViewModel
{
    private void SaveSoon() => _services.Settings.SaveSoon();

    // ---------------------------------------------------------------- general

    public bool LaunchOnStartup
    {
        get => Autostart.IsEnabled();
        set
        {
            Autostart.Set(value);
            Config.General.LaunchOnStartup = value;
            _services.Settings.Save();
            Raise();
        }
    }

    public bool NotificationsEnabled
    {
        get => Config.General.Notifications;
        set
        {
            Config.General.Notifications = value;
            _services.Settings.Save();
            Raise();
        }
    }

    public bool MinimizeToTray
    {
        get => Config.General.MinimizeToTray;
        set
        {
            Config.General.MinimizeToTray = value;
            _services.Settings.Save();
            Raise();
        }
    }

    public bool ShowWizardOnStartup
    {
        get => Config.General.ShowWizardOnStartup;
        set
        {
            Config.General.ShowWizardOnStartup = value;
            _services.Settings.Save();
            Raise();
        }
    }

    public string[] Languages { get; } = { "English" };
    public string[] Themes { get; } = { "Midnight" };

    public string Language
    {
        get => Config.General.Language;
        set
        {
            Config.General.Language = value;
            _services.Settings.Save();
            Raise();
        }
    }

    public string Theme
    {
        get => Config.General.Theme;
        set
        {
            Config.General.Theme = value;
            _services.Settings.Save();
            Raise();
        }
    }

    // ---------------------------------------------------------------- low latency

    public string[] LatencyModes { get; } = { "Off", "Balanced", "Ultra Low Latency" };
    public string[] BufferModes { get; } = { "Automatic", "Low", "Custom" };
    public string[] SyncModes { get; } = { "Automatic", "Low-latency sync", "Stable sync" };

    public string LatencyMode
    {
        get => Config.LowLatency.Mode switch
        {
            LowLatencyMode.UltraLow => "Ultra Low Latency",
            LowLatencyMode.Balanced => "Balanced",
            _ => "Off"
        };
        set
        {
            Config.LowLatency.Mode = value switch
            {
                "Ultra Low Latency" => LowLatencyMode.UltraLow,
                "Balanced" => LowLatencyMode.Balanced,
                _ => LowLatencyMode.Off
            };
            _services.Settings.Save();
            Raise();
            Raise(nameof(LatencyExplanation));
            Raise(nameof(LowLatencyBadge));
            _ = RestartIfRunningAsync();
        }
    }

    public string VideoBufferMode
    {
        get => Config.LowLatency.VideoBuffer switch
        {
            BufferMode.Low => "Low",
            BufferMode.Custom => "Custom",
            _ => "Automatic"
        };
        set
        {
            Config.LowLatency.VideoBuffer = value switch
            {
                "Low" => BufferMode.Low,
                "Custom" => BufferMode.Custom,
                _ => BufferMode.Automatic
            };
            _services.Settings.Save();
            Raise();
            Raise(nameof(IsCustomVideoBuffer));
            Raise(nameof(LatencyExplanation));
        }
    }

    public bool IsCustomVideoBuffer => Config.LowLatency.VideoBuffer == BufferMode.Custom;

    public int CustomVideoBufferMs
    {
        get => Config.LowLatency.VideoBufferMs;
        set
        {
            Config.LowLatency.VideoBufferMs = Math.Clamp(value, 20, 1000);
            SaveSoon();
            Raise();
            Raise(nameof(VideoBufferLabel));
        }
    }

    public string VideoBufferLabel => $"{(Config.LowLatency.VideoBuffer == BufferMode.Automatic ? "Auto" : Config.LowLatency.VideoBufferMs + " ms")}";

    public string SyncModeLabel
    {
        get => Config.LowLatency.Sync switch
        {
            SyncMode.LowLatency => "Low-latency sync",
            SyncMode.Stable => "Stable sync",
            _ => "Automatic"
        };
        set
        {
            Config.LowLatency.Sync = value switch
            {
                "Low-latency sync" => SyncMode.LowLatency,
                "Stable sync" => SyncMode.Stable,
                _ => SyncMode.Automatic
            };
            _services.Settings.Save();
            Raise();
        }
    }

    public string AudioBufferMode
    {
        get => Config.LowLatency.AudioBuffer switch
        {
            BufferMode.Low => "Low",
            BufferMode.Custom => "Custom",
            _ => "Automatic"
        };
        set
        {
            Config.LowLatency.AudioBuffer = value switch
            {
                "Low" => BufferMode.Low,
                "Custom" => BufferMode.Custom,
                _ => BufferMode.Automatic
            };
            _services.Settings.Save();
            Raise();
        }
    }

    public int CustomAudioBufferMs
    {
        get => Config.LowLatency.AudioBufferMs;
        set
        {
            Config.LowLatency.AudioBufferMs = Math.Clamp(value, 10, 500);
            SaveSoon();
            Raise();
        }
    }

    public string LowLatencyBadge => Config.LowLatency.Mode switch
    {
        LowLatencyMode.UltraLow => "Ultra Low Latency",
        LowLatencyMode.Balanced => "Balanced",
        _ => "Latency mode off"
    };

    public bool LowLatencyEnabled
    {
        get => Config.LowLatency.Mode != LowLatencyMode.Off;
        set => LatencyMode = value ? "Ultra Low Latency" : "Off";
    }

    public string LatencyExplanation => Config.LowLatency.Mode switch
    {
        LowLatencyMode.UltraLow =>
            "Prioritises the shortest possible preview delay: no decoder look-ahead, minimal input buffering and an unbuffered encoder queue. Recording stays stable because the encoder still uses a fixed frame pipeline.",
        LowLatencyMode.Balanced =>
            "Smoother pacing with a slightly deeper buffer. Best when the preview drops frames on a busy system.",
        _ =>
            "Largest buffers, lowest risk of frame drops, highest preview delay. Useful for long recordings where preview responsiveness matters less."
    };

    public string LatencyExplanationDetail =>
        $"Buffer: {VideoBufferLabel} · Sync: {SyncModeLabel} · Audio buffer: {AudioBufferMode}. " +
        "Shorter buffers reduce preview delay but leave less slack for encode spikes; the recorder keeps its own fixed queue so a low-latency preview never truncates a file.";

    // ---------------------------------------------------------------- performance

    public bool GpuAcceleration
    {
        get => Config.Performance.GpuAcceleration;
        set
        {
            Config.Performance.GpuAcceleration = value;
            _services.Settings.Save();
            Raise();
        }
    }

    public bool PowerSavingWhenIdle
    {
        get => Config.Performance.PowerSavingWhenIdle;
        set
        {
            Config.Performance.PowerSavingWhenIdle = value;
            _services.Settings.Save();
            Raise();
        }
    }

    public bool ProcessAboveNormal
    {
        get => Config.Performance.ProcessAboveNormalPriority;
        set
        {
            Config.Performance.ProcessAboveNormalPriority = value;
            _services.Settings.Save();
            Raise();
        }
    }

    // ---------------------------------------------------------------- shortcuts

    public string HotkeyRecord
    {
        get => Config.Hotkeys.ToggleRecording;
        set { Config.Hotkeys.ToggleRecording = value; _services.Settings.Save(); Raise(); RegisterHotkeys(); }
    }

    public string HotkeyPause
    {
        get => Config.Hotkeys.PauseRecording;
        set { Config.Hotkeys.PauseRecording = value; _services.Settings.Save(); Raise(); RegisterHotkeys(); }
    }

    public string HotkeyReplay
    {
        get => Config.Hotkeys.SaveReplay;
        set { Config.Hotkeys.SaveReplay = value; _services.Settings.Save(); Raise(); RegisterHotkeys(); }
    }

    public string HotkeyMute
    {
        get => Config.Hotkeys.MuteMicrophone;
        set { Config.Hotkeys.MuteMicrophone = value; _services.Settings.Save(); Raise(); RegisterHotkeys(); }
    }

    public string HotkeyPreview
    {
        get => Config.Hotkeys.TogglePreview;
        set { Config.Hotkeys.TogglePreview = value; _services.Settings.Save(); Raise(); RegisterHotkeys(); }
    }

    // ---------------------------------------------------------------- naming

    public string FilenameTemplate
    {
        get => Config.Recording.FilenameTemplate;
        set
        {
            Config.Recording.FilenameTemplate = value;
            _services.Settings.Save();
            Raise();
        }
    }

    public string InputFormat
    {
        get => Config.Capture.InputPixelFormat;
        set
        {
            Config.Capture.InputPixelFormat = value;
            _services.Settings.Save();
            Raise();
            _ = RestartIfRunningAsync();
        }
    }

    public string[] InputFormatOptions { get; } = { "Auto", "MJPG", "rawvideo" };

    public void SetLowLatencyMode(LowLatencyMode mode)
    {
        Config.LowLatency.Mode = mode;
        _services.Settings.Save();
        RaiseAllSettings();
        _ = RestartIfRunningAsync();
    }
}
