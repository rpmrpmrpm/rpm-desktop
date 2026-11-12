using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// Which accounts a pairing lets share cloud copies.
///
/// <para>The book of trusted peers belongs to the install, and cloud shares belong to accounts. A pairing is
/// agreed between the two accounts that were signed in when the code was typed. These tests pin that a
/// different account on either side gets nothing from that pairing.</para>
/// </summary>
public class PairedAccountsTests
{
    private const string Me = "acct-me";
    private const string Friend = "acct-friend";
    private const string Other = "acct-other";

    private static TrustedPeer Peer(PairedAccounts? pairedAs, string account = "") => new()
    {
        DeviceId = "peer",
        PublicKey = "peer"u8.ToArray(),
        Name = "Friend's PC",
        Kind = PeerKind.Guest,
        AccountUserId = account,
        AccountProvedUtcTicks = account.Length > 0 ? 1 : 0,
        PairedAs = pairedAs,
    };

    private static TrustedPeer PairedWithFriend() =>
        Peer(new PairedAccounts { Mine = Me, Theirs = Friend }, Friend);

    [Fact]
    public void Both_accounts_as_paired_grant_the_friend()
    {
        var grant = PeerTrust.GrantFor(PairedWithFriend(), Me, Friend);

        Assert.NotNull(grant);
        Assert.Equal(Friend, grant.GranteeId);
        Assert.Equal("Friend's PC", grant.GranteeName);
    }

    [Fact]
    public void This_machine_signed_into_another_account_grants_nothing()
    {
        Assert.Null(PeerTrust.GrantFor(PairedWithFriend(), Other, Friend));
    }

    [Fact]
    public void The_peer_signed_into_another_account_is_granted_nothing()
    {
        Assert.Null(PeerTrust.GrantFor(PairedWithFriend(), Me, Other));
    }

    [Fact]
    public void A_record_without_paired_accounts_grants_nothing()
    {
        Assert.Null(PeerTrust.GrantFor(Peer(pairedAs: null, Friend), Me, Friend));
    }

    [Fact]
    public void A_pairing_made_while_signed_out_grants_nothing_after_signing_in()
    {
        var peer = Peer(new PairedAccounts { Mine = "", Theirs = Friend }, Friend);

        Assert.Null(PeerTrust.GrantFor(peer, Me, Friend));
    }

    [Fact]
    public void A_peer_that_first_proved_it_was_signed_out_is_granted_nothing_after_it_signs_in()
    {
        var peer = Peer(new PairedAccounts { Mine = Me, Theirs = "" }, Friend);

        Assert.Null(PeerTrust.GrantFor(peer, Me, Friend));
    }

    [Fact]
    public void A_peer_that_has_not_proved_an_account_yet_is_granted_nothing()
    {
        var peer = Peer(new PairedAccounts { Mine = Me });

        Assert.Null(PeerTrust.GrantFor(peer, Me, Friend));
    }

    [Fact]
    public void A_peer_on_the_same_account_needs_no_grant()
    {
        var peer = Peer(new PairedAccounts { Mine = Me, Theirs = Me }, Me);

        Assert.Null(PeerTrust.GrantFor(peer, Me, Me));
    }

    [Fact]
    public void Signed_out_here_grants_nothing()
    {
        Assert.Null(PeerTrust.GrantFor(PairedWithFriend(), "", Friend));
    }

    [Fact]
    public void The_first_proof_after_pairing_is_written_into_the_paired_accounts()
    {
        var peer = Peer(new PairedAccounts { Mine = Me });

        var proved = PeerTrust.Proved(peer, Friend, nowTicks: 42);

        Assert.NotNull(proved);
        Assert.Equal(Friend, proved.PairedAs!.Theirs);
        Assert.Equal(Friend, proved.AccountUserId);
        Assert.Equal(42, proved.AccountProvedUtcTicks);
    }

    [Fact]
    public void A_proof_that_the_peer_is_signed_out_is_written_as_empty_not_left_unknown()
    {
        var proved = PeerTrust.Proved(Peer(new PairedAccounts { Mine = Me }), "", nowTicks: 42);

        Assert.Equal("", proved!.PairedAs!.Theirs);
    }

    [Fact]
    public void A_later_account_does_not_replace_the_one_the_pairing_was_made_with()
    {
        var proved = PeerTrust.Proved(PairedWithFriend(), Other, nowTicks: 42);

        Assert.NotNull(proved);
        Assert.Equal(Other, proved.AccountUserId);
        Assert.Equal(Friend, proved.PairedAs!.Theirs);
        Assert.Null(PeerTrust.GrantFor(proved, Me, Other));
    }

    [Fact]
    public void A_record_without_paired_accounts_does_not_get_them_from_a_proof()
    {
        var proved = PeerTrust.Proved(Peer(pairedAs: null), Friend, nowTicks: 42);

        Assert.NotNull(proved);
        Assert.Null(proved.PairedAs);
        Assert.Null(PeerTrust.GrantFor(proved, Me, Friend));
    }

    [Fact]
    public void A_proof_that_changes_nothing_writes_nothing()
    {
        Assert.Null(PeerTrust.Proved(PairedWithFriend(), Friend, nowTicks: 42));
    }

    [Fact]
    public void Pairing_again_replaces_the_paired_accounts()
    {
        List<TrustedPeer> known = [PairedWithFriend()];
        var again = Peer(new PairedAccounts { Mine = Other });

        var (_, peers) = PeerTrust.Add(known, again);

        var record = PeerTrust.Find(peers, "peer")!;
        Assert.Equal(Other, record.PairedAs!.Mine);
        Assert.Null(record.PairedAs.Theirs);
    }
}
