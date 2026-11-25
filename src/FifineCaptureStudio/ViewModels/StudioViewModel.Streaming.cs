using System.Windows.Input;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.Streaming.Nixani;
using FifineCaptureStudio.Core.Streaming;
using FifineCaptureStudio.Views;

namespace FifineCaptureStudio.ViewModels;

/// <summary>
/// Console streaming surface. Everything shown here is read from the running backend; the
/// state, counters and latency are never synthesised.
/// </summary>
public sealed partial class StudioViewModel
{
    public PlayStationStreamService PlayStation => _services.PlayStation;

    public bool IsConsoleSource => Config.Capture.SourceKind == CaptureSourceKind.ConsoleStream;
    public bool IsConsoleStreaming => _services.PlayStation.IsStreaming;
    public bool IsPlayStationEngineAvailable => _services.PlayStation.IsEngineAvailable;

    public string ConsoleStateLabel => _services.PlayStation.State switch
    {
        ConsoleStreamState.Streaming => "Streaming",
        ConsoleStreamState.Connecting => "Connecting",
        ConsoleStreamState.Registering => "Registering",
        ConsoleStreamState.Discovering => "Searching",
        ConsoleStreamState.Starting => "Starting engine",
        ConsoleStreamState.EngineMissing => "Engine missing",
        ConsoleStreamState.Failed => "Failed",
        ConsoleStreamState.Stopped => "Stream ended",
        _ => "Idle"
    };

    public string ConsoleStateColor => _services.PlayStation.State switch
    {
        ConsoleStreamState.Streaming => "#FF45D69A",
        ConsoleStreamState.Failed or ConsoleStreamState.EngineMissing => "#FFFF8FA3",
        ConsoleStreamState.Stopped => "#FFFFC46B",
        ConsoleStreamState.Idle => "#FF8E9AB2",
        _ => "#FF35C9FF"
    };

    public string ConsoleStatusText => _services.PlayStation.StatusText;

    public string ConsoleEngineText => _services.PlayStation.EnginePath is { } path
        ? "Engine: " + System.IO.Path.GetFileName(path) + " " + (_services.PlayStation.EngineVersion ?? string.Empty)
        : "Engine: bundled nixani-engine.exe not found — reinstall FIFINE or set the engine path in Gaming Sources → PlayStation Remote Play.";

    public string ConsoleCodecLabel => _services.PlayStation.Options.CodecName;

    public string ConsoleStreamModeLabel =>
        $"{_services.PlayStation.Options.Height}p{_services.PlayStation.Options.BackendFps} · {ConsoleCodecLabel}";

    public string ConsoleStatsText
    {
        get
        {
            var stats = _services.PlayStation.Stats;
            if (stats.FramesReceived == 0)
            {
                return "No console packets received yet.";
            }

            var audio = stats.AudioSampleRate > 0
                ? $" · audio {stats.AudioChannels}ch @ {stats.AudioSampleRate} Hz"
                : string.Empty;
            return $"{stats.FramesReceived} frames · {stats.PacketsLost} lost · RTT {stats.RttMs:0.#} ms" +
                   $" · {stats.FecFailures} FEC fails{audio}";
        }
    }

    public string ConsoleAudioText
    {
        get
        {
            var engine = _services.Audio;
            return engine.HasConsoleAudio || engine.ConsoleSamplesReceived > 0
                ? $"{engine.ConsoleSamplesReceived:N0} samples · level {engine.ConsoleLevel:0.00}"
                : "Console audio: silent";
        }
    }

    public string PlayStationAccountId
    {
        get => Config.PlayStation.AccountId;
        set
        {
            if (value == Config.PlayStation.AccountId)
            {
                return;
            }

            Config.PlayStation.AccountId = (value ?? string.Empty).Trim();
            _services.Settings.SaveSoon();
            Raise();
        }
    }

    private ICommand? _signInPsnCommand;
    public ICommand SignInPsnCommand => _signInPsnCommand ??= new RelayCommand(_ =>
    {
        var accountId = PsnSignInWindow.PromptForAccountId();
        if (string.IsNullOrWhiteSpace(accountId))
        {
            return;
        }

        PlayStationAccountId = accountId;
        Raise(nameof(ConsoleStatusText));
    });

    public string PlayStationEnginePath
    {
        get => Config.PlayStation.EnginePath;
        set
        {
            if (value == Config.PlayStation.EnginePath)
            {
                return;
            }

            Config.PlayStation.EnginePath = (value ?? string.Empty).Trim();
            _services.Settings.SaveSoon();
            Raise();
            Raise(nameof(ConsoleEngineText));
            Raise(nameof(IsPlayStationEngineAvailable));
        }
    }

    public string ConsoleRegistrationLabel => _services.PlayStation.HasRegistration
        ? $"Registered: {Config.PlayStation.ConsoleName}"
        : "Not registered";

