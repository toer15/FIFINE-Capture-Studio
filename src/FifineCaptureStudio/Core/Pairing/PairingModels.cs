using System.IO;
using System.Text.Json;

namespace FifineCaptureStudio.Core.Pairing;

public enum PairingPlatform
{
    Pc,
    PlayStation,
    Xbox
}

/// <summary>Every state the wizard can be in. CONNECTED is only reachable with real frames.</summary>
public enum PairingStage
{
    Idle,
    Searching,
    NoDevices,
    DevicesFound,
    AwaitingCode,
    Authenticating,
    VerifyingVideo,
    Connected,
    Unsupported,
    Interrupted,
    Failed
}

public sealed record PairingDevice(
    string Name,
    string Address,
    int Port,
    PairingPlatform Platform,
    string Transport,
    string Detail,
    bool IsPs5 = false,
    uint Target = 0)
{
    public override string ToString() => $"{Name} · {Address}";
}

/// <summary>What is kept after a session. Never a pairing code, never a credential.</summary>
public sealed record TrustedDevice(
    string Platform,
    string Name,
    string Address,
    string Transport,
    DateTime PairedAtUtc);

public sealed class TrustedDeviceStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly string _path = Path.Combine(AppPaths.Root, "trusted-devices.json");

    public List<TrustedDevice> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new List<TrustedDevice>();
            }

            return JsonSerializer.Deserialize<List<TrustedDevice>>(File.ReadAllText(_path), Options)
                   ?? new List<TrustedDevice>();
        }
        catch (Exception ex)
        {
            Log.Warn("Trusted device list could not be read: " + ex.Message);
            return new List<TrustedDevice>();
        }
    }

    public IReadOnlyList<TrustedDevice> For(PairingPlatform platform) =>
        Load().Where(d => string.Equals(d.Platform, platform.ToString(), StringComparison.OrdinalIgnoreCase)).ToList();

    public void Remember(PairingPlatform platform, string name, string address, string transport)
    {
        try
        {
            var all = Load();
            all.RemoveAll(d => string.Equals(d.Platform, platform.ToString(), StringComparison.OrdinalIgnoreCase) &&
                               string.Equals(d.Address, address, StringComparison.OrdinalIgnoreCase));
            all.Add(new TrustedDevice(platform.ToString(), name, address, transport, DateTime.UtcNow));
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(_path, JsonSerializer.Serialize(all, Options));
        }
        catch (Exception ex)
        {
            Log.Warn("Trusted device could not be saved: " + ex.Message);
        }
    }

    public void Forget(PairingPlatform platform, string address)
    {
        try
        {
            var all = Load();
            all.RemoveAll(d => string.Equals(d.Platform, platform.ToString(), StringComparison.OrdinalIgnoreCase) &&
                               string.Equals(d.Address, address, StringComparison.OrdinalIgnoreCase));
            File.WriteAllText(_path, JsonSerializer.Serialize(all, Options));
        }
        catch (Exception ex)
        {
            Log.Warn("Trusted device could not be removed: " + ex.Message);
        }
    }
}
