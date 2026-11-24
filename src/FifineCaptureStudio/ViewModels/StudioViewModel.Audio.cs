using System.Collections.ObjectModel;
using FifineCaptureStudio.Audio;
using FifineCaptureStudio.Core;

namespace FifineCaptureStudio.ViewModels;

public sealed partial class StudioViewModel
{
    public ObservableCollection<AudioChannelViewModel> AudioChannels { get; } = new();

    public ObservableCollection<AudioDeviceOption> RenderEndpoints { get; } = new();
    public ObservableCollection<AudioDeviceOption> MonitoringOutputs { get; } = new();
    public ObservableCollection<AudioDeviceOption> CaptureEndpoints { get; } = new();
    public ObservableCollection<int> SampleRates { get; } = new() { 44100, 48000, 96000 };

    public ObservableCollection<AudioChannelViewModel> MixerChannels { get; } = new();

    public string AudioEngineState => _services.Audio.IsRunning
        ? (_services.Audio.IsMonitoring ? "Mixer live · monitoring on" : "Mixer live · monitoring off")
        : "Mixer stopped";

    public bool AudioMonitoringEnabled
    {
        get => Config.Audio.MonitoringEnabled;
        set
        {
            Config.Audio.MonitoringEnabled = value;
            _services.Settings.Save();
            Raise();
            _ = RestartAudioAsync();
        }
    }

    public int AudioSampleRate
    {
        get => Config.Audio.SampleRate;
        set
        {
            if (value == Config.Audio.SampleRate)
            {
                return;
            }

            Config.Audio.SampleRate = value;
            _services.Settings.Save();
            Raise();
            _ = RestartAudioAsync();
        }
    }

    public int AudioOffsetMs
    {
        get => Config.Audio.AvOffsetMs;
        set
        {
            Config.Audio.AvOffsetMs = Math.Clamp(value, -500, 500);
            _services.Settings.Save();
            Raise();
            Raise(nameof(AudioOffsetLabel));
        }
    }

    public string AudioOffsetLabel => $"{Config.Audio.AvOffsetMs:+0;-0;0} ms";

    public double MonitoringVolume
    {
        get => Config.Audio.MonitoringVolume;
        set
        {
            Config.Audio.MonitoringVolume = Math.Clamp(value, 0, 1.5);
            _services.Settings.SaveSoon();
            Raise();
        }
    }

    public string AudioLatencyLabel
    {
        get
        {
            var lag = _services.Audio.MonitorLagMs;
            return lag > 0 ? $"Monitor buffer {lag:0} ms" : "Monitoring off";
        }
    }

    // ------------------------------------------------------------ live monitoring

    private static readonly AudioDeviceOption DefaultMonitoringOutput =
        new(string.Empty, "Default Windows audio device", AudioDeviceKind.PlaybackOutput);

    public string MonitoringOutputText => SelectedMonitoringOutput?.Name ?? "Default Windows audio device";

    public AudioDeviceOption? SelectedMonitoringOutput
    {
        get => MonitoringOutputs.FirstOrDefault(o => o.Id == (Config.Audio.MonitoringDeviceId ?? string.Empty))
               ?? MonitoringOutputs.FirstOrDefault();
        set
        {
            if (value is null)
            {
                return;
            }

            Config.Audio.MonitoringDeviceId = value.Id;
            _services.Settings.Save();
            _services.Audio.RestartMonitoring();
            Raise();
            Raise(nameof(MonitoringOutputText));
            Raise(nameof(AudioEngineState));
            Raise(nameof(AudioLatencyLabel));
        }
    }

    private void RefreshMonitoringOutputs()
    {
        MonitoringOutputs.Clear();
        MonitoringOutputs.Add(DefaultMonitoringOutput);
        foreach (var device in RenderEndpoints.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            MonitoringOutputs.Add(device);
        }

        Raise(nameof(SelectedMonitoringOutput));
        Raise(nameof(MonitoringOutputText));
    }

    /// <summary>Per-source monitoring. Monitoring never feeds the recording mix twice.</summary>
    public bool MicMonitorEnabled
    {
        get => Config.Audio.Channel("mic").Monitor;
        set => SetChannelMonitoring("mic", value);
    }

    public bool GameMonitorEnabled
    {
        get => Config.Audio.Channel("game").Monitor;
        set => SetChannelMonitoring("game", value);
    }

