using System.IO;
using FifineCaptureStudio.Audio;
using FifineCaptureStudio.Capture;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.Streaming;
using Xunit;

namespace FifineCaptureStudio.Tests;

/// <summary>
/// Exercises the real CaptureSession recording lifecycle with the configured test-pattern source:
/// start, state transition, stop/finalize and an ffprobe read of the completed file. It is skipped
/// only when this environment has no usable FFmpeg/encoder; the live acceptance run covers the same
/// path with the application's normal ViewModel command.
/// </summary>
public class RecordingSessionIntegrationTests
{
    [Fact]
    public async Task Real_session_records_starts_stops_finalizes_and_can_be_probed()
    {
        var root = Path.Combine(Path.GetTempPath(), "fifine-rec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var ffmpeg = new FfmpegManager();
            ffmpeg.Locate();
            if (!ffmpeg.IsAvailable)
            {
                return;
            }

            var settings = new SettingsService(root);
            settings.Current.Capture.SourceKind = CaptureSourceKind.TestPattern;
            settings.Current.Capture.ResolutionWidth = 320;
            settings.Current.Capture.ResolutionHeight = 180;
            settings.Current.Capture.Fps = 30;
            settings.Current.Capture.Platform = PlatformKind.PS5;
            settings.Current.Recording.Directory = root;
            settings.Current.Recording.Width = 320;
            settings.Current.Recording.Height = 180;
            settings.Current.Recording.Fps = 30;
            settings.Current.Recording.Codec = VideoCodecKind.H264;
            settings.Current.Recording.UseHardwareEncoding = false;
            settings.Current.Recording.Encoder = "libx264";
            settings.Current.Recording.Container = ContainerKind.Mp4;
            settings.Current.Recording.BitrateKbps = 1000;
            settings.Current.Enhancement.Enabled = false;
            settings.Current.Replay.Enabled = false;

            var encoders = new EncoderCatalog(ffmpeg);
            await encoders.ScanAsync();
            if (encoders.Resolve(VideoCodecKind.H264, preferHardware: false, "libx264") is null)
            {
                return;
            }

            using var audio = new AudioEngine(settings);
            await audio.StartAsync();
            await using var console = new PlayStationStreamService(settings, audio);
            await using var session = new CaptureSession(
                ffmpeg,
                settings,
                encoders,
                audio,
                console,
                () => Array.Empty<DisplayInfo>());

            Assert.False(session.IsRecording);

            var started = await session.StartRecordingAsync();
            Assert.True(started.Started, started.Error);
            Assert.True(session.IsRecording);

            await Task.Delay(TimeSpan.FromSeconds(3));

            var path = await session.StopRecordingAsync();
            Assert.False(session.IsRecording);
            Assert.NotNull(path);
            Assert.True(File.Exists(path));
            Assert.True(new FileInfo(path!).Length > 1024);

            var info = await MediaProbe.ProbeAsync(ffmpeg, path!);
            Assert.NotNull(info);
            Assert.Equal(320, info!.Width);
            Assert.Equal(180, info.Height);
            Assert.Equal("h264", info.VideoCodec);
            Assert.True(info.Duration > TimeSpan.FromSeconds(1), $"Duration was {info.Duration}");
            Assert.False(string.IsNullOrWhiteSpace(info.AudioCodec));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
