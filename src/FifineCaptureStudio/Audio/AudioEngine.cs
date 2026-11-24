using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using FifineCaptureStudio.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace FifineCaptureStudio.Audio;

/// <summary>
/// One mixer channel backed by a real WASAPI endpoint (input device or system
/// loopback). Samples are converted to float, resampled to the engine rate,
/// delayed, gain-staged and metered entirely on the mixer thread.
/// </summary>
public sealed class AudioChannel : INotifyPropertyChanged, IDisposable
{
    private const int RingFrames = 240000;      // ~5 s of native audio
    private const int DelayFrames = 144000;     // 3 s at 48 kHz

    private readonly object _gate = new();
    private readonly float[] _ring = new float[RingFrames * 2];
    private int _ringHead;
    private int _ringCount;

    private readonly float[] _delay = new float[DelayFrames * 2];
    private int _delayWrite;

    private float[] _scratch = new float[2 * 8192];
    private IWaveIn? _capture;
    private MMDevice? _device;
    private double _phase;
    private int _nativeRate;
    private int _outRate = 48000;
    private double _peakHold;
    private DateTime _lastCapture;
    private int _restartAttempts;
    private volatile bool _disposed;

    public AudioChannel(AudioChannelSettings settings, int outRate)
    {
        Settings = settings;
        _outRate = outRate;
        Title = settings.DisplayName;
    }

    public AudioChannelSettings Settings { get; private set; }
    public string Role => Settings.Role;
    public string Title { get; private set; }
    public bool IsActive { get; private set; }
    public string StatusText { get; private set; } = "Stopped";
    public string FormatLabel { get; private set; } = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<AudioChannel>? StateChanged;

    public double Level
    {
        get => _level;
        private set
        {
            if (Math.Abs(value - _level) > 0.002)
            {
                _level = value;
                Raise();
            }
        }
    }

    private double _level;
    public double PeakDb { get; private set; } = -60;

    public double Volume
    {
        get => Settings.Volume;
        set
        {
            Settings.Volume = Math.Clamp(value, 0, 1.5);
            Raise();
            Raise(nameof(VolumePercent));
        }
    }

    public double VolumePercent => Math.Round(Volume * 100);

    public bool Muted
    {
        get => Settings.Muted;
        set
        {
            Settings.Muted = value;
            Raise();
            Raise(nameof(MuteGlyph));
        }
    }

    public string MuteGlyph => Muted ? "\uE74F" : "\uE767";

    public bool Monitor
    {
        get => Settings.Monitor;
        set
        {
            Settings.Monitor = value;
            Raise();
        }
    }

    public int DelayMs
    {
        get => Settings.DelayMs;
        set
        {
            Settings.DelayMs = Math.Clamp(value, 0, 2000);
            Raise();
        }
    }

    public string DeviceName => string.IsNullOrEmpty(Settings.DeviceName)
        ? (Settings.UseSystemLoopback ? "System default output" : "No device selected")
        : Settings.DeviceName;

