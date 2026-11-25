namespace FifineCaptureStudio.Core.Accounts;

/// <summary>
/// PlayStation Network account state. Signing in stores the real Online ID and DPAPI-protected
/// tokens; signing out clears only this platform's account. Console pairing and stream state are
/// never read or changed here.
/// </summary>
public sealed class PsnAccountProvider : IDisposable
{
    private readonly SettingsService _settings;
    private readonly PsnAccountLookup _api;

    public PsnAccountProvider(
        SettingsService settings,
        SynchronizationContext? context = null,
        PsnAccountLookup? api = null)
    {
        _settings = settings;
        _api = api ?? new PsnAccountLookup();
        Session = new AccountSession(
            AccountPlatform.PlayStation,
            "PlayStation",
            "PlayStation Network",
            "PSN account ID",
            context);
    }

    public AccountSession Session { get; }

    /// <summary>Restores a saved PSN session and validates it against Sony when the network allows.</summary>
    public async Task RestoreAsync(CancellationToken token)
    {
        var ps = _settings.Current.PlayStation;
        var hasTokens = !string.IsNullOrWhiteSpace(ps.RefreshTokenProtected) ||
                        !string.IsNullOrWhiteSpace(ps.AccessTokenProtected);
        if (!hasTokens)
        {
            Session.SetSignedOut(string.IsNullOrWhiteSpace(ps.AccountId)
                ? "Not signed in."
                : "A PSN account ID is set manually. Sign in to verify your Online ID.");
            return;
        }

        Session.SetSigningIn("Restoring your PlayStation Network sign-in…");
        try
        {
            var access = SecretProtector.Unprotect(ps.AccessTokenProtected);
            var refresh = SecretProtector.Unprotect(ps.RefreshTokenProtected);
            var expires = ps.AccessTokenExpiresUtc;

            PsnAccountInfo? info = null;
            if (!string.IsNullOrWhiteSpace(access) && expires > DateTimeOffset.UtcNow.AddMinutes(1))
            {
                try
                {
                    info = await _api.GetAccountAsync(access, token).ConfigureAwait(false);
                }
                catch (AccountAuthException ex) when (ex.InvalidCredentials)
                {
                    // Expired access token is normal; fall through to the refresh token.
                }
            }

            if (info is null)
            {
                if (string.IsNullOrWhiteSpace(refresh))
                {
                    ClearTokens(keepIdentity: true);
                    Session.SetExpired(ps.OnlineId, "PlayStation Network session expired. Sign in again.");
                    return;
                }

                var refreshed = await _api.RefreshAsync(refresh, token).ConfigureAwait(false);
                var persisted = Persist(refreshed);
                Session.SetSignedIn(
                    refreshed.OnlineId,
                    refreshed.AccountId,
                    persisted
                        ? "Signed in to PlayStation Network."
                        : "Signed in to PlayStation Network. The token could not be stored for the next launch.");
                return;
            }

            var save = Persist(new PsnAuthResult(info.AccountId, info.OnlineId, access, refresh, expires));
            Session.SetSignedIn(
                info.OnlineId,
                info.AccountId,
                save
                    ? "Signed in to PlayStation Network."
                    : "Signed in to PlayStation Network. The token could not be stored for the next launch.");
        }
        catch (AccountAuthException ex) when (ex.InvalidCredentials)
        {
            ClearTokens(keepIdentity: true);
            Session.SetExpired(ps.OnlineId, ex.Message);
        }
        catch (OperationCanceledException)
        {
            Session.SetSignedOut("PlayStation Network restore was interrupted.");
        }
        catch (Exception ex)
        {
            // Sony being unreachable must not erase a valid local account. Keep the last verified
            // Online ID and explain that the check could not be completed.
            var cached = _settings.Current.PlayStation.OnlineId;
            if (!string.IsNullOrWhiteSpace(cached))
            {
                Session.SetSignedIn(
                    cached,
                    _settings.Current.PlayStation.AccountId,
                    "Signed in — Sony could not be reached; using the last verified account.");
            }
            else
            {
                Session.SetError(
                    "PlayStation Network",
                    "Could not contact Sony to restore your sign-in: " + ex.Message);
            }
        }
    }

    /// <summary>Opens Sony's sign-in page, validates the result, and shows the returned Online ID.</summary>
    public async Task SignInAsync(IAccountInteraction interaction, CancellationToken token)
    {
        Session.SetSigningIn("Opening Sony's PlayStation Network sign-in page…");
        try
        {
            var result = await interaction.SignInPlayStationAsync(token).ConfigureAwait(false);
            if (result is null)
            {
                Session.SetSignedOut("PlayStation Network sign-in was cancelled.");
                return;
            }

            if (string.IsNullOrWhiteSpace(result.OnlineId))
            {
                Session.SetError("PlayStation profile", "Sony did not return an Online ID.");
                return;
            }

            var persisted = Persist(result);
            Session.SetSignedIn(
                result.OnlineId,
                result.AccountId,
                persisted
                    ? "Signed in to PlayStation Network."
                    : "Signed in to PlayStation Network. The token could not be stored for the next launch.");
        }
        catch (AccountAuthException ex)
        {
            Session.SetError(ex.Stage, ex.Message);
        }
        catch (OperationCanceledException)
        {
            Session.SetSignedOut("PlayStation Network sign-in was cancelled.");
        }
        catch (Exception ex)
        {
            Session.SetError("PlayStation Network", ex.Message);
        }
    }

    public void SignOut()
    {
        var ps = _settings.Current.PlayStation;
        ps.AccountId = string.Empty;
        ps.OnlineId = string.Empty;
        ps.AccessTokenProtected = string.Empty;
        ps.RefreshTokenProtected = string.Empty;
        ps.AccessTokenExpiresUtc = default;
        _settings.Save();
        Session.SetSignedOut("Signed out of PlayStation Network.");
    }

    public void Dispose() => _api.Dispose();

    private bool Persist(PsnAuthResult result)
    {
        var ps = _settings.Current.PlayStation;
        ps.AccountId = result.AccountId;
        ps.OnlineId = result.OnlineId;
        ps.AccessTokenProtected = SecretProtector.Protect(result.AccessToken);
        ps.RefreshTokenProtected = SecretProtector.Protect(result.RefreshToken);
        ps.AccessTokenExpiresUtc = result.ExpiresAt;
        _settings.Save();
        return !string.IsNullOrWhiteSpace(ps.AccessTokenProtected) &&
               !string.IsNullOrWhiteSpace(ps.RefreshTokenProtected);
    }

    private void ClearTokens(bool keepIdentity)
    {
        var ps = _settings.Current.PlayStation;
        ps.AccessTokenProtected = string.Empty;
        ps.RefreshTokenProtected = string.Empty;
        ps.AccessTokenExpiresUtc = default;
        if (!keepIdentity)
        {
            ps.AccountId = string.Empty;
            ps.OnlineId = string.Empty;
        }

        _settings.Save();
    }
}
