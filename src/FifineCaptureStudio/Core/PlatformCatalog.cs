namespace FifineCaptureStudio.Core;

public enum CompatibilityState
{
    FullySupported,
    OfficiallySupportedMethod,
    Experimental,
    RequiresExternalCaptureHardware
}

/// <summary>
/// What is actually known about capturing each platform. States are deliberately
/// conservative: nothing is marked fully supported until it has been tested on hardware.
/// </summary>
public sealed record PlatformEntry(
    string Family,
    string Name,
    string DeviceId,
    string VideoSource,
    string Methods,
    string Requirements,
    string Resolution,
    string Fps,
    string Audio,
    CompatibilityState State,
    string Notes)
{
    public string StateLabel => State switch
    {
        CompatibilityState.FullySupported => "FULLY SUPPORTED",
        CompatibilityState.OfficiallySupportedMethod => "OFFICIAL METHOD",
        CompatibilityState.Experimental => "EXPERIMENTAL",
        _ => "CAPTURE HARDWARE"
    };

    public string StateColor => State switch
    {
        CompatibilityState.FullySupported => "#FF45D69A",
        CompatibilityState.OfficiallySupportedMethod => "#FF35C9FF",
        CompatibilityState.Experimental => "#FFFFC46B",
        _ => "#FFFF8FA3"
    };

    public override string ToString() => Name;
}

public static class PlatformCatalog
{
    public const string PlayStation = "PlayStation";
    public const string Xbox = "Xbox";
    public const string Pc = "PC";

