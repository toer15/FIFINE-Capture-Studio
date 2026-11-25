using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.Pairing;

namespace FifineCaptureStudio.ViewModels;

/// <summary>
/// Drives the pairing wizard. A platform is only reported CONNECTED after the capture
/// pipeline has produced real decoded frames for that platform — never on discovery alone.
/// </summary>
public sealed class PairingViewModel : INotifyPropertyChanged
{
    private readonly StudioViewModel _studio;
    private readonly TrustedDeviceStore _store = new();
    private CancellationTokenSource? _searchCts;

    public PairingViewModel(StudioViewModel studio, PairingPlatform platform)
    {
        _studio = studio;
        Platform = platform;
        Devices = new ObservableCollection<PairingDevice>();
        TrustedDevices = new ObservableCollection<TrustedDevice>(_store.For(platform));

        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        CancelCommand = new RelayCommand(Cancel);
        PcDirectCommand = new AsyncRelayCommand(PairLocalPcAsync);
        CaptureCardCommand = new AsyncRelayCommand(ContinueWithCaptureCardAsync);
        ForgetCommand = new RelayCommand(p =>
        {
            if (p is TrustedDevice device)
            {
                _store.Forget(Platform, device.Address);
                TrustedDevices.Remove(device);
                Status = $"Forgot {device.Name}.";
            }
        });

        Status = Platform switch
        {
            PairingPlatform.PlayStation => "Enter the pairing code shown on the console. FIFINE finds the console on this network automatically.",
            PairingPlatform.Xbox => "Xbox network video is not available in this build. HDMI capture is the supported path.",
            _ => "FIFINE finds a Link Agent on this network automatically."
        };

        // The console/agent is discovered in the background, so the only thing the user
        // has to provide is the pairing code. Connect searches again if this scan misses it.
        if (Platform != PairingPlatform.Xbox)
        {
            _ = SearchAsync();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public PairingPlatform Platform { get; }
    public string PlatformName => Platform switch
    {
        PairingPlatform.PlayStation => "PlayStation",
        PairingPlatform.Xbox => "Xbox",
        _ => "PC"
    };

    public string PlatformGlyph => Platform switch
    {
        PairingPlatform.PlayStation => "PS",
        PairingPlatform.Xbox => "XB",
        _ => "PC"
    };

    public string ConnectLabel => Platform switch
    {
        PairingPlatform.PlayStation => "Connect PlayStation",
        PairingPlatform.Xbox => "Connect Xbox",
        _ => "Connect PC"
    };

    public string Instructions => Platform switch
    {
        PairingPlatform.PlayStation =>
            "Open Settings → System → Remote Play → Link Device on your PS5 (PS4: Settings → Remote Play → Add Device). " +
            "The console shows an 8-digit code. Enter it below and FIFINE registers with the console through the Remote Play engine, " +
            "then starts the real stream.",
        PairingPlatform.Xbox =>
            "Xbox home streaming is delivered over WebRTC by Microsoft's xHome service; the open clients for it (Greenlight, " +
            "xbox-xcloud-player) are Node/Electron applications, not a library this build can host. FIFINE therefore does not " +
            "offer Xbox network video yet. HDMI capture is the supported path, and the button below starts it.",
        _ =>
            "FIFINE Link Agent is the companion app for another gaming PC. If it is installed and running on the same network, it answers " +
            "the FIFINE discovery broadcast below. You can also capture this PC's display directly."
    };

    public string CodeLabel => Platform switch
    {
        PairingPlatform.PlayStation => "PAIRING CODE SHOWN ON THE TV",
        PairingPlatform.Xbox => "Pairing number (not used by Xbox)",
        _ => "AGENT PAIRING CODE"
    };

    public bool AcceptsCode => Platform != PairingPlatform.Xbox;
    public bool IsPlayStation => Platform == PairingPlatform.PlayStation;
    public bool IsXbox => Platform == PairingPlatform.Xbox;
    public bool IsPc => Platform == PairingPlatform.Pc;

    public ObservableCollection<PairingDevice> Devices { get; }
    public ObservableCollection<TrustedDevice> TrustedDevices { get; }

    private PairingStage _stage = PairingStage.Idle;
    public PairingStage Stage
    {
        get => _stage;
        private set
        {
            _stage = value;
            Raise(nameof(Stage));
            Raise(nameof(StageLabel));
            Raise(nameof(IsBusy));
            Raise(nameof(CanEnterCode));
            Raise(nameof(CanConnect));
            Raise(nameof(IsConnected));
            Raise(nameof(ShowFallback));
            Raise(nameof(StatusColor));
        }
    }

    public string StageLabel => Stage switch
    {
        PairingStage.Idle => "Ready to pair",
        PairingStage.Searching => "Searching for devices…",
        PairingStage.NoDevices => "No devices found",
        PairingStage.DevicesFound => "Device discovered",
        PairingStage.AwaitingCode => "Waiting for pairing number",
        PairingStage.Authenticating => "Authenticating…",
        PairingStage.VerifyingVideo => "Establishing video connection…",
        PairingStage.Connected => "Connected · receiving video",
        PairingStage.Unsupported => "Unsupported connection method",
        PairingStage.Interrupted => "Connection interrupted",
        _ => "Connection failed"
    };

    public string StatusColor => Stage switch
    {
        PairingStage.Connected => "#FF45D69A",
        PairingStage.Unsupported => "#FFFFC46B",
        PairingStage.Failed or PairingStage.Interrupted => "#FFFF8FA3",
        PairingStage.VerifyingVideo or PairingStage.Authenticating => "#FF35C9FF",
        _ => "#FF8E9AB2"
    };

    public bool IsBusy => Stage is PairingStage.Searching or PairingStage.Authenticating or PairingStage.VerifyingVideo;
    public bool CanEnterCode => AcceptsCode && Stage != PairingStage.Connected;
    public bool IsConnected => Stage == PairingStage.Connected;
    public bool ShowFallback => Platform == PairingPlatform.Xbox ||
                                Stage is PairingStage.Unsupported or PairingStage.Failed or PairingStage.Interrupted;

    private string _status = string.Empty;
    public string Status
    {
        get => _status;
        private set
        {
            _status = value;
            Raise(nameof(Status));
        }
    }

    private string _code = string.Empty;
    public string Code
    {
        get => _code;
        set
        {
            _code = PairingCode.Normalize(value);
            Raise(nameof(Code));
            Raise(nameof(CanConnect));
        }
    }

    private PairingDevice? _selectedDevice;
    public PairingDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            _selectedDevice = value;
            if (value is not null)
            {
                Stage = AcceptsCode ? PairingStage.AwaitingCode : PairingStage.DevicesFound;
            }

            Raise(nameof(SelectedDevice));
            Raise(nameof(CanConnect));
        }
    }

