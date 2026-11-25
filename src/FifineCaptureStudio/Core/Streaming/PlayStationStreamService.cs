using System.Text;
using FifineCaptureStudio.Audio;
using FifineCaptureStudio.Core.Streaming.Nixani;

namespace FifineCaptureStudio.Core.Streaming;

/// <summary>
/// PlayStation Remote Play through the Nixani Engine process: discovery, linking with the console's
/// 8-digit code, session start, compressed video into the capture pipeline and console audio into
/// the mixer. The engine is a separate AGPL-3.0 executable bundled under the app's streaming folder;
/// FIFINE starts it and talks to it over stdin/stdout (see THIRD-PARTY-NOTICES.md).
/// </summary>
public sealed class PlayStationStreamService : IAsyncDisposable
{
    private readonly SettingsService _settings;
    private readonly AudioEngine _audio;
    private readonly object _gate = new();

    private NixaniEngineClient? _client;
    private VideoPipeFeed? _pipe;
    private CancellationTokenSource? _lifetime;
    private TaskCompletionSource<bool>? _streaming;
    private volatile bool _intentionalStop;
    private int _reconnects;

    public PlayStationStreamService(SettingsService settings, AudioEngine audio)
    {
        _settings = settings;
        _audio = audio;
    }

    public event Action? Changed;

    public ConsoleStreamState State { get; private set; } = ConsoleStreamState.Idle;
    public string StatusText { get; private set; } = "Not connected";
    public string? LastError { get; private set; }
    public string? ConsoleName { get; private set; }
    public string? VideoPipePath => _pipe?.PipePath;
    public bool IsStreaming => State == ConsoleStreamState.Streaming;
    public bool IsEngineRunning => _client?.IsRunning == true;
    public string? EngineVersion => _client?.EngineVersion;
    public string? EnginePath => _client?.ExePath ?? NixaniEngineClient.LocateEngine(_settings.Current.PlayStation.EnginePath);
    public bool IsEngineAvailable => EnginePath is not null;
    public ConsoleStreamStats Stats => _client?.Stats ?? _stats;
    public long FramesDelivered => _pipe?.FramesQueued ?? 0;
    public long FramesDropped => _pipe?.FramesDropped ?? 0;

    private readonly ConsoleStreamStats _stats = new();

    public ConsoleStreamOptions Options
    {
        get
        {
            var ps = _settings.Current.PlayStation;
            return new ConsoleStreamOptions
            {
                Width = ps.Width,
                Height = ps.Height,
                Fps = ps.Fps,
                BitrateKbps = ps.BitrateKbps,
                Hevc = ps.PreferHevc && ps.IsPs5,
                DualSense = ps.DualSenseHaptics
            };
        }
    }

    // ------------------------------------------------------------------ engine

    private void SetState(ConsoleStreamState state, string text, string? error = null)
    {
        State = state;
        StatusText = text;
        LastError = error;
        Changed?.Invoke();
    }

