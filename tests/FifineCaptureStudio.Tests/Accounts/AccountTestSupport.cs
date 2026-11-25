using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.Accounts;

namespace FifineCaptureStudio.Tests.Accounts;

internal static class AccountTestSupport
{
    public static SettingsService NewSettings(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "FifineAccountTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new SettingsService(root);
    }

    public static void Cleanup(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch
        {
            // A test must not fail because a temp file is still held briefly.
        }
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    public static string XboxDeviceCodeBody(int interval = 1) =>
        "{\"device_code\":\"device\",\"user_code\":\"ABCD-EFGH\",\"verification_uri\":\"https://microsoft.com/devicelogin\",\"expires_in\":60,\"interval\":" +
        interval + "}";

    public static string MicrosoftTokenBody(string access = "ms-access", string refresh = "ms-refresh") =>
        "{\"access_token\":\"" + access + "\",\"refresh_token\":\"" + refresh + "\",\"expires_in\":3600}";

    public static string XboxLiveBody() =>
        "{\"Token\":\"xbl-token\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"user-hash\"}]}}";

    public static string XstsBody() =>
        "{\"Token\":\"xsts-token\",\"DisplayClaims\":{\"xui\":[{\"uhs\":\"user-hash\"}]}}";

    public static string XboxProfileBody(string gamertag = "ActualGamertag", string xuid = "2533274800000000") =>
        "{\"profileUsers\":[{\"id\":\"" + xuid + "\",\"settings\":[{\"id\":\"Gamertag\",\"value\":\"" + gamertag + "\"}]}]}";

    public static string PsnTokenBody(string access = "psn-access", string refresh = "psn-refresh") =>
        "{\"access_token\":\"" + access + "\",\"refresh_token\":\"" + refresh + "\",\"expires_in\":3600}";

    public static string PsnAccountBody(string onlineId = "ActualOnlineId", long userId = 123456789) =>
        "{\"user_id\":" + userId + ",\"online_id\":\"" + onlineId + "\"}";
}

internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, string, Task<HttpResponseMessage>> _handler;

    public FakeHttpMessageHandler(Func<HttpRequestMessage, string, Task<HttpResponseMessage>> handler)
    {
        _handler = handler;
    }

    public List<string> Requests { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var body = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(request.RequestUri?.AbsoluteUri ?? string.Empty);
        return await _handler(request, body);
    }
}

internal sealed class FakeAccountInteraction : IAccountInteraction
{
    public PsnAuthResult? PsnResult { get; set; }
    public bool OpenedExternal { get; private set; }
    public Uri? OpenedUri { get; private set; }

    public Task<PsnAuthResult?> SignInPlayStationAsync(CancellationToken token) =>
        Task.FromResult(PsnResult);

    public void OpenExternal(Uri uri)
    {
        OpenedExternal = true;
        OpenedUri = uri;
    }
}
