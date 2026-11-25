using FifineCaptureStudio.Core.Accounts;
using Xunit;

namespace FifineCaptureStudio.Tests.Accounts;

public class AccountSessionTests
{
    private static AccountSession Xbox() =>
        new(AccountPlatform.Xbox, "Xbox", "Microsoft account · Xbox Live", "XUID");

    private static AccountSession PlayStation() =>
        new(AccountPlatform.PlayStation, "PlayStation", "PlayStation Network", "PSN account ID");

    [Fact]
    public void Ui_moves_from_signed_out_to_waiting_to_signed_in()
    {
        var session = Xbox();

        Assert.True(session.ShowSignedOut);
        Assert.True(session.ShowSignInButton);
        Assert.False(session.ShowSignedIn);
        Assert.Equal("Signed out", session.StatusLabel);

        session.SetSigningIn("Waiting for you to authorize the sign-in…");

        Assert.True(session.ShowBusy);
        Assert.False(session.ShowSignedOut);
        Assert.False(session.ShowSignedIn);
        Assert.Equal("Signing in…", session.StatusLabel);

        session.SetDeviceCode("ABCD-EFGH", "https://microsoft.com/devicelogin");

        Assert.True(session.ShowDeviceCode);
        Assert.Equal("ABCD-EFGH", session.UserCode);

        session.SetSignedIn("ActualGamertag", "2533274800000000", "Signed in to Xbox Live.");

        Assert.True(session.ShowSignedIn);
        Assert.False(session.ShowBusy);
        Assert.False(session.ShowDeviceCode);
        Assert.False(session.ShowSignInButton);
        Assert.Equal("Signed in", session.StatusLabel);
        Assert.Equal("Xbox account: ActualGamertag", session.AccountLine);
        Assert.Equal("XUID: 2533274800000000", session.AccountIdLine);
    }

    [Fact]
    public void Signing_out_returns_to_the_sign_in_state()
    {
        var session = PlayStation();
        session.SetSignedIn("ActualOnlineId", "account-id", "Signed in.");

        session.SetSignedOut("Signed out of PlayStation Network.");

        Assert.True(session.ShowSignedOut);
        Assert.True(session.ShowSignInButton);
        Assert.False(session.ShowSignedIn);
        Assert.Equal(string.Empty, session.AccountLine);
    }

    [Fact]
    public void Setup_state_hides_the_sign_in_button_until_a_client_id_exists()
    {
        var session = Xbox();

        session.SetSetup(true, "Create a Microsoft Entra app registration first.");

        Assert.True(session.ShowSetup);
        Assert.True(session.NeedsSetup);
        Assert.False(session.ShowSignInButton);
        Assert.False(session.ShowSignedOut);
        Assert.Equal("Signed out", session.StatusLabel);
    }

    [Fact]
    public void Profile_failure_is_an_error_and_does_not_claim_signed_in()
    {
        var session = Xbox();

        session.SetError("Xbox profile", "Xbox Live did not return a Gamertag.");

        Assert.True(session.ShowError);
        Assert.False(session.ShowSignedIn);
        Assert.Equal("Action needed", session.StatusLabel);
        Assert.Contains("Xbox profile:", session.StatusText);
        Assert.Contains("Gamertag", session.StatusText);
    }

    [Fact]
    public void Expired_session_keeps_the_last_name_but_not_a_signed_in_state()
    {
        var session = PlayStation();

        session.SetExpired("ActualOnlineId", "PlayStation Network session expired. Sign in again.");

        Assert.True(session.ShowError);
        Assert.True(session.ShowLastAccount);
        Assert.False(session.ShowSignedIn);
        Assert.Equal("Last account: ActualOnlineId", session.LastAccountLine);
        Assert.Contains("expired", session.StatusText);
    }

    [Fact]
    public void Three_platform_sessions_stay_independent()
    {
        var xbox = Xbox();
        var playStation = PlayStation();
        var pc = new AccountSession(AccountPlatform.Pc, "PC", "FIFINE Link Agent", "Agent");
        pc.SetUnavailable("The Link Agent has no account identity.");

        xbox.SetSignedIn("XboxGamertag", "xuid", "ok");
        Assert.True(xbox.ShowSignedIn);
        Assert.False(playStation.ShowSignedIn);
        Assert.True(pc.ShowUnavailable);

        playStation.SetSignedIn("PSNOnlineId", "psn-id", "ok");
        Assert.True(playStation.ShowSignedIn);
        Assert.True(xbox.ShowSignedIn);

        xbox.SetSignedOut("Signed out of Xbox Live.");
        Assert.True(playStation.ShowSignedIn);
        Assert.False(xbox.ShowSignedIn);

        playStation.SetExpired("PSNOnlineId", "PSN session expired.");
        Assert.True(xbox.ShowSignedOut);
        Assert.True(pc.ShowUnavailable);
    }
}
