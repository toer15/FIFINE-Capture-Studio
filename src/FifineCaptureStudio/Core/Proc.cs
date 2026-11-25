using System.Diagnostics;
using System.IO;
using System.Text;

namespace FifineCaptureStudio.Core;

/// <summary>Small helpers around <see cref="Process"/> with correct argument quoting.</summary>
public static class Proc
{
    /// <summary>Quotes a single argument for the Windows command line.</summary>
    public static string Arg(string value)
    {
        if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
        {
            return value;
        }

        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }

            if (ch == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }

            sb.Append('\\', backslashes).Append(ch);
            backslashes = 0;
        }

        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    public static Process Start(
        string exe,
        IEnumerable<string> args,
        bool redirectStdout = true,
        bool redirectStderr = true,
        bool redirectStdin = true,
        string? workingDirectory = null)
    {
        var cwd = workingDirectory ?? AppPaths.Root;
        // A fresh install has no app-data directory until something writes to it. Create the
        // working directory before starting the process; otherwise Process.Start fails with
        // "The directory name is invalid" even though the executable itself is available.
        Directory.CreateDirectory(cwd);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = redirectStdout,
            RedirectStandardError = redirectStderr,
            RedirectStandardInput = redirectStdin,
            StandardOutputEncoding = redirectStdout ? Encoding.UTF8 : null,
            StandardErrorEncoding = redirectStderr ? Encoding.UTF8 : null,
            WorkingDirectory = cwd
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        Log.Debug("spawn: " + exe + " " + string.Join(" ", psi.ArgumentList.Select(Arg)));
        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start " + exe);
    }

    /// <summary>Runs a process to completion, capturing both streams.</summary>
    public static async Task<(int Code, string StdOut, string StdErr)> RunAsync(
        string exe,
        IEnumerable<string> args,
        TimeSpan timeout,
        CancellationToken ct = default)
    {
        using var p = Start(exe, args, redirectStdin: false);
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return (-1, string.Empty, "Process timed out after " + timeout.TotalSeconds + "s");
        }

        return (p.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    /// <summary>Asks a process to stop gracefully, then kills the tree if it lingers.</summary>
    public static async Task StopAsync(Process? process, int graceMs = 2500)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                if (process.StartInfo.RedirectStandardInput)
                {
                    try
                    {
                        await process.StandardInput.WriteAsync("q\n");
                        await process.StandardInput.FlushAsync();
                    }
                    catch
                    {
                        // stdin already closed
                    }
                }

                using var cts = new CancellationTokenSource(graceMs);
                try
                {
                    await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // fall through to kill
                }
            }
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    process.WaitForExit(2000);
                }
            }
            catch
            {
                // already gone
            }

            process.Dispose();
        }
    }
}