    public async Task StartAsync()
    {
        Stop();
        if (_disposed)
        {
            return;
        }

        await Task.Run(() =>
        {
            try
            {
                MMDeviceEnumerator? enumerator = null;
                try
                {
                    enumerator = new MMDeviceEnumerator();

                    // A saved endpoint may have been unplugged since the last session.
                    EnsureUsableDevice(enumerator);

                    if (Settings.UseSystemLoopback)
                    {
                        var id = string.IsNullOrEmpty(Settings.DeviceId)
                            ? AudioDevices.DefaultLoopbackId()
                            : Settings.DeviceId;
                        if (string.IsNullOrEmpty(id))
                        {
                            SetStatus("No playback device", false);
                            return;
                        }

                        _device = enumerator.GetDevice(id);
                        _capture = new WasapiLoopbackCapture(_device);
                    }
                    else
                    {
                        if (string.IsNullOrEmpty(Settings.DeviceId))
                        {
                            SetStatus("Select an input device", false);
                            return;
                        }

                        _device = enumerator.GetDevice(Settings.DeviceId);
                        if (_device.State != DeviceState.Active)
                        {
                            SetStatus("Device unavailable", false);
                            return;
                        }

                        _capture = new WasapiCapture(_device);
                    }

                    Settings.DeviceName = _device.FriendlyName;
                    var format = _capture.WaveFormat;
                    _nativeRate = format.SampleRate;
                    FormatLabel = $"{format.SampleRate / 1000.0:0.#} kHz · {format.Channels} ch";

                    _capture.DataAvailable += OnDataAvailable;
                    _capture.RecordingStopped += OnRecordingStopped;
                    _capture.StartRecording();
                    _lastCapture = DateTime.UtcNow;
                    SetStatus("Live", true);
                }
                finally
                {
                    enumerator?.Dispose();
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Audio channel '{Title}' failed to start", ex);
                SetStatus(Shorten(ex.Message), false);
            }
        }).ConfigureAwait(false);
    }

    private static string Shorten(string message) =>
        message.Length > 60 ? message[..60] + "…" : message;

    /// <summary>Falls back to the current default endpoint when the saved one is gone.</summary>
    private void EnsureUsableDevice(MMDeviceEnumerator enumerator)
    {
        if (string.IsNullOrEmpty(Settings.DeviceId))
        {
            // Start from a sensible default so the mixer works on first launch.
            string? auto = Settings.UseSystemLoopback
                ? AudioDevices.DefaultLoopbackId()
                : Role == "mic" ? AudioDevices.DefaultCaptureId() : null;

            if (auto is null)
            {
                return;
            }

            Settings.DeviceId = auto;
            Settings.DeviceName = AudioDevices.FriendlyName(auto);
            return;
        }

        try
        {
            using var device = enumerator.GetDevice(Settings.DeviceId);
            if (device.State == DeviceState.Active)
            {
                return;
            }
        }
        catch
        {
            // fall through to the default endpoint
        }

        var fallback = Settings.UseSystemLoopback
            ? AudioDevices.DefaultLoopbackId()
            : AudioDevices.DefaultCaptureId();

        Log.Warn($"Audio channel '{Title}': saved device unavailable, switching to the system default.");
        Settings.DeviceId = fallback ?? string.Empty;
        Settings.DeviceName = fallback is null ? string.Empty : AudioDevices.FriendlyName(fallback);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _capture is null)
        {
            return;
        }

        lock (_gate)
        {
            var format = _capture.WaveFormat;
            var needed = (e.BytesRecorded / Math.Max(1, format.BlockAlign)) * 2;
            if (_scratch.Length < needed)
            {
                _scratch = new float[needed];
            }

            var frames = SampleConverter.ToStereoFloat(e.Buffer, e.BytesRecorded, format, _scratch);
            for (var i = 0; i < frames; i++)
            {
                if (_ringCount == RingFrames)
                {
                    // Overrun: drop the oldest frame so the stream stays current.
                    _ringHead = (_ringHead + 1) % RingFrames;
                    _ringCount--;
                }

                var idx = ((_ringHead + _ringCount) % RingFrames) * 2;
                _ring[idx] = _scratch[i * 2];
                _ring[idx + 1] = _scratch[(i * 2) + 1];
                _ringCount++;
            }

            _lastCapture = DateTime.UtcNow;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.Exception is not null)
        {
            Log.Warn($"Audio channel '{Title}' stopped: {e.Exception.Message}");
            SetStatus(Shorten(e.Exception.Message), false);
        }
        else
        {
            SetStatus("Stopped", false);
        }

        // Reconnect: capture devices disappear and reappear (USB capture cards, headsets).
        if (_restartAttempts < 5)
        {
            _restartAttempts++;
            _ = Task.Run(async () =>
            {
                await Task.Delay(1200 * _restartAttempts).ConfigureAwait(false);
                if (_disposed || IsActive)
                {
                    return;
                }

                Log.Info($"Audio channel '{Title}': reconnecting (attempt {_restartAttempts}).");
                await StartAsync().ConfigureAwait(false);
            });
        }
    }

