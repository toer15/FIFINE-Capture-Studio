using System.Net;
using System.Net.Http;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.Accounts;
using Xunit;

namespace FifineCaptureStudio.Tests.Accounts;

public class XboxAccountProviderTests
{
    [Fact]
    public async Task Sign_in_waits_through_pending_authorization_then_shows_the_gamertag()
    {
        var settings = AccountTestSupport.NewSettings(out var root);
        try
        {
            var polls = 0;
            var handler = new FakeHttpMessageHandler((request, body) =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                if (url.Contains("devicecode", StringComparison.Ordinal))
                {
                    return Task.FromResult(AccountTestSupport.Json(AccountTestSupport.XboxDeviceCodeBody()));
                }

                if (url.EndsWith("/token", StringComparison.Ordinal) && body.Contains("device_code", StringComparison.Ordinal))
                {
                    polls++;
                    return Task.FromResult(polls == 1
                        ? AccountTestSupport.Json("{\"error\":\"authorization_pending\"}", HttpStatusCode.BadRequest)
                        : AccountTestSupport.Json(AccountTestSupport.MicrosoftTokenBody()));
                }

                if (url.Contains("user.auth.xboxlive.com", StringComparison.Ordinal))
                {
                    return Task.FromResult(AccountTestSupport.Json(AccountTestSupport.XboxLiveBody()));
                }

                if (url.Contains("xsts.auth.xboxlive.com", StringComparison.Ordinal))
                {
                    return Task.FromResult(AccountTestSupport.Json(AccountTestSupport.XstsBody()));
                }

                if (url.Contains("profile.xboxlive.com", StringComparison.Ordinal))
                {
                    return Task.FromResult(AccountTestSupport.Json(AccountTestSupport.XboxProfileBody("ActualGamertag")));
                }

                return Task.FromResult(AccountTestSupport.Json("{}", HttpStatusCode.NotFound));
            });

            settings.Current.Xbox.ClientId = Guid.NewGuid().ToString();
            settings.Save();

            using var provider = new XboxAccountProvider(settings, null, handler);
            var interaction = new FakeAccountInteraction();
            await provider.SignInAsync(interaction, CancellationToken.None);

            Assert.True(provider.Session.ShowSignedIn);
            Assert.Equal("ActualGamertag", provider.Session.DisplayName);
            Assert.Equal("Xbox account: ActualGamertag", provider.Session.AccountLine);
            Assert.Equal("2533274800000000", settings.Current.Xbox.Xuid);
            Assert.NotEqual("ms-refresh", settings.Current.Xbox.RefreshTokenProtected);
            Assert.Equal("ms-refresh", SecretProtector.Unprotect(settings.Current.Xbox.RefreshTokenProtected));

            var reloaded = new SettingsService(root);
            Assert.Equal("ActualGamertag", reloaded.Current.Xbox.Gamertag);
            Assert.Equal("ms-refresh", SecretProtector.Unprotect(reloaded.Current.Xbox.RefreshTokenProtected));
            Assert.True(interaction.OpenedExternal);
            Assert.Equal("https://microsoft.com/devicelogin", interaction.OpenedUri?.AbsoluteUri);
            Assert.Equal(2, polls);
        }
        finally
        {
            AccountTestSupport.Cleanup(root);
        }
    }

    [Fact]
    public async Task Profile_failure_is_reported_as_profile_failure_not_a_successful_sign_in()
    {
        var settings = AccountTestSupport.NewSettings(out var root);
        try
        {
            var handler = new FakeHttpMessageHandler((request, body) =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                if (url.Contains("devicecode", StringComparison.Ordinal))
                {
                    return Task.FromResult(AccountTestSupport.Json(AccountTestSupport.XboxDeviceCodeBody()));
                }

                if (url.EndsWith("/token", StringComparison.Ordinal))
                {
                    return Task.FromResult(AccountTestSupport.Json(AccountTestSupport.MicrosoftTokenBody()));
                }

                if (url.Contains("profile.xboxlive.com", StringComparison.Ordinal))
                {
                    return Task.FromResult(AccountTestSupport.Json("{}", HttpStatusCode.InternalServerError));
                }

                return Task.FromResult(AccountTestSupport.Json(
                    url.Contains("xsts", StringComparison.Ordinal)
                        ? AccountTestSupport.XstsBody()
                        : AccountTestSupport.XboxLiveBody()));
            });

            settings.Current.Xbox.ClientId = Guid.NewGuid().ToString();
            settings.Save();

            using var provider = new XboxAccountProvider(settings, null, handler);
            await provider.SignInAsync(new FakeAccountInteraction(), CancellationToken.None);

            Assert.True(provider.Session.ShowError);
            Assert.False(provider.Session.ShowSignedIn);
            Assert.StartsWith("Xbox profile:", provider.Session.StatusText);
            Assert.Equal(string.Empty, settings.Current.Xbox.RefreshTokenProtected);
        }
        finally
        {
            AccountTestSupport.Cleanup(root);
        }
    }

    [Fact]
    public async Task Restore_refreshes_the_session_and_updates_the_gamertag()
    {
        var settings = AccountTestSupport.NewSettings(out var root);
        try
        {
            var sawRefreshGrant = false;
            var handler = new FakeHttpMessageHandler((request, body) =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                if (url.EndsWith("/token", StringComparison.Ordinal))
                {
                    sawRefreshGrant = body.Contains("grant_type=refresh_token", StringComparison.Ordinal) &&
                                      body.Contains("refresh_token=saved-refresh", StringComparison.Ordinal);
                    return Task.FromResult(AccountTestSupport.Json(AccountTestSupport.MicrosoftTokenBody()));
                }

                if (url.Contains("user.auth.xboxlive.com", StringComparison.Ordinal))
                {
                    return Task.FromResult(AccountTestSupport.Json(AccountTestSupport.XboxLiveBody()));
                }

                if (url.Contains("xsts.auth.xboxlive.com", StringComparison.Ordinal))
                {
                    return Task.FromResult(AccountTestSupport.Json(AccountTestSupport.XstsBody()));
                }

                if (url.Contains("profile.xboxlive.com", StringComparison.Ordinal))
                {
                    return Task.FromResult(AccountTestSupport.Json(AccountTestSupport.XboxProfileBody("RestoredGamertag")));
                }

                return Task.FromResult(AccountTestSupport.Json("{}", HttpStatusCode.NotFound));
            });

            var xbox = settings.Current.Xbox;
            xbox.ClientId = Guid.NewGuid().ToString();
            xbox.Gamertag = "OldGamertag";
            xbox.Xuid = "old-xuid";
            xbox.RefreshTokenProtected = SecretProtector.Protect("saved-refresh");
            settings.Save();

            var reloaded = new SettingsService(root);
            using var provider = new XboxAccountProvider(reloaded, null, handler);
            await provider.RestoreAsync(CancellationToken.None);

            Assert.True(provider.Session.ShowSignedIn);
            Assert.Equal("RestoredGamertag", provider.Session.DisplayName);
            Assert.Equal("RestoredGamertag", reloaded.Current.Xbox.Gamertag);
            Assert.True(sawRefreshGrant);
        }
        finally
        {
            AccountTestSupport.Cleanup(root);
        }
    }

    [Fact]
    public async Task Network_failure_keeps_the_last_verified_xbox_account_signed_in()
    {
        var settings = AccountTestSupport.NewSettings(out var root);
        try
        {
            var xbox = settings.Current.Xbox;
            xbox.ClientId = Guid.NewGuid().ToString();
            xbox.Gamertag = "CachedGamertag";
            xbox.Xuid = "cached-xuid";
            xbox.RefreshTokenProtected = SecretProtector.Protect("saved-refresh");
            settings.Save();

            var handler = new FakeHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new HttpRequestException("Xbox Live offline")));

            using var provider = new XboxAccountProvider(settings, null, handler);
            await provider.RestoreAsync(CancellationToken.None);

            Assert.True(provider.Session.ShowSignedIn);
            Assert.Equal("CachedGamertag", provider.Session.DisplayName);
            Assert.Contains("last verified account", provider.Session.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            AccountTestSupport.Cleanup(root);
        }
    }

    [Fact]
    public async Task Expired_refresh_token_keeps_the_last_name_but_clears_the_saved_token()
    {
        var settings = AccountTestSupport.NewSettings(out var root);
        try
        {
            var xbox = settings.Current.Xbox;
            xbox.ClientId = Guid.NewGuid().ToString();
            xbox.Gamertag = "CachedGamertag";
            xbox.Xuid = "cached-xuid";
            xbox.RefreshTokenProtected = SecretProtector.Protect("expired-refresh");
            settings.Current.PlayStation.OnlineId = "PSNName";
            settings.Save();

            var handler = new FakeHttpMessageHandler((_, _) =>
                Task.FromResult(AccountTestSupport.Json("{\"error\":\"invalid_grant\"}", HttpStatusCode.BadRequest)));

            using var provider = new XboxAccountProvider(settings, null, handler);
            await provider.RestoreAsync(CancellationToken.None);

            Assert.True(provider.Session.ShowError);
            Assert.True(provider.Session.ShowLastAccount);
            Assert.Equal("Last account: CachedGamertag", provider.Session.LastAccountLine);
            Assert.Equal(string.Empty, settings.Current.Xbox.RefreshTokenProtected);
            Assert.Equal("PSNName", settings.Current.PlayStation.OnlineId);
        }
        finally
        {
            AccountTestSupport.Cleanup(root);
        }
    }

    [Fact]
    public async Task Missing_client_id_shows_setup_and_never_starts_a_network_sign_in()
    {
        var settings = AccountTestSupport.NewSettings(out var root);
        try
        {
            var handler = new FakeHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new InvalidOperationException("network must not be used")));
            using var provider = new XboxAccountProvider(settings, null, handler);

            Assert.True(provider.Session.ShowSetup);
            Assert.False(provider.Session.ShowSignInButton);

            await provider.SignInAsync(new FakeAccountInteraction(), CancellationToken.None);

            Assert.Empty(handler.Requests);
            Assert.True(provider.Session.ShowSetup);
        }
        finally
        {
            AccountTestSupport.Cleanup(root);
        }
    }

    [Fact]
    public void Invalid_client_id_is_rejected_and_valid_one_unlocks_sign_in()
    {
        var settings = AccountTestSupport.NewSettings(out var root);
        try
        {
            using var provider = new XboxAccountProvider(settings);

            Assert.False(provider.SetClientId("not-a-guid"));
            Assert.True(provider.Session.ShowSetup);

            var id = Guid.NewGuid().ToString();
            Assert.True(provider.SetClientId(id));
            Assert.False(provider.Session.ShowSetup);
            Assert.True(provider.Session.ShowSignInButton);
            Assert.Equal(id, settings.Current.Xbox.ClientId);
        }
        finally
        {
            AccountTestSupport.Cleanup(root);
        }
    }

    [Fact]
    public void Sign_out_clears_only_the_xbox_account()
    {
        var settings = AccountTestSupport.NewSettings(out var root);
        try
        {
            var xbox = settings.Current.Xbox;
            xbox.ClientId = Guid.NewGuid().ToString();
            xbox.Gamertag = "ActualGamertag";
            xbox.Xuid = "xuid";
            xbox.RefreshTokenProtected = SecretProtector.Protect("refresh");
            settings.Current.PlayStation.OnlineId = "PSNName";
            settings.Current.PlayStation.AccountId = "psn-id";
            settings.Save();

            using var provider = new XboxAccountProvider(settings);
            provider.Session.SetSignedIn("ActualGamertag", "xuid", "Signed in.");

            provider.SignOut();

            Assert.True(provider.Session.ShowSignedOut);
            Assert.Equal(string.Empty, settings.Current.Xbox.Gamertag);
            Assert.Equal(string.Empty, settings.Current.Xbox.Xuid);
            Assert.Equal(string.Empty, settings.Current.Xbox.RefreshTokenProtected);
            Assert.Equal("PSNName", settings.Current.PlayStation.OnlineId);
            Assert.Equal("psn-id", settings.Current.PlayStation.AccountId);
            Assert.False(string.IsNullOrWhiteSpace(settings.Current.Xbox.ClientId));
        }
        finally
        {
            AccountTestSupport.Cleanup(root);
        }
    }
}
