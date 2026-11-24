using System.IO;
using Microsoft.Win32;

namespace FifineCaptureStudio.Core;

/// <summary>
/// Detection for AMD Radeon hardware and the ADLX SDK. ADLX is the only supported way to
/// read or change driver-level AFMF state; without the SDK the app reports that honestly
/// instead of showing a toggle that pretends to work.
/// </summary>
public sealed record AmdStatus(
    bool IsAmdGpu,
    string GpuName,
    string DriverVersion,
    bool AdlxPresent,
    string AdlxPath,
    bool AfmfControllable,
    string Detail)
{
    public string GpuLabel => IsAmdGpu ? GpuName : "No AMD Radeon GPU detected";
    public string DriverLabel => string.IsNullOrEmpty(DriverVersion) ? "Unknown driver" : $"Driver {DriverVersion}";

    public string AdlxLabel => AdlxPresent
        ? "ADLX runtime found: " + AdlxPath
        : "ADLX runtime not installed";

    public string AfmfLabel => AfmfControllable
        ? "AFMF can be read and controlled"
        : "AFMF cannot be controlled from this build";
}

public static class AmdDriver
{
    private const string DisplayClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static AmdStatus Detect()
    {
        var gpuName = string.Empty;
        var driverVersion = string.Empty;
        var isAmd = false;

        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (classKey is not null)
            {
                foreach (var name in classKey.GetSubKeyNames())
                {
                    if (name.Length != 4 || !name.All(char.IsDigit))
                    {
                        continue;
                    }

                    using var adapter = classKey.OpenSubKey(name);
                    var desc = adapter?.GetValue("DriverDesc") as string ?? string.Empty;
                    var provider = adapter?.GetValue("ProviderName") as string ?? string.Empty;
                    var version = adapter?.GetValue("DriverVersion") as string ?? string.Empty;

                    if (desc.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
                        desc.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
                        provider.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase))
                    {
                        isAmd = true;
                        gpuName = desc;
                        driverVersion = version;
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("AMD adapter detection failed: " + ex.Message);
        }

        var adlxPath = FindAdlx();
        var adlxPresent = adlxPath.Length > 0;

        var detail = !isAmd
            ? "AFMF is an AMD Radeon driver feature. This PC does not report an AMD display adapter, so the setting does not apply."
            : !adlxPresent
                ? "AFMF is a driver-level feature of AMD Radeon GPUs. It can only be read or changed through AMD's ADLX SDK, " +
                  "and no ADLX runtime (adlx64.dll) is installed with this build. The state below is therefore unknown and " +
                  "FIFINE Capture Studio will not pretend to toggle it. Install the ADLX SDK runtime to enable control."
                : "An ADLX runtime was found, but this build does not bind its interfaces yet, so AFMF remains read-only.";

        return new AmdStatus(
            isAmd,
            gpuName,
            driverVersion,
            adlxPresent,
            adlxPath,
            AfmfControllable: false,
            detail);
    }

    private static string FindAdlx()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "adlx64.dll"),
            Path.Combine(AppContext.BaseDirectory, "ADLX", "adlx64.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "adlx64.dll")
        };

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
                // Ignore unreadable paths.
            }
        }

        return string.Empty;
    }

    /// <summary>Attempts to change AFMF. Always fails while no ADLX binding is present.</summary>
    public static bool TrySetAfmfFluidMotion(bool enabled, out string message)
    {
        message = enabled
            ? "AFMF was not enabled: no ADLX SDK runtime is available to talk to the AMD driver."
            : "AFMF was not disabled: no ADLX SDK runtime is available to talk to the AMD driver.";
        return false;
    }
}