    /// <summary>Pulls frames from the channel and adds them into the destination mix.</summary>
    public void Render(float[] destination, int frames)
    {
        if (!IsActive)
        {
            DecayLevel();
            return;
        }

        if (_scratch.Length < frames * 2)
        {
            _scratch = new float[frames * 2];
        }

        var gain = Muted ? 0.0 : Settings.Volume;
        var peak = 0.0f;
        double sumSq = 0;

        lock (_gate)
        {
            var step = _nativeRate <= 0 ? 1.0 : (double)_nativeRate / _outRate;
            for (var i = 0; i < frames; i++)
            {
                while (_phase >= 1.0)
                {
                    if (_ringCount > 0)
                    {
                        _ringHead = (_ringHead + 1) % RingFrames;
                        _ringCount--;
                    }

                    _phase -= 1.0;
                }

                float l;
                float r;
                if (_ringCount >= 2)
                {
                    var a = _ringHead * 2;
                    var b = ((_ringHead + 1) % RingFrames) * 2;
                    var p = (float)_phase;
                    l = _ring[a] + (_ring[b] - _ring[a]) * p;
                    r = _ring[a + 1] + (_ring[b + 1] - _ring[a + 1]) * p;
                }
                else if (_ringCount == 1)
                {
                    var a = _ringHead * 2;
                    l = _ring[a];
                    r = _ring[a + 1];
                }
                else
                {
                    l = 0;
                    r = 0;
                }

                _phase += step;
                _scratch[i * 2] = (float)(l * gain);
                _scratch[(i * 2) + 1] = (float)(r * gain);
            }
        }

        var delayFrames = Settings.DelayMs * _outRate / 1000;
        for (var i = 0; i < frames; i++)
        {
            var l = _scratch[i * 2];
            var r = _scratch[(i * 2) + 1];

            var pl = Math.Abs(l);
            var pr = Math.Abs(r);
            if (pl > peak) peak = pl;
            if (pr > peak) peak = pr;
            sumSq += (l * l) + (r * r);

            if (delayFrames > 0)
            {
                var w = _delayWrite * 2;
                _delay[w] = l;
                _delay[w + 1] = r;

                var read = ((_delayWrite - delayFrames) % DelayFrames + DelayFrames) % DelayFrames;
                destination[i * 2] += _delay[read * 2];
                destination[(i * 2) + 1] += _delay[(read * 2) + 1];
                _delayWrite = (_delayWrite + 1) % DelayFrames;
            }
            else
            {
                destination[i * 2] += l;
                destination[(i * 2) + 1] += r;
            }
        }

        _peakHold = Math.Max(peak, _peakHold * 0.75);
        Level = Math.Min(1.0, _peakHold);
        var db = peak > 0 ? 20 * Math.Log10(peak) : -60;
        PeakDb = Math.Round(Math.Max(-60, db), 1);
        _ = sumSq;
    }

    private void DecayLevel()
    {
        _peakHold *= 0.6;
        Level = Math.Min(1.0, _peakHold);
    }

    /// <summary>True when the endpoint has delivered audio recently.</summary>
    public bool IsReceiving => IsActive && (DateTime.UtcNow - _lastCapture).TotalMilliseconds < 1500;

    public void Stop()
    {
        var capture = _capture;
        _capture = null;
        if (capture is not null)
        {
            try
            {
                capture.DataAvailable -= OnDataAvailable;
                capture.RecordingStopped -= OnRecordingStopped;
                capture.StopRecording();
            }
            catch
            {
                // already stopped
            }

            try { capture.Dispose(); } catch { }
        }

        try { _device?.Dispose(); } catch { }
        _device = null;

        lock (_gate)
        {
            _ringHead = 0;
            _ringCount = 0;
            _phase = 0;
        }

        SetStatus("Stopped", false);
        _level = 0;
        Raise(nameof(Level));
    }

    public void ApplySettings(AudioChannelSettings settings)
    {
        Settings = settings;
        Title = settings.DisplayName;
        SetStatus(IsActive ? "Live" : "Stopped", IsActive);
        Raise(nameof(DeviceName));
        Raise(nameof(Volume));
        Raise(nameof(VolumePercent));
        Raise(nameof(Muted));
        Raise(nameof(MuteGlyph));
        Raise(nameof(Monitor));
        Raise(nameof(DelayMs));
    }

