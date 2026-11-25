namespace FifineCaptureStudio.Core.Accounts;

public enum AccountPlatform
{
    Xbox,
    PlayStation,
    Pc
}

public enum AccountStatus
{
    Unavailable,
    SignedOut,
    SigningIn,
    SignedIn,
    Error
}

/// <summary>The signed-in PSN account and the tokens needed to restore the session.</summary>
public sealed record PsnAuthResult(
    string AccountId,
    string OnlineId,
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt);

public sealed record PsnAccountInfo(string AccountId, string OnlineId);

public sealed record XboxProfile(string Gamertag, string Xuid);

/// <summary>The code Microsoft shows to the user, plus the URL where it is entered.</summary>
public sealed record XboxDeviceCodeInfo(
    string DeviceCode,
    string UserCode,
    string VerificationUri,
    int ExpiresInSeconds,
    int IntervalSeconds);

/// <summary>
/// A failure with the authentication stage attached so the UI can say whether Microsoft/Sony
/// sign-in, token exchange, Xbox Live, or profile retrieval failed.
/// </summary>
public sealed class AccountAuthException : Exception
{
    public AccountAuthException(
        string stage,
        string message,
        bool invalidCredentials = false,
        Exception? inner = null)
        : base(message, inner)
    {
        Stage = stage;
        InvalidCredentials = invalidCredentials;
    }

    public string Stage { get; }

    /// <summary>True when the provider explicitly rejected the saved token/refresh token.</summary>
    public bool InvalidCredentials { get; }
}

public interface IAccountInteraction
{
    /// <summary>Opens the provider's own sign-in page and returns the verified account, or null if cancelled.</summary>
    Task<PsnAuthResult?> SignInPlayStationAsync(CancellationToken token);

    /// <summary>Opens an external provider page (Microsoft's device-code verification page).</summary>
    void OpenExternal(Uri uri);
}
