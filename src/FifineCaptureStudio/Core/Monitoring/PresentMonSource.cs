using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace FifineCaptureStudio.Core.Monitoring;

public sealed record PcFpsReading(
    bool Available,
    double Fps,
    double PresentedFps,
    double DisplayedFps,
    double FrameTimeMs,
    long DroppedFrames,
    string SwapChain,
    int SwapChainCount,
    string Detail);

/// <summary>
/// One persistent PresentMon console capture per selected game. PresentMon consumes the Windows ETW
/// present events; it never injects into the game. Rows are parsed as they arrive and grouped by swap
/// chain, so an overlay or launcher chain cannot be mistaken for the game's main chain.
/// </summary>
public sealed class PresentMonSource : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ChainBuffer> _chains = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _errors = new();
    private Process? _process;
    private string _status = "Not started";
    private long _lastRowTicks;
    private long _lastStartTicks;
    private int _processId;
    private string? _sessionName;

    private sealed class ChainBuffer
    {
        public readonly Queue<Row> Rows = new();
        public long Dropped;
    }

    private readonly record struct Row(double Time, double PresentMs, double DisplayMs, bool Dropped);

    public bool IsRunning => _process is { HasExited: false };
    public string Status { get { lock (_gate) return _status; } }

    public (bool Ok, string Message) Start(int processId, string processName)
    {
        Stop();
        var exe = PresentMonTool.ResolvePath();
        if (exe is null)
        {
            lock (_gate) _status = "PresentMon is not installed.";
            return (false, _status);
        }

        _processId = processId;
        var session = "FIFINE_FPS_" + processId + "_" + Guid.NewGuid().ToString("N")[..6];
        _sessionName = session;
        var start = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.Unicode,
            StandardErrorEncoding = System.Text.Encoding.Unicode
        };
        start.ArgumentList.Add("--process_id");
        start.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--output_stdout");
        start.ArgumentList.Add("--no_console_stats");
        start.ArgumentList.Add("--v1_metrics");
        start.ArgumentList.Add("--stop_existing_session");
        start.ArgumentList.Add("--terminate_on_proc_exit");
        start.ArgumentList.Add("--session_name");
        start.ArgumentList.Add(session);

        try
        {
            lock (_gate)
            {
                _chains.Clear();
                _errors.Clear();
                _header = null;
                _lastRowTicks = 0;
                _lastStartTicks = Stopwatch.GetTimestamp();
                _status = $"Starting PresentMon for {processName} (PID {processId})…";
                _sessionName = session;
            }

            _process = new Process { StartInfo = start, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, e) => OnOutput(e.Data);
            _process.ErrorDataReceived += (_, e) => OnError(e.Data);
            _process.Exited += (_, _) => OnExited();
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            return (true, _status);
        }
        catch (Exception ex)
        {
            lock (_gate) _status = "PresentMon could not start: " + ex.Message;
            return (false, _status);
        }
    }

    public void Stop()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            _process = null;
        }

        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
        }
        catch
        {
            // The ETW session is named and the next start uses --stop_existing_session.
        }
        finally
        {
            process.Dispose();
        }

        // Killing PresentMon does not remove its real-time ETW session. Leaving sessions behind
        // makes later captures lose present events, so stop the named session explicitly.
        var session = _sessionName;
        _sessionName = null;
        if (!string.IsNullOrWhiteSpace(session))
        {
            try
            {
                var logman = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "logman.exe"),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                logman.ArgumentList.Add("stop");
                logman.ArgumentList.Add(session);
                logman.ArgumentList.Add("-ets");
                using var cleanup = Process.Start(logman);
                cleanup?.WaitForExit(2000);
            }
            catch
            {
                // The session may already be gone; the next unique session is unaffected.
            }
        }

        lock (_gate)
        {
            _status = "Stopped";
        }
    }

    private void OnExited()
    {
        lock (_gate)
        {
            if (_lastRowTicks == 0)
            {
                _status = _errors.Count > 0 ? _errors.Last() : "PresentMon exited without frame data.";
            }
            else
            {
                _status = "Target process exited — PC FPS unavailable.";
            }
        }
    }

    private void OnError(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var text = line.Trim();
        lock (_gate)
        {
            _errors.Enqueue(text);
            while (_errors.Count > 8) _errors.Dequeue();

            var lower = text.ToLowerInvariant();
            if (lower.Contains("access") || lower.Contains("privilege") || lower.Contains("permission"))
            {
                _status = "PresentMon needs ETW access. Add this Windows user to Performance Log Users " +
                          "or run FIFINE as administrator; FPS is unavailable until then.";
            }
        }
    }

    private void OnOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        var parsed = PresentMonCsv.TryParse(line, out var header, out var values);
        if (!parsed || header is null) return;

        lock (_gate)
        {
            if (_header is null)
            {
                _header = header;
                return;
            }

            if (!int.TryParse(Field(values, _header, "ProcessID"), out var pid) || pid != _processId)
            {
                return;
            }

            if (!double.TryParse(Field(values, _header, "TimeInSeconds"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var time))
            {
                return;
            }

            var chain = Field(values, _header, "SwapChainAddress") ?? "unknown";
            if (!_chains.TryGetValue(chain, out var buffer))
            {
                buffer = new ChainBuffer();
                _chains[chain] = buffer;
            }

            var presentMs = ParseDouble(Field(values, _header, "msBetweenPresents"));
            var displayMs = ParseDouble(Field(values, _header, "msBetweenDisplayChange"));
            var dropped = Field(values, _header, "Dropped") == "1";
            buffer.Rows.Enqueue(new Row(time, presentMs, displayMs, dropped));
            if (dropped) buffer.Dropped++;
            while (buffer.Rows.Count > 240) buffer.Rows.Dequeue();

            _lastRowTicks = Stopwatch.GetTimestamp();
            _status = "PresentMon measuring";
        }
    }

    private Dictionary<string, int>? _header;

    private static string? Field(string[] values, Dictionary<string, int> header, string name) =>
        header.TryGetValue(name, out var index) && index >= 0 && index < values.Length ? values[index] : null;

    private static double ParseDouble(string? text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && !double.IsNaN(value)
            ? value
            : 0;

    public PcFpsReading Snapshot()
    {
        lock (_gate)
        {
            if (!IsRunning && _lastRowTicks == 0)
            {
                return new PcFpsReading(false, 0, 0, 0, 0, 0, "—", 0, _status);
            }

            if (_lastRowTicks == 0)
            {
                var waited = (Stopwatch.GetTimestamp() - _lastStartTicks) / (double)Stopwatch.Frequency;
                return new PcFpsReading(false, 0, 0, 0, 0, 0, "—", 0,
                    waited < 3 ? _status : "No PresentMon frame rows yet — the game may not be presenting, " +
                                            "or its API is unsupported.");
            }

            var stale = (Stopwatch.GetTimestamp() - _lastRowTicks) / (double)Stopwatch.Frequency;
            if (stale > 2)
            {
                return new PcFpsReading(false, 0, 0, 0, 0, 0, "—", _chains.Count,
                    "PresentMon has no recent presents — game paused or closed.");
            }

            var primary = SelectPrimaryChain(_chains.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.Rows.Select(r => r.Time).ToArray()));
            var chain = primary is not null && _chains.TryGetValue(primary, out var buffer)
                ? new { Address = primary, Buffer = buffer }
                : null;

            if (chain is null || chain.Buffer.Rows.Count == 0)
            {
                return new PcFpsReading(false, 0, 0, 0, 0, 0, "—", _chains.Count, _status);
            }

            var latest = chain.Buffer.Rows.Max(r => r.Time);
            var rows = chain.Buffer.Rows.Where(r => r.Time >= latest - 2.0).ToArray();
            if (rows.Length < 4)
            {
                return new PcFpsReading(false, 0, 0, 0, 0, 0, chain.Address, _chains.Count,
                    "Not enough recent presents to measure FPS.");
            }

            var span = rows[^1].Time - rows[0].Time;
            if (span <= 0)
            {
                return new PcFpsReading(false, 0, 0, 0, 0, 0, chain.Address, _chains.Count,
                    "Present timestamps are not advancing.");
            }

            var presented = PresentMonMath.Rate(rows.Select(r => r.Time).ToArray());
            var presentTimes = rows.Select(r => r.PresentMs).Where(v => v > 0).OrderBy(v => v).ToArray();
            var displayTimes = rows.Select(r => r.DisplayMs).Where(v => v > 0).OrderBy(v => v).ToArray();
            var frameTime = presentTimes.Length > 0 ? PresentMonMath.Median(presentTimes) : 1000.0 / Math.Max(1, presented);
            var displayed = displayTimes.Length > 0 ? 1000.0 / PresentMonMath.Median(displayTimes) : 0;
            var dropped = rows.LongCount(r => r.Dropped);

            var detail = $"{_chains.Count} swap chain{(_chains.Count == 1 ? string.Empty : "s")} · " +
                         $"active {Short(chain.Address)} · {rows.Length} presents";
            return new PcFpsReading(true, Math.Round(presented, 1), Math.Round(presented, 1),
                Math.Round(displayed, 1), Math.Round(frameTime, 2), dropped, chain.Address, _chains.Count, detail);
        }
    }

    /// <summary>
    /// A launcher, overlay or compositor can own a second chain with a single present. Prefer the
    /// chain with the most presents in its last two seconds, not merely the last touched one, so the
    /// game's main swapchain wins over an incidental overlay chain.
    /// </summary>
    public static string? SelectPrimaryChain(IReadOnlyDictionary<string, double[]> chains)
    {
        return chains
            .Select(pair =>
            {
                var latest = pair.Value.Length == 0 ? 0 : pair.Value.Max();
                return new
                {
                    Address = pair.Key,
                    Recent = pair.Value.Count(time => time >= latest - 2.0),
                    Latest = latest,
                    Total = pair.Value.Length
                };
            })
            .OrderByDescending(item => item.Recent)
            .ThenByDescending(item => item.Latest)
            .ThenByDescending(item => item.Total)
            .Select(item => item.Address)
            .FirstOrDefault();
    }

    private static double Median(double[] sorted) => PresentMonMath.Median(sorted);

    private static string Short(string chain) =>
        chain.Length > 10 ? chain[..6] + "…" + chain[^4..] : chain;

    public void Dispose() => Stop();
}

