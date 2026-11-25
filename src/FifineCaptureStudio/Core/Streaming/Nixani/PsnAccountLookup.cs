using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FifineCaptureStudio.Core.Streaming.Nixani;

/// <summary>
/// One-time PlayStation Network sign-in used to obtain the account ID a PS5 requires when it
/// accepts a Remote Play link code. The account ID is a numeric PSN user id encoded as its eight
/// little-endian bytes in base64, which is what chiaki and the engine expect. Only that ID is kept;
/// the OAuth token is discarded after the lookup.
/// </summary>
public static class PsnAccountLookup
{
    // Sony's Remote Play Windows client credentials, as published by chiaki-ng's
    // psn-account-id script. This is a public OAuth client, not a user secret.
    private const string ClientId = "ba495a24-818c-472b-b12d-ff231c1b5745";
    private const string ClientSecret = "mvaiZkRsAsI1IBkY";
    private const string RedirectUri = "https://remoteplay.dl.playstation.net/remoteplay/redirect";
    private const string TokenUrl = "https://auth.api.sonyentertainmentnetwork.com/2.0/oauth/token";

    public const string RedirectPrefix = "https://remoteplay.dl.playstation.net/remoteplay/redirect";

    public static string LoginUrl { get; } =
        "https://auth.api.sonyentertainmentnetwork.com/2.0/oauth/authorize" +
        "?service_entity=urn:service-entity:psn&response_type=code" +
        "&client_id=" + ClientId +
        "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
        "&scope=psn:clientapp&request_locale=en_US&ui=pr&service_logo=ps" +
        "&layout_type=popup&smcid=remoteplay&prompt=always&PlatformPrivacyWs1=minimal";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>Exchanges the OAuth redirect code for the PSN account ID (base64, 8 bytes little-endian).</summary>
    public static async Task<string> ExchangeCodeAsync(string code, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException("PlayStation Network did not return a sign-in code.");
        }

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(ClientId + ":" + ClientSecret));
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri
            })
        };
        tokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);

        using var tokenResponse = await Http.SendAsync(tokenRequest, token).ConfigureAwait(false);
        var tokenBody = await tokenResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"PlayStation Network rejected the sign-in ({(int)tokenResponse.StatusCode}). Try again with a fresh code.");
        }

        string accessToken;
        using (var document = JsonDocument.Parse(tokenBody))
        {
            accessToken = document.RootElement.TryGetProperty("access_token", out var element)
                ? element.GetString() ?? string.Empty
                : string.Empty;
        }

        if (accessToken.Length == 0)
        {
            throw new InvalidOperationException("PlayStation Network did not return an access token.");
        }

        using var accountRequest = new HttpRequestMessage(HttpMethod.Get, TokenUrl + "/" + Uri.EscapeDataString(accessToken));
        accountRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
        accountRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var accountResponse = await Http.SendAsync(accountRequest, token).ConfigureAwait(false);
        var accountBody = await accountResponse.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        if (!accountResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"PlayStation Network account lookup failed ({(int)accountResponse.StatusCode}).");
        }

        using var accountDocument = JsonDocument.Parse(accountBody);
        if (!accountDocument.RootElement.TryGetProperty("user_id", out var userIdElement))
        {
            throw new InvalidOperationException("PlayStation Network did not return a user id.");
        }

        var text = userIdElement.ValueKind == JsonValueKind.String
            ? userIdElement.GetString()
            : userIdElement.GetRawText();
        if (!ulong.TryParse(text, out var userId))
        {
            throw new InvalidOperationException("PlayStation Network returned an unreadable user id.");
        }

        var bytes = BitConverter.GetBytes(userId);
        if (!BitConverter.IsLittleEndian)
        {
            Array.Reverse(bytes);
        }

        return Convert.ToBase64String(bytes);
    }
}
