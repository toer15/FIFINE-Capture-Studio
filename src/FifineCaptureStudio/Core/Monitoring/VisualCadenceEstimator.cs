using System.Diagnostics;

namespace FifineCaptureStudio.Core.Monitoring;

/// <summary>
/// Estimates the original game's cadence from the decoded video, never from the capture device's
/// configured FPS. It compares 16×9 tile luminance (not whole-frame hashes), so a repeated frame is
/// recognised even when codec noise changes individual pixels. The result is deliberately an estimate
/// with a confidence: an HDMI/Remote Play stream cannot expose the console's internal render counter.
/// </summary>
public sealed class VisualCadenceEstimator
{
    public const int Width = 64;
    public const int Height = 36;
    public const int TileColumns = 16;
    public const int TileRows = 9;

    private const double TileChangeThreshold = 5.0;   // average luma delta inside one tile
    private const double FrameChangeThreshold = 0.85; // mean tile delta for a new visual frame
    private const double StrongTileThreshold = 9.0;   // one clearly changed tile is enough
    private const double SceneCutMean = 15.0;
    private const double StaticSeconds = 0.9;
    private const int MaxSamples = 240;

    private readonly object _gate = new();
    private readonly List<Sample> _samples = new();
    private readonly double[] _previous = new double[TileColumns * TileRows];
    private readonly double[] _current = new double[TileColumns * TileRows];
    private bool _hasPrevious;
    private long _lastChangeTicks;
    private double _lastStableFps;
    private double _lastStableConfidence;
    private long _lastStableTicks;
    private CadenceReading _reading = new(false, 0, 0, false, false, "Waiting for frames", 0);

    private readonly record struct Sample(long Ticks, bool Changed, double Score);