/// <summary>Pure present-event arithmetic shared by the live source and tests.</summary>
public static class PresentMonMath
{
    /// <summary>Presents per second over the supplied event times (one present is the interval's origin).</summary>
    public static double Rate(IReadOnlyList<double> times)
    {
        if (times.Count < 2) return 0;
        var span = times.Max() - times.Min();
        return span <= 0 ? 0 : (times.Count - 1) / span;
    }

    public static double Median(double[] sorted) =>
        sorted.Length == 0 ? 0 : sorted[sorted.Length / 2];
}

/// <summary>Minimal RFC-4180 parser for PresentMon's CSV output, including quoted commas.</summary>
public static class PresentMonCsv
{
    public static bool TryParse(string line, out Dictionary<string, int>? header, out string[] values)
    {
        values = Split(line.TrimEnd('\r', '\n', '\uFEFF'));
        header = null;
        if (values.Length == 0) return false;
        values[0] = values[0].TrimStart('\uFEFF');

        if (values[0].Equals("Application", StringComparison.OrdinalIgnoreCase))
        {
            header = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < values.Length; i++) header[values[i]] = i;
            return true;
        }

        // Data rows are passed through with a null header; the caller already has the real one.
        return true;
    }

    public static string[] Split(string line)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = !quoted;
                }
            }
            else if (c == ',' && !quoted)
            {
                values.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        values.Add(current.ToString());
        return values.ToArray();
    }
}