    private ICommand? _forgetConsoleCommand;
    public ICommand ForgetConsoleCommand => _forgetConsoleCommand ??= new RelayCommand(_ =>
    {
        _services.PlayStation.Forget();
        RaiseConsoleState();
    });

    private ICommand? _stopConsoleStreamCommand;
    public ICommand StopConsoleStreamCommand => _stopConsoleStreamCommand ??= new AsyncRelayCommand(async _ =>
    {
        await _services.PlayStation.StopAsync();
        RaiseConsoleState();
    });

    private ICommand? _useConsoleSourceCommand;
    public ICommand UseConsoleSourceCommand => _useConsoleSourceCommand ??= new AsyncRelayCommand(async _ =>
    {
        await SelectConsoleSourceAsync();
    });

    /// <summary>Switches the capture pipeline to the console stream and starts it.</summary>
    public async Task<bool> SelectConsoleSourceAsync()
    {
        Config.Capture.SourceKind = CaptureSourceKind.ConsoleStream;
        Config.Capture.Platform = PlatformKind.PS5;
        Config.Capture.ResolutionWidth = Config.PlayStation.Width;
        Config.Capture.ResolutionHeight = Config.PlayStation.Height;
        Config.Capture.Fps = Config.PlayStation.Fps;
        _services.Settings.Save();

        Raise(nameof(SelectedSourceKind));
        Raise(nameof(IsConsoleSource));
        Raise(nameof(SourceKindHint));
        await RebuildSourceOptionsAsync(restart: false);

        if (!_services.Session.IsPreviewing)
        {
            await StartCaptureAsync();
        }
        else
        {
            await RestartIfRunningAsync();
        }

        return _services.Session.IsPreviewing;
    }

    /// <summary>
    /// Switches to the console stream and only returns true once real decoded frames arrive.
    /// </summary>
    public async Task<bool> StartConsoleCaptureAndVerifyAsync(TimeSpan timeout)
    {
        if (!await SelectConsoleSourceAsync())
        {
            return false;
        }

        var before = _services.Session.FramesDecoded;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            RaiseConsoleState();
            if (_services.Session.HasSignal && _services.Session.FramesDecoded > before)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Sends the local controller to the console while streaming. The pad is only forwarded during
    /// a live session, so input never goes anywhere else.
    /// </summary>
    public void ForwardLocalController(ControllerState state)
    {
        if (!_services.PlayStation.IsStreaming || !Config.PlayStation.ForwardController)
        {
            return;
        }

        if (!state.Connected)
        {
            _services.PlayStation.SendPad(NixaniPadState.Idle);
            return;
        }

        uint buttons = 0;
        if (state.Up) buttons |= 1u << 0;
        if (state.Down) buttons |= 1u << 1;
        if (state.Left) buttons |= 1u << 2;
        if (state.Right) buttons |= 1u << 3;
        if (state.A) buttons |= 1u << 4;
        if (state.B) buttons |= 1u << 5;
        if (state.X) buttons |= 1u << 6;
        if (state.Y) buttons |= 1u << 7;
        if (state.LeftBumper) buttons |= 1u << 8;
        if (state.RightBumper) buttons |= 1u << 9;
        if (state.LeftStickButton) buttons |= 1u << 10;
        if (state.RightStickButton) buttons |= 1u << 11;
        if (state.Back) buttons |= 1u << 12;
        if (state.Start) buttons |= 1u << 13;
        if (state.Guide) buttons |= 1u << 15;

        _services.PlayStation.SendPad(new NixaniPadState(
            buttons,
            (byte)Math.Clamp(state.LeftTrigger * 255, 0, 255),
            (byte)Math.Clamp(state.RightTrigger * 255, 0, 255),
            ToAxis(state.LeftX),
            ToAxis(-state.LeftY),
            ToAxis(state.RightX),
            ToAxis(-state.RightY)));
    }

    private static short ToAxis(float value) =>
        (short)Math.Clamp(Math.Round(value * 32767f), -32768, 32767);

    public void RaiseConsoleState()
    {
        Raise(nameof(IsConsoleSource));
        Raise(nameof(IsConsoleStreaming));
        Raise(nameof(IsPlayStationEngineAvailable));
        Raise(nameof(ConsoleStateLabel));
        Raise(nameof(ConsoleStateColor));
        Raise(nameof(ConsoleStatusText));
        Raise(nameof(ConsoleEngineText));
        Raise(nameof(ConsoleCodecLabel));
        Raise(nameof(ConsoleStreamModeLabel));
        Raise(nameof(ConsoleStatsText));
        Raise(nameof(ConsoleAudioText));
        Raise(nameof(ConsoleRegistrationLabel));
    }
}
