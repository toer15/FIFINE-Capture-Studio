using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace FifineCaptureStudio.Core.Pairing;

/// <summary>
/// Real local-network discovery. PC agents answer a FIFINE broadcast; consoles are found by
/// probing the control port they actually listen on. Nothing here authenticates anything —
/// discovery only tells you a host exists.
/// </summary>
public static class LanDiscovery
{
    public const int DiscoveryPort = 47812;
    public const string DiscoveryPayload = "FIFINE_DISCOVER_V1";
    public const int PlayStationPort = 9295;
    public const int XboxPort = 5050;

    private sealed record AgentReply(string? name, int port, string? platform, string? version);

    public static async Task<IReadOnlyList<PairingDevice>> SearchAsync(
        PairingPlatform platform,
        TimeSpan timeout,
        CancellationToken token)
    {
        return platform switch
        {
            PairingPlatform.Pc => await SearchAgentsAsync(timeout, token).ConfigureAwait(false),
            PairingPlatform.PlayStation => await ScanForPortAsync(
                platform, PlayStationPort, "PlayStation console", "Remote Play control port 9295",
                "A host answered on the port PlayStation uses for Remote Play registration.",
                timeout, token).ConfigureAwait(false),
            PairingPlatform.Xbox => await ScanForPortAsync(
                platform, XboxPort, "Xbox console", "Xbox streaming port 5050",
                "A host answered on the port the Xbox app uses for console streaming.",
                timeout, token).ConfigureAwait(false),
            _ => Array.Empty<PairingDevice>()
        };
    }

    /// <summary>Broadcasts the FIFINE Link Agent discovery datagram and collects real replies.</summary>
    private static Task<IReadOnlyList<PairingDevice>> SearchAgentsAsync(TimeSpan timeout, CancellationToken token) =>
        QueryAgentsAsync(BroadcastAddresses(), timeout, token);

    /// <summary>Sends the FIFINE discovery datagram to specific hosts and collects replies.</summary>
    public static async Task<IReadOnlyList<PairingDevice>> QueryAgentsAsync(
        IEnumerable<IPAddress> targets,
        TimeSpan timeout,
        CancellationToken token)
    {
        var found = new List<PairingDevice>();

        try
        {
            using var udp = new UdpClient(AddressFamily.InterNetwork)
            {
                EnableBroadcast = true
            };
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
            var payload = Encoding.UTF8.GetBytes(DiscoveryPayload);

            foreach (var target in targets)
            {
                try
                {
                    await udp.SendAsync(payload, payload.Length, new IPEndPoint(target, DiscoveryPort))
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Discovery datagram to {target} failed: {ex.Message}");
                }
            }

            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    break;
                }

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(remaining);
                UdpReceiveResult result;
                try
                {
                    result = await udp.ReceiveAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                var text = Encoding.UTF8.GetString(result.Buffer);
                AgentReply? reply = null;
                try
                {
                    reply = JsonSerializer.Deserialize<AgentReply>(text);
                }
                catch
                {
                    // Not one of ours.
                }

                if (reply is null)
                {
                    continue;
                }

                var address = result.RemoteEndPoint.Address.ToString();
                if (found.Any(d => d.Address == address))
                {
                    continue;
                }

                found.Add(new PairingDevice(
                    string.IsNullOrWhiteSpace(reply.name) ? "FIFINE Link Agent" : reply.name!,
                    address,
                    reply.port > 0 ? reply.port : 47810,
                    PairingPlatform.Pc,
                    "FIFINE Link Agent",
                    $"Agent version {reply.version ?? "unknown"} · TCP {reply.port}"));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Agent discovery failed: " + ex.Message);
        }

        return found;
    }

    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        var list = new List<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily != AddressFamily.InterNetwork || info.IPv4Mask is null)
                    {
                        continue;
                    }

                    var address = info.Address.GetAddressBytes();
                    var mask = info.IPv4Mask.GetAddressBytes();
                    var broadcast = new byte[4];
                    for (var i = 0; i < 4; i++)
                    {
                        broadcast[i] = (byte)(address[i] | (mask[i] ^ 255));
                    }

                    list.Add(new IPAddress(broadcast));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Network interface enumeration failed: " + ex.Message);
        }

        return list.Distinct();
    }

    /// <summary>Scans the local /24 for hosts that accept a connection on the given port.</summary>
    private static async Task<IReadOnlyList<PairingDevice>> ScanForPortAsync(
        PairingPlatform platform,
        int port,
        string name,
        string transport,
        string detail,
        TimeSpan timeout,
        CancellationToken token)
    {
        var found = new List<PairingDevice>();
        var local = LocalIPv4();
        if (local is null)
        {
            Log.Warn("No local IPv4 address; console scan skipped.");
            return found;
        }

        var prefix = local.GetAddressBytes();
        prefix[3] = 0;

        var tasks = new List<Task<(string Address, bool Open)>>();
        var gate = new SemaphoreSlim(48);

        for (var host = 1; host < 255; host++)
        {
            token.ThrowIfCancellationRequested();
            var addressBytes = (byte[])prefix.Clone();
            addressBytes[3] = (byte)host;
            var address = new IPAddress(addressBytes).ToString();

            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var open = await ProbeTcpAsync(address, port, 420, token).ConfigureAwait(false);
                    return (address, open);
                }
                finally
                {
                    gate.Release();
                }
            }, token));
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(timeout, token).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // Partial results are still useful.
        }
        catch (OperationCanceledException)
        {
        }

        foreach (var task in tasks.Where(t => t.IsCompletedSuccessfully))
        {
            var (address, open) = task.Result;
            if (!open)
            {
                continue;
            }

            found.Add(new PairingDevice($"{name} at {address}", address, port, platform, transport, detail));
        }

        return found.OrderBy(d => d.Address, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static async Task<bool> ProbeTcpAsync(string host, int port, int timeoutMs, CancellationToken token = default)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    public static IPAddress? LocalIPv4()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var info in nic.GetIPProperties().UnicastAddresses)
                {
                    if (info.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(info.Address))
                    {
                        return info.Address;
                    }
                }
            }
        }
        catch
        {
            // Fall through to null.
        }

        return null;
    }
}
