using System.IO;
using System.IO.Pipes;
using System.Threading.Channels;
using FifineCaptureStudio.Core;

namespace FifineCaptureStudio.Audio;

/// <summary>
/// Streams 16-bit stereo PCM into an FFmpeg input through a named pipe. This is
/// how the mixed audio from the C# engine reaches the encoder while video is read
/// from the capture device: one encoder process, correctly interleaved A/V.
/// </summary>
public sealed class PcmPipeSink : IDisposable
{
    private readonly Channel<byte[]> _queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(240)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });

    private NamedPipeServerStream? _server;
    private CancellationTokenSource? _cts;
    private Task? _writer;
    private Task? _accept;

    public PcmPipeSink(string name, int sampleRate, int channels)
    {
        Name = name;
        SampleRate = sampleRate;
        Channels = channels;
    }

    public string Name { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public string PipePath => @"\\.\pipe\" + Name;
    public bool Connected { get; private set; }
    public event Action? ConnectedChanged;

    public void Open()
    {
        Directory.CreateDirectory(AppPaths.Root);
        _cts = new CancellationTokenSource();
        _server = new NamedPipeServerStream(
            Name,
            PipeDirection.Out,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            1 << 20,
            1 << 20);

        var token = _cts.Token;
        var server = _server;
        _accept = Task.Run(async () =>
        {
            try
            {
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                Connected = true;
                Log.Info($"PCM pipe connected: {PipePath}");
                ConnectedChanged?.Invoke();

                // Feed a short silence pre-roll so the demuxer never sees an empty stream.
                var silence = new byte[(int)(SampleRate * Channels * 2 * 0.12)];
                await server.WriteAsync(silence, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
            catch (Exception ex)
            {
                Log.Warn("PCM pipe accept failed: " + ex.Message);
            }
        }, token);

        _writer = Task.Run(() => WriterLoopAsync(token));
    }

    private async Task WriterLoopAsync(CancellationToken token)
    {
        try
        {
            await foreach (var buffer in _queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                var server = _server;
                if (server is null || !server.IsConnected)
                {
                    continue;
                }

                try
                {
                    await server.WriteAsync(buffer, token).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    Connected = false;
                    ConnectedChanged?.Invoke();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
    }

    /// <summary>Queues interleaved 16-bit PCM produced by the mixer.</summary>
    public void Write(byte[] pcm16)
    {
        if (!Connected || _server is null)
        {
            return;
        }

        _queue.Writer.TryWrite(pcm16);
    }

    public void Close()
    {
        try { _queue.Writer.TryComplete(); } catch { }
        try { _cts?.Cancel(); } catch { }
        try { _server?.Dispose(); } catch { }
        _server = null;
        Connected = false;
        _cts?.Dispose();
        _cts = null;
    }

    public void Dispose() => Close();
}
