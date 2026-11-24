using System.IO;
using FifineCaptureStudio.Audio;
using FifineCaptureStudio.Capture;
using FifineCaptureStudio.Core.Streaming;
using FifineCaptureStudio.Library;

namespace FifineCaptureStudio.Core;

/// <summary>
/// Composition root: every subsystem is created once, in dependency order, and
/// disposed on shutdown. The UI only talks to these services.
/// </summary>
public sealed class AppServices : IAsyncDisposable
{
    public AppServices()
    {
        Settings = new SettingsService();
        Ffmpeg = new FfmpegManager();
        Encoders = new EncoderCatalog(Ffmpeg);
        Audio = new AudioEngine(Settings);
        Performance = new PerformanceSampler();
        Controllers = new ControllerMonitor();
        Library = new RecordingsLibrary(Ffmpeg);
        Hotkeys = new HotkeyManager();
        Updates = new Updates.UpdateService(Settings);
        PlayStation = new PlayStationStreamService(Settings, Audio);
        Enhancement = new Enhancement.EnhancementService(Settings);
        FrameGeneration = new FrameGeneration.FrameGenerationService(Settings);
        Session = new CaptureSession(Ffmpeg, Settings, Encoders, Audio, PlayStation, () => Displays);
    }

    public SettingsService Settings { get; }
    public FfmpegManager Ffmpeg { get; }
    public EncoderCatalog Encoders { get; }
    public AudioEngine Audio { get; }
    public PerformanceSampler Performance { get; }
    public ControllerMonitor Controllers { get; }
    public RecordingsLibrary Library { get; }
    public HotkeyManager Hotkeys { get; }
    public Updates.UpdateService Updates { get; }
    public PlayStationStreamService PlayStation { get; }
    public Enhancement.EnhancementService Enhancement { get; }
    public FrameGeneration.FrameGenerationService FrameGeneration { get; }
    public CaptureSession Session { get; }

    public IReadOnlyList<DShowDevice> VideoDevices { get; private set; } = Array.Empty<DShowDevice>();
    public IReadOnlyList<DShowDevice> AudioInputDevices { get; private set; } = Array.Empty<DShowDevice>();
    public IReadOnlyList<DisplayInfo> Displays { get; private set; } = Array.Empty<DisplayInfo>();
    public IReadOnlyList<WindowInfo> Windows { get; private set; } = Array.Empty<WindowInfo>();
    public IReadOnlyList<AudioDeviceOption> CaptureEndpoints { get; private set; } = Array.Empty<AudioDeviceOption>();
    public IReadOnlyList<AudioDeviceOption> RenderEndpoints { get; private set; } = Array.Empty<AudioDeviceOption>();

    public DateTime LastDeviceScan { get; private set; } = DateTime.MinValue;
    public event Action? DevicesChanged;

    public async Task InitializeAsync()
    {
        AppPaths.EnsureCreated();
        CleanupOrphanedParts();

        Ffmpeg.Locate();
        Performance.SetDrive(Settings.Current.Recording.Directory);
        RefreshDevices();

        Performance.Start(1000);
        Controllers.Start();
        Enhancement.Initialize();

        if (Ffmpeg.IsAvailable)
        {
            await Encoders.ScanAsync().ConfigureAwait(false);
            _ = Task.Run(() => Encoders.VerifyAllAsync());
        }

        Log.Info($"Services ready. FFmpeg installed: {Ffmpeg.IsAvailable}. Encoders: {Encoders.Available.Count}.");
    }

    public void RefreshDevices()
    {
        try
        {
            VideoDevices = Devices.EnumerateVideoDevices();
            AudioInputDevices = Devices.EnumerateAudioDevices();
            Displays = Devices.EnumerateDisplays();
            Windows = Devices.EnumerateCapturableWindows();
            CaptureEndpoints = AudioDevices.CaptureInputs();
            RenderEndpoints = AudioDevices.LoopbackDevices();
            LastDeviceScan = DateTime.Now;
            DevicesChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Device refresh failed", ex);
        }
    }

    private static void CleanupOrphanedParts()
    {
        try
        {
            if (Directory.Exists(AppPaths.RecordPartsDir))
            {
                foreach (var dir in Directory.GetDirectories(AppPaths.RecordPartsDir))
                {
                    if (Directory.GetLastWriteTime(dir) < DateTime.Now.AddDays(-2))
                    {
                        Directory.Delete(dir, true);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Part cleanup failed: " + ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Log.Info("Shutting down.");
        Hotkeys.Dispose();
        Controllers.Dispose();
        Performance.Dispose();

        try { await Session.DisposeAsync().ConfigureAwait(false); } catch { }
        try { await PlayStation.DisposeAsync().ConfigureAwait(false); } catch { }
        try { Enhancement.Dispose(); } catch { }
        try { FrameGeneration.Dispose(); } catch { }
        try { await Audio.StopAsync().ConfigureAwait(false); } catch { }
        Audio.Dispose();

        Settings.Save();
        Log.Info("=== Shutdown complete ===");
    }
}