    public bool CanConnect => !IsBusy && Platform switch
    {
        PairingPlatform.PlayStation => Code.Length == 8,
        PairingPlatform.Pc => SelectedDevice is not null,
        _ => false
    };

    // ---------------------------------------------------------------- live metrics (from the real session)

    public string LiveResolution => _studio.ResolutionLabel;
    public string LiveFps => _studio.MeasuredFpsValue > 0
        ? $"{_studio.MeasuredFpsValue:0.#} fps"
        : "Waiting for frames";
    public string LiveAudio => _studio.AudioStatusText;
    public string LiveLatency => _studio.LatencyText;
    public string LiveDropped => _studio.DroppedFramesText;
    public string LiveQuality => _studio.ConnectionQualityLabel;

    public void RefreshLiveMetrics()
    {
        Raise(nameof(LiveResolution));
        Raise(nameof(LiveFps));
        Raise(nameof(LiveAudio));
        Raise(nameof(LiveLatency));
        Raise(nameof(LiveDropped));
        Raise(nameof(LiveQuality));
    }

    // ---------------------------------------------------------------- commands

    public ICommand ConnectCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand PcDirectCommand { get; }
    public ICommand CaptureCardCommand { get; }
    public ICommand ForgetCommand { get; }

    private async Task SearchAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        Stage = PairingStage.Searching;
        Status = Platform == PairingPlatform.Pc
            ? "Broadcasting FIFINE discovery on the local network…"
            : Platform == PairingPlatform.PlayStation
                ? "Asking the Remote Play engine to find consoles on this network…"
                : "Scanning the local network for console streaming ports…";
        Devices.Clear();
        SelectedDevice = null;

        if (Platform == PairingPlatform.PlayStation)
        {
            await SearchPlayStationAsync().ConfigureAwait(true);
            return;
        }

