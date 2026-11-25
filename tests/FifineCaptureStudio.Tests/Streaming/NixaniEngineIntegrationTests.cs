using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.Streaming;
using FifineCaptureStudio.Core.Streaming.Nixani;
using Xunit;

namespace FifineCaptureStudio.Tests.Streaming;

/// <summary>
/// Integration tests for the PlayStation backend. They start a real child process that speaks the
/// Nixani Engine protocol v3 and stream real H.264 access units, so framing, registration,
/// session handling, statistics and the named-pipe transport are exercised without a console.
/// </summary>
public sealed class NixaniEngineIntegrationTests
{
    [Fact]
    public async Task MockEngine_registers_lists_consoles_and_streams_frames()
    {
        var exe = MockEngine.RequirePath();
        await using var client = new NixaniEngineClient(exe, MockEngine.Arguments());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var states = new List<ConsoleStreamState>();
        var frames = 0;
        var audio = 0;
        client.StateChanged += (state, _, _) =>
        {
            lock (states)
            {
                states.Add(state);
            }
        };
        client.VideoFrame += _ => Interlocked.Increment(ref frames);
        client.Audio += _ => Interlocked.Increment(ref audio);

        await client.StartAsync(cts.Token);
        Assert.True(client.IsRunning);
        Assert.Contains("mock", client.EngineVersion ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var hosts = await client.DiscoverAsync(TimeSpan.FromMilliseconds(600), cts.Token);
        Assert.Single(hosts);
        Assert.Equal("Mock PlayStation 5", hosts[0].Name);
        Assert.True(hosts[0].IsPs5);
        Assert.Equal(1_000_100u, hosts[0].Target);

        var registration = await client.RegisterAsync("127.0.0.1", hosts[0].Target, 12345678, 0, new byte[8],
            TimeSpan.FromSeconds(10), cts.Token);
        Assert.True(registration.Ok);
        Assert.Equal(16, registration.RegistKey.Length);
        Assert.Equal("Mock PlayStation 5", registration.Nickname);

        // A rejected code must come back as a failure, not a hang.
        var rejected = await client.RegisterAsync("127.0.0.1", hosts[0].Target, 99999999, 0, new byte[8],
            TimeSpan.FromSeconds(10), cts.Token);
        Assert.False(rejected.Ok);
        Assert.Contains("expired", rejected.Error, StringComparison.OrdinalIgnoreCase);

        await client.StartSessionAsync(new NixaniSessionParams(
            "127.0.0.1", true, registration.RegistKey, registration.RpKey, new byte[8],
            1080, 60, 0, 0, true), cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && Volatile.Read(ref frames) < 12)
        {
            await Task.Delay(100);
        }

        Assert.True(frames >= 12, $"expected at least 12 video frames, got {frames}");
        Assert.True(Volatile.Read(ref audio) > 0);
        Assert.True(client.Stats.PacketsReceived > 0);
        Assert.True(client.Stats.AudioSampleRate is 48000);
        lock (states)
        {
            Assert.Contains(ConsoleStreamState.Streaming, states);
        }

        Assert.True(await client.StopSessionAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ConsoleFrames_reach_ffmpeg_through_the_named_pipe_and_decode()
    {
        var ffmpeg = FfmpegProbe.RequirePath();
        var exe = MockEngine.RequirePath();

        await using var pipe = new VideoPipeFeed();
        pipe.Open();
        await pipe.ServerReady.WaitAsync(TimeSpan.FromSeconds(10));

        await using var client = new NixaniEngineClient(exe, MockEngine.Arguments());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var frames = 0;
        client.VideoFrame += frame =>
        {
            pipe.Push(frame.Data);
            Interlocked.Increment(ref frames);
        };

        await client.StartAsync(cts.Token);
        await client.StartSessionAsync(new NixaniSessionParams(
            "127.0.0.1", true, new byte[16], new byte[16], new byte[8], 1080, 60, 0, 0, false), cts.Token);

        var output = Path.Combine(Path.GetTempPath(), "fifine-pipe-" + Guid.NewGuid().ToString("N") + ".mp4");
        var args = new[]
        {
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "h264", "-framerate", "60", "-i", pipe.PipePath,
            "-t", "3", "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
            "-f", "mp4", output
        };

        var process = Process.Start(new ProcessStartInfo(ffmpeg, args)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;

        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cts.Token);
        var stderr = await stderrTask;

        Assert.True(process.ExitCode == 0, "ffmpeg failed: " + stderr);
        Assert.True(File.Exists(output));
        var length = new FileInfo(output).Length;
        Assert.True(length > 10_000, $"decoded recording is only {length} bytes");
        Assert.True(Volatile.Read(ref frames) >= 30, $"only {frames} frames were pushed");

        File.Delete(output);
    }

    [Fact]
    public void LinkCode_accepts_exactly_eight_digits()
    {
        Assert.True(NixaniEngineClient.IsLinkCode("12345678", out var code));
        Assert.Equal(12345678u, code);
        Assert.False(NixaniEngineClient.IsLinkCode("1234567", out _));
        Assert.False(NixaniEngineClient.IsLinkCode("1234567a", out _));
        Assert.False(NixaniEngineClient.IsLinkCode(null, out _));
    }

    [Fact]
    public void AccountId_parses_hex_and_base64()
    {
        var hex = PlayStationStreamService.ParseAccountId("0102030405060708");
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, hex);

        var base64 = PlayStationStreamService.ParseAccountId(Convert.ToBase64String(hex));
        Assert.Equal(hex, base64);

        Assert.Equal(new byte[8], PlayStationStreamService.ParseAccountId("nonsense"));
    }

    [Fact]
    public void AccountId_validation_rejects_placeholder_zero_ids()
    {
        Assert.False(PlayStationStreamService.IsValidAccountId(null));
        Assert.False(PlayStationStreamService.IsValidAccountId(string.Empty));
        Assert.False(PlayStationStreamService.IsValidAccountId("000000000000"));
        Assert.False(PlayStationStreamService.IsValidAccountId("0000000000000000"));
        Assert.False(PlayStationStreamService.IsValidAccountId(Convert.ToBase64String(new byte[8])));
        Assert.True(PlayStationStreamService.IsValidAccountId("0102030405060708"));
        Assert.True(PlayStationStreamService.IsValidAccountId(
            Convert.ToBase64String(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 })));
    }

    [Fact]
    public void Console_settings_migrate_and_are_normalised()
    {
        var settings = new AppSettings
        {
            SchemaVersion = 2
        };
        settings.PlayStation.Resolution = 999;
        settings.PlayStation.Fps = 144;
        settings.PlayStation.BitrateKbps = -5;

        AppSettings.Normalize(settings);

        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal(1080, settings.PlayStation.Resolution);
        Assert.Equal(60, settings.PlayStation.Fps);
        Assert.Equal(0, settings.PlayStation.BitrateKbps);
        Assert.True(settings.PlayStation.IsPs5);
        Assert.False(settings.PlayStation.HasRegistration);
        Assert.NotNull(settings.Xbox);
        Assert.False(settings.Xbox.HasToken);
    }

    [Fact]
    public void Secrets_round_trip_through_dpapi()
    {
        var raw = new byte[] { 9, 8, 7, 6, 5, 4, 3, 2, 1, 0, 1, 2, 3, 4, 5, 6 };
        var stored = SecretStore.ProtectBytes(raw);
        Assert.StartsWith("dpapi:", stored);
        Assert.Equal(raw, SecretStore.UnprotectBytes(stored));
        Assert.Null(SecretStore.UnprotectBytes("dpapi:bm90LXZhbGlk"));
    }
}

internal static class MockEngine
{
    /// <summary>The mock takes the Annex B asset and how many loops to stream.</summary>
    public static string[] Arguments()
    {
        var root = RepositoryRoot() ?? throw new InvalidOperationException("Repository root not found.");
        var asset = Path.Combine(root, "tests", "assets", "mock-ps5.h264");
        if (!File.Exists(asset))
        {
            throw new InvalidOperationException("Missing test asset: " + asset);
        }

        return new[] { asset, "4" };
    }

    public static string RequirePath() => Find() ??
        throw new InvalidOperationException(
            "NixaniEngineMock.exe has not been built. Build the solution first (it is a solution project).");

    private static string? Find()
    {
        var root = RepositoryRoot();
        if (root is null)
        {
            return null;
        }

        foreach (var configuration in new[] { "Release", "Debug" })
        {
            var path = Path.Combine(root, "tests", "NixaniEngineMock", "bin", configuration, "net8.0",
                "NixaniEngineMock.exe");
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    public static string? RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "FIFINE.CaptureStudio.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}

internal static class FfmpegProbe
{
    public static string RequirePath()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppPaths.FfmpegDir, "bin", "ffmpeg.exe"),
            Path.Combine(AppPaths.FfmpegDir, "ffmpeg.exe")
        };

        var onPath = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? Array.Empty<string>();
        candidates.AddRange(onPath.Select(dir => Path.Combine(dir, "ffmpeg.exe")));

        var found = candidates.FirstOrDefault(File.Exists);
        return found ?? throw new InvalidOperationException(
            "FFmpeg is required for the console decode test. Install it (Settings → Capture) or put it on PATH.");
    }
}