    private void SetStatus(string text, bool active)
    {
        StatusText = text;
        IsActive = active;
        Raise(nameof(StatusText));
        Raise(nameof(IsActive));
        StateChanged?.Invoke(this);
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}

/// <summary>Converts interleaved PCM of any common wave format to stereo float frames.</summary>
internal static class SampleConverter
{
    public static int ToStereoFloat(byte[] buffer, int bytes, WaveFormat format, float[] dest, int destOffset = 0)
    {
        var channels = Math.Max(1, format.Channels);
        var bps = Math.Max(1, format.BitsPerSample);
        var frameBytes = Math.Max(1, format.BlockAlign);
        var frames = bytes / frameBytes;
        var maxFrames = (dest.Length - destOffset) / 2;
        if (frames > maxFrames)
        {
            frames = maxFrames;
        }

        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format.Encoding == WaveFormatEncoding.Extensible && bps == 32);

        for (var f = 0; f < frames; f++)
        {
            var offset = f * frameBytes;
            float l, r;

            if (channels == 1)
            {
                var v = ReadSample(buffer, offset, bps, isFloat);
                l = v;
                r = v;
            }
            else
            {
                l = ReadSample(buffer, offset, bps, isFloat);
                r = ReadSample(buffer, offset + bps / 8, bps, isFloat);
            }

            dest[destOffset + (f * 2)] = l;
            dest[destOffset + (f * 2) + 1] = r;
        }

        return frames;
    }

    private static float ReadSample(byte[] buffer, int offset, int bits, bool isFloat)
    {
        if (offset < 0 || offset >= buffer.Length)
        {
            return 0;
        }

        switch (bits)
        {
            case 32 when isFloat:
                return offset + 4 <= buffer.Length ? BitConverter.ToSingle(buffer, offset) : 0;

            case 32:
                return offset + 4 <= buffer.Length
                    ? BitConverter.ToInt32(buffer, offset) / 2147483648f
                    : 0;

            case 24:
                if (offset + 3 > buffer.Length)
                {
                    return 0;
                }

                var v24 = buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16);
                if ((v24 & 0x800000) != 0)
                {
                    v24 |= unchecked((int)0xFF000000);
                }

                return v24 / 8388608f;

            case 16:
                return offset + 2 <= buffer.Length ? BitConverter.ToInt16(buffer, offset) / 32768f : 0;

            case 8:
                return (buffer[offset] - 128) / 128f;

            default:
                return 0;
        }
    }
}