    public CadenceReading Current
    {
        get
        {
            lock (_gate)
            {
                return _reading;
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _samples.Clear();
            _hasPrevious = false;
            _lastChangeTicks = 0;
            _lastStableFps = 0;
            _lastStableConfidence = 0;
            _reading = new CadenceReading(false, 0, 0, false, false, "Waiting for frames", 0);
        }
    }

    /// <summary>Accepts one 64×36 BGRA sample and one decoder timestamp.</summary>
    public void Observe(long ticks, ReadOnlySpan<byte> bgra, double streamFps = 60)
    {
        if (bgra.Length < Width * Height * 4)
        {
            return;
        }

        lock (_gate)
        {
            ComputeTiles(bgra);
            if (!_hasPrevious)
            {
                _current.CopyTo(_previous, 0);
                _hasPrevious = true;
                return;
            }

            var changedTiles = 0;
            var maxTile = 0.0;
            var sum = 0.0;
            for (var i = 0; i < _current.Length; i++)
            {
                var delta = Math.Abs(_current[i] - _previous[i]);
                sum += delta;
                if (delta > maxTile) maxTile = delta;
                if (delta > TileChangeThreshold) changedTiles++;
            }

            var mean = sum / _current.Length;
            var changed = changedTiles >= 1 && (mean >= FrameChangeThreshold || maxTile >= StrongTileThreshold);
            _current.CopyTo(_previous, 0);

            // A hard cut invalidates the temporal window; do not count it as a rendered frame.
            if (changedTiles >= 120 && mean >= SceneCutMean)
            {
                _samples.Clear();
                _samples.Add(new Sample(ticks, false, mean));
                _lastChangeTicks = ticks;
                _reading = new CadenceReading(false, 0, 0, false, false, "Scene cut — re-measuring", 0);
                return;
            }

            _samples.Add(new Sample(ticks, changed, mean));
            if (changed)
            {
                _lastChangeTicks = ticks;
            }

            if (_samples.Count > MaxSamples)
            {
                _samples.RemoveRange(0, _samples.Count - MaxSamples);
            }

            Evaluate(ticks, streamFps);
        }
    }

    private void Evaluate(long now, double streamFps)
    {
        if (_samples.Count < 12)
        {
            _reading = new CadenceReading(false, 0, 0, false, false, "Measuring…", _samples.Count);
            return;
        }

        var first = _samples[0].Ticks;
        var span = (now - first) / (double)Stopwatch.Frequency;
        if (span < 0.9)
        {
            _reading = new CadenceReading(false, 0, 0, false, false, "Measuring…", _samples.Count);
            return;
        }

        var staticFor = (now - _lastChangeTicks) / (double)Stopwatch.Frequency;
        if (staticFor >= StaticSeconds)
        {
            var stableAge = (now - _lastStableTicks) / (double)Stopwatch.Frequency;
            if (_lastStableFps > 0 && stableAge <= 10)
            {
                var staticConfidence = Math.Min(0.45, _lastStableConfidence * 0.55);
                _reading = new CadenceReading(true, _lastStableFps, staticConfidence, false, true,
                    "Static scene — last stable estimate", _samples.Count);
            }
            else
            {
                _reading = new CadenceReading(false, 0, 0, false, true,
                    "Static scene — FPS temporarily unavailable", _samples.Count);
            }

            return;
        }

        var changes = 0;
        foreach (var sample in _samples)
        {
            if (sample.Changed) changes++;
        }

        var raw = changes / span;
        if (raw < 1)
        {
            _reading = new CadenceReading(false, 0, 0, false, false, "Too few new frames", _samples.Count);
            return;
        }

        // Snap only when the evidence is already very close to a whole number. Otherwise preserve the
        // measured value instead of forcing a familiar cadence.
        var snapped = Math.Round(raw);
        var fps = Math.Abs(raw - snapped) <= 0.65 ? snapped : Math.Round(raw, 1);

        var half = _samples.Count / 2;
        var firstHalf = Rate(_samples.Take(half).ToArray());
        var secondHalf = Rate(_samples.Skip(half).ToArray());
        var mid = Math.Max(1.0, (firstHalf + secondHalf) / 2);
        var regularity = 1 - Math.Min(1, Math.Abs(firstHalf - secondHalf) / mid);
        var coverage = Math.Min(1, span / 2.5);
        var confidence = Math.Clamp(0.42 + (0.36 * coverage) + (0.17 * regularity), 0, 0.95);
        if (changes < 8) confidence = Math.Min(confidence, 0.4);

        var streamLimited = fps >= streamFps * 0.95 && streamFps > 0;
        var detail = streamLimited
            ? $"At the input limit ({streamFps:0} FPS stream)"
            : $"{changes} new frames over {span:0.0} s";

        _reading = new CadenceReading(true, fps, confidence, streamLimited, false, detail, _samples.Count);

        // Do not let a scene going static drag the last good cadence down sample by sample; only a
        // window that still contains recent motion may replace the stable estimate.
        var recent = _samples.Skip(Math.Max(0, _samples.Count - 45)).Count(s => s.Changed);
        if (confidence >= 0.55 && recent >= 8 && staticFor <= 0.08)
        {
            _lastStableFps = fps;
            _lastStableConfidence = confidence;
            _lastStableTicks = now;
        }
    }

    private double Rate(Sample[] samples)
    {
        if (samples.Length < 2) return 0;
        var span = (samples[^1].Ticks - samples[0].Ticks) / (double)Stopwatch.Frequency;
        var changes = samples.Count(s => s.Changed);
        return span <= 0 ? 0 : changes / span;
    }

    private void ComputeTiles(ReadOnlySpan<byte> bgra)
    {
        const int tileWidth = Width / TileColumns;
        const int tileHeight = Height / TileRows;
        for (var ty = 0; ty < TileRows; ty++)
        {
            for (var tx = 0; tx < TileColumns; tx++)
            {
                double sum = 0;
                for (var y = 0; y < tileHeight; y++)
                {
                    var row = ((ty * tileHeight) + y) * Width;
                    for (var x = 0; x < tileWidth; x++)
                    {
                        var offset = (row + (tx * tileWidth) + x) * 4;
                        // BGRA -> luma. Alpha is ignored because capture overlays can vary it.
                        sum += (0.114 * bgra[offset]) + (0.587 * bgra[offset + 1]) + (0.299 * bgra[offset + 2]);
                    }
                }

                _current[(ty * TileColumns) + tx] = sum / (tileWidth * tileHeight);
            }
        }
    }

    public int SampleCount
    {
        get
        {
            lock (_gate) return _samples.Count;
        }
    }
}
