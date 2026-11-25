using System.IO;
using System.IO.Pipes;
using System.Threading.Channels;

namespace FifineCaptureStudio.Core.Streaming;

/// <summary>
/// Publishes compressed console frames to FFmpeg through a Windows named pipe. FFmpeg reads the
/// pipe as an ordinary input file, so the existing preview/recording pipeline is reused unchanged.
/// The pipe is re-accepted after every FFmpeg restart (quality change, recording start), and a
/// keyframe is requested whenever frames had to be dropped so the decoder can recover.
/// </summary>
public sealed class VideoPipeFeed : IAsyncDisposable
{
    private readonly string _name;
    private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(24)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });

    private CancellationTokenSource? _cts;
    private Task? _acceptTask;
    private readonly TaskCompletionSource _serverReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public VideoPipeFeed()
    {
        _name = "fifine-console-" + Guid.NewGuid().ToString("N")[..10];
    }

    public string PipePath => @"\\.\pipe\" + _name;
    public bool Connected { get; private set; }
    public long FramesQueued { get; private set; }
    public long FramesDropped { get; private set; }
    public bool HasConsumer => Connected;
    public Task ServerReady => _serverReady.Task;

    /// <summary>Raised when a frame could not be delivered, so the backend should send a keyframe.</summary>
    public event Action? FramesLost;

    public void Open()
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _acceptTask = Task.Run(() => AcceptLoopAsync(token), token);
    }

    /// <summary>Queues one Annex B frame. Returns false when it had to be dropped.</summary>
    public bool Push(byte[] frame)
    {
        if (!Connected)
        {
            FramesDropped++;
            FramesLost?.Invoke();
            return false;
        }

        if (_frames.Writer.TryWrite(frame))
        {
            FramesQueued++;
            return true;
        }

        FramesDropped++;
        FramesLost?.Invoke();
        return false;
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    _name,
                    PipeDirection.Out,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    1 << 20,
                    1 << 20);

                _serverReady.TrySetResult();
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);
                Connected = true;

                // Anything buffered before FFmpeg attached starts mid-GOP; ask for a fresh keyframe.
                while (_frames.Reader.TryRead(out _))
                {
                    // drain stale frames
                }

                FramesLost?.Invoke();

                while (!token.IsCancellationRequested && server.IsConnected)
                {
                    var frame = await _frames.Reader.ReadAsync(token).ConfigureAwait(false);
                    await server.WriteAsync(frame, token).ConfigureAwait(false);
                    await server.FlushAsync(token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // FFmpeg closed the pipe (restart) — accept the next connection.
            }
            catch (Exception ex)
            {
                Log.Warn("Console video pipe failed: " + ex.Message);
                await Task.Delay(250, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                Connected = false;
                try { server?.Dispose(); } catch { }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts?.Cancel(); } catch { }
        _frames.Writer.TryComplete();
        if (_acceptTask is not null)
        {
            try { await _acceptTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }

        _cts?.Dispose();
        Connected = false;
    }
}