    public async Task<bool> EnsureEngineAsync(CancellationToken token)
    {
        if (_client?.IsRunning == true)
        {
            return true;
        }

        var path = NixaniEngineClient.LocateEngine(_settings.Current.PlayStation.EnginePath);
        if (path is null)
        {
            SetState(ConsoleStreamState.EngineMissing,
                "The bundled PlayStation streaming engine is missing. Reinstall FIFINE Capture Studio, or set the engine path under Gaming Sources → PlayStation Remote Play.",
                "nixani-engine.exe not found");
            return false;
        }

        try
        {
            _lifetime?.Dispose();
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            var client = new NixaniEngineClient(path);
            client.HostsChanged += _ => Changed?.Invoke();
            client.StateChanged += OnStateChanged;
            client.VideoFrame += OnVideoFrame;
            client.Audio += OnAudio;
            client.LogMessage += message => Log.Debug(message);
            client.LoginPinRequested += wrong =>
                SetState(ConsoleStreamState.Connecting,
                    wrong ? "The console PIN was rejected — enter it again." : "The console is asking for its login PIN.",
                    wrong ? "Wrong console PIN" : null);

            _client = client;
            SetState(ConsoleStreamState.Starting, "Starting the Remote Play engine…");
            await client.StartAsync(_lifetime.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Remote Play engine failed to start", ex);
            _client = null;
            SetState(ConsoleStreamState.Failed, "The Remote Play engine could not start: " + ex.Message, ex.Message);
            return false;
        }
    }

    private void OnStateChanged(ConsoleStreamState state, string text, bool isError)
    {
        switch (state)
        {
            case ConsoleStreamState.Connecting:
                SetState(ConsoleStreamState.Connecting, string.IsNullOrWhiteSpace(text) ? "Connecting to the console…" : text);
                break;

            case ConsoleStreamState.Streaming:
                _reconnects = 0;
                SetState(ConsoleStreamState.Streaming,
                    string.IsNullOrWhiteSpace(text) ? "Receiving console video." : text);
                _streaming?.TrySetResult(true);
                break;

            case ConsoleStreamState.Stopped:
                if (_intentionalStop)
                {
                    SetState(ConsoleStreamState.Idle, "Stream stopped.");
                    break;
                }

                SetState(ConsoleStreamState.Stopped,
                    string.IsNullOrWhiteSpace(text) ? "The console ended the stream." : text);
                _streaming?.TrySetResult(false);
                _ = AutoReconnectAsync();
                break;

            case ConsoleStreamState.Failed:
                SetState(ConsoleStreamState.Failed, text, text);
                _streaming?.TrySetResult(false);
                break;
        }
    }

    private async Task AutoReconnectAsync()
    {
        if (_intentionalStop || _reconnects >= 3)
        {
            return;
        }

        _reconnects++;
        SetState(ConsoleStreamState.Connecting, $"Stream lost — reconnecting ({_reconnects}/3)…");
        await Task.Delay(TimeSpan.FromSeconds(2 * _reconnects)).ConfigureAwait(false);
        if (_intentionalStop || _client?.IsRunning != true)
        {
            return;
        }

        try
        {
            var ok = await ConnectAsync(CancellationToken.None).ConfigureAwait(false);
            if (!ok && _reconnects < 3)
            {
                await AutoReconnectAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Remote Play reconnect failed: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ discovery + registration

    public async Task<IReadOnlyList<NixaniHost>> DiscoverAsync(TimeSpan timeout, CancellationToken token)
    {
        if (!await EnsureEngineAsync(token).ConfigureAwait(false))
        {
            return Array.Empty<NixaniHost>();
        }

        SetState(ConsoleStreamState.Discovering, "Searching the local network for PlayStation consoles…");
        var hosts = await _client!.DiscoverAsync(timeout, token).ConfigureAwait(false);
        if (hosts.Count == 0)
        {
            SetState(ConsoleStreamState.Idle,
                "No PlayStation console answered. Make sure Remote Play is enabled and the console is on the same network.");
        }
        else
        {
            SetState(ConsoleStreamState.Idle, $"{hosts.Count} console(s) found.");
        }

        return hosts;
    }

    /// <summary>Links with the console using the 8-digit code shown on the TV.</summary>
    public async Task<NixaniRegistration> RegisterAsync(string address, string code, uint consolePin, bool isPs5, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return new NixaniRegistration(false, "No console has been discovered yet. Wait for the scan or press Connect again.", string.Empty,
                Array.Empty<byte>(), 0, Array.Empty<byte>(), Array.Empty<byte>(), 0, 0);
        }

        if (!NixaniEngineClient.IsLinkCode(code, out var linkCode))
        {
            return new NixaniRegistration(false, "The pairing number must be the 8 digits shown on the console.",
                string.Empty, Array.Empty<byte>(), 0, Array.Empty<byte>(), Array.Empty<byte>(), 0, 0);
        }

        if (!await EnsureEngineAsync(token).ConfigureAwait(false))
        {
            return new NixaniRegistration(false, LastError ?? "Engine unavailable.", string.Empty,
                Array.Empty<byte>(), 0, Array.Empty<byte>(), Array.Empty<byte>(), 0, 0);
        }

        var ps = _settings.Current.PlayStation;
        ps.ConsoleAddress = address.Trim();
        ps.ConsolePin = consolePin;
        ps.IsPs5 = isPs5;
        var accountId = ParseAccountId(ps.AccountId);

        // The PS5 validates the PSN account ID during link-code registration and rejects an
        // all-zero one with CE-110032-7. Stop before the engine sends a request that cannot work.
        if (isPs5 && accountId.All(value => value == 0))
        {
            const string missingAccount = "The PS5 needs your PSN account ID once before it accepts the code. " +
                "Add it under Gaming Sources → PlayStation Remote Play, then show a new code on the console and press Connect.";
            SetState(ConsoleStreamState.Failed, missingAccount, "PSN account ID required");
            return new NixaniRegistration(false, missingAccount, string.Empty, Array.Empty<byte>(), 0,
                Array.Empty<byte>(), Array.Empty<byte>(), 0, 0);
        }

        SetState(ConsoleStreamState.Registering, "Registering with the console — keep the code on screen…");
        NixaniRegistration result;
        try
        {
            result = await _client!.RegisterAsync(ps.ConsoleAddress, isPs5, linkCode, consolePin,
                accountId, TimeSpan.FromSeconds(90), token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetState(ConsoleStreamState.Failed, "Registration failed: " + ex.Message, ex.Message);
            return new NixaniRegistration(false, ex.Message, string.Empty, Array.Empty<byte>(), 0,
                Array.Empty<byte>(), Array.Empty<byte>(), 0, 0);
        }

        if (!result.Ok)
        {
            SetState(ConsoleStreamState.Failed,
                "The console rejected the pairing code: " + (string.IsNullOrWhiteSpace(result.Error) ? "no reason given" : result.Error),
                result.Error);
            return result;
        }

        ps.RegistKey = SecretStore.ProtectBytes(result.RegistKey);
        ps.RpKey = SecretStore.ProtectBytes(result.RpKey);
        ps.ConsoleName = string.IsNullOrWhiteSpace(result.Nickname) ? ps.ConsoleName : result.Nickname;
        ps.HostId = ps.ConsoleAddress;
        _settings.Save();

        ConsoleName = ps.ConsoleName;
        SetState(ConsoleStreamState.Idle, $"Registered {ps.ConsoleName}. Ready to start Remote Play.");
        return result;
    }

    /// <summary>PSN account id, accepted as 16 hex digits or base64 (as chiaki documents it).</summary>
    public static byte[] ParseAccountId(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return new byte[8];
        }

        try
        {
            if (value.Length == 16 && value.All(Uri.IsHexDigit))
            {
                return Convert.FromHexString(value);
            }

            var bytes = Convert.FromBase64String(value);
            if (bytes.Length == 8)
            {
                return bytes;
            }
        }
        catch
        {
            // fall through
        }

        return new byte[8];
    }

    public bool HasRegistration
    {
        get
        {
            var ps = _settings.Current.PlayStation;
            return SecretStore.UnprotectBytes(ps.RegistKey) is { Length: 16 }
                   && SecretStore.UnprotectBytes(ps.RpKey) is { Length: 16 };
        }
    }

    public void Forget()
    {
        var ps = _settings.Current.PlayStation;
        ps.RegistKey = string.Empty;
        ps.RpKey = string.Empty;
        ps.ConsoleName = string.Empty;
        ps.ConsoleAddress = string.Empty;
        ps.HostId = string.Empty;
        _settings.Save();
        SetState(ConsoleStreamState.Idle, "Console registration removed.");
    }

    // ------------------------------------------------------------------ session

    public async Task<bool> ConnectAsync(CancellationToken token)
    {
        if (IsStreaming)
        {
            return true;
        }

        var ps = _settings.Current.PlayStation;
        var registKey = SecretStore.UnprotectBytes(ps.RegistKey);
        var rpKey = SecretStore.UnprotectBytes(ps.RpKey);
        if (registKey is not { Length: 16 } || rpKey is not { Length: 16 })
        {
            SetState(ConsoleStreamState.Failed,
                "This console is not registered yet. Pair it with the code shown in Settings → System → Remote Play → Link Device.",
                "Console not registered");
            return false;
        }

        if (!await EnsureEngineAsync(token).ConfigureAwait(false))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(ps.ConsoleAddress))
        {
            SetState(ConsoleStreamState.Failed, "No console is stored yet. Pair with the code shown on the console.", "No console");
            return false;
        }

        _intentionalStop = false;
        _pipe ??= CreatePipe();

        var options = Options;
        var accountId = ParseAccountId(ps.AccountId);
        _streaming = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SetState(ConsoleStreamState.Connecting,
            $"Connecting to {ps.ConsoleName} at {ps.Resolution}p{ps.Fps} · {options.CodecName}…");

        try
        {
            await _client!.StartSessionAsync(new NixaniSessionParams(
                ps.ConsoleAddress,
                ps.IsPs5,
                registKey,
                rpKey,
                accountId,
                options.BackendResolution,
                options.BackendFps,
                options.BitrateKbps,
                (byte)(options.Hevc ? 1 : 0),
                options.DualSense), token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetState(ConsoleStreamState.Failed, "Could not start the Remote Play session: " + ex.Message, ex.Message);
            return false;
        }

        var streaming = _streaming.Task;
        var completed = await Task.WhenAny(streaming, Task.Delay(TimeSpan.FromSeconds(45), token)).ConfigureAwait(false);
        if (completed != streaming || !await streaming.ConfigureAwait(false))
        {
            if (!IsStreaming)
            {
                await StopSessionAsync().ConfigureAwait(false);
                SetState(ConsoleStreamState.Failed,
                    LastError ?? "The console did not start streaming. Check Remote Play, the console's network and that it is not in rest mode.",
                    LastError ?? "Session timeout");
                return false;
            }
        }

        Log.Info($"Remote Play streaming from {ps.ConsoleName} ({ps.ConsoleAddress}) at {ps.Resolution}p{ps.Fps}");
        return true;
    }

    private VideoPipeFeed CreatePipe()
    {
        var pipe = new VideoPipeFeed();
        pipe.FramesLost += () =>
        {
            if (IsStreaming)
            {
                _client?.RequestKeyframe();
            }
        };
        pipe.Open();
        return pipe;
    }

    private void OnVideoFrame(CompressedVideoFrame frame)
    {
        _pipe?.Push(frame.Data);
    }

    private void OnAudio(ConsoleAudioPacket packet)
    {
        _audio.PushConsolePcm(packet.Pcm16, packet.Channels, packet.SampleRate);
    }

    public void SendPad(NixaniPadState pad)
    {
        if (IsStreaming && _settings.Current.PlayStation.ForwardController)
        {
            _client?.SendPad(pad);
        }
    }

    public void SendLoginPin(string pin)
    {
        _client?.SendLoginPin(pin);
    }

    public void RequestKeyframe() => _client?.RequestKeyframe();

    private async Task StopSessionAsync()
    {
        try
        {
            if (_client is not null)
            {
                await _client.StopSessionAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        catch
        {
            // engine gone
        }
    }

    public async Task StopAsync()
    {
        _intentionalStop = true;
        await StopSessionAsync().ConfigureAwait(false);

        if (_pipe is not null)
        {
            await _pipe.DisposeAsync().ConfigureAwait(false);
            _pipe = null;
        }

        _audio.ClearConsoleAudio();
        SetState(ConsoleStreamState.Idle, "Stream stopped.");
    }

    /// <summary>Full teardown, used when the app closes or the engine path changes.</summary>
    public async Task ShutdownAsync()
    {
        await StopAsync().ConfigureAwait(false);
        var client = _client;
        _client = null;
        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        try { _lifetime?.Cancel(); } catch { }
        _lifetime?.Dispose();
        _lifetime = null;
        SetState(ConsoleStreamState.Idle, "Engine stopped.");
    }

    /// <summary>Loops the local controller state into the console while streaming.</summary>
    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
    }

    public string DescribeState() => new StringBuilder()
        .Append(State).Append(" · ").Append(StatusText)
        .ToString();
}

