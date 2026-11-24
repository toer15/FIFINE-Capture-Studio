using System.Collections.ObjectModel;
using System.ComponentModel;
using FifineCaptureStudio.Audio;
using FifineCaptureStudio.Core;

namespace FifineCaptureStudio.ViewModels;

/// <summary>Binds a real WASAPI mixer channel to the UI.</summary>
public sealed class AudioChannelViewModel : ObservableObject
{
    private readonly AudioChannel _channel;
    private readonly AppServices _services;

    public AudioChannelViewModel(AudioChannel channel, AppServices services)
    {
        _channel = channel;
        _services = services;

        Devices = new ObservableCollection<AudioDeviceOption>();
        BuildDeviceList();

        _channel.PropertyChanged += OnChannelPropertyChanged;
        _channel.StateChanged += _ =>
        {
            Raise(nameof(Level));
            Raise(nameof(StatusText));
            Raise(nameof(IsLive));
            Raise(nameof(DeviceName));
        };
    }

    public AudioChannel Channel => _channel;
    public string Role => _channel.Role;
    public string Title => _channel.Title;
    public ObservableCollection<AudioDeviceOption> Devices { get; }

    private void OnChannelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AudioChannel.Level))
        {
            Raise(nameof(Level));
        }

        Raise(nameof(Volume));
        Raise(nameof(VolumePercent));
        Raise(nameof(Muted));
        Raise(nameof(MuteGlyph));
        Raise(nameof(Monitor));
        Raise(nameof(DelayMs));
        Raise(nameof(StatusText));
        Raise(nameof(IsLive));
        Raise(nameof(DeviceName));
        Raise(nameof(DeviceLabel));
        Raise(nameof(LevelDecibels));
    }

    private void BuildDeviceList()
    {
        Devices.Clear();
        if (Role == "game")
        {
            Devices.Add(new AudioDeviceOption(string.Empty, "System default output", AudioDeviceKind.SystemLoopback));
            foreach (var device in _services.RenderEndpoints)
            {
                Devices.Add(device);
            }
        }
        else
        {
            foreach (var device in _services.CaptureEndpoints)
            {
                Devices.Add(device);
            }
        }

        // Include the currently configured device even if it is unplugged right now.
        if (!string.IsNullOrEmpty(_channel.Settings.DeviceId)
            && Devices.All(d => d.Id != _channel.Settings.DeviceId))
        {
            Devices.Insert(0, new AudioDeviceOption(
                _channel.Settings.DeviceId,
                _channel.Settings.DeviceName + " (offline)",
                _channel.Settings.UseSystemLoopback ? AudioDeviceKind.SystemLoopback : AudioDeviceKind.CaptureInput));
        }
    }

    public void RefreshDevices()
    {
        BuildDeviceList();
        Raise(nameof(SelectedDevice));
    }

    public AudioDeviceOption? SelectedDevice
    {
        get => Devices.FirstOrDefault(d => d.Id == _channel.Settings.DeviceId)
               ?? Devices.FirstOrDefault();
        set
        {
            if (value is null)
            {
                return;
            }

            if (_channel.Settings.DeviceId == value.Id && _channel.IsActive)
            {
                return;
            }

            _channel.Settings.DeviceId = value.Id;
            _channel.Settings.DeviceName = value.Name;
            _channel.Settings.UseSystemLoopback = value.IsLoopback;
            _services.Settings.Save();
            Raise();
            Raise(nameof(DeviceName));
            Raise(nameof(DeviceLabel));
            _ = _channel.StartAsync();
        }
    }

    public double Volume
    {
        get => _channel.Volume;
        set
        {
            _channel.Volume = value;
            _services.Settings.SaveSoon();
            Raise();
            Raise(nameof(VolumePercent));
            Raise(nameof(LevelDecibels));
        }
    }

    public double VolumePercent => Math.Round(Volume * 100);

    public bool Muted
    {
        get => _channel.Muted;
        set
        {
            _channel.Muted = value;
            _services.Settings.Save();
            Raise();
            Raise(nameof(MuteGlyph));
        }
    }

    public string MuteGlyph => Muted ? "\uE74F" : "\uE767";

    public bool Monitor
    {
        get => _channel.Monitor;
        set
        {
            _channel.Monitor = value;
            _services.Settings.Save();
            Raise();
        }
    }

    public int DelayMs
    {
        get => _channel.DelayMs;
        set
        {
            _channel.DelayMs = value;
            _services.Settings.SaveSoon();
            Raise();
        }
    }

    public double Level => _channel.Level;
    public string LevelDecibels => _channel.PeakDb <= -60 ? "−∞ dB" : $"{_channel.PeakDb:0.0} dB";
    public bool IsLive => _channel.IsActive && _channel.IsReceiving;
    public string StatusText => _channel.StatusText;
    public string DeviceName => _channel.DeviceName;
    public string DeviceLabel => AudioDevices.FormatLabel(_channel.DeviceName);
    public string FormatLabel => _channel.FormatLabel;
    public string Glyph => Role switch
    {
        "mic" => "\uE720",
        "capture" => "\uE7F4",
        _ => "\uE767"
    };
}