    public bool CaptureMonitorEnabled
    {
        get => Config.Audio.Channel("capture").Monitor;
        set => SetChannelMonitoring("capture", value);
    }

    private void SetChannelMonitoring(string role, bool enabled)
    {
        var channel = Config.Audio.Channel(role);
        if (channel.Monitor == enabled)
        {
            return;
        }

        channel.Monitor = enabled;
        _services.Settings.Save();

        if (enabled && Config.Audio.MonitoringEnabled && !_services.Audio.IsMonitoring)
        {
            _services.Audio.RestartMonitoring();
        }

        Raise();
        Raise(nameof(MicMonitorEnabled));
        Raise(nameof(GameMonitorEnabled));
        Raise(nameof(CaptureMonitorEnabled));
        Raise(nameof(AudioEngineState));
    }

    public string MonitoringRouteNote =>
        "Monitoring is a separate output path: turning it on or off never adds or removes audio in the recording.";

    public string SeparateTracksNote =>
        "Each source is written to its own audio track, so game audio, capture card audio and the microphone stay editable after recording.";

    private void LoadAudioSettings()
    {
        RenderEndpoints.Clear();
        foreach (var device in _services.RenderEndpoints)
        {
            RenderEndpoints.Add(device);
        }

        CaptureEndpoints.Clear();
        foreach (var device in _services.CaptureEndpoints)
        {
            CaptureEndpoints.Add(device);
        }

        AudioChannels.Clear();
        foreach (var channel in _services.Audio.Channels)
        {
            AudioChannels.Add(new AudioChannelViewModel(channel, _services));
        }

        EnsureDefaultAudioDevices();
        RefreshMonitoringOutputs();
        Raise(nameof(AudioStatusText));
        Raise(nameof(AudioEngineState));
    }

    private void EnsureDefaultAudioDevices()
    {
        var changed = false;
        var game = Config.Audio.Channel("game");
        if (string.IsNullOrEmpty(game.DeviceId))
        {
            var id = AudioDevices.DefaultLoopbackId();
            if (!string.IsNullOrEmpty(id))
            {
                game.DeviceId = id;
                game.DeviceName = AudioDevices.FriendlyName(id);
                game.UseSystemLoopback = true;
                changed = true;
            }
        }

        var mic = Config.Audio.Channel("mic");
        if (string.IsNullOrEmpty(mic.DeviceId))
        {
            var id = AudioDevices.DefaultCaptureId();
            if (!string.IsNullOrEmpty(id))
            {
                mic.DeviceId = id;
                mic.DeviceName = AudioDevices.FriendlyName(id);
                mic.UseSystemLoopback = false;
                changed = true;
            }
        }

        var capture = Config.Audio.Channel("capture");
        if (string.IsNullOrEmpty(capture.DeviceId))
        {
            var card = _services.CaptureEndpoints.FirstOrDefault(d =>
                d.Name.Contains("fifine", StringComparison.OrdinalIgnoreCase)
                || d.Name.Contains("capture", StringComparison.OrdinalIgnoreCase)
                || d.Name.Contains("hdmi", StringComparison.OrdinalIgnoreCase));
            if (card is not null)
            {
                capture.DeviceId = card.Id;
                capture.DeviceName = card.Name;
                changed = true;
            }
        }

        if (changed)
        {
            _services.Settings.Save();
            foreach (var channel in AudioChannels)
            {
                channel.RefreshDevices();
                _ = channel.Channel.StartAsync();
            }
        }
    }

    private async Task RestartAudioAsync()
    {
        IsBusy = true;
        BusyLabel = "Restarting audio engine…";
        try
        {
            await _services.Audio.StopAsync();
            await _services.Audio.StartAsync();
            LoadAudioSettings();
            Notify("Audio engine restarted.", false);
        }
        catch (Exception ex)
        {
            Notify("Audio restart failed: " + ex.Message, true);
        }
        finally
        {
            IsBusy = false;
            Raise(nameof(AudioEngineState));
            Raise(nameof(AudioStatusText));
        }
    }

    public bool MicMuted => AudioChannels.FirstOrDefault(c => c.Role == "mic")?.Muted ?? false;
    public string MicMuteLabel => MicMuted ? "Unmute microphone" : "Mute microphone";
}
