using System.Collections.ObjectModel;
using FifineCaptureStudio.Core;

namespace FifineCaptureStudio.ViewModels;

public sealed record CaptureModeOption(string Key, string Label, string Detail, bool Available)
{
    public override string ToString() => Label;
}

public sealed partial class StudioViewModel
{
    public ObservableCollection<CaptureModeOption> CaptureModes { get; } = new();

    public ObservableCollection<PlatformEntry> PlayStationPlatforms { get; } = new();
    public ObservableCollection<PlatformEntry> XboxPlatforms { get; } = new();
    public ObservableCollection<PlatformEntry> PcPlatforms { get; } = new();

    private string _captureMode = "card";

    public string SelectedCaptureMode
    {
        get => _captureMode;
        set
        {
            if (string.IsNullOrEmpty(value) || value == _captureMode)
            {
                return;
            }

            var option = CaptureModes.FirstOrDefault(m => m.Key == value);
            if (option is null || !option.Available)
            {
                Notify(option?.Detail ?? "That capture mode is not available on this PC.", true);
                Raise();
                return;
            }

            _captureMode = value;
            ApplyCaptureMode(option);
            Raise();
            Raise(nameof(CaptureModeNote));
        }
    }

    public string CaptureModeNote => CaptureModes.FirstOrDefault(m => m.Key == _captureMode)?.Detail ?? string.Empty;

    private void RefreshCaptureModes()
    {
        var hasCard = VideoDevices.Count > 0;
        var hasDisplay = Displays.Count > 0;

        CaptureModes.Clear();
        CaptureModes.Add(new CaptureModeOption("card", "HDMI Capture Card",
            hasCard
                ? "UVC / DirectShow capture device detected."
                : "No capture device is connected — plug in a UVC card on USB 3.0.",
            hasCard));

        CaptureModes.Add(new CaptureModeOption("universal", "FIFINE Universal Software Capture",
            "Picks the best available source automatically: capture card, then display, then game window.",
            hasCard || hasDisplay));

        CaptureModes.Add(new CaptureModeOption("network", "FIFINE PC Network Capture",
            "Requires FIFINE Link Agent on the gaming PC. The companion app is not part of this build.",
            false));

        CaptureModes.Add(new CaptureModeOption("console", "Compatible Console Network Capture",
            "No console exposes a sanctioned third-party video API. Use an HDMI capture card for consoles.",
            false));

        CaptureModes.Add(new CaptureModeOption("direct", "PC Direct Capture",
            hasDisplay ? "Capture this PC's display or a single game window." : "No display was detected.",
            hasDisplay));

        // Keep the selection valid when devices disappear.
        var current = CaptureModes.FirstOrDefault(m => m.Key == _captureMode);
        if (current is null || !current.Available)
        {
            _captureMode = CaptureModes.FirstOrDefault(m => m.Available)?.Key ?? "card";
        }

        Raise(nameof(SelectedCaptureMode));
        Raise(nameof(CaptureModeNote));
    }

    private void ApplyCaptureMode(CaptureModeOption option)
    {
        switch (option.Key)
        {
            case "universal":
            {
                if (VideoDevices.FirstOrDefault(d => d.IsCaptureCard) is { } card)
                {
                    SelectedVideoDevice = card;
                }
                else if (Displays.Count > 0)
                {
                    SelectedDisplay = Displays[0];
                }

                break;
            }

            case "direct":
                if (Displays.Count > 0)
                {
                    SelectedDisplay = Displays[0];
                }

                break;

            case "card":
                if (VideoDevices.FirstOrDefault() is { } device)
                {
                    SelectedVideoDevice = device;
                }

                break;
        }
    }

    private void RefreshPlatformDatabase()
    {
        PlayStationPlatforms.Clear();
        XboxPlatforms.Clear();
        PcPlatforms.Clear();

        foreach (var entry in PlatformCatalog.ForFamily(PlatformCatalog.PlayStation))
        {
            PlayStationPlatforms.Add(entry);
        }

        foreach (var entry in PlatformCatalog.ForFamily(PlatformCatalog.Xbox))
        {
            XboxPlatforms.Add(entry);
        }

        foreach (var entry in PlatformCatalog.ForFamily(PlatformCatalog.Pc))
        {
            PcPlatforms.Add(entry);
        }
    }
}
