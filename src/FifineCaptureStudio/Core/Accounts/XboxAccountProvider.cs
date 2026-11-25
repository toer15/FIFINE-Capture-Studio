using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FifineCaptureStudio.Core.Accounts;

/// <summary>
/// Xbox Live account state. Authentication is Microsoft's public-client device-code flow followed
/// by the Xbox Live user/XSTS exchange and the profile service. A Microsoft Entra app registration
/// client ID is required by Microsoft; it is a public identifier, and no secret is stored.
/// </summary>
public sealed class XboxAccountProvider : IDisposable
{
    private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string XboxUserAuthUrl = "https://user.auth.xboxlive.com/user/authenticate";
    private const string XstsUrl = "https://xsts.auth.xboxlive.com/xsts/authorize";
    private const string ProfileUrl = "https://profile.xboxlive.com/users/me/profile/settings?settings=Gamertag";
    private const string Scope = "XboxLive.signin offline_access";

    public const string SetupHelp =
        "Xbox sign-in uses Microsoft's device-code flow and needs one app registration:\n" +
        "1. Open portal.azure.com → Microsoft Entra ID → App registrations → New registration.\n" +
        "2. Choose personal Microsoft accounts (or accounts in any organization and personal accounts).\n" +
        "3. Under Authentication, set Allow public client flows to Yes.\n" +
        "4. Approve the Xbox Live permission if Microsoft asks for it.\n" +
        "5. Copy the Application (client) ID and paste it below. No client secret is used.";

    private readonly SettingsService _settings;
    private readonly HttpClient _http;

    public XboxAccountProvider(
        SettingsService settings,
        SynchronizationContext? context = null,
        HttpMessageHandler? handler = null)
    {
        _settings = settings;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
        Session = new AccountSession(
            AccountPlatform.Xbox,
            "Xbox",
            "Microsoft account · Xbox Live",
            "XUID",
            context);
        RefreshSetupState();
    }

    public AccountSession Session { get; }

    public string ClientId => (_settings.Current.Xbox.ClientId ?? string.Empty).Trim();

    /// <summary>Shows the setup panel when no client ID is configured; never hides a signed-in session.</summary>
    public void RefreshSetupState()
    {
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            if (!Session.IsSignedIn)
            {
                Session.SetSignedOut("Xbox sign-in needs a Microsoft Entra app registration client ID.");
            }

            Session.SetSetup(true, SetupHelp);
            return;
        }

