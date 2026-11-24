using System.Windows.Input;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.Pairing;

namespace FifineCaptureStudio.ViewModels;

public sealed partial class StudioViewModel
{
    private PairingViewModel? _pairing;

    public PairingViewModel? Pairing => _pairing;

    public bool IsPairingOpen => _pairing is not null;

    private ICommand? _openPairingCommand;

    public ICommand OpenPairingCommand => _openPairingCommand ??= new RelayCommand(p =>
    {
        var platform = p switch
        {
            PairingPlatform value => value,
            "PlayStation" => PairingPlatform.PlayStation,
            "Xbox" => PairingPlatform.Xbox,
            _ => PairingPlatform.Pc
        };
        OpenPairing(platform);
    });

    public void OpenPairing(PairingPlatform platform)
    {
        _pairing = new PairingViewModel(this, platform);
        Raise(nameof(Pairing));
        Raise(nameof(IsPairingOpen));
    }

    public void ClosePairing()
    {
        _pairing = null;
        Raise(nameof(Pairing));
        Raise(nameof(IsPairingOpen));
    }

    public void OpenLiveCapturePage() => CurrentPage = "capture";

    public bool HasCaptureCard => VideoDevices.Any(d => d.IsCaptureCard);

    /// <summary>Parameter for the pairing buttons so each platform opens its own workflow.</summary>
    public string PairingPlatformParameter => Config.Capture.Platform switch
    {
        PlatformKind.PS5 => "PlayStation",
        PlatformKind.Xbox => "Xbox",
        _ => "PC"
    };

    public string PairingButtonLabel => $"Pair {PairingPlatformParameter}";

    public string? SessionLastError => _services.Session.LastError;

    public double MeasuredFpsValue => _services.Session.MeasuredFps;

    public string ConnectionQualityLabel
    {
        get
        {
            var dropped = _services.Session.PreviewGapCount;
            var decoded = Math.Max(1, _services.Session.FramesDecoded);
            var ratio = dropped * 100.0 / (decoded + dropped);

            if (!_services.Session.HasSignal)
            {
                return "No signal";
            }

            return ratio switch
            {
                < 0.5 => "Excellent",
                < 2 => "Good",
                < 5 => "Fair",
                _ => "Poor"
            };
        }
    }

    /// <summary>
    /// Starts the real capture pipeline for a platform and only returns true when decoded
    /// frames actually arrive. Used by pairing so CONNECTED always means real video.
    /// </summary>
    public async Task<bool> StartCaptureAndVerifyAsync(PlatformKind platform, bool preferCard, TimeSpan timeout)
    {
        Config.Capture.Platform = platform;

        if (preferCard)
        {
            var card = VideoDevices.FirstOrDefault(d => d.IsCaptureCard);
            if (card is not null)
            {
                Config.Capture.VideoDeviceName = card.Name;
                Config.Capture.VideoDeviceId = card.Id;
                Config.Capture.SourceKind = CaptureSourceKind.CaptureCard;
            }
        }
        else if (Displays.Count > 0)
        {
            Config.Capture.SourceKind = CaptureSourceKind.Display;
            Config.Capture.DisplayIndex = Displays[0].Index;
        }

        _services.Settings.Save();
        Raise(nameof(SelectedSourceKind));
        await RebuildSourceOptionsAsync(restart: false).ConfigureAwait(true);

        if (!_services.Session.IsPreviewing)
        {
            await StartCaptureAsync().ConfigureAwait(true);
        }
        else
        {
            await RestartIfRunningAsync().ConfigureAwait(true);
        }

        var before = _services.Session.FramesDecoded;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200).ConfigureAwait(true);
            if (_services.Session.HasSignal && _services.Session.FramesDecoded > before)
            {
                Raise(nameof(ConnectionQualityLabel));
                return true;
            }
        }

        return false;
    }
}
