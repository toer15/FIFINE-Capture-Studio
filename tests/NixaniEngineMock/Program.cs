using System.Buffers.Binary;
using System.Diagnostics;
using FifineCaptureStudio.Core.Streaming.Nixani;

// A stand-in for nixani-engine.exe: it speaks protocol v3 over stdin/stdout, registers with any
// code, then streams a real H.264 access-unit file at a fixed frame rate with PCM audio and
// statistics. Used to test FIFINE's client, transport and pipeline without a console.

var videoPath = args.FirstOrDefault();
var loops = args.Length > 1 && int.TryParse(args[1], out var parsedLoops) ? parsedLoops : 3;

using var input = Console.OpenStandardInput();
using var output = Console.OpenStandardOutput();
var writeLock = new object();

void Send(NxeWriter writer)
{
    var bytes = writer.ToArray();
    lock (writeLock)
    {
        output.Write(bytes, 0, bytes.Length);
        output.Flush();
    }
}

byte[]? ReadFrame()
{
    var header = new byte[4];
    if (!ReadExact(input, header, 4))
    {
        return null;
    }

    var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
    var body = new byte[size];
    return ReadExact(input, body, (int)size) ? body : null;
}

static bool ReadExact(Stream stream, byte[] buffer, int count)
{
    var offset = 0;
    while (offset < count)
    {
        var read = stream.Read(buffer, offset, count - offset);
        if (read <= 0)
        {
            return false;
        }

        offset += read;
    }

    return true;
}

var accessUnits = videoPath is not null && File.Exists(videoPath)
    ? SplitAccessUnits(File.ReadAllBytes(videoPath))
    : new List<byte[]>();

var logPath = Path.Combine(Path.GetTempPath(), "nixani-mock.log");
void Log(string message)
{
    try
    {
        File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
    }
    catch
    {
        // diagnostics only
    }
}

Log($"start args=[{string.Join("|", args)}] accessUnits={accessUnits.Count}");

using var cts = new CancellationTokenSource();
var padMessages = 0;
var running = true;

// The session worker: starts only after SessionStart, ends on SessionStop.
Thread? sessionThread = null;
CancellationTokenSource? sessionCts = null;

while (running)
{
    var body = ReadFrame();
    if (body is null)
    {
        break;
    }

    var type = BinaryPrimitives.ReadUInt16LittleEndian(body);
    var reader = new NxeReader(body.AsSpan(2));

    switch (type)
    {
        case NxeMessage.Hello:
            reader.U32();
            Send(new NxeWriter(NxeMessage.HelloReply).U32(NxeMessage.ProtocolVersion).Str("mock-1.0.0"));
            break;

        case NxeMessage.DiscoveryStart:
            Send(new NxeWriter(NxeMessage.Hosts)
                .U32(1)
                .U8(1)
                .Str("Mock PlayStation 5")
                .Str("127.0.0.1")
                .Str("mock-host-id")
                .Str("08.60")
                .Str("Mock Game")
                .U8(1)
                .U32(1_000_100u));
            break;

        case NxeMessage.RegistStart:
        {
            var address = reader.Str();
            var target = reader.U32();
            if (target != 1_000_100u)
            {
                Send(new NxeWriter(NxeMessage.RegistResult).U8(0).Str("Wrong console target."));
                break;
            }
            reader.Bytes(8);
            var linkCode = reader.U32();
            var pin = reader.U32();
            var keys = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
            if (linkCode == 99999999)
            {
                Send(new NxeWriter(NxeMessage.RegistResult).U8(0).Str("The registration code has expired."));
            }
            else
            {
                Send(new NxeWriter(NxeMessage.RegistResult)
                    .U8(1)
                    .Str(string.Empty)
                    .Str("Mock PlayStation 5")
                    .Bytes(new byte[] { 1, 2, 3, 4, 5, 6 })
                    .U32(0)
                    .Bytes(keys)
                    .Bytes(keys.Reverse().ToArray())
                    .U32(0)
                    .U32(pin));
            }

            _ = address;
            break;
        }

        case NxeMessage.SessionStart:
        {
            reader.Str();
            var ps5 = reader.U8();
            reader.Bytes(16);
            reader.Bytes(16);
            reader.Bytes(8);
            var resolution = reader.U32();
            var fps = reader.U32();
            reader.U32();
            var codec = reader.U8();
            reader.U8();
            reader.U8();
            reader.Str();
            reader.Bytes(32);
            _ = (ps5, resolution, fps, codec);

            Log("SessionStart received");
            sessionCts?.Cancel();
            sessionCts = new CancellationTokenSource();
            var token = sessionCts.Token;
            sessionThread = new Thread(() => RunSession(token))
            {
                IsBackground = true,
                Name = "mock-session"
            };
            sessionThread.Start();
            break;
        }

        case NxeMessage.SessionStop:
            sessionCts?.Cancel();
            sessionThread?.Join(2000);
            Send(new NxeWriter(NxeMessage.SessionStopped));
            break;

        case NxeMessage.Pad:
            padMessages++;
            break;

        case NxeMessage.RequestKeyframe:
            break;
    }
}

