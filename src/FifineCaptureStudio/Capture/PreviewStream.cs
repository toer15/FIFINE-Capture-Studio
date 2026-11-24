using System.Diagnostics;
using System.IO;
using System.Windows.Media.Imaging;
using FifineCaptureStudio.Core;

namespace FifineCaptureStudio.Capture;

public sealed record PreviewFrame(BitmapSource Image, long DecodedAtTicks, long Sequence, double DecodeMs);

/// <summary>
/// Splits the MJPEG stream FFmpeg writes to stdout into JPEG frames and decodes
/// them into frozen WPF bitmaps. Reading and decoding run on separate threads:
/// the reader always drains the pipe (so FFmpeg is never blocked by a slow
/// preview) and only the newest complete frame is passed to the decoder.
/// </summary>
internal sealed class PreviewStream : IDisposable
{
    private const int MaxFrameBytes = 24 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly Thread _readerThread;
    private readonly Thread _decodeThread;
    private readonly object _frameLock = new();
    private readonly AutoResetEvent _frameReady = new(false);
    private readonly byte[] _latestFrame = new byte[MaxFrameBytes];
    private readonly byte[] _decodeBuffer = new byte[MaxFrameBytes];

    private int _latestLength;
    private bool _hasFrame;
    private volatile bool _stop;
    private long _sequence;

    public PreviewStream(Stream stream)
    {
        _stream = stream;
        _readerThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "FIFINE.PreviewReader",
            Priority = ThreadPriority.AboveNormal
        };
        _decodeThread = new Thread(DecodeLoop)
        {
            IsBackground = true,
            Name = "FIFINE.PreviewDecoder",
            Priority = ThreadPriority.Normal
        };
    }

    public event Action<PreviewFrame>? FrameDecoded;
    public long FramesDecoded { get; private set; }
    public long FramesSkipped { get; private set; }
    public double LastDecodeMs { get; private set; }
    public string? LastError { get; private set; }

    public void Start()
    {
        _decodeThread.Start();
        _readerThread.Start();
    }

    private void ReadLoop()
    {
        var readBuffer = new byte[512 * 1024];
        var pending = new byte[MaxFrameBytes];
        var pendingLength = 0;

        while (!_stop)
        {
            int read;
            try
            {
                read = _stream.Read(readBuffer, 0, readBuffer.Length);
            }
            catch (Exception ex)
            {
                if (!_stop)
                {
                    LastError = ex.Message;
                    Log.Warn("Preview stream ended: " + ex.Message);
                }

                break;
            }

            if (read <= 0)
            {
                break;
            }

            if (pendingLength + read > pending.Length)
            {
                // Extremely large frame or mis-sync: drop the backlog and resync on the next SOI.
                pendingLength = 0;
            }

            Buffer.BlockCopy(readBuffer, 0, pending, pendingLength, read);
            pendingLength += read;

            var consumed = 0;
            while (!_stop)
            {
                var start = FindMarker(pending, consumed, pendingLength, 0xD8);
                if (start < 0)
                {
                    consumed = Math.Max(consumed, pendingLength - 1);
                    break;
                }

                var end = FindMarker(pending, start + 2, pendingLength, 0xD9);
                if (end < 0)
                {
                    consumed = start;
                    break;
                }

                Publish(pending, start, end + 2 - start);
                consumed = end + 2;
            }

            if (consumed > 0)
            {
                var remaining = pendingLength - consumed;
                if (remaining > 0)
                {
                    Buffer.BlockCopy(pending, consumed, pending, 0, remaining);
                }

                pendingLength = remaining;
            }
        }

        _frameReady.Set();
    }

    /// <summary>Hands the newest frame to the decoder, replacing any older undecoded one.</summary>
    private void Publish(byte[] buffer, int offset, int length)
    {
        if (length <= 0 || length > MaxFrameBytes)
        {
            return;
        }

        lock (_frameLock)
        {
            if (_hasFrame)
            {
                FramesSkipped++;
            }

            Buffer.BlockCopy(buffer, offset, _latestFrame, 0, length);
            _latestLength = length;
            _hasFrame = true;
        }

        _frameReady.Set();
    }

    private void DecodeLoop()
    {
        while (!_stop)
        {
            _frameReady.WaitOne(200);

            int length;
            lock (_frameLock)
            {
                if (!_hasFrame)
                {
                    if (_stop)
                    {
                        return;
                    }

                    continue;
                }

                length = _latestLength;
                Buffer.BlockCopy(_latestFrame, 0, _decodeBuffer, 0, length);
                _hasFrame = false;
            }

            Decode(_decodeBuffer, 0, length);
        }
    }

    /// <summary>Finds 0xFF followed by the requested marker byte.</summary>
    private static int FindMarker(byte[] buffer, int from, int length, byte marker)
    {
        for (var i = Math.Max(0, from); i < length - 1; i++)
        {
            if (buffer[i] == 0xFF && buffer[i + 1] == marker)
            {
                return i;
            }
        }

        return -1;
    }

    private void Decode(byte[] buffer, int offset, int length)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var ms = new MemoryStream(buffer, offset, length, writable: false);
            var decoder = new JpegBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0)
            {
                return;
            }

            var frame = decoder.Frames[0];
            frame.Freeze();

            var decodeMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            LastDecodeMs = decodeMs;
            FramesDecoded++;
            var seq = Interlocked.Increment(ref _sequence);
            FrameDecoded?.Invoke(new PreviewFrame(frame, Stopwatch.GetTimestamp(), seq, decodeMs));
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Debug("JPEG decode failed: " + ex.Message);
        }
    }

    public void Stop()
    {
        _stop = true;
        _frameReady.Set();
        try
        {
            if (_readerThread.IsAlive)
            {
                _readerThread.Join(600);
            }

            if (_decodeThread.IsAlive)
            {
                _decodeThread.Join(600);
            }
        }
        catch
        {
            // ignore
        }
    }

    public void Dispose()
    {
        Stop();
        _frameReady.Dispose();
    }
}