/// <summary>
/// Owns the audio mixer. Real capture, real mixing, real meters, real monitoring.
/// The final mixes are streamed to the encoder through <see cref="PcmPipeSink"/>.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly SettingsService _settings;
    private readonly List<AudioChannel> _channels = new();
    private readonly object _mixGate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private float[] _mixAll = new float[2 * 4800];
    private float[] _mixGame = new float[2 * 4800];
    private float[] _mixMic = new float[2 * 4800];
    private float[] _monitorMix = new float[2 * 4800];
    private float[] _channelScratch = new float[2 * 4800];
    private byte[] _pcm16 = new byte[2 * 4800 * 2];

    private Timer? _timer;
    private long _lastTicks;
    private int _mixing;
    private double _meterClock;
    private WasapiOut? _monitor;
    private BufferedWaveProvider? _monitorBuffer;
    private PcmPipeSink? _gameSink;
    private PcmPipeSink? _micSink;
    private PcmPipeSink? _mixedSink;
    private bool _separateTracks;

    public AudioEngine(SettingsService settings)
    {
        _settings = settings;
    }

    public IReadOnlyList<AudioChannel> Channels => _channels;
    public bool IsRunning { get; private set; }
    public bool IsMonitoring { get; private set; }
    public string? LastError { get; private set; }
    public int SampleRate => _settings.Current.Audio.SampleRate;
    public double MonitorLagMs { get; private set; } = -1;

    public event Action? MetersUpdated;
    public event Action<string>? StatusChanged;

    /// <summary>Creates the channels for the configured devices and starts capture.</summary>
    public async Task StartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        LastError = null;

        var audio = _settings.Current.Audio;
        _channels.Clear();
        foreach (var role in new[] { "game", "capture", "mic" })
        {
            var cfg = audio.Channel(role);
            var channel = new AudioChannel(cfg, audio.SampleRate);
            _channels.Add(channel);
            await channel.StartAsync().ConfigureAwait(false);
        }

        StartMonitoring();

        IsRunning = true;
        _lastTicks = _clock.ElapsedTicks;
        _timer = new Timer(_ => Mix(), null, 10, 10);
        StatusChanged?.Invoke("Audio engine running");
        Log.Info("Audio engine started with " + _channels.Count + " channels.");
    }

    public async Task StopAsync()
    {
        IsRunning = false;
        _timer?.Dispose();
        _timer = null;

        foreach (var channel in _channels)
        {
            channel.Dispose();
        }

        _channels.Clear();
        StopMonitoring();
        CloseRecordSinks();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void StartMonitoring()
    {
        StopMonitoring();
        var audio = _settings.Current.Audio;
        if (!audio.MonitoringEnabled)
        {
            return;
        }

        try
        {
            MMDevice? device = null;
            if (!string.IsNullOrEmpty(audio.MonitoringDeviceId))
            {
                var enumerator = new MMDeviceEnumerator();
                device = enumerator.GetDevice(audio.MonitoringDeviceId);
            }

            _monitorBuffer = new BufferedWaveProvider(new WaveFormat(audio.SampleRate, 16, 2))
            {
                BufferDuration = TimeSpan.FromMilliseconds(400),
                DiscardOnBufferOverflow = true
            };

            _monitor = device is null
                ? new WasapiOut(AudioClientShareMode.Shared, 60)
                : new WasapiOut(device, AudioClientShareMode.Shared, true, 60);

            _monitor.Init(_monitorBuffer);
            _monitor.Play();
            IsMonitoring = true;
            Log.Info("Audio monitoring active.");
        }
        catch (Exception ex)
        {
            IsMonitoring = false;
            LastError = "Monitoring unavailable: " + ex.Message;
            Log.Warn("Audio monitoring failed: " + ex.Message);
        }
    }

    /// <summary>Restarts only the monitoring output, leaving capture channels untouched.</summary>
    public void RestartMonitoring()
    {
        StopMonitoring();
        if (IsRunning)
        {
            StartMonitoring();
        }

        Log.Info(IsMonitoring ? "Monitoring output restarted." : "Monitoring output stopped.");
    }

    private void StopMonitoring()
    {
        try { _monitor?.Stop(); } catch { }
        try { _monitor?.Dispose(); } catch { }
        _monitor = null;
        _monitorBuffer = null;
        IsMonitoring = false;
    }

    private void Mix()
    {
        if (!IsRunning || Interlocked.Exchange(ref _mixing, 1) == 1)
        {
            return;
        }

        try
        {
            var now = _clock.ElapsedTicks;
            var elapsed = (now - _lastTicks) / (double)Stopwatch.Frequency;
            _lastTicks = now;

            var frames = (int)Math.Round(elapsed * SampleRate);
            frames = Math.Clamp(frames, 0, 4800 * 2);
            if (frames == 0)
            {
                return;
            }

            EnsureBuffers(frames);
            Array.Clear(_mixAll, 0, frames * 2);
            Array.Clear(_mixGame, 0, frames * 2);
            Array.Clear(_mixMic, 0, frames * 2);
            Array.Clear(_monitorMix, 0, frames * 2);

            lock (_mixGate)
            {
                foreach (var channel in _channels)
                {
                    Array.Clear(_channelScratch, 0, frames * 2);
                    channel.Render(_channelScratch, frames);

                    AddInto(_mixAll, _channelScratch, frames);
                    if (string.Equals(channel.Role, "mic", StringComparison.OrdinalIgnoreCase))
                    {
                        AddInto(_mixMic, _channelScratch, frames);
                    }
                    else
                    {
                        AddInto(_mixGame, _channelScratch, frames);
                    }

                    if (channel.Settings.Monitor && !channel.Settings.Muted)
                    {
                        AddInto(_monitorMix, _channelScratch, frames);
                    }
                }
            }

            if (IsMonitoring && _monitorBuffer is not null)
            {
                var gain = _settings.Current.Audio.MonitoringVolume;
                var bytes = FloatToPcm16(_monitorMix, frames, gain, _pcm16);
                try
                {
                    _monitorBuffer.AddSamples(_pcm16, 0, bytes);
                    MonitorLagMs = Math.Round(_monitorBuffer.BufferedDuration.TotalMilliseconds, 0);
                }
                catch
                {
                    IsMonitoring = false;
                }
            }

            if (_gameSink is not null && _micSink is not null)
            {
                var bytes = FloatToPcm16(_mixGame, frames, 1.0, _pcm16);
                _gameSink.Write(_pcm16.AsSpan(0, bytes).ToArray());
                bytes = FloatToPcm16(_mixMic, frames, 1.0, _pcm16);
                _micSink.Write(_pcm16.AsSpan(0, bytes).ToArray());
            }
            else if (_mixedSink is not null)
            {
                var bytes = FloatToPcm16(_mixAll, frames, 1.0, _pcm16);
                _mixedSink.Write(_pcm16.AsSpan(0, bytes).ToArray());
            }

            _meterClock += elapsed;
            if (_meterClock >= 0.033)
            {
                _meterClock = 0;
                MetersUpdated?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Debug("Mixer tick failed: " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _mixing, 0);
        }
    }

    private void EnsureBuffers(int frames)
    {
        var needed = frames * 2;
        if (_mixAll.Length < needed)
        {
            _mixAll = new float[needed];
            _mixGame = new float[needed];
            _mixMic = new float[needed];
            _monitorMix = new float[needed];
            _channelScratch = new float[needed];
            _pcm16 = new byte[needed * 2];
        }
    }

    private static void AddInto(float[] destination, float[] source, int frames)
    {
        var count = frames * 2;
        for (var i = 0; i < count; i++)
        {
            destination[i] += source[i];
        }
    }

    private static int FloatToPcm16(float[] source, int frames, double gain, byte[] destination)
    {
        var count = frames * 2;
        for (var i = 0; i < count; i++)
        {
            var v = source[i] * gain;
            v = Math.Clamp(v, -1f, 1f);
            var s = (short)(v * 32767);
            destination[i * 2] = (byte)(s & 0xFF);
            destination[(i * 2) + 1] = (byte)((s >> 8) & 0xFF);
        }

        return count * 2;
    }

    /// <summary>Opens encoder pipe sinks and returns their pipe paths in FFmpeg input order.</summary>
    public IReadOnlyList<string> OpenRecordSinks(bool separateTracks)
    {
        CloseRecordSinks();
        _separateTracks = separateTracks;
        var suffix = Guid.NewGuid().ToString("N")[..8];

        if (separateTracks)
        {
            _gameSink = new PcmPipeSink("fifine-game-" + suffix, SampleRate, 2);
            _micSink = new PcmPipeSink("fifine-mic-" + suffix, SampleRate, 2);
            _gameSink.Open();
            _micSink.Open();
            return new[] { _gameSink.PipePath, _micSink.PipePath };
        }

        _mixedSink = new PcmPipeSink("fifine-mix-" + suffix, SampleRate, 2);
        _mixedSink.Open();
        return new[] { _mixedSink.PipePath };
    }

    public void CloseRecordSinks()
    {
        _gameSink?.Dispose();
        _micSink?.Dispose();
        _mixedSink?.Dispose();
        _gameSink = null;
        _micSink = null;
        _mixedSink = null;
        _separateTracks = false;
    }

    public bool SeparateTracksActive => _separateTracks;
    public string? ActiveSinkSummary => _gameSink is not null
        ? "2 tracks (game + mic)"
        : _mixedSink is not null ? "1 mixed track" : null;

    public ValueTask DisposeAsync() => new(StopAsync());

    public void Dispose()
    {
        IsRunning = false;
        _timer?.Dispose();
        foreach (var channel in _channels)
        {
            channel.Dispose();
        }

        StopMonitoring();
        CloseRecordSinks();
    }
}