sessionCts?.Cancel();
sessionThread?.Join(2000);
return 0;

void RunSession(CancellationToken token)
{
    var stop = new CancellationTokenSource();
    using var registration = token.Register(() => stop.Cancel());
    Send(new NxeWriter(NxeMessage.SessionState).U8(1).U8(0).Str("connecting"));
    Thread.Sleep(250);
    Send(new NxeWriter(NxeMessage.SessionState).U8(2).U8(0).Str("streaming"));
    Send(new NxeWriter(NxeMessage.AudioFormat).U32(2).U32(48000));

    try
    {
        if (accessUnits.Count == 0)
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        var sentFrames = 0L;
        var sentAudio = 0L;
        var nextStats = TimeSpan.Zero;
        var loop = 0;

        while (!stop.IsCancellationRequested && loop < loops)
        {
            for (var i = 0; i < accessUnits.Count && !stop.IsCancellationRequested; i++)
            {
                var due = TimeSpan.FromSeconds(sentFrames / 60.0);
                var wait = due - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    stop.Token.WaitHandle.WaitOne(wait);
                }

                Send(new NxeWriter(NxeMessage.Video)
                    .U64((ulong)clock.ElapsedTicks)
                    .I32(0)
                    .U8(0)
                    .Bytes(accessUnits[i]));
                sentFrames++;

                if (sentFrames % 6 == 0)
                {
                    var silence = new byte[480 * 2 * 2];
                    Send(new NxeWriter(NxeMessage.Audio).Bytes(silence));
                    sentAudio++;
                }

                if (clock.Elapsed >= nextStats)
                {
                    nextStats = clock.Elapsed + TimeSpan.FromMilliseconds(500);
                    Send(new NxeWriter(NxeMessage.Stats)
                        .U64((ulong)sentFrames)
                        .U64(0)
                        .U32(1500)
                        .U64(0)
                        .U64(2));
                }
            }

            loop++;
        }

        _ = sentAudio;
        Send(new NxeWriter(NxeMessage.SessionState).U8(3).U8(0).Str("stream ended"));
    }
    catch (Exception ex)
    {
        Log("session exception: " + ex);
    }

    Log("session finished");
}

static List<byte[]> SplitAccessUnits(byte[] annexB)
{
    var units = new List<byte[]>();
    var starts = new List<int>();
    for (var i = 0; i + 3 < annexB.Length; i++)
    {
        if (annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1)
        {
            starts.Add(i);
            i += 2;
        }
    }

    if (starts.Count == 0)
    {
        return units;
    }

    // With aud=1 every access unit begins with an access-unit delimiter (NAL type 9).
    var boundaries = new List<int>();
    for (var index = 0; index < starts.Count; index++)
    {
        var nalType = annexB[starts[index] + 3] & 0x1F;
        if (nalType == 9)
        {
            boundaries.Add(starts[index]);
        }
    }

    if (boundaries.Count == 0)
    {
        boundaries.Add(starts[0]);
        boundaries.Add(annexB.Length);
    }
    else
    {
        boundaries.Add(annexB.Length);
    }

    for (var i = 0; i + 1 < boundaries.Count; i++)
    {
        var length = boundaries[i + 1] - boundaries[i];
        if (length <= 0)
        {
            continue;
        }

        var unit = new byte[length];
        Buffer.BlockCopy(annexB, boundaries[i], unit, 0, length);
        units.Add(unit);
    }

    return units;
}
