using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FifineCaptureStudio.Audio;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.Streaming;

namespace FifineCaptureStudio.Capture;

public sealed record RecordingStartResult(bool Started, string? Error);

public sealed class RecordingRun
{
    public string FinalPath = string.Empty;
    public string PartsDirectory = string.Empty;
    public List<string> Parts { get; } = new();
    public DateTime StartedAt = DateTime.Now;
    public TimeSpan Accumulated = TimeSpan.Zero;
    public DateTime ActiveSince = DateTime.Now;
    public long BytesAtPartStart;
    public double Fps;
    public long Dropped;
    public double BitrateMbps;
    public string Encoder = string.Empty;
    public ContainerKind Container;
    public int Width;
    public int Height;
    public int FpsTarget;
    public string Source = string.Empty;
    public PlatformKind Platform;
    public int AudioTracks;
    public double MeasuredDuration;

    public TimeSpan Elapsed => Accumulated + (ActiveSince == default ? TimeSpan.Zero : DateTime.Now - ActiveSince);
}

/// <summary>
/// Owns the single FFmpeg capture process for a session and switches it between
/// preview, recording and replay-buffer output sets. One process is used on
/// purpose: most UVC capture cards allow only one open handle, so preview and
/// recording must share the hardware pipeline.
/// </summary>
public sealed class CaptureSession : IAsyncDisposable
{
    private static readonly Regex PtsRegex = new(@"pts_time:([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex FrameRegex = new(@"frame=\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex FpsRegex = new(@"fps=\s*([\d.]+)", RegexOptions.Compiled);
    private static readonly Regex BitrateRegex = new(@"bitrate=\s*([\d.]+)kbits/s", RegexOptions.Compiled);
    private static readonly Regex DropRegex = new(@"drop=\s*(\d+)", RegexOptions.Compiled);
    private static readonly Regex SpeedRegex = new(@"speed=\s*([\d.]+)x", RegexOptions.Compiled);

    private readonly FfmpegManager _ffmpeg;
    private readonly SettingsService _settings;
    private readonly EncoderCatalog _encoders;
    private readonly AudioEngine _audio;
    private readonly PlayStationStreamService _console;
    private readonly Func<IReadOnlyList<DisplayInfo>> _displays;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Process? _process;
    private PreviewStream? _preview;
    private CancellationTokenSource? _stderrCts;
    private Task? _stderrTask;
    private Timer? _statusTimer;
    private PipelineOptions? _activeOptions;
    private RecordingRun? _run;
    private volatile bool _expectExit;
    private int _reconnectAttempts;
    private bool _paused;
    private bool _previewFullResolution = true;
    private readonly PreviewFallbackMonitor _previewLoad = new();
    private DateTime _lastFrameAt = DateTime.MinValue;
    private long _lastDecodeTicks;
    private DateTime _lastFrameWall = DateTime.MinValue;
    private double _lastIntervalMs;
    private double _avgIntervalMs;
    private double _jitterMs;
    private double _presentLatencyMs;
    private double _decodeMs;
    private long _previewGaps;
    private long _framesSeen;
    private DateTime _fpsWindowStart = DateTime.Now;
    private long _fpsWindowFrames;
    private double _measuredFps;
    private double _stderrDrop;
    private long _lastRecordFrame;
    private DateTime _lastRecordFrameAt = DateTime.Now;

    public CaptureSession(
        FfmpegManager ffmpeg,
        SettingsService settings,
        EncoderCatalog encoders,
        AudioEngine audio,
        PlayStationStreamService console,
        Func<IReadOnlyList<DisplayInfo>> displays)
    {
        _ffmpeg = ffmpeg;
        _settings = settings;
        _encoders = encoders;
        _audio = audio;
        _console = console;
        _displays = displays;
    }

    public event Action? StateChanged;
    public event Action<PreviewFrame>? FrameArrived;
    public event Action<string, bool>? Notice;

    public bool IsPreviewing { get; private set; }
    public bool IsRecording { get; private set; }
    public bool IsPaused => _paused;
    public bool IsReplayBufferActive => IsPreviewing && !IsRecording && _settings.Current.Replay.Enabled;
    public bool IsBusy { get; private set; }
    public string? LastError { get; private set; }
    public string StatusLine { get; private set; } = "Idle";
    public string PipelineMode { get; private set; } = "Stopped";

    public double MeasuredFps => _measuredFps;
    public double FrameTimeMs => _lastIntervalMs;
    public double AverageFrameTimeMs => _avgIntervalMs;
    public double JitterMs => _jitterMs;
    public double DecodeMs => _decodeMs;
    public double PresentLatencyMs => _presentLatencyMs;
    public long PreviewGapCount => _previewGaps;
    public long FramesDecoded => _framesSeen;
    public int ActiveSourceWidth => _activeOptions?.Source.Width ?? 0;
    public int ActiveSourceHeight => _activeOptions?.Source.Height ?? 0;

    /// <summary>Size of the frames FFmpeg actually hands to the enhancement engine.</summary>
    public int ActivePreviewWidth => _activeOptions is null ? 0 : PipelineArgs.EstimatePreviewSize(_activeOptions).Width;
    public int ActivePreviewHeight => _activeOptions is null ? 0 : PipelineArgs.EstimatePreviewSize(_activeOptions).Height;
    public bool PreviewAtNativeResolution => ActivePreviewWidth > 0
                                             && ActivePreviewWidth >= ActiveSourceWidth
                                             && ActivePreviewWidth > 1600;
    public int ActiveOutputWidth => _activeOptions?.EncodeWidth ?? 0;
    public int ActiveOutputHeight => _activeOptions?.EncodeHeight ?? 0;
    public double RecordingFps => _run?.Fps ?? 0;
    public long RecordingDropped => _run?.Dropped ?? 0;
    public double RecordingBitrateMbps => _run?.BitrateMbps ?? 0;
    public long RecordingBytes => CurrentRecordingBytes();
    public TimeSpan RecordingElapsed => _run?.Elapsed ?? TimeSpan.Zero;
    public string? CurrentRecordingPath => _run?.FinalPath;
    public string? ActiveEncoder => _run?.Encoder ?? _activeOptions?.Encoder.Id;
    public string? ActiveContainer => _run?.Container.ToString();
    public bool HasSignal => (DateTime.Now - _lastFrameAt).TotalSeconds < 3;
    public DateTime PreviewStartedAt { get; private set; } = DateTime.MaxValue;
    public long RecordedFrames { get; private set; }
    public double LastRecordSpeed { get; private set; }
    public string? AudioTrackSummary => _audio.ActiveSinkSummary;

    /// <summary>Estimated capture-to-preview latency derived from buffering, clearly labelled in the UI.</summary>
    public double EstimatedCaptureLatencyMs
    {
        get
        {
            var frame = Math.Max(1.0, 1000.0 / Math.Max(1, _settings.Current.Capture.Fps));
            return _settings.Current.LowLatency.Mode switch
            {
                LowLatencyMode.UltraLow => Math.Round(frame + 18, 1),
                LowLatencyMode.Balanced => Math.Round(frame * 2 + 42, 1),
                _ => Math.Round(frame * 3 + 75, 1)
            };
        }
    }

    public double AudioDelayMs
    {
        get
        {
            var audio = _settings.Current.Audio;
            var buffer = audio.MonitoringEnabled ? 60 : 0;
            return Math.Round((double)buffer + audio.AvOffsetMs, 1);
        }
    }

    // ------------------------------------------------------------------ preview

    public async Task<bool> StartPreviewAsync(bool restart = false)
    {
        if (IsRecording)
        {
            return false;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsPreviewing && !restart)
            {
                return true;
            }

            if (!_ffmpeg.IsAvailable)
            {
                LastError = "FFmpeg engine not installed. Install it from Settings → Capture.";
                StatusLine = "Engine missing";
                Notice?.Invoke(LastError, true);
                RaiseState();
                return false;
            }

            var options = BuildOptions(recordPath: null);

            if (options.Source.Kind == CaptureSourceKind.ConsoleStream)
            {
                // The console backend must be streaming before FFmpeg opens the video pipe.
                if (!await _console.ConnectAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    LastError = _console.LastError ?? _console.StatusText;
                    StatusLine = "Console not ready";
                    Notice?.Invoke(LastError, true);
                    RaiseState();
                    return false;
                }

                options = BuildOptions(recordPath: null);
            }

            if (options.Source.Kind == CaptureSourceKind.CaptureCard && string.IsNullOrWhiteSpace(options.Source.DeviceName))
            {
                LastError = "No capture device selected.";
                StatusLine = "No device";
                Notice?.Invoke(LastError, true);
                RaiseState();
                return false;
            }

            await StopProcessAsync(intentional: true).ConfigureAwait(false);
            PrepareRing(options);
            var started = await StartProcessAsync(options).ConfigureAwait(false);
            if (!started)
            {
                StatusLine = "Capture failed";
                RaiseState();
                return false;
            }

            _activeOptions = options;
            IsPreviewing = true;
            PreviewStartedAt = DateTime.MaxValue;
            _reconnectAttempts = 0;
            PipelineMode = options.HasRingOutput ? "Replay buffer + preview" : "Preview";
            StatusLine = "Capture ready";
            StartStatusTimer();
            RaiseState();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsRecording)
            {
                await FinishRecordingAsync(concat: true).ConfigureAwait(false);
            }

            await StopProcessAsync(intentional: true).ConfigureAwait(false);
            if (_console.IsStreaming || _console.State != ConsoleStreamState.Idle)
            {
                await _console.StopAsync().ConfigureAwait(false);
            }

            IsPreviewing = false;
            PipelineMode = "Stopped";
            StatusLine = "Idle";
            StopStatusTimer();
            RaiseState();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Restarts the pipeline so replay/quality/device changes take effect.</summary>
    public async Task RestartPreviewAsync()
    {
        if (IsRecording)
        {
            return;
        }

        IsPreviewing = false;
        PreviewStartedAt = DateTime.MaxValue;
        _previewGaps = 0;
        await StartPreviewAsync(restart: true).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- recording

    public async Task<RecordingStartResult> StartRecordingAsync()
    {
        if (IsRecording)
        {
            return new RecordingStartResult(false, "A recording is already in progress.");
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_ffmpeg.IsAvailable)
            {
                return new RecordingStartResult(false, "FFmpeg engine not installed.");
            }

            var s = _settings.Current;
            var encoder = await ResolveEncoderAsync().ConfigureAwait(false);
            if (encoder is null)
            {
                return new RecordingStartResult(false, "No working video encoder is available for this codec.");
            }

            var partDir = Path.Combine(AppPaths.RecordPartsDir, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(partDir);

            var finalPath = BuildRecordingPath();
            var partPath = Path.Combine(partDir, "part_000." + Extension(s.Recording.Container));

            var run = new RecordingRun
            {
                FinalPath = finalPath,
                PartsDirectory = partDir,
                Encoder = encoder.Id,
                Container = s.Recording.Container,
                Width = s.Recording.Width,
                Height = s.Recording.Height,
                FpsTarget = s.Recording.Fps,
                Source = SourceResolver.FromSettings(s, _displays()).Label,
                Platform = s.Capture.Platform,
                StartedAt = DateTime.Now,
                ActiveSince = DateTime.Now,
                AudioTracks = s.Recording.SeparateAudioTracks ? 2 : 1
            };
            run.Parts.Add(partPath);

            var pipes = _audio.OpenRecordSinks(s.Recording.SeparateAudioTracks);
            var options = BuildOptions(recordPath: partPath, encoderOverride: encoder, audioPipes: pipes);
            // The encoded size can differ from the request (no upscaling), so record what is written.
            run.Width = options.EncodeWidth;
            run.Height = options.EncodeHeight;
            run.FpsTarget = options.EncodeFps;
            await StopProcessAsync(intentional: true).ConfigureAwait(false);
            PrepareRing(options);
            var started = await StartProcessAsync(options).ConfigureAwait(false);
            if (!started)
            {
                run.Parts.Clear();
                _audio.CloseRecordSinks();
                try { Directory.Delete(partDir, true); } catch { }
                await StartPreviewAsync().ConfigureAwait(false);
                return new RecordingStartResult(false, LastError ?? "The encoder failed to start.");
            }

            _run = run;
            _activeOptions = options;
            _paused = false;
            IsRecording = true;
            IsPreviewing = true;
            PreviewStartedAt = DateTime.MinValue;
            PipelineMode = "Recording";
            StatusLine = "Recording";
            _stderrDrop = 0;
            _lastRecordFrame = 0;
            _lastRecordFrameAt = DateTime.Now;
            StartStatusTimer();
            RaiseState();
            Notice?.Invoke($"Recording started · {encoder.DisplayName}", false);
            Log.Info($"Recording started: {partPath} [{encoder.Id}]");
            return new RecordingStartResult(true, null);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to start recording", ex);
            return new RecordingStartResult(false, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> PauseRecordingAsync()
    {
        if (!IsRecording || _paused || _run is null)
        {
            return false;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _run.Accumulated += DateTime.Now - _run.ActiveSince;
            _paused = true;
            await StopProcessAsync(intentional: true).ConfigureAwait(false);
            _audio.CloseRecordSinks();

            var previewOptions = BuildOptions(recordPath: null);
            PrepareRing(previewOptions);
            await StartProcessAsync(previewOptions).ConfigureAwait(false);
            _activeOptions = previewOptions;
            PipelineMode = "Recording paused";
            StatusLine = "Paused";
            Notice?.Invoke("Recording paused — the file segment was closed safely.", false);
            RaiseState();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ResumeRecordingAsync()
    {
        if (!IsRecording || !_paused || _run is null)
        {
            return false;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var s = _settings.Current;
            var encoder = _encoders.Resolve(s.Recording.Codec, s.Recording.UseHardwareEncoding, s.Recording.Encoder);
            if (encoder is null)
            {
                return false;
            }

            var partPath = Path.Combine(_run.PartsDirectory, $"part_{_run.Parts.Count:000}." + Extension(s.Recording.Container));
            _run.Parts.Add(partPath);
            _run.BytesAtPartStart = _run.Parts.Take(_run.Parts.Count - 1).Sum(FileLength);

            var pipes = _audio.OpenRecordSinks(s.Recording.SeparateAudioTracks);
            var options = BuildOptions(recordPath: partPath, encoderOverride: encoder, audioPipes: pipes);
            await StopProcessAsync(intentional: true).ConfigureAwait(false);
            PrepareRing(options);
            var started = await StartProcessAsync(options).ConfigureAwait(false);
            if (!started)
            {
                _audio.CloseRecordSinks();
                return false;
            }

            _activeOptions = options;
            _run.ActiveSince = DateTime.Now;
            _paused = false;
            PipelineMode = "Recording";
            StatusLine = "Recording";
            Notice?.Invoke("Recording resumed.", false);
            RaiseState();
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> StopRecordingAsync()
    {
        if (!IsRecording)
        {
            return null;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var path = await FinishRecordingAsync(concat: true).ConfigureAwait(false);
            return path;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> FinishRecordingAsync(bool concat)
    {
        var run = _run;
        if (run is null)
        {
            return null;
        }

        if (!_paused)
        {
            run.Accumulated += DateTime.Now - run.ActiveSince;
        }

        run.ActiveSince = default;
        _paused = false;
        IsRecording = false;
        await StopProcessAsync(intentional: true).ConfigureAwait(false);
        _audio.CloseRecordSinks();
        _run = null;
        PipelineMode = "Preview";
        StatusLine = "Processing recording…";
        RaiseState();

        var final = await Task.Run(() => FinaliseRecordingAsync(run, concat)).ConfigureAwait(false);

        // Return to plain preview (plus replay ring when enabled).
        var previewOptions = BuildOptions(recordPath: null);
        PrepareRing(previewOptions);
        await StartProcessAsync(previewOptions).ConfigureAwait(false);
        _activeOptions = previewOptions;
        PipelineMode = previewOptions.HasRingOutput ? "Replay buffer + preview" : "Preview";
        StatusLine = final is null ? "Recording failed" : "Capture ready";
        RaiseState();

        if (final is null)
        {
            Notice?.Invoke("Recording could not be finalised. Parts are kept in " + run.PartsDirectory, true);
        }
        else
        {
            Notice?.Invoke("Recording saved · " + Path.GetFileName(final), false);
        }

        return final;
    }

    private async Task<string?> FinaliseRecordingAsync(RecordingRun run, bool concat)
    {
        try
        {
            var parts = run.Parts.Where(p => File.Exists(p) && new FileInfo(p).Length > 1024).ToList();
            if (parts.Count == 0)
            {
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(run.FinalPath)!);

            if (parts.Count == 1)
            {
                try
                {
                    File.Move(parts[0], run.FinalPath, true);
                }
                catch (IOException)
                {
                    // Different volume: copy then clean up.
                    File.Copy(parts[0], run.FinalPath, true);
                    try { File.Delete(parts[0]); } catch { }
                }
            }
            else if (concat)
            {
                var listPath = Path.Combine(run.PartsDirectory, "concat.txt");
                var sb = new StringBuilder();
                foreach (var part in parts)
                {
                    sb.AppendLine("file '" + part.Replace("'", "'\\''") + "'");
                }

                await File.WriteAllTextAsync(listPath, sb.ToString()).ConfigureAwait(false);

                var args = new List<string>
                {
                    "-hide_banner", "-loglevel", "error",
                    "-f", "concat", "-safe", "0", "-i", listPath,
                    "-c", "copy",
                    "-max_muxing_queue_size", "2048"
                };

                if (run.Container == ContainerKind.Mp4)
                {
                    args.AddRange(new[] { "-f", "mp4", "-movflags", "+faststart" });
                }
                else
                {
                    args.AddRange(new[] { "-f", "matroska" });
                }

                args.AddRange(new[] { "-y", run.FinalPath });
                var result = await Proc.RunAsync(_ffmpeg.FfmpegPath!, args, TimeSpan.FromMinutes(10)).ConfigureAwait(false);
                if (result.Code != 0 || !File.Exists(run.FinalPath))
                {
                    Log.Error("Concat failed: " + result.StdErr);
                    // Keep the parts, but surface a usable file when possible.
                    if (parts.Count > 0)
                    {
                        File.Copy(parts[0], run.FinalPath, true);
                    }
                }
            }

            if (File.Exists(run.FinalPath))
            {
                var info = await MediaProbe.ProbeAsync(_ffmpeg, run.FinalPath).ConfigureAwait(false);
                if (info is not null && info.Duration > TimeSpan.Zero)
                {
                    run.MeasuredDuration = info.Duration.TotalSeconds;
                }
            }

            WriteMetadata(run);

            try
            {
                Directory.Delete(run.PartsDirectory, true);
            }
            catch
            {
                // parts folder will be cleaned next launch
            }

            Log.Info("Recording finalised: " + run.FinalPath);
            return File.Exists(run.FinalPath) ? run.FinalPath : null;
        }
        catch (Exception ex)
        {
            Log.Error("Recording finalise failed", ex);
            return null;
        }
    }

    private void WriteMetadata(RecordingRun run)
    {
        try
        {
            var meta = new
            {
                product = "FIFINE Capture Studio",
                platform = run.Platform.ToString(),
                source = run.Source,
                width = run.Width,
                height = run.Height,
                fps = run.FpsTarget,
                encoder = run.Encoder,
                container = run.Container.ToString(),
                startedAt = run.StartedAt,
                durationSeconds = Math.Round(run.MeasuredDuration > 0 ? run.MeasuredDuration : run.Elapsed.TotalSeconds, 2),
                audioTracks = run.AudioTracks,
                bitrateKbps = _settings.Current.Recording.BitrateKbps
            };
            File.WriteAllText(
                run.FinalPath + ".fifine.json",
                JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warn("Could not write recording metadata: " + ex.Message);
        }
    }

    /// <summary>
    /// A persisted output folder can be malformed (for example an old automation left a
    /// path beyond Windows' limit) or disappear after the settings were saved. Recording must
    /// still work, so use the normal recordings folder and explain the change instead of failing
    /// the whole capture session.
    /// </summary>
    private string PrepareRecordingDirectory(string? configured, string purpose)
    {
        var configuredDirectory = string.IsNullOrWhiteSpace(configured)
            ? AppPaths.DefaultRecordingsDir
            : configured;

        if (TryPrepareDirectory(configuredDirectory, out var configuredError))
        {
            return configuredDirectory;
        }

        Log.Warn($"Configured {purpose} directory cannot be used: {configuredError}");

        // Videos can be redirected or protected by Controlled Folder Access even when the
        // directory itself can be created. Prefer the Videos folder, then the app-data
        // fallback that is always under this application's own root.
        var candidates = new[]
        {
            AppPaths.DefaultRecordingsDir,
            Path.Combine(AppPaths.Root, "Recordings"),
            AppPaths.Root
        };

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!TryPrepareDirectory(candidate, out var fallbackError))
            {
                Log.Warn($"{purpose} fallback directory cannot be used: {candidate} ({fallbackError})");
                continue;
            }

            if (!string.Equals(candidate, configuredDirectory, StringComparison.OrdinalIgnoreCase))
            {
                _settings.Current.Recording.Directory = candidate;
                _settings.SaveSoon();
                Notice?.Invoke(
                    $"The configured {purpose} folder could not be used. Saving to {candidate} instead.",
                    true);
            }

            return candidate;
        }

        // Let the normal start-recording error path report the original problem rather than
        // pretending that a recording can be written somewhere none of the locations work.
        Directory.CreateDirectory(configuredDirectory);
        return configuredDirectory;
    }

    private static bool TryPrepareDirectory(string directory, out string? error)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".fifine-write-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (File.Create(probe))
            {
            }

            File.Delete(probe);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
    }

    private string BuildRecordingPath()
    {
        var s = _settings.Current.Recording;
        var dir = PrepareRecordingDirectory(s.Directory, "recording");

        var platform = _settings.Current.Capture.Platform switch
        {
            PlatformKind.PS5 => "PS5",
            PlatformKind.Xbox => "Xbox",
            _ => "PC"
        };

        var name = s.FilenameTemplate
            .Replace("{platform}", platform)
            .Replace("{date}", DateTime.Now.ToString("yyyy-MM-dd"))
            .Replace("{time}", DateTime.Now.ToString("HH-mm-ss"));
        if (string.IsNullOrWhiteSpace(name))
        {
            name = platform + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        }

        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '-');
        }

        var path = Path.Combine(dir, name + "." + Extension(s.Container));
        var i = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(dir, $"{name}_{i++}." + Extension(s.Container));
        }

        return path;
    }

    private static string Extension(ContainerKind container) => container == ContainerKind.Mkv ? "mkv" : "mp4";

    // -------------------------------------------------------------- instant replay

    public async Task<string?> SaveReplayAsync(int seconds)
    {
        if (IsRecording)
        {
            Notice?.Invoke("Instant replay is paused while a recording is in progress.", true);
            return null;
        }

        if (!_settings.Current.Replay.Enabled)
        {
            Notice?.Invoke("Enable Instant Replay first.", true);
            return null;
        }

        var dir = AppPaths.ReplayDir;
        var segments = Directory.Exists(dir)
            ? Directory.GetFiles(dir, "replay_*.mkv")
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList()
            : new List<FileInfo>();

        if (segments.Count == 0)
        {
            Notice?.Invoke("No replay data buffered yet. Wait a few seconds after enabling Instant Replay.", true);
            return null;
        }

        var needed = Math.Max(1, seconds / Math.Max(1, _settings.Current.Recording.RingSegmentSecondsClamped())) + 1;
        var selected = segments.Take(needed).OrderBy(f => f.LastWriteTimeUtc).ToList();

        var s = _settings.Current.Recording;
        var platform = _settings.Current.Capture.Platform switch
        {
            PlatformKind.PS5 => "PS5",
            PlatformKind.Xbox => "Xbox",
            _ => "PC"
        };
        var dirOut = PrepareRecordingDirectory(s.Directory, "replay");
        var target = Path.Combine(dirOut, $"{platform}_Replay_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");
        var listPath = Path.Combine(AppPaths.CacheDir, "replay-concat-" + Guid.NewGuid().ToString("N") + ".txt");

        await File.WriteAllTextAsync(listPath, string.Join(Environment.NewLine,
            selected.Select(f => "file '" + f.FullName.Replace("'", "'\\''") + "'"))).ConfigureAwait(false);

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "concat", "-safe", "0", "-i", listPath,
            "-c", "copy", "-movflags", "+faststart", "-y", target
        };

        StatusLine = "Saving replay…";
        RaiseState();

        var result = await Proc.RunAsync(_ffmpeg.FfmpegPath!, args, TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        try { File.Delete(listPath); } catch { }

        if (result.Code != 0 || !File.Exists(target))
        {
            Log.Error("Replay save failed: " + result.StdErr);
            Notice?.Invoke("Replay save failed. " + FirstLine(result.StdErr), true);
            StatusLine = "Replay save failed";
            RaiseState();
            return null;
        }

        var replayRun = new RecordingRun
        {
            FinalPath = target,
            Width = _settings.Current.Recording.Width,
            Height = _settings.Current.Recording.Height,
            FpsTarget = _settings.Current.Recording.Fps,
            Encoder = _activeOptions?.Encoder.Id ?? "unknown",
            Container = ContainerKind.Mp4,
            Platform = _settings.Current.Capture.Platform,
            Source = _activeOptions?.Source.Label ?? "Capture",
            Accumulated = TimeSpan.FromSeconds(seconds),
            AudioTracks = _settings.Current.Recording.SeparateAudioTracks ? 2 : 1
        };
        WriteMetadata(replayRun);

        Notice?.Invoke("Replay saved · " + Path.GetFileName(target), false);
        StatusLine = "Replay saved";
        RaiseState();
        return target;
    }

    private void PrepareRing(PipelineOptions options)
    {
        if (!options.HasRingOutput)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(options.RingDirectory!);
            foreach (var file in Directory.GetFiles(options.RingDirectory!, "replay_*.mkv"))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Could not reset replay buffer: " + ex.Message);
        }
    }

    // -------------------------------------------------------------- process plumbing

    private PipelineOptions BuildOptions(
        string? recordPath,
        EncoderInfo? encoderOverride = null,
        IReadOnlyList<string>? audioPipes = null)
    {
        var s = _settings.Current;
        var source = SourceResolver.FromSettings(s, _displays());
        var encoder = encoderOverride
                      ?? _encoders.Resolve(s.Recording.Codec, s.Recording.UseHardwareEncoding, s.Recording.Encoder)
                      ?? new EncoderInfo("libx264", "CPU x264 (software)", "CPU", VideoCodecKind.H264, false);

        var width = s.Recording.Width > 0 ? s.Recording.Width : source.Width;
        var height = s.Recording.Height > 0 ? s.Recording.Height : source.Height;
        if (recordPath is null && source.Kind == CaptureSourceKind.CaptureCard)
        {
            width = s.Capture.ResolutionWidth;
            height = s.Capture.ResolutionHeight;
        }

        // Never upscale: a recording can only be as large as the source delivers.
        if (source.Width > 0 && source.Height > 0 && width > source.Width)
        {
            var scale = source.Width / (double)width;
            width = source.Width;
            height = (int)Math.Round(height * scale);
        }

        // Likewise, do not claim a frame rate the source does not produce.
        var encodeFps = Math.Clamp(s.Recording.Fps, 24, 240);
        if (source.Kind != CaptureSourceKind.TestPattern && source.Fps > 0)
        {
            encodeFps = Math.Min(encodeFps, source.Fps);
        }

        // Frame generation multiplies the recorded frame rate with interpolated frames.
        var frameGeneration = Math.Clamp(s.Recording.FrameGeneration, 1, 4);
        if (frameGeneration > 1 && (width > 2560 || height > 1440))
        {
            frameGeneration = 1;
        }

        if (frameGeneration > 1 && encodeFps * frameGeneration > 240)
        {
            frameGeneration = Math.Max(1, 240 / Math.Max(1, encodeFps));
        }

        var outputFps = Math.Clamp(encodeFps * frameGeneration, 24, 240);

        var bitrate = s.Recording.RateControl switch
        {
            RateControlMode.Custom => s.Recording.BitrateKbps,
            RateControlMode.Quality => s.Recording.BitrateKbps,
            _ => EstimateBitrate(width, height, s.Recording.Fps, s.Recording.QualityPreset)
        };

        // Enhancement wants the native frame; plain preview keeps the cheap 1600 px MJPEG stream.
        // The native path is capped at 3840 px wide so an 8K desktop capture cannot turn the
        // MJPEG transport into a multi-hundred-megabyte-per-second firehose.
        var enhancementPreview = s.Enhancement.Enabled && s.Enhancement.PreviewEnhanced;
        var previewWidth = PipelineArgs.ResolvePreviewWidth(
            source.Width,
            enhancementPreview,
            _previewFullResolution && s.Enhancement.FullResolutionPreview);

        var replayEnabled = s.Replay.Enabled;
        return new PipelineOptions
        {
            Source = source,
            PreviewWidth = previewWidth,
            PreviewEnabled = true,
            MeasureLatency = false,
            RecordPath = recordPath,
            RingDirectory = replayEnabled ? AppPaths.ReplayDir : null,
            RingSegmentSeconds = s.Recording.RingSegmentSecondsClamped(),
            RingSegmentCount = Math.Max(4, (s.Replay.DurationSeconds / Math.Max(1, s.Recording.RingSegmentSecondsClamped())) + 3),
            EncodeWidth = Even(width),
            EncodeHeight = Even(height),
            EncodeFps = outputFps,
            FrameGeneration = frameGeneration,
            Encoder = encoder,
            BitrateKbps = bitrate,
            QualityPreset = s.Recording.QualityPreset,
            Container = s.Recording.Container,
            AudioBitrateKbps = s.Recording.AudioBitrateKbps,
            AudioPipes = audioPipes ?? Array.Empty<string>(),
            SeparateAudioTracks = s.Recording.SeparateAudioTracks,
            AudioSampleRate = s.Audio.SampleRate,
            Latency = s.LowLatency.Mode,
            VideoBufferMs = s.LowLatency.VideoBufferMs,
            EnhancementFilter = PipelineArgs.BuildRecordingEnhancementFilter(s.Enhancement, Even(width), Even(height)),
            ConsoleVideoPipe = source.Kind == CaptureSourceKind.ConsoleStream ? _console.VideoPipePath ?? string.Empty : string.Empty,
            ConsoleCodec = s.PlayStation.PreferHevc && s.PlayStation.IsPs5 ? "hevc" : "h264"
        };
    }

    private static int Even(int value) => value - (value % 2);

    public static int EstimateBitrate(int width, int height, int fps, string preset)
    {
        var baseKbps = (long)width * height * fps * 0.00016;
        var factor = preset switch
        {
            "High" => 1.25,
            "Low" => 0.55,
            _ => 1.0
        };
        return (int)Math.Round(baseKbps * factor);
    }

    private async Task<EncoderInfo?> ResolveEncoderAsync()
    {
        var s = _settings.Current;
        var candidates = new List<EncoderInfo>();
        var preferred = _encoders.Resolve(s.Recording.Codec, s.Recording.UseHardwareEncoding, s.Recording.Encoder);
        if (preferred is not null)
        {
            candidates.Add(preferred);
        }

        candidates.AddRange(_encoders.Available.Where(e => e.Codec == s.Recording.Codec && candidates.All(c => c.Id != e.Id)));

        foreach (var candidate in candidates)
        {
            if (await _encoders.VerifyAsync(candidate).ConfigureAwait(false))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task<bool> StartProcessAsync(PipelineOptions options)
    {
        if (!_ffmpeg.IsAvailable)
        {
            return false;
        }

        LastError = null;
        var args = PipelineArgs.Build(options);
        Process? process = null;
        try
        {
            _expectExit = false;
            process = Proc.Start(_ffmpeg.FfmpegPath!, args, redirectStdout: true, redirectStderr: true, redirectStdin: true);
            _process = process;

            if (options.PreviewEnabled)
            {
                _preview?.Stop();
                _preview = new PreviewStream(process.StandardOutput.BaseStream);
                _preview.FrameDecoded += OnFrameDecoded;
                _preview.Start();
            }

            _stderrCts = new CancellationTokenSource();
            var token = _stderrCts.Token;
            var procRef = process;
            _stderrTask = Task.Run(() => ReadStderrAsync(procRef, token), token);

            process.EnableRaisingEvents = true;
            process.Exited += OnProcessExited;

            // Give the pipeline a moment: a bad device/format fails fast and we can report it.
            await Task.Delay(900).ConfigureAwait(false);
            if (process.HasExited)
            {
                LastError = LastError ?? "The capture pipeline exited immediately.";
                Notice?.Invoke(LastError, true);
                Log.Error("Pipeline failed: " + LastError);
                await StopProcessAsync(intentional: true).ConfigureAwait(false);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Error("Could not start capture pipeline", ex);
            try { process?.Kill(true); } catch { }
            return false;
        }
    }

    private async Task ReadStderrAsync(Process process, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                HandleStderrLine(line);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
        catch (Exception ex)
        {
            Log.Debug("stderr reader ended: " + ex.Message);
        }
    }

    private void HandleStderrLine(string line)
    {
        if (line.Length == 0)
        {
            return;
        }

        if (line.Contains("pts_time:", StringComparison.Ordinal))
        {
            var m = PtsRegex.Match(line);
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                // Device timestamps are relative to stream start; used only for cadence, not absolute latency.
            }
        }

        if (FrameRegex.IsMatch(line) || line.Contains("fps=", StringComparison.Ordinal))
        {
            var fps = FpsRegex.Match(line);
            if (fps.Success && double.TryParse(fps.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedFps))
            {
                if (_run is not null)
                {
                    _run.Fps = Math.Round(parsedFps, 1);
                }
            }

            var frames = FrameRegex.Match(line);
            if (frames.Success && long.TryParse(frames.Groups[1].Value, out var frameCount))
            {
                Interlocked.Exchange(ref _lastRecordFrame, frameCount);
                _lastRecordFrameAt = DateTime.Now;
                RecordedFrames = frameCount;
            }

            var drop = DropRegex.Match(line);
            if (drop.Success && double.TryParse(drop.Groups[1].Value, out var parsedDrop) && parsedDrop > _stderrDrop)
            {
                _stderrDrop = parsedDrop;
                if (_run is not null)
                {
                    _run.Dropped = (long)parsedDrop;
                }
            }

            var bitrate = BitrateRegex.Match(line);
            if (bitrate.Success && double.TryParse(bitrate.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var kbps))
            {
                if (_run is not null)
                {
                    _run.BitrateMbps = Math.Round(kbps / 1000.0, 2);
                }
            }

            var speed = SpeedRegex.Match(line);
            if (speed.Success && double.TryParse(speed.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedSpeed))
            {
                LastRecordSpeed = parsedSpeed;
            }
        }

        var lower = line.ToLowerInvariant();
        if (lower.Contains("error") || lower.Contains("invalid") || lower.Contains("could not")
            || lower.Contains("cannot") || lower.Contains("failed") || lower.Contains("unable"))
        {
            LastError = line.Trim();
            Log.Warn("ffmpeg: " + line.Trim());
        }
        else
        {
            Log.Debug("ffmpeg: " + line);
        }
    }

    private void OnFrameDecoded(PreviewFrame frame)
    {
        var now = DateTime.Now;
        var interval = _lastFrameWall == DateTime.MinValue ? 0 : (now - _lastFrameWall).TotalMilliseconds;
        _lastFrameWall = now;
        _lastFrameAt = now;

        if (interval > 0)
        {
            _lastIntervalMs = interval;
            _avgIntervalMs = _avgIntervalMs <= 0 ? interval : (_avgIntervalMs * 0.9) + (interval * 0.1);
            var jitter = Math.Abs(interval - _avgIntervalMs);
            _jitterMs = _jitterMs <= 0 ? jitter : (_jitterMs * 0.9) + (jitter * 0.1);

            var target = _settings.Current.Capture.Fps > 0 ? _settings.Current.Capture.Fps : 60;
            // Ignore the first frames of a pipeline: a restart always leaves one wall-clock gap
            // that is not a dropped frame.
            // Preview deliberately drops frames when the UI is busy, so only a real stall
            // (three frame periods) counts as a gap.
            if (interval > (1000.0 / target) * 3.0 && _framesSeen > 4)
            {
                _previewGaps++;
            }
        }

        _decodeMs = frame.DecodeMs;
        TrackPreviewDecodeLoad(frame.DecodeMs);
        _framesSeen++;
        _fpsWindowFrames++;
        PreviewStartedAt = PreviewStartedAt == DateTime.MaxValue ? now : PreviewStartedAt;

        var elapsed = (now - _fpsWindowStart).TotalSeconds;
        if (elapsed >= 0.5)
        {
            _measuredFps = Math.Round(_fpsWindowFrames / elapsed, 1);
            _fpsWindowFrames = 0;
            _fpsWindowStart = now;
        }

        FrameArrived?.Invoke(frame);
    }

    public void NotePresented(long decodedTicks)
    {
        var ms = (Stopwatch.GetTimestamp() - decodedTicks) * 1000.0 / Stopwatch.Frequency;
        _presentLatencyMs = _presentLatencyMs <= 0 ? ms : (_presentLatencyMs * 0.85) + (ms * 0.15);
    }

    /// <summary>Lets a user re-request native enhancement input after an automatic downgrade.</summary>
    public void RequestFullResolutionPreview()
    {
        _previewFullResolution = true;
        _previewLoad.RequestFullResolution();
    }

    /// <summary>
    /// Watches MJPEG decode cost when the enhancement preview runs above 1600 px. Decoding a 1080p
    /// or 1440p JPEG on the CPU is the new bottleneck of this transport; when it cannot hold the
    /// source frame budget the preview drops back to the lightweight size instead of building delay.
    /// Recording and the encoded output always keep their full resolution.
    /// </summary>
    private void TrackPreviewDecodeLoad(double decodeMs)
    {
        if (_activeOptions is null || _previewLoad.FallbackApplied || !_settings.Current.Enhancement.Enabled)
        {
            return;
        }

        if (ActivePreviewWidth <= 1600 || decodeMs <= 0)
        {
            return;
        }

        var fps = Math.Max(24, _settings.Current.Capture.Fps);
        if (!_previewLoad.Observe(decodeMs, fps))
        {
            return;
        }

        _previewFullResolution = false;
        _settings.Current.Enhancement.FullResolutionPreview = false;
        _settings.SaveSoon();
        var width = ActivePreviewWidth;
        Log.Warn(
            $"Native preview decode fallback after {_previewLoad.OverloadedFrames} consecutive frames: " +
            $"{_previewLoad.AverageDecodeMs:0.0} ms average at {fps} fps.");
        Notice?.Invoke(
            $"Decoding {width} px MJPEG frames averaged {_previewLoad.AverageDecodeMs:0.0} ms, which cannot hold {fps} fps here. " +
            "The enhancement preview returned to 1600 px; the recording and encoded output keep the full source resolution.",
            true);

        _ = Task.Run(RestartPreviewAsync);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (_expectExit)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await Task.Delay(150).ConfigureAwait(false);
            var wasRecording = IsRecording;
            IsPreviewing = false;
            LastError ??= "The capture pipeline stopped unexpectedly.";
            Log.Warn("Capture pipeline exited unexpectedly. " + (LastError ?? string.Empty));
            RaiseState();

            if (wasRecording)
            {
                Notice?.Invoke("Capture stopped unexpectedly. The recording part was kept and finalised.", true);
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_run is not null)
                    {
                        var run = _run;
                        _run = null;
                        IsRecording = false;
                        _paused = false;
                        _audio.CloseRecordSinks();
                        await Task.Run(() => FinaliseRecordingAsync(run, true)).ConfigureAwait(false);
                    }
                }
                finally
                {
                    _gate.Release();
                }

                return;
            }

            // Auto-reconnect for capture device disconnects.
            if (_reconnectAttempts >= 5)
            {
                Notice?.Invoke("Capture device disconnected. Reconnect the device, then press Start Capture.", true);
                StatusLine = "Device disconnected";
                return;
            }

            _reconnectAttempts++;
            Notice?.Invoke($"Capture signal lost — reconnecting ({_reconnectAttempts}/5)…", true);
            await Task.Delay(1500 * _reconnectAttempts).ConfigureAwait(false);

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (IsRecording || IsPreviewing)
                {
                    return;
                }

                var options = BuildOptions(recordPath: null);
                PrepareRing(options);
                if (await StartProcessAsync(options).ConfigureAwait(false))
                {
                    IsPreviewing = true;
                    _activeOptions = options;
                    PipelineMode = options.HasRingOutput ? "Replay buffer + preview" : "Preview";
                    StatusLine = "Capture ready";
                    Notice?.Invoke("Capture reconnected.", false);
                }
            }
            finally
            {
                _gate.Release();
                RaiseState();
            }
        });
    }

    private async Task StopProcessAsync(bool intentional)
    {
        _expectExit = true;
        StopStatusTimer();

        var preview = _preview;
        var process = _process;
        _process = null;

        if (process is not null)
        {
            process.Exited -= OnProcessExited;

            // Ask FFmpeg to quit while the preview reader is still draining stdout.
            // If the reader stops first, FFmpeg blocks on the full pipe and never reads
            // the quit request, which leaves an MP4 without its moov atom.
            try
            {
                if (!process.HasExited)
                {
                    var stdin = process.StandardInput;
                    await stdin.WriteAsync("q\n").ConfigureAwait(false);
                    await stdin.FlushAsync().ConfigureAwait(false);
                    using var cts = new CancellationTokenSource(6000);
                    try
                    {
                        await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        Log.Warn("Encoder did not exit cleanly; terminating.");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Graceful encoder stop failed: " + ex.Message);
            }
            finally
            {
                preview?.Stop();
                _preview = null;
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                        process.WaitForExit(2000);
                    }
                }
                catch
                {
                    // already gone
                }

                process.Dispose();
            }
        }
        else
        {
            _preview = null;
            preview?.Stop();
        }

        try { _stderrCts?.Cancel(); } catch { }

        try { _stderrCts?.Dispose(); } catch { }
        _stderrCts = null;
        _stderrTask = null;

        if (intentional)
        {
            _expectExit = false;
        }
    }

    private void StartStatusTimer()
    {
        StopStatusTimer();
        _statusTimer = new Timer(_ =>
        {
            try
            {
                if (IsRecording && _run is not null)
                {
                    RaiseState();
                }
                else if (IsPreviewing && !HasSignal && (DateTime.Now - PreviewStartedAt).TotalSeconds > 2)
                {
                    if (StatusLine != "Waiting for signal…")
                    {
                        StatusLine = "Waiting for signal…";
                        RaiseState();
                    }
                }
            }
            catch
            {
                // never let the timer throw
            }
        }, null, 500, 500);
    }

    private void StopStatusTimer()
    {
        _statusTimer?.Dispose();
        _statusTimer = null;
    }

    private long CurrentRecordingBytes()
    {
        if (_run is null)
        {
            return 0;
        }

        long total = 0;
        foreach (var part in _run.Parts)
        {
            total += FileLength(part);
        }

        return total;
    }

    private long FileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string FirstLine(string text)
    {
        var line = text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line is null ? string.Empty : (line.Length > 160 ? line[..160] : line);
    }

    private void RaiseState() => StateChanged?.Invoke();

    public void SetPlatform(PlatformKind platform)
    {
        _settings.Current.Capture.Platform = platform;
        _settings.Save();
        RaiseState();
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch
        {
            // shutting down
        }

        _gate.Dispose();
    }
}

internal static class RecordingSettingsExtensions
{
    public static int RingSegmentSecondsClamped(this RecordingSettings settings) => 2;
}
