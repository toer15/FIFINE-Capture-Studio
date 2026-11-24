using System.Collections.ObjectModel;
using FifineCaptureStudio.Core;

namespace FifineCaptureStudio.ViewModels;

public sealed record QualityOption(string Label, int Width, int Height, bool Native, string Detail)
{
    public override string ToString() => Label;
    public bool IsAuto => Width == 0;
}

public sealed partial class StudioViewModel
{
    public ObservableCollection<QualityOption> CaptureQualityOptions { get; } = new();

    public string[] VideoQualityPresets { get; } = { "Low", "Balanced", "High", "Ultra", "Custom" };

    /// <summary>Source size actually available from the selected capture method.</summary>
    public int SourceWidth => Math.Max(Config.Capture.ResolutionWidth, 0);
    public int SourceHeight => Math.Max(Config.Capture.ResolutionHeight, 0);

    /// <summary>Rebuilds the quality list from what the source can really deliver.</summary>
    public void RefreshQualityOptions()
    {
        var sourceWidth = SourceWidth;
        var sourceHeight = SourceHeight;

        CaptureQualityOptions.Clear();
        var autoDetail = sourceHeight > 0
            ? $"Match the source: {sourceWidth} × {sourceHeight}"
            : "Best resolution the source reports";
        CaptureQualityOptions.Add(new QualityOption("Auto (Recommended)", 0, 0, true, autoDetail));

        foreach (var (width, height, label) in new[]
                 {
                     (854, 480, "480p (SD)"),
                     (1280, 720, "720p (HD)"),
                     (1920, 1080, "1080p (Full HD)"),
                     (2560, 1440, "1440p (QHD)"),
                     (3840, 2160, "2160p (4K UHD)")
                 })
        {
            var native = sourceHeight == 0 || height <= sourceHeight;
            var detail = native
                ? $"{width} × {height}"
                : $"{width} × {height} · upscaled from {sourceWidth} × {sourceHeight}, no extra detail";
            CaptureQualityOptions.Add(new QualityOption(label, width, height, native, detail));
        }

        Raise(nameof(SelectedCaptureQuality));
        Raise(nameof(QualityDetailText));
        Raise(nameof(QualityUpscaleNotice));
    }

    public QualityOption? SelectedCaptureQuality
    {
        get
        {
            if (Config.Recording.Width <= 0 || Config.Recording.Height <= 0)
            {
                return CaptureQualityOptions.FirstOrDefault(q => q.IsAuto) ?? CaptureQualityOptions.FirstOrDefault();
            }

            return CaptureQualityOptions.FirstOrDefault(q =>
                       !q.IsAuto && q.Width == Config.Recording.Width && q.Height == Config.Recording.Height)
                   ?? CaptureQualityOptions.FirstOrDefault(q => q.IsAuto)
                   ?? CaptureQualityOptions.FirstOrDefault();
        }
        set
        {
            if (value is null)
            {
                return;
            }

            if (value.IsAuto)
            {
                Config.Recording.Width = 0;
                Config.Recording.Height = 0;
            }
            else
            {
                Config.Recording.Width = value.Width;
                Config.Recording.Height = value.Height;
            }

            _services.Settings.Save();
            Raise();
            Raise(nameof(QualityDetailText));
            Raise(nameof(QualityUpscaleNotice));
            Raise(nameof(RecordingSummary));
            Raise(nameof(BitrateSummary));
        }
    }

    public string QualityDetailText => SelectedCaptureQuality?.Detail ?? string.Empty;

    public string QualityUpscaleNotice
    {
        get
        {
            var option = SelectedCaptureQuality;
            if (option is null || option.IsAuto || option.Native)
            {
                return string.Empty;
            }

            return $"Recording is upscaled to {option.Width} × {option.Height}. The source only delivers " +
                   $"{SourceWidth} × {SourceHeight}, so no extra image detail is added.";
        }
    }

    public bool HasUpscaleNotice => QualityUpscaleNotice.Length > 0;

    public string SelectedVideoQuality
    {
        get => Config.Recording.QualityPreset;
        set
        {
            if (value is null || value == Config.Recording.QualityPreset)
            {
                return;
            }

            Config.Recording.QualityPreset = value;
            Config.Recording.RateControl = value == "Custom" ? RateControlMode.Custom : RateControlMode.Preset;
            _services.Settings.Save();
            Raise();
            Raise(nameof(IsCustomQuality));
            Raise(nameof(IsCustomBitrate));
            Raise(nameof(BitrateSummary));
            Raise(nameof(RecordingSummary));
        }
    }

    public bool IsCustomQuality => string.Equals(Config.Recording.QualityPreset, "Custom", StringComparison.OrdinalIgnoreCase);

    /// <summary>Human-readable note about how the pipeline will handle the chosen quality.</summary>
    public string QualityPipelineNote
    {
        get
        {
            var option = SelectedCaptureQuality;
            if (option is null || option.IsAuto)
            {
                return "Preview and recording follow the capture source exactly; nothing is scaled.";
            }

            if (!option.Native)
            {
                return "Scaling happens inside the encoder, so HDMI passthrough and the live preview keep the original signal.";
            }

            return "Downscaling is done in the encoder with a high-quality filter; the preview keeps the native signal.";
        }
    }
}
