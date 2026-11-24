using SharpDX.DXGI;

namespace FifineCaptureStudio.Core.Enhancement;

/// <summary>
/// Enumerates real DXGI adapters so the engine can report the hardware it will actually use
/// instead of assuming a GPU model.
/// </summary>
public static class GpuDetection
{
    public sealed record AdapterEntry(int Index, GpuAdapterInfo Info);

    public static string LastError { get; private set; } = string.Empty;

    public static IReadOnlyList<AdapterEntry> Detect()
    {
        var list = new List<AdapterEntry>();
        try
        {
            using var factory = new Factory1();
            for (var i = 0; i < 8; i++)
            {
                Adapter1? adapter = null;
                try
                {
                    adapter = factory.GetAdapter1(i);
                }
                catch
                {
                    break;
                }

                using (adapter)
                {
                    var description = adapter.Description1;
                    var vendor = description.VendorId;
                    ulong vram = 0;
                    try
                    {
                        using var adapter3 = adapter.QueryInterface<Adapter3>();
                        var info = adapter3.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
                        vram = (ulong)Math.Max(0, info.Budget);
                    }
                    catch
                    {
                        // Older drivers do not expose budget information.
                    }

                    list.Add(new AdapterEntry(i, new GpuAdapterInfo(
                        description.Description.Trim(),
                        string.Empty,
                        vram,
                        vendor == 0x1002,
                        true,
                        vendor)));
                }
            }
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name + ": " + ex.Message;
            FifineCaptureStudio.Core.Log.Warn("GPU detection failed: " + LastError);
        }

        return list;
    }

    /// <summary>Prefers a discrete AMD adapter, then any discrete adapter.</summary>
    public static AdapterEntry? Primary(IReadOnlyList<AdapterEntry> adapters)
    {
        return adapters.FirstOrDefault(a => a.Info.IsAmd && a.Info.DedicatedVideoMemory > 2UL * 1024 * 1024 * 1024)
               ?? adapters.OrderByDescending(a => a.Info.DedicatedVideoMemory).FirstOrDefault();
    }
}
