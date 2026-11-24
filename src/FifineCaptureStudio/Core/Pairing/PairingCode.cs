namespace FifineCaptureStudio.Core.Pairing;

/// <summary>Console pairing numbers have no fixed length and are never stored.</summary>
public static class PairingCode
{
    public static string Normalize(string? input) =>
        new string((input ?? string.Empty).Where(char.IsDigit).ToArray());

    public static bool IsUsable(string? input) => Normalize(input).Length > 0;
}
