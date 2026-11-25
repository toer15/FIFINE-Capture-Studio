using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FifineCaptureStudio.Core.Accounts;

/// <summary>
/// Sony's own PlayStation Network OAuth flow, as used by the Remote Play Windows client. The
/// account-info response contains both the numeric user id (the PS5 registration id) and the
/// actual Online ID, so the signed-in panel can show a real account name. Tokens are returned to
/// the caller for DPAPI protection; nothing here writes them to disk or logs them.
/// </summary>
public sealed class PsnAccountLookup : IDisposable
{
    private const string ClientId = "ba495a24-818c-472b-b12d-ff231c1b5745";
    private const string ClientSecret = "mvaiZkRsAsI1IBkY";
    private const string RedirectUri = "https://remoteplay.dl.playstation.net/remoteplay/redirect";
    private const string TokenUrl = "https://auth.api.sonyentertainmentnetwork.com/2.0/oauth/token";
    private const string LegacyProfileUrl =
        "https://us-prof.np.community.playstation.net/userProfile/v1/users/me/profile2?fields=onlineId";

    public const string RedirectPrefix = RedirectUri;

    public static string LoginUrl { get; } =
        "https://auth.api.sonyentertainmentnetwork.com/2.0/oauth/authorize" +
        "?service_entity=urn:service-entity:psn&response_type=code" +
        "&client_id=" + ClientId +
        "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
        "&scope=psn:clientapp&request_locale=en_US&ui=pr&service_logo=ps" +
        "&layout_type=popup&smcid=remoteplay&prompt=always&PlatformPrivacyWs1=minimal";

    private readonly HttpClient _http;

    public PsnAccountLookup(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Exchanges the OAuth redirect code and fetches the real Online ID.</summary>
    public async Task<PsnAuthResult> ExchangeCodeAsync(string code, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new AccountAuthException("PlayStation sign-in", "Sony did not return a sign-in code.", true);
        }

        var tokens = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = RedirectUri
        }, token).ConfigureAwait(false);

        var account = await GetAccountAsync(tokens.AccessToken, token).ConfigureAwait(false);
        return new PsnAuthResult(account.AccountId, account.OnlineId, tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAt);
    }

    /// <summary>Refreshes a saved PSN session and re-reads the Online ID from Sony.</summary>
    public async Task<PsnAuthResult> RefreshAsync(string refreshToken, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new AccountAuthException("PlayStation Network", "No saved PlayStation Network refresh token.", true);
        }

        var tokens = await RequestTokenAsync(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["redirect_uri"] = RedirectUri,
            ["scope"] = "psn:clientapp"
        }, token).ConfigureAwait(false);

        var account = await GetAccountAsync(tokens.AccessToken, token).ConfigureAwait(false);
        return new PsnAuthResult(
            account.AccountId,
            account.OnlineId,
            tokens.AccessToken,
            string.IsNullOrWhiteSpace(tokens.RefreshToken) ? refreshToken : tokens.RefreshToken,
            tokens.ExpiresAt);
    }

    /// <summary>Reads user_id and online_id for an access token. 401/403 means the token is no longer valid.</summary>
    public async Task<PsnAccountInfo> GetAccountAsync(string accessToken, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new AccountAuthException("PlayStation Network", "Sony did not return an access token.", true);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, TokenUrl + "/" + Uri.EscapeDataString(accessToken));
        request.Headers.Authorization = BasicAuth();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new AccountAuthException(
                "PlayStation Network",
                "Sony rejected the saved PlayStation Network sign-in. Sign in again.",
                invalidCredentials: true);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new AccountAuthException(
                "PlayStation Network",
                $"Sony's account lookup failed ({(int)response.StatusCode}).");
        }

        using var document = ParseJson(body, "Sony's account lookup");
        var root = document.RootElement;

        if (!root.TryGetProperty("user_id", out var userIdElement) ||
            !ulong.TryParse(ScalarText(userIdElement), out var userId))
        {
            throw new AccountAuthException("PlayStation profile", "Sony did not return a usable PSN account ID.");
        }

        var onlineId = root.TryGetProperty("online_id", out var onlineElement)
            ? ScalarText(onlineElement)
            : string.Empty;

        if (string.IsNullOrWhiteSpace(onlineId))
        {
            onlineId = await TryReadLegacyOnlineIdAsync(accessToken, token).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(onlineId))
        {
            throw new AccountAuthException(
                "PlayStation profile",
                "Sony signed you in but did not return an Online ID. Try signing in again.");
        }

        return new PsnAccountInfo(AccountIdFromUserId(userId), onlineId.Trim());
    }

    private async Task<string> TryReadLegacyOnlineIdAsync(string accessToken, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LegacyProfileUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return string.Empty;
            }

            var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("profile", out var profile) &&
                profile.TryGetProperty("onlineId", out var onlineId))
            {
                return ScalarText(onlineId);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warn("Sony's legacy profile lookup did not return an Online ID: " + ex.Message);
        }

        return string.Empty;
    }

    private async Task<PsnTokens> RequestTokenAsync(Dictionary<string, string> form, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Authorization = BasicAuth();

        using var response = await _http.SendAsync(request, token).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = string.Empty;
            try
            {
                using var errorDocument = JsonDocument.Parse(body);
                if (errorDocument.RootElement.TryGetProperty("error", out var errorElement))
                {
                    error = ScalarText(errorElement);
                }
            }
            catch (JsonException)
            {
                // Sony sometimes returns an HTML error page; the status code is the useful part.
            }

            var invalid = response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ||
                          string.Equals(error, "invalid_grant", StringComparison.OrdinalIgnoreCase);
            var message = string.Equals(error, "invalid_grant", StringComparison.OrdinalIgnoreCase)
                ? "The saved PlayStation Network session expired. Sign in again."
                : $"Sony rejected the PlayStation Network sign-in ({(int)response.StatusCode}). Try again with a fresh code.";
            throw new AccountAuthException("Sony token exchange", message, invalid);
        }

        using var document = ParseJson(body, "Sony's token exchange");
        var root = document.RootElement;
        var accessToken = root.TryGetProperty("access_token", out var accessElement)
            ? ScalarText(accessElement)
            : string.Empty;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new AccountAuthException("Sony token exchange", "Sony did not return an access token.", true);
        }

        var refreshToken = root.TryGetProperty("refresh_token", out var refreshElement)
            ? ScalarText(refreshElement)
            : string.Empty;
        var expiresIn = root.TryGetProperty("expires_in", out var expiresElement) &&
                        int.TryParse(ScalarText(expiresElement), out var parsed)
            ? parsed
            : 3600;

        return new PsnTokens(
            accessToken,
            refreshToken,
            DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(expiresIn, 60, 86_400)));
    }

    private static AuthenticationHeaderValue BasicAuth() =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(ClientId + ":" + ClientSecret)));

    private static JsonDocument ParseJson(string body, string stage)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new AccountAuthException(stage, "Sony returned an unreadable response.", inner: ex);
        }
    }

    private static string ScalarText(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.Number => element.GetRawText(),
        _ => string.Empty
    };

    private static string AccountIdFromUserId(ulong userId)
    {
        var bytes = BitConverter.GetBytes(userId);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return Convert.ToBase64String(bytes);
    }

    private sealed record PsnTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
}