    public static IReadOnlyList<PlatformEntry> All { get; } = new List<PlatformEntry>
    {
        // ---------------------------------------------------------------- PlayStation
        new(PlayStation, "PlayStation 1", "SCPH- series", "Composite / SCART / S-Video",
            "Analogue video into a capture device with analogue input",
            "Analogue capture hardware (e.g. USB composite/SCART grabber); no digital or network video output exists",
            "240p / 288p / 480i", "50 / 60 Hz (interlaced)",
            "Analogue stereo (RCA) into the capture device",
            CompatibilityState.RequiresExternalCaptureHardware,
            "The console has no USB, network or HDMI video path. FIFINE records it only through an analogue capture device."),

        new(PlayStation, "PlayStation 2", "SCPH-3000x – 9000x", "Composite / SCART / component (YPbPr)",
            "Analogue video into a capture device; the network adapter exposes no video API",
            "Analogue or component capture hardware. The PS2 network adapter is for games only",
            "480i / 480p / 576i", "50 / 60 Hz (interlaced)",
            "Analogue stereo into the capture device",
            CompatibilityState.RequiresExternalCaptureHardware,
            "Progressive-scan titles output 480p over component, which most USB capture devices accept as 720x480."),

        new(PlayStation, "PlayStation 3", "CECH- series", "HDMI (HDCP protected for games) / component",
            "HDMI or component into a capture device; HDCP must be handled by the source, never bypassed",
            "HDMI capture hardware that manages HDCP legitimately, or component capture hardware",
            "720p / 1080i / 1080p", "60 Hz",
            "HDMI LPCM stereo / 5.1 into the card",
            CompatibilityState.RequiresExternalCaptureHardware,
            "PS3 game output is HDCP protected over HDMI. FIFINE never strips HDCP; use the component output or a compliant source."),

        new(PlayStation, "PlayStation 4", "CUH-1000 / 1100", "HDMI (HDCP can be disabled in console settings)",
            "HDMI into a UVC capture card. Remote Play is Sony's own client, not a third-party video API",
            "UVC capture card on USB 3.0; HDCP disabled on the console",
            "720p / 1080p", "30 / 60 FPS",
            "HDMI embedded audio into the card",
            CompatibilityState.RequiresExternalCaptureHardware,
            "Sony does not licence a third-party API that yields console frames; HDMI capture is the supported path."),

        new(PlayStation, "PlayStation 4 Slim", "CUH-2000", "HDMI (HDCP toggle)",
            "Same as PS4 — UVC capture card",
            "UVC capture card on USB 3.0; HDCP disabled",
            "720p / 1080p", "30 / 60 FPS",
            "HDMI embedded audio",
            CompatibilityState.RequiresExternalCaptureHardware,
            "Same video path as the launch PS4."),

        new(PlayStation, "PlayStation 4 Pro", "CUH-7000", "HDMI (HDCP toggle)",
            "UVC capture card; 4K output can be forced to 1080p for capture",
            "UVC capture card on USB 3.0; HDCP disabled; console output set to 1080p for capture",
            "1080p (4K is not capturable over most UVC cards)", "60 FPS",
            "HDMI embedded audio",
            CompatibilityState.RequiresExternalCaptureHardware,
            "The Pro outputs 4K, but almost no UVC card accepts 4K60; set the console to 1080p for capture."),

        new(PlayStation, "PlayStation 5", "CFI-1000 / 1100", "HDMI 2.1 (HDCP toggle)",
            "UVC capture card at 1080p60/1440p60; 4K120 needs an HDMI 2.1 capture device",
            "UVC capture card on USB 3.0; HDCP disabled; HDMI 2.1 card for 4K120",
            "1080p / 1440p / 2160p (card dependent)", "60 / 120 FPS (card dependent)",
            "HDMI embedded audio",
            CompatibilityState.RequiresExternalCaptureHardware,
            "FIFINE reads the real driver modes, so a 1080p60 card never advertises 1440p or 120 FPS."),

        new(PlayStation, "PlayStation 5 Slim", "CFI-2000", "HDMI 2.1 (HDCP toggle)",
            "Same as PS5", "UVC capture card on USB 3.0; HDCP disabled",
            "1080p / 1440p / 2160p (card dependent)", "60 / 120 FPS (card dependent)",
            "HDMI embedded audio",
            CompatibilityState.RequiresExternalCaptureHardware,
            "Identical capture path to the standard PS5."),

        new(PlayStation, "PlayStation 5 Pro", "CFI-7000", "HDMI 2.1 (HDCP toggle, 8K capable)",
            "UVC capture card; 8K output is not capturable with consumer UVC hardware",
            "HDMI 2.1 capture device; HDCP disabled",
            "1080p / 1440p / 2160p (card dependent)", "60 / 120 FPS (card dependent)",
            "HDMI embedded audio",
            CompatibilityState.RequiresExternalCaptureHardware,
            "8K output cannot be captured by consumer UVC cards; the app lists only driver modes."),

        // ---------------------------------------------------------------- Xbox
        new(Xbox, "Original Xbox", "Xbox (2001)", "Composite / SCART / component",
            "Analogue into a capture device", "Analogue capture hardware",
            "480i / 480p", "50 / 60 Hz", "Analogue stereo",
            CompatibilityState.RequiresExternalCaptureHardware,
            "No digital video output on the original hardware."),

        new(Xbox, "Xbox 360", "Xenon – Jasper", "Component / HDMI (HDMI is not HDCP protected for games)",
            "HDMI or component into a capture device", "HDMI or component capture hardware",
            "720p / 1080p", "60 Hz", "HDMI or analogue stereo",
            CompatibilityState.RequiresExternalCaptureHardware,
            "Game output over HDMI is capturable with a compliant card."),

        new(Xbox, "Xbox 360 S / E", "Trinity / Corona / Winchester", "HDMI / component",
            "Same as Xbox 360", "HDMI capture hardware", "720p / 1080p", "60 Hz", "HDMI embedded audio",
            CompatibilityState.RequiresExternalCaptureHardware,
            "Identical capture path to earlier 360 revisions."),

        new(Xbox, "Xbox One", " launch model", "HDMI (no HDCP on games)",
            "UVC capture card", "UVC capture card on USB 3.0", "720p / 1080p", "60 FPS", "HDMI embedded audio",
            CompatibilityState.RequiresExternalCaptureHardware,
            "Game output is not HDCP protected, so a standard UVC card works."),

        new(Xbox, "Xbox One S", "1681", "HDMI (4K output, HDR)",
            "UVC capture card at up to 1080p60", "UVC capture card on USB 3.0", "1080p", "60 FPS", "HDMI embedded audio",
            CompatibilityState.RequiresExternalCaptureHardware,
            "4K/HDR output exceeds most UVC cards; set 1080p output for capture."),

        new(Xbox, "Xbox One X", "1787", "HDMI 2.0 (4K, HDR)",
            "UVC capture card at up to 1080p60 or 1440p", "UVC capture card on USB 3.0",
            "1080p / 1440p (card dependent)", "60 FPS", "HDMI embedded audio",
            CompatibilityState.RequiresExternalCaptureHardware,
            "Renders at 4K but sends whatever the console output setting says; 1440p capture needs a capable card."),

        new(Xbox, "Xbox Series S", "RRS-0000x", "HDMI 2.1 (up to 1440p120 / 4K60)",
            "UVC capture card", "HDMI 2.0/2.1 capture card on USB 3.0",
            "1080p / 1440p (card dependent)", "60 / 120 FPS (card dependent)", "HDMI embedded audio",
            CompatibilityState.RequiresExternalCaptureHardware,
            "120 FPS capture requires a card that exposes a 120 Hz mode — FIFINE only offers what the driver reports."),

        new(Xbox, "Xbox Series X", "RRT-0000x", "HDMI 2.1 (up to 4K120)",
            "UVC capture card; 4K120 needs an HDMI 2.1 card",
            "HDMI 2.1 capture card on USB 3.0",
            "1080p / 1440p / 2160p (card dependent)", "60 / 120 FPS (card dependent)", "HDMI embedded audio",
            CompatibilityState.RequiresExternalCaptureHardware,
            "The app never claims 4K120 unless the card reports that mode and the encoder can sustain it."),

        // ---------------------------------------------------------------- PC
        new(Pc, "Local PC display / game window", "This Windows PC", "GDI desktop and window capture, DirectShow cards",
            "Desktop Duplication class capture through FFmpeg gdigrab, per-window capture, or a capture card",
            "Windows 10/11; hardware overlays require display capture instead of window capture",
            "Up to the display mode, per monitor", "Up to the display refresh rate", "WASAPI loopback and microphone",
            CompatibilityState.OfficiallySupportedMethod,
            "Verified end to end on this machine with the generated test source; display/window capture uses the same pipeline."),

        new(Pc, "Remote gaming PC (FIFINE Link Agent)", "Any Windows 10/11 PC", "Network stream from the companion agent",
            "FIFINE Link Agent encodes the desktop and streams it over the local network",
            "Companion agent on the gaming PC, both machines on the same LAN, TCP 47810",
            "Up to 1080p60 per stream (configurable)", "30 / 60 FPS", "Stereo audio alongside video",
            CompatibilityState.Experimental,
            "The transport is designed but has not been validated with a second machine, so it is labelled experimental."),

        new(Pc, "Capture card passthrough", "Any UVC card", "HDMI in, USB out",
            "UVC/DirectShow input through FFmpeg", "UVC card on USB 3.0",
            "Driver reported modes", "Driver reported rates", "Card audio endpoint",
            CompatibilityState.OfficiallySupportedMethod,
            "Mode list comes from the driver, never from a hardcoded table.")
    };

    public static IReadOnlyList<PlatformEntry> ForFamily(string family) =>
        All.Where(p => p.Family == family).ToList();
}
