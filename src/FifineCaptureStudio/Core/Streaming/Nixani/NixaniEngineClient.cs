using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace FifineCaptureStudio.Core.Streaming.Nixani;

public sealed record NixaniHost(
    byte State,
    string Name,
    string Address,
    string HostId,
    string SystemVersion,
    string RunningApp,
    bool IsPs5,
    uint Target)
{
    public string StateLabel => State switch
    {
        1 => "On",
        2 => "Rest mode",
        _ => "Unknown state"
    };

    public override string ToString() => $"{Name} · {Address}";
}

public sealed record NixaniRegistration(
    bool Ok,
    string Error,
    string Nickname,
    byte[] Mac,
    uint Target,
    byte[] RegistKey,
    byte[] RpKey,
    uint RpKeyType,
    uint ConsolePin);

public sealed record NixaniSessionParams(
    string Host,
    bool IsPs5,
    byte[] RegistKey,
    byte[] RpKey,
    byte[] AccountId,
    int Resolution,
    int Fps,
    int BitrateKbps,
    byte Codec,
    bool DualSense,
    bool Internet = false,
    string PsnToken = "",
    byte[]? DeviceUid = null);

public readonly record struct NixaniPadState(
    uint Buttons,
    byte L2,
    byte R2,
    short LeftX,
    short LeftY,
    short RightX,
    short RightY)
{
    public static NixaniPadState Idle { get; } = new(0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Drives one <c>nixani-engine.exe</c> process over its standard input/output protocol (v3):
/// discovery, linking with the console's 8-digit code, session start, video/audio reception,
/// controller state and statistics. The engine decodes nothing, so every frame and sample
/// reported here is raw backend output.
/// </summary>
public sealed class NixaniEngineClient : IAsyncDisposable
{
    private const string EngineExeName = "nixani-engine.exe";

    /// <summary>The console shows 8 digits; the engine takes them as a number.</summary>
    public static bool IsLinkCode(string? code, out uint value)
    {
        value = 0;
        return code is { Length: 8 } && code.All(char.IsDigit) && uint.TryParse(code, out value);
    }

    private readonly string _exePath;
    private readonly string[] _arguments;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly List<NixaniHost> _hosts = new();
    private readonly object _hostLock = new();

    private Process? _process;
    private Stream? _input;
    private Task? _readerTask;
    private Task? _stderrTask;
    private CancellationTokenSource? _cts;

    private TaskCompletionSource<bool>? _hello;
    private TaskCompletionSource<NixaniRegistration>? _registration;
    private TaskCompletionSource<bool>? _stopped;
    private TaskCompletionSource<bool>? _streaming;

    public NixaniEngineClient(string exePath, IEnumerable<string>? arguments = null)
    {
        _exePath = exePath;
        _arguments = arguments?.ToArray() ?? Array.Empty<string>();
    }

    public string ExePath => _exePath;
    public bool IsRunning => _process is { HasExited: false };
    public string? EngineVersion { get; private set; }
    public ConsoleStreamStats Stats { get; } = new();

    public event Action<IReadOnlyList<NixaniHost>>? HostsChanged;
    public event Action<ConsoleStreamState, string, bool>? StateChanged;
    public event Action<CompressedVideoFrame>? VideoFrame;
    public event Action<ConsoleAudioPacket>? Audio;
    public event Action<NixaniHost>? ConsoleWoke;
    public event Action<string>? RegistrationProgress;
    public event Action<string>? LogMessage;
    public event Action<bool>? LoginPinRequested;
    public event Action<bool>? CantDisplayChanged;

    // ------------------------------------------------------------------ discovery

    public static string? LocateEngine(string? configuredPath)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            candidates.Add(configuredPath.Trim());
        }

        var env = Environment.GetEnvironmentVariable("FIFINE_NIXANI_ENGINE");
        if (!string.IsNullOrWhiteSpace(env))
        {
            candidates.Add(env);
        }

        candidates.Add(Path.Combine(AppPaths.AppDir, "streaming", EngineExeName));
        candidates.Add(Path.Combine(AppPaths.AppDir, EngineExeName));
        candidates.Add(Path.Combine(AppPaths.Root, "streaming", EngineExeName));

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            candidates.Add(Path.Combine(dir, EngineExeName));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    // ------------------------------------------------------------------ lifecycle

    public async Task StartAsync(CancellationToken token)
    {
        if (IsRunning)
        {
            return;
        }

        if (!File.Exists(_exePath))
        {
            throw new FileNotFoundException("nixani-engine.exe was not found.", _exePath);
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        var startInfo = new ProcessStartInfo(_exePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_exePath) ?? AppPaths.Root
        };

        foreach (var argument in _arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.Exited += (_, _) => OnProcessExited();
        _process.Start();
        _input = _process.StandardInput.BaseStream;
        _hello = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var token2 = _cts.Token;
        _readerTask = Task.Run(() => ReadLoopAsync(token2), token2);
        _stderrTask = Task.Run(() => ReadStderrAsync(token2), token2);

        await WriteAsync(new NxeWriter(NxeMessage.Hello).U32(NxeMessage.ProtocolVersion).ToArray(), token2)
            .ConfigureAwait(false);

        var hello = await WithTimeout(_hello.Task, TimeSpan.FromSeconds(10), token2).ConfigureAwait(false);
        if (!hello)
        {
            throw new TimeoutException("The Remote Play engine did not answer the protocol handshake.");
        }

        Log.Info($"Nixani engine ready: {EngineVersion ?? "unknown version"} ({_exePath})");
    }

    private void OnProcessExited()
    {
        var reason = "The Remote Play engine stopped.";
        _hello?.TrySetException(new InvalidOperationException(reason));
        _registration?.TrySetException(new InvalidOperationException(reason));
        _streaming?.TrySetException(new InvalidOperationException(reason));
        _stopped?.TrySetResult(true);
        StateChanged?.Invoke(ConsoleStreamState.Failed, reason, true);
    }

    // ------------------------------------------------------------------ commands

    public async Task<IReadOnlyList<NixaniHost>> DiscoverAsync(TimeSpan timeout, CancellationToken token)
    {
        lock (_hostLock)
        {
            _hosts.Clear();
        }

        await WriteAsync(new NxeWriter(NxeMessage.DiscoveryStart).ToArray(), token).ConfigureAwait(false);
        try
        {
            await Task.Delay(timeout, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // discovery window cancelled: return what arrived
        }

        try
        {
            await WriteAsync(new NxeWriter(NxeMessage.DiscoveryStop).ToArray(), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // engine may have exited
        }

        lock (_hostLock)
        {
            return _hosts.ToArray();
        }
    }

    public async Task WakeAsync(NixaniHost host, byte[] registKey, CancellationToken token)
    {
        var writer = new NxeWriter(NxeMessage.Wake).Str(host.Address).Bytes(Pad16(registKey)).U8(host.IsPs5 ? (byte)1 : (byte)0);
        await WriteAsync(writer.ToArray(), token).ConfigureAwait(false);
    }

    /// <summary>Links with the console using the 8-digit code the console is displaying.</summary>
    public async Task<NixaniRegistration> RegisterAsync(
        string address,
        bool ps5,
        uint linkCode,
        uint consolePin,
        byte[] accountId,
        TimeSpan timeout,
        CancellationToken token)
    {
        _registration = new TaskCompletionSource<NixaniRegistration>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new NxeWriter(NxeMessage.RegistStart)
            .Str(address)
            .U32(ps5 ? 1u : 0u)
            .Bytes(Pad8(accountId))
            .U32(linkCode)
            .U32(consolePin);

        await WriteAsync(writer.ToArray(), token).ConfigureAwait(false);
        return await WithTimeout(_registration.Task, timeout, token).ConfigureAwait(false);
    }

    public async Task CancelRegistrationAsync()
    {
        if (_input is null)
        {
            return;
        }

        try
        {
            await WriteAsync(new NxeWriter(NxeMessage.RegistCancel).ToArray(), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            // engine gone
        }
    }

    public async Task StartSessionAsync(NixaniSessionParams session, CancellationToken token)
    {
        _streaming = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var writer = new NxeWriter(NxeMessage.SessionStart)
            .Str(session.Host)
            .U8(session.IsPs5 ? (byte)1 : (byte)0)
            .Bytes(Pad16(session.RegistKey))
            .Bytes(Pad16(session.RpKey))
            .Bytes(Pad8(session.AccountId))
            .U32((uint)session.Resolution)
            .U32((uint)session.Fps)
            .U32((uint)Math.Max(0, session.BitrateKbps))
            .U8(session.Codec)
            .U8(session.DualSense ? (byte)1 : (byte)0)
            .U8(session.Internet ? (byte)1 : (byte)0)
            .Str(session.PsnToken)
            .Bytes(Pad32(session.DeviceUid));

        await WriteAsync(writer.ToArray(), token).ConfigureAwait(false);
    }

    public async Task<bool> StopSessionAsync(TimeSpan timeout)
    {
        if (_input is null)
        {
            return true;
        }

        var stopped = _stopped;
        try
        {
            await WriteAsync(new NxeWriter(NxeMessage.SessionStop).ToArray(), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch
        {
            return true;
        }

        if (stopped is null)
        {
            return true;
        }

        try
        {
            return await WithTimeout(stopped.Task, timeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }

    public void SendPad(NixaniPadState pad)
    {
        if (_input is null)
        {
            return;
        }

        var writer = new NxeWriter(NxeMessage.Pad)
            .U32(pad.Buttons)
            .U8(pad.L2)
            .U8(pad.R2)
            .I16(pad.LeftX)
            .I16(pad.LeftY)
            .I16(pad.RightX)
            .I16(pad.RightY)
            .Bytes(new byte[] { 0xFF, 0, 0, 0, 0, 0, 0xFF, 0, 0, 0, 0, 0 })
            .U8(0)
            .F32(0)
            .F32(0)
            .F32(0)
            .F32(0)
            .F32(0)
            .F32(0)
            .U64(0);

        _ = WriteAsync(writer.ToArray(), CancellationToken.None);
    }

    public void SendLoginPin(string pin)
    {
        if (_input is null)
        {
            return;
        }

        _ = WriteAsync(new NxeWriter(NxeMessage.LoginPin).Str(pin).ToArray(), CancellationToken.None);
    }

    public void RequestKeyframe()
    {
        if (_input is null)
        {
            return;
        }

        Stats.KeyframeRequests++;
        _ = WriteAsync(new NxeWriter(NxeMessage.RequestKeyframe).ToArray(), CancellationToken.None);
    }

    // ------------------------------------------------------------------ writing

    private async Task WriteAsync(byte[] frame, CancellationToken token)
    {
        var stream = _input;
        if (stream is null)
        {
            return;
        }

        await _writeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ------------------------------------------------------------------ reading

    private async Task ReadLoopAsync(CancellationToken token)
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        var stream = process.StandardOutput.BaseStream;
        var header = new byte[4];
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (!await ReadExactAsync(stream, header, 4, token).ConfigureAwait(false))
                {
                    break;
                }

                var size = BinaryPrimitives.ReadUInt32LittleEndian(header);
                if (size < 2 || size > NxeMessage.MaxFrameBytes)
                {
                    throw new InvalidDataException($"Engine frame size {size} is out of range.");
                }

                var body = new byte[size];
                if (!await ReadExactAsync(stream, body, (int)size, token).ConfigureAwait(false))
                {
                    break;
                }

                var type = BinaryPrimitives.ReadUInt16LittleEndian(body);
                Dispatch(type, body.AsSpan(2));
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // engine closed its output
        }
        catch (Exception ex)
        {
            Log.Error("Engine reader failed", ex);
            StateChanged?.Invoke(ConsoleStreamState.Failed, "Engine protocol error: " + ex.Message, true);
        }
    }

    private void Dispatch(ushort type, ReadOnlySpan<byte> payload)
    {
        var reader = new NxeReader(payload);
        switch (type)
        {
            case NxeMessage.HelloReply:
            {
                var version = reader.U32();
                EngineVersion = reader.Str();
                Stats.BackendVersion = EngineVersion;
                Log.Debug($"Engine protocol {version}, build {EngineVersion}");
                _hello?.TrySetResult(true);
                break;
            }

            case NxeMessage.Log:
            {
                var level = reader.U8();
                var text = reader.Str();
                LogMessage?.Invoke($"[engine:{level}] {text}");
                if (level <= 1)
                {
                    Log.Warn("Engine: " + text);
                }
                else
                {
                    Log.Debug("Engine: " + text);
                }

                break;
            }

            case NxeMessage.Hosts:
            {
                var count = reader.U32();
                var list = new List<NixaniHost>((int)Math.Min(count, 64));
                for (var i = 0; i < count; i++)
                {
                    var state = reader.U8();
                    var name = reader.Str();
                    var address = reader.Str();
                    var hostId = reader.Str();
                    var systemVersion = reader.Str();
                    var runningApp = reader.Str();
                    var ps5 = reader.U8() != 0;
                    var target = reader.U32();
                    list.Add(new NixaniHost(state, name, address, hostId, systemVersion, runningApp, ps5, target));
                }

                lock (_hostLock)
                {
                    _hosts.Clear();
                    _hosts.AddRange(list);
                    HostsChanged?.Invoke(_hosts.ToArray());
                }

                break;
            }

            case NxeMessage.RegistResult:
            {
                var ok = reader.U8() != 0;
                var error = reader.Str();
                NixaniRegistration registration;
                if (ok)
                {
                    registration = new NixaniRegistration(
                        true,
                        string.Empty,
                        reader.Str(),
                        reader.Bytes(6),
                        reader.U32(),
                        reader.Bytes(16),
                        reader.Bytes(16),
                        reader.U32(),
                        reader.U32());
                }
                else
                {
                    registration = new NixaniRegistration(false, error, string.Empty, Array.Empty<byte>(), 0,
                        Array.Empty<byte>(), Array.Empty<byte>(), 0, 0);
                }

                _registration?.TrySetResult(registration);
                break;
            }

            case NxeMessage.RegistProgress:
                RegistrationProgress?.Invoke(reader.Str());
                break;

            case NxeMessage.SessionState:
            {
                var state = reader.U8();
                var isError = reader.U8() != 0;
                var text = reader.Str();
                var mapped = state switch
                {
                    1 => ConsoleStreamState.Connecting,
                    2 => ConsoleStreamState.Streaming,
                    3 => ConsoleStreamState.Stopped,
                    _ => ConsoleStreamState.Connecting
                };

                if (mapped == ConsoleStreamState.Streaming)
                {
                    _streaming?.TrySetResult(true);
                }

                if (mapped == ConsoleStreamState.Stopped)
                {
                    _stopped?.TrySetResult(true);
                }

                StateChanged?.Invoke(mapped, text, isError || mapped == ConsoleStreamState.Failed);
                break;
            }

            case NxeMessage.Video:
            {
                var engineTime = reader.U64();
                var lost = reader.I32();
                var recovered = reader.U8() != 0;
                var data = reader.Blob();
                Stats.FramesReceived++;
                Stats.LastPacketUtc = DateTime.UtcNow;
                VideoFrame?.Invoke(new CompressedVideoFrame(data, (long)engineTime, lost, recovered));
                break;
            }

            case NxeMessage.AudioFormat:
            {
                Stats.AudioChannels = (int)reader.U32();
                Stats.AudioSampleRate = (int)reader.U32();
                Log.Info($"Console audio: {Stats.AudioChannels} ch @ {Stats.AudioSampleRate} Hz");
                break;
            }

            case NxeMessage.Audio:
            {
                var pcm = reader.Blob();
                Stats.AudioSamplesReceived += pcm.Length / 4;
                Stats.LastPacketUtc = DateTime.UtcNow;
                Audio?.Invoke(new ConsoleAudioPacket(pcm, Math.Max(8000, Stats.AudioSampleRate),
                    Math.Max(1, Stats.AudioChannels)));
                break;
            }

            case NxeMessage.Stats:
            {
                Stats.PacketsReceived = (long)reader.U64();
                Stats.PacketsLost = (long)reader.U64();
                Stats.RttMs = reader.U32() / 1000.0;
                Stats.FecFailures = (long)reader.U64();
                Stats.KeyframeRequests = (long)reader.U64();
                break;
            }

            case NxeMessage.SessionStopped:
                _stopped?.TrySetResult(true);
                break;

            case NxeMessage.LoginPinRequest:
                LoginPinRequested?.Invoke(reader.U8() != 0);
                break;

            case NxeMessage.CantDisplay:
                CantDisplayChanged?.Invoke(reader.U8() != 0);
                break;

            case NxeMessage.Nickname:
                reader.Str();
                break;

            default:
                Log.Debug($"Unhandled engine message {type}");
                break;
        }
    }

    private async Task ReadStderrAsync(CancellationToken token)
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            while (!token.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Length > 0)
                {
                    Log.Debug("engine stderr: " + line);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Debug("engine stderr reader ended: " + ex.Message);
        }
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken token)
    {
        var offset = 0;
        while (offset < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), token).ConfigureAwait(false);
            if (read <= 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, TimeSpan timeout, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        try
        {
            return await task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException($"The engine did not answer within {timeout.TotalSeconds:0} s.");
        }
    }

    private static byte[] Pad16(byte[]? value) => Pad(value, 16);
    private static byte[] Pad8(byte[]? value) => Pad(value, 8);
    private static byte[] Pad32(byte[]? value) => Pad(value, 32);

    private static byte[] Pad(byte[]? value, int length)
    {
        var result = new byte[length];
        if (value is not null)
        {
            Array.Copy(value, result, Math.Min(value.Length, length));
        }

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(true);
                _process.WaitForExit(2000);
            }
        }
        catch
        {
            // already gone
        }

        try { _input?.Dispose(); } catch { }
        try { _process?.Dispose(); } catch { }

        if (_readerTask is not null)
        {
            try { await _readerTask.ConfigureAwait(false); } catch { }
        }

        if (_stderrTask is not null)
        {
            try { await _stderrTask.ConfigureAwait(false); } catch { }
        }

        _cts?.Dispose();
        _writeGate.Dispose();
        _process = null;
        _input = null;
    }
}