        Session.SetSetup(false, string.Empty);
        if (Session.Status == AccountStatus.SignedOut && string.IsNullOrWhiteSpace(Session.StatusText))
        {
            Session.SetSignedOut("Not signed in.");
        }
    }

    /// <summary>Saves the public Application (client) ID used for Microsoft's device-code flow.</summary>
    public bool SetClientId(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (!Guid.TryParse(text, out var parsed))
        {
            Session.SetSetup(
                true,
                "That does not look like an Application (client) ID. Paste the GUID from the Azure app registration.");
            return false;
        }

        var xbox = _settings.Current.Xbox;
        if (!string.Equals(xbox.ClientId, parsed.ToString(), StringComparison.OrdinalIgnoreCase) &&
            Session.IsSignedIn)
        {
            SignOut();
        }

        xbox.ClientId = parsed.ToString();
        _settings.Save();
        Session.SetSetup(false, string.Empty);
        Session.SetSignedOut("Microsoft client ID saved. Sign in to Xbox.");
        return true;
    }

    public async Task RestoreAsync(CancellationToken token)
    {
        var xbox = _settings.Current.Xbox;
        RefreshSetupState();

        if (string.IsNullOrWhiteSpace(xbox.RefreshTokenProtected))
        {
            if (!string.IsNullOrWhiteSpace(xbox.Gamertag))
            {
                Session.SetSignedOut("Xbox session expired. Sign in again.");
            }
            else if (!string.IsNullOrWhiteSpace(ClientId))
            {
                Session.SetSignedOut("Not signed in.");
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(ClientId))
        {
            Session.SetSetup(true, SetupHelp);
            return;
        }

        Session.SetSigningIn("Restoring your Xbox Live sign-in…");
        try
        {
            var refresh = SecretProtector.Unprotect(xbox.RefreshTokenProtected);
            if (string.IsNullOrWhiteSpace(refresh))
            {
                throw new AccountAuthException(
                    "Microsoft sign-in",
                    "The saved Xbox sign-in could not be read. Sign in again.",
                    invalidCredentials: true);
            }

            var tokens = await RefreshMicrosoftTokenAsync(refresh, token).ConfigureAwait(false);
            Session.SetSigningIn("Microsoft sign-in restored. Retrieving your Xbox Live profile…");
            var profile = await GetProfileAsync(tokens.AccessToken, token).ConfigureAwait(false);
            var persisted = Persist(tokens.RefreshToken, profile);
            Session.SetSignedIn(
                profile.Gamertag,
                profile.Xuid,
                persisted
                    ? "Signed in to Xbox Live."
                    : "Signed in to Xbox Live. The token could not be stored for the next launch.");
        }
        catch (AccountAuthException ex) when (ex.InvalidCredentials)
        {
            ClearTokens();
            Session.SetExpired(xbox.Gamertag, "Xbox session expired. Sign in again.");
        }
        catch (OperationCanceledException)
        {
            Session.SetSignedOut("Xbox sign-in restore was interrupted.");
        }
        catch (Exception ex)
        {
            // A transient Xbox Live problem must not erase a valid local account.
            if (!string.IsNullOrWhiteSpace(xbox.Gamertag))
            {
                Session.SetSignedIn(
                    xbox.Gamertag,
                    xbox.Xuid,
                    "Signed in — Xbox Live could not be reached; using the last verified account.");
            }
            else
            {
                Session.SetError(
                    "Xbox Live",
                    "Could not contact Xbox Live to restore your sign-in: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Starts Microsoft's device-code flow, opens the verification page, polls for approval, then
    /// resolves the Gamertag through Xbox Live. Pending authorization is a normal state, not an error.
    /// </summary>
    public async Task SignInAsync(IAccountInteraction interaction, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            RefreshSetupState();
            return;
        }

        Session.SetSetup(false, string.Empty);
        Session.SetSigningIn("Requesting a Microsoft sign-in code…");
        try
        {
            var code = await RequestDeviceCodeAsync(token).ConfigureAwait(false);
            Session.SetDeviceCode(code.UserCode, code.VerificationUri);
            Session.SetSigningIn("Waiting for you to authorize FIFINE in the Microsoft sign-in page…");
            interaction.OpenExternal(new Uri(code.VerificationUri));

            var tokens = await PollForTokenAsync(code, token).ConfigureAwait(false);
            Session.SetSigningIn("Microsoft sign-in complete. Retrieving your Xbox Live profile…");
            var profile = await GetProfileAsync(tokens.AccessToken, token).ConfigureAwait(false);
            var persisted = Persist(tokens.RefreshToken, profile);
            Session.SetSignedIn(
                profile.Gamertag,
                profile.Xuid,
                persisted
                    ? "Signed in to Xbox Live."
                    : "Signed in to Xbox Live. The token could not be stored for the next launch.");
        }
        catch (AccountAuthException ex)
        {
            Session.SetError(ex.Stage, ex.Message);
        }
        catch (OperationCanceledException)
        {
            Session.SetSignedOut("Xbox sign-in was cancelled.");
        }
        catch (Exception ex)
        {
            Session.SetError("Xbox sign-in", ex.Message);
        }
    }

    public void SignOut()
    {
        var xbox = _settings.Current.Xbox;
        xbox.Gamertag = string.Empty;
        xbox.Xuid = string.Empty;
        xbox.RefreshTokenProtected = string.Empty;
        xbox.Token = string.Empty;
        _settings.Save();
        Session.SetSignedOut("Signed out of Xbox Live.");
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            Session.SetSetup(true, SetupHelp);
        }
    }

    public void Dispose() => _http.Dispose();

    private async Task<XboxDeviceCodeInfo> RequestDeviceCodeAsync(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, DeviceCodeUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["scope"] = Scope
            })
        };

        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = ReadError(body);
            var message = string.Equals(error, "invalid_scope", StringComparison.OrdinalIgnoreCase)
                ? "Microsoft rejected the XboxLive.signin permission. Add/consent to it on the app registration, then try again."
                : $"Microsoft rejected the sign-in request ({(int)response.StatusCode}). Check the Application (client) ID and that Allow public client flows is enabled.";
            throw new AccountAuthException("Microsoft sign-in", message);
        }

        using var document = ParseJson(body, "Microsoft sign-in");
        var root = document.RootElement;
        var deviceCode = ReadString(root, "device_code");
        var userCode = ReadString(root, "user_code");
        var verificationUri = ReadString(root, "verification_uri");
        if (string.IsNullOrWhiteSpace(verificationUri))
        {
            verificationUri = ReadString(root, "verification_url");
        }

        if (string.IsNullOrWhiteSpace(deviceCode) ||
            string.IsNullOrWhiteSpace(userCode) ||
            string.IsNullOrWhiteSpace(verificationUri))
        {
            throw new AccountAuthException("Microsoft sign-in", "Microsoft did not return a sign-in code.");
        }

        return new XboxDeviceCodeInfo(
            deviceCode,
            userCode,
            verificationUri,
            ReadInt(root, "expires_in", 900),
            ReadInt(root, "interval", 5));
    }

    private async Task<MicrosoftTokens> PollForTokenAsync(XboxDeviceCodeInfo code, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, code.ExpiresInSeconds));
        var interval = Math.Max(1, code.IntervalSeconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), token).ConfigureAwait(false);

            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = ClientId,
                    ["device_code"] = code.DeviceCode
                })
            };

            using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using var document = ParseJson(body, "Microsoft token exchange");
                var root = document.RootElement;
                var access = ReadString(root, "access_token");
                var refresh = ReadString(root, "refresh_token");
                if (string.IsNullOrWhiteSpace(access))
                {
                    throw new AccountAuthException("Microsoft token exchange", "Microsoft did not return an access token.");
                }

                return new MicrosoftTokens(
                    access,
                    refresh,
                    DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(ReadInt(root, "expires_in", 3600), 60, 86_400)));
            }

            var error = ReadError(body);
            switch (error)
            {
                case "authorization_pending":
                    Session.SetSigningIn("Waiting for you to authorize FIFINE in the Microsoft sign-in page…");
                    continue;
                case "slow_down":
                    interval += 5;
                    continue;
                case "expired_token":
                    throw new AccountAuthException(
                        "Microsoft sign-in",
                        "The Microsoft sign-in code expired. Start the sign-in again.",
                        invalidCredentials: true);
                case "authorization_declined":
                    throw new AccountAuthException("Microsoft sign-in", "The Microsoft sign-in was declined.", true);
                case "bad_verification_code":
                    throw new AccountAuthException("Microsoft sign-in", "Microsoft rejected the sign-in code.", true);
                case "invalid_grant":
                    throw new AccountAuthException(
                        "Microsoft sign-in",
                        "The Microsoft sign-in is no longer valid. Start it again.",
                        invalidCredentials: true);
                default:
                    throw new AccountAuthException(
                        "Microsoft sign-in",
                        string.IsNullOrWhiteSpace(error)
                            ? $"Microsoft's sign-in poll failed ({(int)response.StatusCode})."
                            : "Microsoft sign-in failed: " + error);
            }
        }

        throw new AccountAuthException(
            "Microsoft sign-in",
            "The Microsoft sign-in code expired before it was approved. Start again.",
            invalidCredentials: true);
    }

    private async Task<MicrosoftTokens> RefreshMicrosoftTokenAsync(string refreshToken, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["refresh_token"] = refreshToken,
                ["scope"] = Scope
            })
        };

        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = ReadError(body);
            var invalid = string.Equals(error, "invalid_grant", StringComparison.OrdinalIgnoreCase) ||
                          response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized;
            throw new AccountAuthException(
                "Microsoft sign-in",
                invalid
                    ? "The saved Microsoft sign-in expired. Sign in again."
                    : $"Microsoft's token refresh failed ({(int)response.StatusCode}).",
                invalid);
        }

        using var document = ParseJson(body, "Microsoft token refresh");
        var root = document.RootElement;
        var access = ReadString(root, "access_token");
        if (string.IsNullOrWhiteSpace(access))
        {
            throw new AccountAuthException("Microsoft token refresh", "Microsoft did not return an access token.", true);
        }

        return new MicrosoftTokens(
            access,
            ReadString(root, "refresh_token"),
            DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(ReadInt(root, "expires_in", 3600), 60, 86_400)));
    }

    private async Task<XboxProfile> GetProfileAsync(string microsoftAccessToken, CancellationToken token)
    {
        var xboxToken = await AuthenticateWithXboxLiveAsync(microsoftAccessToken, token).ConfigureAwait(false);
        var xsts = await AuthorizeWithXstsAsync(xboxToken.Token, token).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, ProfileUrl);
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"XBL3.0 x={xsts.UserHash};{xsts.Token}");
        request.Headers.TryAddWithoutValidation("x-xbl-contract-version", "2");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new AccountAuthException(
                "Xbox profile",
                $"Xbox Live accepted the sign-in but the Gamertag request failed ({(int)response.StatusCode}).");
        }

        using var document = ParseJson(body, "Xbox profile");
        if (!document.RootElement.TryGetProperty("profileUsers", out var users) ||
            users.ValueKind != JsonValueKind.Array ||
            users.GetArrayLength() == 0)
        {
            throw new AccountAuthException("Xbox profile", "Xbox Live did not return a profile.");
        }

        var user = users[0];
        var xuid = ReadString(user, "id");
        var gamertag = string.Empty;
        if (user.TryGetProperty("settings", out var settings) && settings.ValueKind == JsonValueKind.Array)
        {
            foreach (var setting in settings.EnumerateArray())
            {
                if (string.Equals(ReadString(setting, "id"), "Gamertag", StringComparison.OrdinalIgnoreCase))
                {
                    gamertag = ReadString(setting, "value");
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(gamertag))
        {
            throw new AccountAuthException("Xbox profile", "Xbox Live did not return a Gamertag.");
        }

        return new XboxProfile(gamertag, xuid);
    }

    private async Task<XboxToken> AuthenticateWithXboxLiveAsync(string microsoftAccessToken, CancellationToken token)
    {
        var body = new
        {
            RpsTicket = "d=" + microsoftAccessToken,
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT",
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com"
            }
        };

        var response = await PostJsonAsync(XboxUserAuthUrl, body, token).ConfigureAwait(false);
        if (response.Status != HttpStatusCode.OK)
        {
            throw new AccountAuthException(
                "Xbox Live",
                $"Xbox Live rejected the Microsoft sign-in ({(int)response.Status}).");
        }

        using var document = ParseJson(response.Body, "Xbox Live");
        return new XboxToken(
            ReadString(document.RootElement, "Token"),
            ReadUserHash(document.RootElement));
    }

    private async Task<XboxToken> AuthorizeWithXstsAsync(string xboxToken, CancellationToken token)
    {
        var body = new
        {
            RelyingParty = "http://xboxlive.com",
            TokenType = "JWT",
            Properties = new
            {
                UserTokens = new[] { xboxToken },
                SandboxId = "RETAIL"
            }
        };

        var response = await PostJsonAsync(XstsUrl, body, token).ConfigureAwait(false);
        if (response.Status != HttpStatusCode.OK)
        {
            throw new AccountAuthException("Xbox Live", XstsError(response.Body, response.Status));
        }

        using var document = ParseJson(response.Body, "Xbox Live");
        return new XboxToken(
            ReadString(document.RootElement, "Token"),
            ReadUserHash(document.RootElement));
    }

    private async Task<(HttpStatusCode Status, string Body)> PostJsonAsync(
        string url,
        object body,
        CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };

        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
    }

    private static string ReadUserHash(JsonElement root)
    {
        if (root.TryGetProperty("DisplayClaims", out var claims) &&
            claims.TryGetProperty("xui", out var xui) &&
            xui.ValueKind == JsonValueKind.Array &&
            xui.GetArrayLength() > 0)
        {
            return ReadString(xui[0], "uhs");
        }

        return string.Empty;
    }

    private static string XstsError(string body, HttpStatusCode status)
    {
        var code = 0L;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("XErr", out var error))
            {
                _ = long.TryParse(error.ValueKind == JsonValueKind.Number
                    ? error.GetRawText()
                    : error.GetString(), out code);
            }
        }
        catch (JsonException)
        {
            // The status code still tells the user where the failure happened.
        }

        return code switch
        {
            2148916233 => "This Microsoft account does not have an Xbox profile yet. Create one on an Xbox console or xbox.com, then try again.",
            2148916235 => "Xbox Live is not available in this account's country or region.",
            2148916238 => "This is a child account. An adult must add it to a Microsoft family group first.",
            2148916236 or 2148916237 => "This account needs adult verification before Xbox Live can be used.",
            _ => $"Xbox Live rejected the sign-in ({(int)status}{(code == 0 ? string.Empty : ", XErr " + code)})."
        };
    }

    private bool Persist(string refreshToken, XboxProfile profile)
    {
        var xbox = _settings.Current.Xbox;
        xbox.Gamertag = profile.Gamertag;
        xbox.Xuid = profile.Xuid;
        xbox.RefreshTokenProtected = SecretProtector.Protect(refreshToken);
        xbox.Token = string.Empty;
        _settings.Save();
        return !string.IsNullOrWhiteSpace(xbox.RefreshTokenProtected);
    }

    private void ClearTokens()
    {
        var xbox = _settings.Current.Xbox;
        xbox.RefreshTokenProtected = string.Empty;
        xbox.Token = string.Empty;
        _settings.Save();
    }

    private static JsonDocument ParseJson(string body, string stage)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new AccountAuthException(stage, "The provider returned an unreadable response.", inner: ex);
        }
    }

    private static string ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? ReadString(value) : string.Empty;

    private static string ReadString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Number => element.GetRawText(),
        _ => string.Empty
    };

    private static int ReadInt(JsonElement element, string name, int fallback)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : int.TryParse(ReadString(value), out var parsed)
                ? parsed
                : fallback;
    }

    private static string ReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return ReadString(document.RootElement, "error");
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private sealed record XboxToken(string Token, string UserHash);
    private sealed record MicrosoftTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
}
