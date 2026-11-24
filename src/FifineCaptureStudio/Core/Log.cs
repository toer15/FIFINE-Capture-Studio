using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace FifineCaptureStudio.Core;

/// <summary>
/// Thread-safe rolling file logger. The log is intentionally verbose: capture
/// applications live or die on diagnosable pipeline errors, so every FFmpeg
/// invocation and its stderr is recorded.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<string> Recent = new();
    private static string _file = string.Empty;

    public static event Action<string>? LineWritten;

    public static void Init()
    {
        AppPaths.EnsureCreated();
        _file = Path.Combine(AppPaths.LogsDir, "capture-studio.log");
        try
        {
            if (File.Exists(_file) && new FileInfo(_file).Length > 4 * 1024 * 1024)
            {
                var old = _file + ".old";
                File.Delete(old);
                File.Move(_file, old);
            }
        }
        catch
        {
            // Logging must never prevent the application from starting.
        }

        Info("=== FIFINE Capture Studio " + Version() + " starting ===");
    }

    /// <summary>
    /// The build's semantic version. AssemblyVersion stays fixed for binding stability, so the
    /// informational version ("1.0.1+<sha>") is what users must see.
    /// </summary>
    public static string Version()
    {
        var informational = System.Reflection.CustomAttributeExtensions
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(Log).Assembly)
            ?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational.Split('+')[0];
        }

        return typeof(Log).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}");

    public static void Debug(string message)
    {
#if DEBUG
        Write("DEBUG", message);
#endif
    }

    public static IReadOnlyList<string> RecentLines() => Recent.ToArray();

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}";
        Recent.Enqueue(line);
        while (Recent.Count > 400)
        {
            Recent.TryDequeue(out _);
        }

        lock (Gate)
        {
            try
            {
                File.AppendAllText(_file, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // ignore disk errors while logging
            }
        }

        try
        {
            LineWritten?.Invoke(line);
        }
        catch
        {
            // listeners must not break logging
        }
    }
}
