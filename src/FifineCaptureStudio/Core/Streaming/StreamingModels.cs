using System.Text.Json.Serialization;

namespace FifineCaptureStudio.Core.Streaming;

/// <summary>Lifecycle of a console stream, as reported by the backend.</summary>
public enum ConsoleStreamState
{
    Idle,
    EngineMissing,
    Starting,
    Discovering,
    Registering,
    Connecting,
    Streaming,
    Stopped,
    Failed
}

/// <summary>Which console backend a stream comes from.</summary>
public enum ConsoleBackend
{
    PlayStation,
    Xbox
}

/// <summary>
/// One frame of compressed video as delivered by a console backend. The bytes are a single
/// complete access unit in Annex B form (H.264 or HEVC), exactly what FFmpeg decodes.
/// </summary>
public sealed record CompressedVideoFrame(
    byte[] Data,
    long TimestampTicks,
    int PacketsLost,
    bool Recovered);

/// <summary>PCM audio delivered by a console backend (interleaved signed 16-bit).</summary>
public sealed record ConsoleAudioPacket(byte[] Pcm16, int SampleRate, int Channels);

/// <summary>
/// Real counters reported by the streaming backend. Nothing here is synthesised: fields stay
/// at zero until the backend has actually reported them.
/// </summary>
public sealed class ConsoleStreamStats
{
    public long PacketsReceived { get; set; }
    public long PacketsLost { get; set; }
    public long FecFailures { get; set; }
    public long KeyframeRequests { get; set; }
    public double RttMs { get; set; }
    public long FramesReceived { get; set; }
    public long FramesWrittenToPipeline { get; set; }
    public long FramesDroppedByPipeline { get; set; }
    public long AudioSamplesReceived { get; set; }
    public int AudioChannels { get; set; }
    public int AudioSampleRate { get; set; }
    public string? BackendVersion { get; set; }
    public DateTime LastPacketUtc { get; set; }
}

/// <summary>Quality settings a console backend is actually asked for.</summary>
public sealed class ConsoleStreamOptions
{
    public int Width { get; init; } = 1920;
    public int Height { get; init; } = 1080;
    public int Fps { get; init; } = 60;
    public int BitrateKbps { get; init; }
    public bool Hevc { get; init; }
    public bool DualSense { get; init; }

    /// <summary>Resolution as the backend names it (360/540/720/1080).</summary>
    [JsonIgnore]
    public int BackendResolution => Height switch
    {
        <= 360 => 360,
        <= 540 => 540,
        <= 720 => 720,
        _ => 1080
    };

    [JsonIgnore]
    public int BackendFps => Fps >= 60 ? 60 : 30;

    [JsonIgnore]
    public string CodecName => Hevc ? "HEVC" : "H.264";
}
