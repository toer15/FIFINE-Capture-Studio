using System.Security.Cryptography;
using System.Text;

namespace FifineCaptureStudio.Core.Accounts;

/// <summary>
/// Protects account tokens with Windows DPAPI for the current user. Settings.json only ever
/// receives the base64 ciphertext; a failure returns an empty string rather than falling back
/// to plain text.
/// </summary>
public static class SecretProtector
{
    public static string Protect(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        try
        {
            var cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plaintext),
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(cipher);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not protect an account token with DPAPI: " + ex.Message);
            return string.Empty;
        }
    }

    public static string Unprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText))
        {
            return string.Empty;
        }

        try
        {
            var plain = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedText),
                optionalEntropy: null,
                scope: DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not unprotect a saved account token: " + ex.Message);
            return string.Empty;
        }
    }
}
