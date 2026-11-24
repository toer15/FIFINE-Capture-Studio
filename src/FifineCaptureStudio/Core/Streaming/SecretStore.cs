using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FifineCaptureStudio.Core.Streaming;

/// <summary>
/// Protects console credentials (Remote Play registration keys, Xbox tokens) with Windows DPAPI
/// scoped to the current user, so a copy of settings.json cannot be replayed on another machine
/// or by another account.
/// </summary>
public static class SecretStore
{
    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    public static string Protect(byte[] plain)
    {
        if (plain.Length == 0)
        {
            return string.Empty;
        }

        var input = new DataBlob { Size = plain.Length, Data = Marshal.AllocHGlobal(plain.Length) };
        try
        {
            Marshal.Copy(plain, 0, input.Data, plain.Length);
            if (!CryptProtectData(ref input, "FIFINE Capture Studio", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var output))
            {
                Log.Warn("DPAPI protect failed, storing value unprotected.");
                return "plain:" + Convert.ToBase64String(plain);
            }

            try
            {
                var protectedBytes = new byte[output.Size];
                Marshal.Copy(output.Data, protectedBytes, 0, output.Size);
                return "dpapi:" + Convert.ToBase64String(protectedBytes);
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
        }
    }

    public static byte[]? Unprotect(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        try
        {
            if (stored.StartsWith("plain:", StringComparison.Ordinal))
            {
                return Convert.FromBase64String(stored[6..]);
            }

            var payload = Convert.FromBase64String(stored.StartsWith("dpapi:", StringComparison.Ordinal)
                ? stored[6..]
                : stored);

            var input = new DataBlob { Size = payload.Length, Data = Marshal.AllocHGlobal(payload.Length) };
            try
            {
                Marshal.Copy(payload, 0, input.Data, payload.Length);
                if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        CryptProtectUiForbidden, out var output))
                {
                    Log.Warn("DPAPI unprotect failed (different Windows user?).");
                    return null;
                }

                try
                {
                    var plain = new byte[output.Size];
                    Marshal.Copy(output.Data, plain, 0, output.Size);
                    return plain;
                }
                finally
                {
                    LocalFree(output.Data);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(input.Data);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Stored credential could not be read: " + ex.Message);
            return null;
        }
    }

    public static string ProtectString(string value) =>
        string.IsNullOrEmpty(value) ? string.Empty : Protect(Encoding.UTF8.GetBytes(value));

    public static string UnprotectString(string? stored)
    {
        var bytes = Unprotect(stored);
        return bytes is null ? string.Empty : Encoding.UTF8.GetString(bytes);
    }

    public static string ProtectBytes(byte[]? value) => value is { Length: > 0 } ? Protect(value) : string.Empty;

    public static byte[]? UnprotectBytes(string? stored) => Unprotect(stored);
}
