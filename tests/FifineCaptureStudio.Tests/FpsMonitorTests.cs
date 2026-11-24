using FifineCaptureStudio.Core.Monitoring;
using Xunit;

namespace FifineCaptureStudio.Tests;

public class FpsMonitorTests
{
    private const int W = VisualCadenceEstimator.Width;
    private const int H = VisualCadenceEstimator.Height;
    private const long Tick = 10_000_000 / 60; // one 60 Hz interval in Stopwatch ticks

    private static byte[] Frame(int phase = 0, int noise = 0)
    {
        var pixels = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
        {
            for (var x = 0; x < W; x++)
            {
                var offset = ((y * W) + x) * 4;
                var value = (byte)(((x + phase * 7) % 32) * 6);
                var n = noise == 0 ? 0 : ((x + y) % 2 == 0 ? noise : -noise);
                pixels[offset] = (byte)Math.Clamp(value + n, 0, 255);
                pixels[offset + 1] = (byte)Math.Clamp((value / 2) + n, 0, 255);
                pixels[offset + 2] = (byte)Math.Clamp(255 - value + n, 0, 255);
                pixels[offset + 3] = 255;
            }
        }

        return pixels;
    }

    private static byte[] SmallMotion(int phase)
    {
        var pixels = new byte[W * H * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = 32;
            pixels[i + 1] = 32;
            pixels[i + 2] = 32;
            pixels[i + 3] = 255;
        }

        var x = ((phase % W) + W) % W;
        for (var y = 0; y < H; y++)
        {
            var offset = ((y * W) + x) * 4;
            pixels[offset] = 255;
            pixels[offset + 1] = 255;
            pixels[offset + 2] = 255;
        }

        return pixels;
    }

