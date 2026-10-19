using RPM.Desktop.Services.Account;
using RPM.Desktop.Services.Catalog;

namespace RPM.Desktop.Tests;

/// <summary>
/// The two rules that decide whether anything may be sent to the rpm API, tested directly rather
/// than through a container: one for the API itself, one for Discogs behind it.
///
/// <para>If either rule allows a request too early, the app fails without a message. The lookup
/// window would search a catalogue with no signed-in user and report "no releases", and the
/// Discogs provider would throw a <c>DiscogsUnavailableException</c> for every keystroke and every
/// step of the style pass before any request was sent.</para>
/// </summary>
public class AccountGateTests
{
    [Fact]
    public void Signed_in_with_an_api_is_the_only_open_door() =>
        Assert.Equal(AccountAccess.Open, AccountGate.Decide(signedIn: true, hasApi: true));

    /// <summary>The user can fix this one, so it is kept separate from the one they cannot fix.</summary>
    [Fact]
    public void Signed_out_asks_for_a_sign_in() =>
        Assert.Equal(AccountAccess.SignInRequired, AccountGate.Decide(signedIn: false, hasApi: true));

    /// <summary>No API configured at all: the user did not cause it and cannot fix it, so it is a
    /// different answer from "sign in", not a login dialog that would not help.</summary>
    [Fact]
    public void No_api_is_not_a_question_for_the_user()
    {
        Assert.Equal(AccountAccess.NotConfigured, AccountGate.Decide(signedIn: true, hasApi: false));
        Assert.Equal(AccountAccess.NotConfigured, AccountGate.Decide(signedIn: false, hasApi: false));
    }

    /// <summary>Every Discogs call carries an rpm bearer token, so a signed-out session can never be
    /// answered, whatever the last known connection status was.</summary>
    [Fact]
    public void Discogs_is_never_asked_while_signed_out()
    {
        Assert.False(DiscogsService.MayAsk(signedIn: false, DiscogsStatus.Unknown));
        Assert.False(DiscogsService.MayAsk(signedIn: false, new DiscogsStatus(true, true, "vinylfan")));
        Assert.False(DiscogsService.MayAsk(signedIn: false, DiscogsStatus.Unavailable));
    }

    /// <summary>Signed in and unknown still counts as yes. A refusal costs one call, which the
    /// search already handles, and tells us the answer. Assuming no would hide the user's Discogs
    /// results for the rest of the session.</summary>
    [Fact]
    public void Signed_in_asks_while_the_connection_is_unknown() =>
        Assert.True(DiscogsService.MayAsk(signedIn: true, DiscogsStatus.Unknown));

    [Fact]
    public void Signed_in_asks_a_connected_account() =>
        Assert.True(DiscogsService.MayAsk(signedIn: true, new DiscogsStatus(true, true, "vinylfan")));

    /// <summary>Asked, and refused. That is a real answer, so no more requests are sent.</summary>
    [Fact]
    public void A_known_refusal_stops_the_asking()
    {
        Assert.False(DiscogsService.MayAsk(signedIn: true, new DiscogsStatus(false, true, null)));
        Assert.False(DiscogsService.MayAsk(signedIn: true, DiscogsStatus.Unavailable));
    }

    /// <summary>
    /// Sharing over the local network is between accounts: the beacon carries an account
    /// fingerprint, the grant that lets a borrower reach the cloud part of a shelf is tied to the
    /// account, and a borrowed record that exists only in the store is signed with the account's
    /// token. A machine with no account cannot provide any of this, so it does not share.
    /// </summary>
    [Fact]
    public void A_machine_with_no_account_does_not_share()
    {
        Assert.True(RPM.Desktop.Services.Sharing.SharingService.MayServe(
            sharingOn: true, libraryOpen: true, signedIn: true, harness: false));

        Assert.False(RPM.Desktop.Services.Sharing.SharingService.MayServe(
            sharingOn: true, libraryOpen: true, signedIn: false, harness: false));
        Assert.False(RPM.Desktop.Services.Sharing.SharingService.MayServe(
            sharingOn: true, libraryOpen: false, signedIn: true, harness: false));
        Assert.False(RPM.Desktop.Services.Sharing.SharingService.MayServe(
            sharingOn: false, libraryOpen: true, signedIn: true, harness: false));
    }

    /// <summary>
    /// The design harness does not send a beacon. The reason is its identity, not its contents.
    ///
    /// <para>A <c>--gallery</c> run opens a seeded temp library, but the peer key it would announce
    /// is the MACHINE's, from LocalData. A harness running beside the real app would then be two
    /// servers with one device id. A borrower keeps one address per device and uses whichever
    /// announced last, so it could be served the demo library (or an empty one before the seed
    /// finished). Read as the whole collection, that looks like every record was deleted, and a
    /// borrowed shelf would empty and refill on alternate passes.</para>
    /// </summary>
    [Fact]
    public void The_design_harness_does_not_share_the_machines_library()
    {
        Assert.False(RPM.Desktop.Services.Sharing.SharingService.MayServe(
            sharingOn: true, libraryOpen: true, signedIn: true, harness: true));
    }
}
