using System.Windows.Input;
using FifineCaptureStudio.Core;

namespace FifineCaptureStudio.ViewModels;

public sealed record FrameGenerationOption(int Multiplier, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class StudioViewModel
{
    private AmdStatus? _amd;

    public AmdStatus Amd => _amd ??= AmdDriver.Detect();

    public string AmdGpuLabel => Amd.GpuLabel;
    public string AmdDriverLabel => Amd.DriverLabel;
    public string AmdAdlxLabel => Amd.AdlxLabel;
    public string AmdAfmfLabel => Amd.AfmfLabel;
    public string AmdDetail => Amd.Detail;
    public bool AmdIsAmd => Amd.IsAmdGpu;
    public bool AmdControllable => Amd.AfmfControllable;
    public bool AmdAfmfUnknown => !Amd.AfmfControllable;

    public string AmdAfmfState => AmdControllable ? "Unknown (read from driver)" : "Not readable — ADLX unavailable";

    /// <summary>
    /// Driver-level AFMF is a system-wide AMD setting. Until ADLX is bound, this setter
    /// refuses and explains rather than flipping a local flag.
    /// </summary>
    public bool AfmfEnabled
    {
        get => false;
        set
        {
            if (!value)
            {
                return;
            }

            AmdDriver.TrySetAfmfFluidMotion(true, out var message);
            Notify(message, true);
            Raise();
        }
    }

    private ICommand? _refreshAmd;

    public ICommand RefreshAmdStatusCommand => _refreshAmd ??= new RelayCommand(RefreshAmdStatus);

    public void RefreshAmdStatus()
    {
        _amd = AmdDriver.Detect();
        Raise(nameof(Amd));
        Raise(nameof(AmdGpuLabel));
        Raise(nameof(AmdDriverLabel));
        Raise(nameof(AmdAdlxLabel));
        Raise(nameof(AmdAfmfLabel));
        Raise(nameof(AmdDetail));
        Raise(nameof(AmdIsAmd));
        Raise(nameof(AmdControllable));
        Raise(nameof(AmdAfmfUnknown));
        Raise(nameof(AmdAfmfState));
        Raise(nameof(AfmfEnabled));
    }

    public IReadOnlyList<FrameGenerationOption> FrameGenerationOptions { get; } = new[]
    {
        new FrameGenerationOption(1, "Off"),
        new FrameGenerationOption(2, "2X Frame Generation"),
        new FrameGenerationOption(4, "4X Frame Generation")
    };

    public FrameGenerationOption? SelectedFrameGeneration
    {
        get => FrameGenerationOptions.FirstOrDefault(o => o.Multiplier == Math.Max(1, Config.Recording.FrameGeneration))
               ?? FrameGenerationOptions[0];
        set
        {
            if (value is null)
            {
                return;
            }

            Config.Recording.FrameGeneration = value.Multiplier;
            _services.Settings.Save();
            Raise();
            Raise(nameof(FrameGenerationSummary));
            Raise(nameof(FrameGenerationSupported));
        }
    }

    /// <summary>Interpolation runs in the encoder, so it costs CPU and only affects the file.</summary>
    public bool FrameGenerationSupported => Config.Recording.Width == 0 || Config.Recording.Height <= 1440;

    public string FrameGenerationSummary
    {
        get
        {
            var multiplier = Math.Max(1, Config.Recording.FrameGeneration);
            if (multiplier <= 1)
            {
                return "Frame generation is off. The recording matches the captured frame rate.";
            }

            var source = Config.Capture.Fps > 0 ? Config.Capture.Fps : 60;
            var output = source * multiplier;
            return $"Recording output: {source} → {output} FPS via FFmpeg motion interpolation (minterpolate). " +
                   "This runs on the CPU during encoding, applies to the recording only, and does not change the live preview " +
                   "or the console's own rendering. 8X and 16X are not offered because motion interpolation cannot sustain them " +
                   "at usable quality.";
        }
    }

    public string FrameGenerationLimitations =>
        "Generated frames are interpolated, not rendered. They add smoothness to the file but no new game information, " +
        "and fast motion can show artefacts. Preview and passthrough are untouched.";
}
