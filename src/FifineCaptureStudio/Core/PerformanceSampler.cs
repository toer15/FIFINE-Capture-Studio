using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace FifineCaptureStudio.Core;

public sealed record SystemSnapshot(
    double CpuPercent,
    double GpuPercent,
    double GpuEncodePercent,
    double RamUsedGb,
    double RamTotalGb,
    long DriveFreeBytes,
    long DriveTotalBytes,
    DateTime Timestamp);

/// <summary>
/// Samples real system counters: CPU from GetSystemTimes, GPU utilisation from the
/// WDDM "GPU Engine" performance counters, RAM from GlobalMemoryStatusEx and disk
/// capacity from the recording volume. When a counter is unavailable the value is
/// -1 and the UI shows "n/a" rather than inventing a number.
/// </summary>
public sealed class PerformanceSampler : IDisposable
{
    private readonly List<PerformanceCounter> _gpu3D = new();
    private readonly List<PerformanceCounter> _gpuEncode = new();
    private readonly object _gate = new();
    private Timer? _timer;
    private bool _gpuCountersReady;
    private ulong _prevIdle, _prevKernel, _prevUser;
    private string _driveRoot = "C:\\";

    public string CpuName { get; private set; } = "CPU";
    public string GpuName { get; private set; } = "GPU";
    public double RamTotalGb { get; private set; }
    public event Action<SystemSnapshot>? Sampled;

    public PerformanceSampler()
    {
        CpuName = ReadCpuName();
        GpuName = ReadGpuName();
        GetSystemTimes(out _, out _, out _);
        try
        {
            using var pc = new PerformanceCounter("Processor", "% Processor Time", "_Total", true);
            pc.NextValue();
        }
        catch
        {
            // CPU sampling falls back to GetSystemTimes deltas.
        }
    }

    public void Start(int intervalMs = 1000)
    {
        Stop();
        _timer = new Timer(_ => Sample(), null, 200, intervalMs);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void SetDrive(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(root))
            {
                _driveRoot = root;
            }
        }
        catch
        {
            // keep previous drive
        }
    }

    public SystemSnapshot Sample()
    {
        double cpu;
        GetSystemTimes(out var idle, out var kernel, out var user);
        var idleDelta = idle - _prevIdle;
        var kernelDelta = kernel - _prevKernel;
        var userDelta = user - _prevUser;
        var total = kernelDelta + userDelta;
        cpu = total == 0 ? 0 : 100.0 * (total - idleDelta) / total;
        _prevIdle = idle;
        _prevKernel = kernel;
        _prevUser = user;

        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        GlobalMemoryStatusEx(ref mem);
        RamTotalGb = mem.ullTotalPhys / 1073741824.0;
        var ramUsed = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;

        EnsureGpuCounters();
        var gpu = ReadGpu(_gpu3D);
        var gpuEnc = ReadGpu(_gpuEncode);

        long free = 0, totalBytes = 0;
        try
        {
            var drive = new DriveInfo(_driveRoot);
            free = drive.AvailableFreeSpace;
            totalBytes = drive.TotalSize;
        }
        catch
        {
            // drive may be disconnected
        }

        var snapshot = new SystemSnapshot(
            Math.Clamp(cpu, 0, 100),
            gpu,
            gpuEnc,
            ramUsed,
            RamTotalGb,
            free,
            totalBytes,
            DateTime.Now);

        Sampled?.Invoke(snapshot);
        return snapshot;
    }

    private void EnsureGpuCounters()
    {
        if (_gpuCountersReady)
        {
            return;
        }

        _gpuCountersReady = true;
        try
        {
            if (!PerformanceCounterCategory.Exists("GPU Engine"))
            {
                return;
            }

            var category = new PerformanceCounterCategory("GPU Engine");
            foreach (var instance in category.GetInstanceNames())
            {
                try
                {
                    if (instance.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase))
                    {
                        var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, true);
                        pc.NextValue();
                        _gpu3D.Add(pc);
                    }
                    else if (instance.Contains("engtype_VideoEncode", StringComparison.OrdinalIgnoreCase))
                    {
                        var pc = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, true);
                        pc.NextValue();
                        _gpuEncode.Add(pc);
                    }
                }
                catch
                {
                    // counter instance raced with process exit
                }
            }

            Log.Info($"GPU counters: {_gpu3D.Count} 3D engine instances, {_gpuEncode.Count} encode instances.");
        }
        catch (Exception ex)
        {
            Log.Warn("GPU performance counters unavailable: " + ex.Message);
        }
    }

    private static double ReadGpu(List<PerformanceCounter> counters)
    {
        if (counters.Count == 0)
        {
            return -1;
        }

        double sum = 0;
        var ok = false;
        lock (counters)
        {
            foreach (var pc in counters)
            {
                try
                {
                    sum += pc.NextValue();
                    ok = true;
                }
                catch
                {
                    // instance vanished
                }
            }
        }

        return ok ? Math.Clamp(sum, 0, 100) : -1;
    }

    private static string ReadCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? "CPU";
        }
        catch
        {
            return "CPU";
        }
    }

    private static string ReadGpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (key is null)
            {
                return "GPU";
            }

            foreach (var name in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(name);
                var desc = sub?.GetValue("DriverDesc") as string;
                if (!string.IsNullOrWhiteSpace(desc))
                {
                    return desc.Trim();
                }
            }
        }
        catch
        {
            // ignore
        }

        return "GPU";
    }

    public void Dispose()
    {
        Stop();
        foreach (var pc in _gpu3D)
        {
            pc.Dispose();
        }

        foreach (var pc in _gpuEncode)
        {
            pc.Dispose();
        }

        _gpu3D.Clear();
        _gpuEncode.Clear();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);
}
