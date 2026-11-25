using System.Net;
using System.Net.Http;
using FifineCaptureStudio.Core;
using FifineCaptureStudio.Core.Accounts;
using Xunit;

namespace FifineCaptureStudio.Tests.Accounts;

public class PsnAccountProviderTests
{
    [Fact]
    public async Task Exchange_code_reads_the_real_online_id_and_psn_account_id()
    {
        var handler = new FakeHttpMessageHandler(async (request, body) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Post && url.EndsWith("/2.0/oauth/token", StringComparison.Ordinal))
            {
                Assert.Contains("grant_type=authorization_code", body);
                return AccountTestSupport.Json(AccountTestSupport.PsnTokenBody());
            }

            if (request.Method == HttpMethod.Get && url.EndsWith("/2.0/oauth/token/psn-access", StringComparison.Ordinal))
            {
                return AccountTestSupport.Json(AccountTestSupport.PsnAccountBody("ActualOnlineId", 123456789));
            }

            return AccountTestSupport.Json("{}", HttpStatusCode.NotFound);
        });

        using var lookup = new PsnAccountLookup(handler);
        var result = await lookup.ExchangeCodeAsync("sony-code", CancellationToken.None);

        Assert.Equal("ActualOnlineId", result.OnlineId);
        Assert.Equal(
            Convert.ToBase64String(BitConverter.GetBytes(123456789UL)),
            result.AccountId);
        Assert.Equal("psn-access", result.AccessToken);
        Assert.Equal("psn-refresh", result.RefreshToken);
        Assert.True(result.ExpiresAt > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Missing_online_id_is_reported_as_a_profile_failure()
    {
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Post)
            {
                return Task.FromResult(AccountTestSupport.Json(AccountTestSupport.PsnTokenBody()));
            }

            if (url.Contains("/2.0/oauth/token/psn-access", StringComparison.Ordinal))
            {
                return Task.FromResult(AccountTestSupport.Json("{\"user_id\":123456789}"));
            }

            return Task.FromResult(AccountTestSupport.Json("{}", HttpStatusCode.NotFound));
        });

        using var lookup = new PsnAccountLookup(handler);
        var error = await Assert.ThrowsAsync<AccountAuthException>(
            () => lookup.ExchangeCodeAsync("sony-code", CancellationToken.None));

        Assert.Equal("PlayStation profile", error.Stage);
        Assert.Contains("Online ID", error.Message);
    }

    [Fact]
    public async Task Unauthorized_account_lookup_is_marked_as_invalid_credentials()
    {
        var handler = new FakeHttpMessageHandler((request, _) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Post)
            {
                return Task.FromResult(AccountTestSupport.Json(AccountTestSupport.PsnTokenBody()));
            }

            return Task.FromResult(AccountTestSupport.Json("{}", HttpStatusCode.Unauthorized));
        });

        using var lookup = new PsnAccountLookup(handler);
        var error = await Assert.ThrowsAsync<AccountAuthException>(
            () => lookup.ExchangeCodeAsync("sony-code", CancellationToken.None));

        Assert.True(error.InvalidCredentials);
        Assert.Contains("rejected", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refresh_uses_the_refresh_token_grant()
    {
        var sawRefreshGrant = false;
        var handler = new FakeHttpMessageHandler((request, body) =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (request.Method == HttpMethod.Post)
            {
                sawRefreshGrant = body.Contains("grant_type=refresh_token", StringComparison.Ordinal) &&
                                  body.Contains("refresh_token=saved-refresh", StringComparison.Ordinal);
                return Task.FromResult(AccountTestSupport.Json(AccountTestSupport.PsnTokenBody()));
            }

            if (url.Contains("/2.0/oauth/token/psn-access", StringComparison.Ordinal))
            {
                return Task.FromResult(AccountTestSupport.Json(AccountTestSupport.PsnAccountBody()));
            }

            return Task.FromResult(AccountTestSupport.Json("{}", HttpStatusCode.NotFound));
        });

        using var lookup = new PsnAccountLookup(handler);
        var result = await lookup.RefreshAsync("saved-refresh", CancellationToken.None);

        Assert.True(sawRefreshGrant);
        Assert.Equal("ActualOnlineId", result.OnlineId);
    }

    [Fact]
    public async Task Sign_in_stores_the_online_id_and_tokens_protected()
    {
        var settings = AccountTestSupport.NewSettings(out var root);
        try
        {
            using var provider = new PsnAccountProvider(settings);
            var interaction = new FakeAccountInteraction
            {
                PsnResult = new PsnAuthResult(
                    "account-id",
                    "ActualOnlineId",
                    "access-token",
                    "refresh-token",
                    DateTimeOffset.UtcNow.AddHours(1))
            };

            await provider.SignInAsync(interaction, CancellationToken.None);

            Assert.True(provider.Session.ShowSignedIn);
            Assert.Equal("ActualOnlineId", provider.Session.DisplayName);
            Assert.Equal("PlayStation account: ActualOnlineId", provider.Session.AccountLine);
            Assert.Equal("ActualOnlineId", settings.Current.PlayStation.OnlineId);
            Assert.NotEqual("access-token", settings.Current.PlayStation.AccessTokenProtected);
            Assert.NotEqual("refresh-token", settings.Current.PlayStation.RefreshTokenProtected);
            Assert.Equal("access-token", SecretProtector.Unprotect(settings.Current.PlayStation.AccessTokenProtected));
            Assert.Equal("refresh-token", SecretProtector.Unprotect(settings.Current.PlayStation.RefreshTokenProtected));

            var reloaded = new SettingsService(root);
            Assert.Equal("ActualOnlineId", reloaded.Current.PlayStation.OnlineId);
            Assert.Equal("access-token", SecretProtector.Unprotect(reloaded.Current.PlayStation.AccessTokenProtected));
        }
        finally
        {
            AccountTestSupport.Cleanup(root);
        }
    }

    [Fact]
    public async Task Cancelling_sign_in_is_a_cancel_not_a_connection_failure()
    {
        var settings = AccountTestSupport.NewSettings(out var root);
        try
        {
            using var provider = new PsnAccountProvider(settings);
            var interaction = new FakeAccountInteraction { PsnResult = null };

            await provider.SignInAsync(interaction, CancellationToken.None);

            Assert.True(provider.Session.ShowSignedOut);
            Assert.Contains("cancelled", provider.Session.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Connection failed", provider.Session.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            AccountTestSupport.Cleanup(root);
        }
    }

    [Fact]
    public async Task Restore_refreshes_a_saved_session_and_reads_the_online_id()
    {
        var settings = AccountTestSupport.NewSettings(out var root);
        try
        {
            var ps = settings.Current.PlayStation;
            ps.AccountId = "old-id";
            ps.OnlineId = "CachedName";
            ps.RefreshTokenProtected = SecretProtector.Protect("saved-refresh");
            settings.Save();

            var handler = new FakeHttpMessageHandler((request, _) =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                if (request.Method == HttpMethod.Post)
                {
                    return Task.FromResult(AccountTestSupport.Json(AccountTestSupport.PsnTokenBody()));
                }

                if (url.Contains("/2.0/oauth/token/psn-access", StringComparison.Ordinal))
                {
                    return Task.FromResult(AccountTestSupport.Json(AccountTestSupport.PsnAccountBody("RestoredOnlineId")));
                }

                return Task.FromResult(AccountTestSupport.Json("{}", HttpStatusCode.NotFound));
            });

            var reloaded = new SettingsService(root);
            using var provider = new PsnAccountProvider(reloaded, null, new PsnAccountLookup(handler));
            await provider.RestoreAsync(CancellationToken.None);

            Assert.True(provider.Session.ShowSignedIn);
            Assert.Equal("RestoredOnlineId", provider.Session.DisplayName);
            Assert.Equal("RestoredOnlineId", reloaded.Current.PlayStation.OnlineId);
            Assert.False(string.IsNullOrWhiteSpace(settings.Current.PlayStation.RefreshTokenProtected));
        }
        finally
        {
            AccountTestSupport.Cleanup(root);
        }
    }

    [Fact]
    public async Task Network_failure_keeps_the_last_verified_account_signed_in()
    {
        var settings = AccountTestSupport.NewSettings(out var root);
        try
        {
            var ps = settings.Current.PlayStation;
            ps.AccountId = "account-id";
            ps.OnlineId = "CachedOnlineId";
            ps.RefreshTokenProtected = SecretProtector.Protect("saved-refresh");
            settings.Save();

            var handler = new FakeHttpMessageHandler((_, _) =>
                Task.FromException<HttpResponseMessage>(new HttpRequestException("network offline")));

            using var provider = new PsnAccountProvider(settings, null, new PsnAccountLookup(handler));
            await provider.RestoreAsync(CancellationToken.None);

            Assert.True(provider.Session.ShowSignedIn);
            Assert.Equal("CachedOnlineId", provider.Session.DisplayName);
            Assert.Contains("last verified account", provider.Session.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            AccountTestSupport.Cleanup(root);
        }
    }

    [Fact]
    public async Task Expired_refresh_token_clears_only_the_psn_tokens_and_keeps_the_last_name()
    {
        var settings = AccountTestSupport.NewSettings(out var root);
        try
        {
            var ps = settings.Current.PlayStation;
            ps.OnlineId = "CachedOnlineId";
            ps.RefreshTokenProtected = SecretProtector.Protect("expired-refresh");
            settings.Current.Xbox.Gamertag = "XboxName";
            settings.Save();

            var handler = new FakeHttpMessageHandler((_, _) =>
                Task.FromResult(AccountTestSupport.Json("{\"error\":\"invalid_grant\"}", HttpStatusCode.BadRequest)));

            using var provider = new PsnAccountProvider(settings, null, new PsnAccountLookup(handler));
            await provider.RestoreAsync(CancellationToken.None);

            Assert.True(provider.Session.ShowError);
            Assert.True(provider.Session.ShowLastAccount);
            Assert.Equal("Last account: CachedOnlineId", provider.Session.LastAccountLine);
            Assert.Equal(string.Empty, settings.Current.PlayStation.RefreshTokenProtected);
            Assert.Equal("XboxName", settings.Current.Xbox.Gamertag);
        }
        finally
        {
            AccountTestSupport.Cleanup(root);
        }
    }

    [Fact]
    public async Task Manual_account_id_alone_is_not_a_signed_in_account()
    {
        var settings = AccountTestSupport.NewSettings(out var root);
        try
        {
            settings.Current.PlayStation.AccountId = "manual-id";
            settings.Save();

            using var provider = new PsnAccountProvider(settings);
            await provider.RestoreAsync(CancellationToken.None);

            Assert.True(provider.Session.ShowSignedOut);
            Assert.Contains("manual", provider.Session.StatusText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            AccountTestSupport.Cleanup(root);
        }
    }

    [Fact]
    public async Task Sign_out_clears_only_the_playstation_account()
    {
        var settings = AccountTestSupport.NewSettings(out var root);
        try
        {
            settings.Current.Xbox.Gamertag = "XboxName";
            settings.Current.Xbox.Xuid = "xuid";
            settings.Save();

            using var provider = new PsnAccountProvider(settings);
            await provider.SignInAsync(
                new FakeAccountInteraction
                {
                    PsnResult = new PsnAuthResult(
                        "account-id",
                        "ActualOnlineId",
                        "access-token",
                        "refresh-token",
                        DateTimeOffset.UtcNow.AddHours(1))
                },
                CancellationToken.None);

            provider.SignOut();

            Assert.True(provider.Session.ShowSignedOut);
            Assert.Equal(string.Empty, settings.Current.PlayStation.AccountId);
            Assert.Equal(string.Empty, settings.Current.PlayStation.OnlineId);
            Assert.Equal(string.Empty, settings.Current.PlayStation.RefreshTokenProtected);
            Assert.Equal("XboxName", settings.Current.Xbox.Gamertag);
            Assert.Equal("xuid", settings.Current.Xbox.Xuid);
        }
        finally
        {
            AccountTestSupport.Cleanup(root);
        }
    }
}
