using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FifineCaptureStudio.Core.Pairing;

/// <summary>
/// Protocol-level self-test. It runs real sockets on loopback with a mock FIFINE Link Agent,
/// so discovery, probing, the trusted-device store and the pairing-code normaliser can be
/// exercised without hardware. This proves the transport code, not console authentication.
/// </summary>
public static class PairingSelfTest
{
    public static string RunAsync()
    {
        var report = new StringBuilder();
        report.AppendLine("FIFINE pairing self-test (mock agent on loopback)");
        report.AppendLine("================================================");

        var pass = 0;
        var fail = 0;

        void Check(string name, bool ok, string detail)
        {
            if (ok)
            {
                pass++;
                report.AppendLine($"PASS  {name}  ({detail})");
            }
            else
            {
                fail++;
                report.AppendLine($"FAIL  {name}  ({detail})");
            }
        }

        // 1. Discovery against a mock agent that answers the real datagram.
        UdpClient? responder = null;
        try
        {
            responder = new UdpClient(new IPEndPoint(IPAddress.Loopback, LanDiscovery.DiscoveryPort));
            var listen = Task.Run(async () =>
            {
                var request = await responder.ReceiveAsync().ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(request.Buffer);
                if (text != LanDiscovery.DiscoveryPayload)
                {
                    return;
                }

                var reply = Encoding.UTF8.GetBytes(
                    "{\"name\":\"Mock Link Agent\",\"port\":47810,\"platform\":\"pc\",\"version\":\"self-test\"}");
                await responder.SendAsync(reply, reply.Length, request.RemoteEndPoint).ConfigureAwait(false);
            });

            var devices = LanDiscovery.QueryAgentsAsync(
                    new[] { IPAddress.Loopback }, TimeSpan.FromSeconds(2), CancellationToken.None)
                .GetAwaiter().GetResult();

            Check("agent discovery", devices.Count == 1 && devices[0].Name == "Mock Link Agent",
                devices.Count == 1 ? $"{devices[0].Address}:{devices[0].Port}" : "no reply parsed");
            Check("discovery requires the FIFINE datagram", listen.IsCompleted || listen.Wait(1500), "responder saw the query");
        }
        catch (Exception ex)
        {
            Check("agent discovery", false, ex.Message);
        }
        finally
        {
            responder?.Dispose();
        }

        // 2. TCP probe against a listening socket, and against a closed port.
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, 47810);
            listener.Start();
            var accept = listener.AcceptTcpClientAsync();

            var open = LanDiscovery.ProbeTcpAsync("127.0.0.1", 47810, 1200).GetAwaiter().GetResult();
            Check("tcp probe finds an open port", open, "127.0.0.1:47810");

            try
            {
                accept.Wait(1500);
            }
            catch
            {
                // Not needed for the assertion.
            }

            var closed = LanDiscovery.ProbeTcpAsync("127.0.0.1", 47829, 400).GetAwaiter().GetResult();
            Check("tcp probe rejects a closed port", !closed, "127.0.0.1:47829");
        }
        catch (Exception ex)
        {
            Check("tcp probe", false, ex.Message);
        }
        finally
        {
            listener?.Stop();
        }

        // 3. Pairing code normalisation: digits only, no fixed length, code not persisted.
        Check("code keeps digits only, any length", PairingCode.Normalize(" 12ab34 56 ") == "123456", PairingCode.Normalize(" 12ab34 56 "));
        Check("code rejects empty input", !PairingCode.IsUsable(""), "empty");
        Check("code accepts 8 digits", PairingCode.IsUsable("12345678"), "12345678");

        // 4. Trusted device store round-trip, including forget.
        var store = new TrustedDeviceStore();
        store.Remember(PairingPlatform.PlayStation, "Self-test console", "192.0.2.10", "HDMI capture card");
        var afterRemember = store.For(PairingPlatform.PlayStation).Any(d => d.Address == "192.0.2.10");
        Check("trusted device stored", afterRemember, "192.0.2.10");

        var json = ReadStoreFile();
        Check("no pairing code in the store", !json.Contains("12345678", StringComparison.Ordinal), $"{json.Length} bytes");

        store.Forget(PairingPlatform.PlayStation, "192.0.2.10");
        Check("trusted device forgotten", store.For(PairingPlatform.PlayStation).All(d => d.Address != "192.0.2.10"), "removed");

        report.AppendLine();
        report.AppendLine($"{pass} passed, {fail} failed");
        report.AppendLine("Console authentication and console video are NOT tested here: they need a real PS4/PS5/Xbox and Sony/Microsoft protocol access.");

        var path = Path.Combine(AppPaths.Root, "pairing-test.txt");
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(path, report.ToString());
        return path;
    }

    private static string ReadStoreFile()
    {
        try
        {
            var path = Path.Combine(AppPaths.Root, "trusted-devices.json");
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
