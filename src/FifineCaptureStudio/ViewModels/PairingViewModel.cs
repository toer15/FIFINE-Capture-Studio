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

        SearchCommand = new AsyncRelayCommand(SearchAsync);
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
            PairingPlatform.PlayStation => "Select a discovered console or type its address, then press Connect PlayStation.",
            PairingPlatform.Xbox => "Select a discovered console or type its address, then press Connect Xbox.",
            _ => "Pair this PC for display capture, or find a FIFINE Link Agent on the network."
        };
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
        PairingPlatform.PlayStation => "Pairing number shown on the TV",
        PairingPlatform.Xbox => "Pairing number (not used by Xbox)",
        _ => "Agent pairing code"
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
    public bool ShowFallback => Stage is PairingStage.Unsupported or PairingStage.Failed or PairingStage.Interrupted;

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

    private string _manualAddress = string.Empty;
    public string ManualAddress
    {
        get => _manualAddress;
        set
        {
            _manualAddress = (value ?? string.Empty).Trim();
            Raise(nameof(ManualAddress));
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
                ManualAddress = value.Address;
                Stage = AcceptsCode ? PairingStage.AwaitingCode : PairingStage.DevicesFound;
            }

            Raise(nameof(SelectedDevice));
            Raise(nameof(CanConnect));
        }
    }

    public bool CanConnect => !IsBusy && (Platform == PairingPlatform.Xbox || Code.Length > 0 || ManualAddress.Length > 0);

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

    public ICommand SearchCommand { get; }
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
                    PairingPlatform.Pc => "No FIFINE Link Agent answered. Start the agent on the gaming PC, or enter its address manually.",
                    PairingPlatform.PlayStation => "No PlayStation answered on port 9295. Type the console's IP address, or use HDMI capture.",
                    _ => "No Xbox answered on port 5050. Type the console's IP address, or use HDMI capture."
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
                         "and it is on the same network — or type its IP address.";
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
        var address = ManualAddress.Length > 0 ? ManualAddress : SelectedDevice?.Address ?? string.Empty;

        if (Platform == PairingPlatform.Pc)
        {
            await PairAgentAsync(address).ConfigureAwait(true);
            return;
        }

        if (Platform == PairingPlatform.PlayStation)
        {
            await ConnectPlayStationAsync(address).ConfigureAwait(true);
            return;
        }

        if (Code.Length == 0)
        {
            Status = "Enter the pairing number shown on the television first.";
            return;
        }

        if (address.Length == 0)
        {
            Status = "Select the console or type its IP address.";
            return;
        }

        Stage = PairingStage.Authenticating;
        Status = $"Contacting {address}…";

        var port = Platform == PairingPlatform.PlayStation ? LanDiscovery.PlayStationPort : LanDiscovery.XboxPort;
        var reachable = await LanDiscovery.ProbeTcpAsync(address, port, 1500).ConfigureAwait(true);

        // The code is never stored: it is used for this attempt and dropped.
        Code = string.Empty;

        if (!reachable)
        {
            Stage = PairingStage.Failed;
            Status = $"No console answered at {address}:{port}. Check the IP address, that the console is on and on the same network.";
            return;
        }

        Stage = PairingStage.Unsupported;
        Status = $"Console reached at {address}. Xbox home streaming runs over Microsoft's xHome WebRTC service; the open clients " +
                 "for it (Greenlight, xbox-xcloud-player) are Node/Electron applications, so this build has no Xbox network video " +
                 "transport and will not claim a connection. HDMI capture is the supported path, and the button below starts it.";
    }

    /// <summary>The real PS5/PS4 flow: link with the displayed code, then start Remote Play.</summary>
    private async Task ConnectPlayStationAsync(string address)
    {
        if (Code.Length == 0)
        {
            Status = "Enter the 8-digit code shown on the console first.";
            return;
        }

        if (address.Length == 0)
        {
            Status = "Select the console or type its IP address.";
            return;
        }

        if (_studio.PlayStation.HasRegistration && Code.Length == 0)
        {
            // already linked: straight to a session
        }

        Stage = PairingStage.Authenticating;
        Status = $"Registering with the console at {address}…";

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

    private async Task PairAgentAsync(string address)
    {
        if (address.Length == 0)
        {
            Stage = PairingStage.Failed;
            Status = "Search for an agent or type its address first.";
            return;
        }

        Stage = PairingStage.Authenticating;
        Status = $"Contacting FIFINE Link Agent at {address}…";

        var port = SelectedDevice?.Port ?? 47810;
        var reachable = await LanDiscovery.ProbeTcpAsync(address, port, 1500).ConfigureAwait(true);

        if (!reachable)
        {
            Stage = PairingStage.Failed;
            Status = $"Nothing answered at {address}:{port}. The Link Agent is not running there, or the port is blocked.";
            return;
        }

        Stage = PairingStage.Unsupported;
        Status = $"A host answered at {address}:{port}, but the FIFINE Link Agent video transport is not part of this build, " +
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
            _store.Remember(Platform, SelectedDevice?.Name ?? PlatformName, SelectedDevice?.Address ?? ManualAddress, "HDMI capture card");
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
        if (Stage is PairingStage.Searching or PairingStage.Authenticating or PairingStage.VerifyingVideo)
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