        try
        {
            var timeout = Platform == PairingPlatform.Pc ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(6);
            var found = await LanDiscovery.SearchAsync(Platform, timeout, _searchCts.Token).ConfigureAwait(true);
            foreach (var device in found)
            {
                Devices.Add(device);
            }

            if (found.Count == 0)
            {
                Stage = PairingStage.NoDevices;
                Status = Platform switch
                {
                    PairingPlatform.Pc => "No FIFINE Link Agent answered. Start the agent on the gaming PC and make sure both computers are on the same network.",
                    PairingPlatform.PlayStation => "No PlayStation answered on port 9295. Enable Remote Play and use HDMI capture if the console is still not found.",
                    _ => "No Xbox answered on port 5050. HDMI capture is the supported path."
                };
            }
            else
            {
                Stage = PairingStage.DevicesFound;
                SelectedDevice = found[0];
                Status = $"{found.Count} device(s) found.";
            }
        }
        catch (Exception ex)
        {
            Stage = PairingStage.Failed;
            Status = "Discovery failed: " + ex.Message;
        }

        Raise(nameof(CanConnect));
    }

    private async Task SearchPlayStationAsync()
    {
        if (!_studio.PlayStation.IsEngineAvailable)
        {
            Stage = PairingStage.Unsupported;
            Status = "The PlayStation streaming engine (nixani-engine.exe) is not installed, so console discovery and Remote Play " +
                     "are unavailable. Build it from github.com/Nixani/nixani-engine and set its path in Settings → PlayStation, " +
                     "or use HDMI capture below.";
            return;
        }

        try
        {
            var hosts = await _studio.PlayStation.DiscoverAsync(TimeSpan.FromSeconds(6), _searchCts!.Token)
                .ConfigureAwait(true);
            foreach (var host in hosts)
            {
                Devices.Add(new PairingDevice(
                    host.Name,
                    host.Address,
                    LanDiscovery.PlayStationPort,
                    PairingPlatform.PlayStation,
                    "Remote Play engine",
                    $"{host.StateLabel} · {(host.IsPs5 ? "PS5" : "PS4")} · {host.SystemVersion}"));
            }

            if (hosts.Count == 0)
            {
                Stage = PairingStage.NoDevices;
                Status = "No console answered the Remote Play engine's discovery. Check that the console is on, Remote Play is enabled, " +
                         "and it is on the same network, then press Connect again. HDMI capture also works.";
            }
            else
            {
                Stage = PairingStage.DevicesFound;
                SelectedDevice = Devices[0];
                Status = $"{hosts.Count} PlayStation console(s) found.";
            }
        }
        catch (Exception ex)
        {
            Stage = PairingStage.Failed;
            Status = "Console discovery failed: " + ex.Message;
        }
    }

    private async Task ConnectAsync()
    {
        if (Platform == PairingPlatform.Pc)
        {
            await PairAgentAsync().ConfigureAwait(true);
            return;
        }

        if (Platform == PairingPlatform.PlayStation)
        {
            await ConnectPlayStationAsync().ConfigureAwait(true);
            return;
        }

        Stage = PairingStage.Unsupported;
        Status = "Xbox home streaming runs over Microsoft's xHome WebRTC service; the open clients " +
                 "for it (Greenlight, xbox-xcloud-player) are Node/Electron applications, so this build has no Xbox network video " +
                 "transport and will not claim a connection. HDMI capture is the supported path, and the button below starts it.";
    }

    /// <summary>The real PS5/PS4 flow: link with the displayed code, then start Remote Play.</summary>
    private async Task ConnectPlayStationAsync()
    {
        if (Code.Length == 0)
        {
            Status = "Enter the pairing code shown on the console first.";
            return;
        }

        if (SelectedDevice is null)
        {
            // The sheet opened before the console was awake or Remote Play was enabled.
            // Scan again instead of making the user type an address.
            await SearchAsync().ConfigureAwait(true);
            if (SelectedDevice is null)
            {
                return;
            }
        }

        var address = SelectedDevice.Address;

        Stage = PairingStage.Authenticating;
        Status = "Registering with the console…";

        var code = Code;
        Code = string.Empty;
        var registration = await _studio.PlayStation
            .RegisterAsync(address, code, 0, CancellationToken.None)
            .ConfigureAwait(true);

        if (!registration.Ok)
        {
            Stage = PairingStage.Failed;
            Status = "Registration failed: " + registration.Error;
            return;
        }

        Stage = PairingStage.VerifyingVideo;
        Status = "Registered. Starting Remote Play and waiting for console video…";

        var verified = await _studio.StartConsoleCaptureAndVerifyAsync(TimeSpan.FromSeconds(45))
            .ConfigureAwait(true);

        if (verified)
        {
            Stage = PairingStage.Connected;
            Status = $"Connected · receiving live video from {registration.Nickname}.";
            _store.Remember(Platform, registration.Nickname, address, "PlayStation Remote Play");
            TrustedDevices.Clear();
            foreach (var device in _store.For(Platform))
            {
                TrustedDevices.Add(device);
            }

            _studio.OpenLiveCapturePage();
            return;
        }

        Stage = PairingStage.Failed;
        Status = _studio.PlayStation.LastError is { Length: > 0 } error
            ? "The console did not deliver video: " + error
            : _studio.PlayStation.StatusText;
    }

    private async Task PairAgentAsync()
    {
        if (SelectedDevice is null)
        {
            await SearchAsync().ConfigureAwait(true);
            if (SelectedDevice is null)
            {
                return;
            }
        }

        var address = SelectedDevice.Address;
        Stage = PairingStage.Authenticating;
        Status = "Contacting FIFINE Link Agent…";

        var port = SelectedDevice.Port;
        var reachable = await LanDiscovery.ProbeTcpAsync(address, port, 1500).ConfigureAwait(true);

        if (!reachable)
        {
            Stage = PairingStage.Failed;
            Status = "No FIFINE Link Agent answered. Start it on the other PC and try again.";
            return;
        }

        Stage = PairingStage.Unsupported;
        Status = "A host answered, but the FIFINE Link Agent video transport is not part of this build, " +
                 "so no frames can be received yet. Pairing will not report CONNECTED without real frames. " +
                 "Use 'Capture this PC' to capture the local display now.";
    }

    /// <summary>PC mode: real display capture through the existing engine, verified by frames.</summary>
    private async Task PairLocalPcAsync()
    {
        Stage = PairingStage.VerifyingVideo;
        Status = "Starting PC display capture…";

        var verified = await _studio.StartCaptureAndVerifyAsync(PlatformKind.PC, preferCard: false, TimeSpan.FromSeconds(12))
            .ConfigureAwait(true);

        if (verified)
        {
            Stage = PairingStage.Connected;
            Status = "Connected · receiving live frames from this PC.";
            _store.Remember(Platform, "This PC", "local", "Display capture");
            TrustedDevices.Clear();
            foreach (var device in _store.For(Platform))
            {
                TrustedDevices.Add(device);
            }

            _studio.OpenLiveCapturePage();
            return;
        }

        Stage = PairingStage.Failed;
        Status = _studio.SessionLastError is { Length: > 0 } error
            ? "No frames arrived: " + error
            : "The pipeline started but no frames arrived, so the connection is not reported as connected.";
    }

    /// <summary>Console: HDMI capture card is the sanctioned transport — start it and verify frames.</summary>
    private async Task ContinueWithCaptureCardAsync()
    {
        Stage = PairingStage.VerifyingVideo;
        Status = _studio.HasCaptureCard
            ? "Starting HDMI capture and waiting for real frames…"
            : "No capture card is detected. Connect the console to a UVC capture card on USB 3.0, then try again.";

        var kind = Platform == PairingPlatform.PlayStation ? PlatformKind.PS5 : PlatformKind.Xbox;
        var verified = await _studio.StartCaptureAndVerifyAsync(kind, preferCard: true, TimeSpan.FromSeconds(15))
            .ConfigureAwait(true);

        if (verified)
        {
            Stage = PairingStage.Connected;
            Status = $"Connected · receiving live {PlatformName} video.";
            _store.Remember(Platform, SelectedDevice?.Name ?? PlatformName, SelectedDevice?.Address ?? "HDMI capture card", "HDMI capture card");
            TrustedDevices.Clear();
            foreach (var device in _store.For(Platform))
            {
                TrustedDevices.Add(device);
            }

            _studio.OpenLiveCapturePage();
            return;
        }

        Stage = PairingStage.Failed;
        Status = _studio.HasCaptureCard
            ? "No frames arrived from the capture card. Check the console output, HDCP and the card's USB connection."
            : "No capture card and no frames, so pairing cannot be reported as connected.";
    }

    private void Cancel()
    {
        _searchCts?.Cancel();
        if (Stage is PairingStage.Authenticating or PairingStage.VerifyingVideo)
        {
            Stage = PairingStage.Idle;
            Status = "Cancelled.";
            return;
        }

        _studio.ClosePairing();
    }

    private void Raise(string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