    private static byte[] Solid(byte value)
    {
        var pixels = new byte[W * H * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = value;
            pixels[i + 1] = value;
            pixels[i + 2] = value;
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    /// <summary>pattern is "new"/"duplicate" per 60 Hz stream sample.</summary>
    private static CadenceReading Run(double streamFps, Func<int, bool> isNew, int seconds = 3, int noise = 0)
    {
        var estimator = new VisualCadenceEstimator();
        var samples = (int)(seconds * streamFps);
        var phase = 0;
        var last = Frame(0, noise);
        var ticks = 1_000_000L;
        for (var i = 0; i < samples; i++)
        {
            if (i > 0 && isNew(i))
            {
                phase++;
                last = Frame(phase, noise);
            }
            else if (i == 0)
            {
                last = Frame(0, noise);
            }

            estimator.Observe(ticks, last, streamFps);
            ticks += (long)(10_000_000 / streamFps);
        }

        return estimator.Current;
    }

    [Fact]
    public void Thirty_fps_inside_sixty_is_estimated_as_thirty()
    {
        var reading = Run(60, i => i % 2 == 0);
        Assert.True(reading.Available);
        Assert.InRange(reading.Fps, 29, 31);
        Assert.True(reading.Confidence >= 0.55);
        Assert.False(reading.IsStreamLimited);
    }

    [Fact]
    public void Forty_fps_inside_sixty_is_estimated_as_forty()
    {
        // 40 fps in a 60 Hz stream: two new frames then one repeated frame.
        var reading = Run(60, i => i % 3 != 2);
        Assert.True(reading.Available);
        Assert.InRange(reading.Fps, 39, 41);
        Assert.True(reading.Confidence >= 0.5);
    }

    [Fact]
    public void Sixty_fps_is_reported_as_stream_limited()
    {
        var reading = Run(60, _ => true);
        Assert.True(reading.Available);
        Assert.InRange(reading.Fps, 58, 62);
        Assert.True(reading.IsStreamLimited);
        Assert.True(reading.Confidence >= 0.5);
    }

    [Fact]
    public void Repeated_frames_are_not_counted_as_new_game_frames()
    {
        var reading = Run(60, _ => false);
        Assert.False(reading.Available);
        Assert.True(reading.IsStatic);
    }

    [Fact]
    public void Compression_sized_noise_does_not_invent_frames()
    {
        var reading = Run(60, _ => false, noise: 1);
        Assert.False(reading.Available);
        Assert.True(reading.IsStatic);
    }

    [Fact]
    public void Static_scene_keeps_the_last_stable_estimate_with_low_confidence()
    {
        var estimator = new VisualCadenceEstimator();
        var ticks = 1_000_000L;
        var phase = 0;
        for (var i = 0; i < 180; i++)
        {
            if (i % 2 == 0) phase++;
            estimator.Observe(ticks, Frame(phase), 60);
            ticks += Tick;
        }

        var stable = estimator.Current;
        Assert.True(stable.Available);
        Assert.InRange(stable.Fps, 28, 32);

        // Two seconds of an unchanged frame: the estimate must be retained but marked static.
        var frozen = Frame(phase);
        for (var i = 0; i < 120; i++)
        {
            estimator.Observe(ticks, frozen, 60);
            ticks += Tick;
        }

        var paused = estimator.Current;
        Assert.True(paused.Available);
        Assert.True(paused.IsStatic);
        Assert.True(paused.Confidence < stable.Confidence);
        Assert.True(Math.Abs(stable.Fps - paused.Fps) < 0.01,
            $"stable={stable.Fps:0.0} ({stable.Confidence:0.00}) paused={paused.Fps:0.0} ({paused.Confidence:0.00}) {paused.Detail}");
    }

    [Fact]
    public void Thin_moving_object_is_detected_as_new_frames()
    {
        var estimator = new VisualCadenceEstimator();
        var ticks = 1_000_000L;
        var phase = 0;
        for (var i = 0; i < 180; i++)
        {
            if (i % 2 == 0) phase++;
            estimator.Observe(ticks, SmallMotion(phase), 60);
            ticks += Tick;
        }

        var reading = estimator.Current;
        Assert.True(reading.Available, reading.Detail);
        Assert.InRange(reading.Fps, 29, 31);
    }

    [Fact]
    public void Scene_cut_resets_the_window_instead_of_reporting_a_fake_rate()
    {
        var estimator = new VisualCadenceEstimator();
        var ticks = 1_000_000L;
        for (var i = 0; i < 120; i++)
        {
            estimator.Observe(ticks, Frame(i / 2), 60);
            ticks += Tick;
        }

        estimator.Observe(ticks, Solid(250), 60);
        var cut = estimator.Current;
        Assert.False(cut.Available);
        Assert.Contains("cut", cut.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PresentMon_csv_parser_handles_quoted_commas_and_bom()
    {
        var line = "\uFEFFApplication,ProcessID,SwapChainAddress,TimeInSeconds,msBetweenPresents\r\n";
        Assert.True(PresentMonCsv.TryParse(line, out var header, out var headerValues));
        Assert.NotNull(header);
        Assert.Equal(0, header!["Application"]);
        Assert.Equal(4, header["msBetweenPresents"]);
        Assert.Equal(5, headerValues.Length);

        var row = "\"Game, The\",4242,0xABC,1.25,16.67";
        Assert.True(PresentMonCsv.TryParse(row, out var noHeader, out var values));
        Assert.Null(noHeader);
        Assert.Equal("Game, The", values[0]);
        Assert.Equal("4242", values[1]);
        Assert.Equal("16.67", values[4]);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public void PresentMon_rates_thirty_sixty_and_one_hundred_twenty(double expected)
    {
        var times = Enumerable.Range(0, (int)(expected * 2) + 1).Select(i => i / expected).ToArray();
        Assert.Equal(expected, PresentMonMath.Rate(times), precision: 6);
        Assert.Equal(1000 / expected,
            PresentMonMath.Median(Enumerable.Repeat(1000 / expected, 9).ToArray()),
            precision: 6);
    }

    [Fact]
    public void PresentMon_primary_chain_is_the_game_not_a_single_late_overlay_present()
    {
        var game = Enumerable.Range(0, 120).Select(i => 10.0 + (i / 60.0)).ToArray();
        var overlay = new[] { 12.0 };
        var launcher = new[] { 1.0, 1.1, 1.2 };
        var primary = PresentMonSource.SelectPrimaryChain(new Dictionary<string, double[]>
        {
            ["0xGAME"] = game,
            ["0xOVERLAY"] = overlay,
            ["0xLAUNCHER"] = launcher
        });

        Assert.Equal("0xGAME", primary);
    }

    [Fact]
    public void Fps_settings_clamp_overlay_and_keep_process_identity()
    {
        var settings = new FpsMonitorSettings
        {
            OverlayScale = 5,
            OverlayCorner = (OverlayCorner)99,
            GameProcessId = 1234,
            GameProcessName = "game"
        };

        settings.Normalize();

        Assert.Equal(1.5, settings.OverlayScale);
        Assert.Equal(OverlayCorner.TopLeft, settings.OverlayCorner);
        Assert.Equal(1234, settings.GameProcessId);
        Assert.Equal("game", settings.GameProcessName);
    }
}
