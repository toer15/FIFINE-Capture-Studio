namespace FifineCaptureStudio.Core.Accounts;

/// <summary>
/// Owns the three platform account sessions. Xbox and PlayStation restore and sign out
/// independently; PC is deliberately marked unavailable because the FIFINE Link Agent pairs a
/// device but exposes no account identity.
/// </summary>
public sealed class AccountManager : IDisposable
{
    private readonly PsnAccountProvider _playStation;
    private readonly XboxAccountProvider _xbox;

    public AccountManager(SettingsService settings, SynchronizationContext? context = null)
    {
        _playStation = new PsnAccountProvider(settings, context);
        _xbox = new XboxAccountProvider(settings, context);

        Pc = new AccountSession(
            AccountPlatform.Pc,
            "PC",
            "FIFINE Link Agent",
            "Agent",
            context);
        Pc.SetUnavailable(
            "The FIFINE Link Agent pairs another gaming PC over the LAN. It does not expose an account identity, " +
            "so there is no PC gaming account to sign in to in this build.");
    }

    public AccountSession Xbox => _xbox.Session;
    public AccountSession PlayStation => _playStation.Session;
    public AccountSession Pc { get; }

    public string XboxClientId => _xbox.ClientId;

    /// <summary>Restores both providers in parallel. A failure in one never stops the other.</summary>
    public Task RestoreAsync(CancellationToken token = default) =>
        Task.WhenAll(_playStation.RestoreAsync(token), _xbox.RestoreAsync(token));

    public Task SignInAsync(AccountPlatform platform, IAccountInteraction interaction, CancellationToken token = default) =>
        platform switch
        {
            AccountPlatform.PlayStation => _playStation.SignInAsync(interaction, token),
            AccountPlatform.Xbox => _xbox.SignInAsync(interaction, token),
            _ => Task.CompletedTask
        };

    public void SignOut(AccountPlatform platform)
    {
        switch (platform)
        {
            case AccountPlatform.PlayStation:
                _playStation.SignOut();
                break;
            case AccountPlatform.Xbox:
                _xbox.SignOut();
                break;
        }
    }

    public bool SetXboxClientId(string? value) => _xbox.SetClientId(value);

    public void RefreshXboxSetup() => _xbox.RefreshSetupState();

    public void Dispose()
    {
        _playStation.Dispose();
        _xbox.Dispose();
    }
}
