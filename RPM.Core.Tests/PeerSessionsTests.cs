using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// The door: who gets a token, and for how long.
///
/// <para>Every one of these is a way in that must stay shut: an untrusted key, a replayed answer,
/// a challenge answered for a different machine, a token that outlived the trust it was issued
/// against. The last one is the one with a person behind it: "stop sharing with that computer" has
/// to stop the music now, not in half an hour.</para>
/// </summary>
public class PeerSessionsTests
{
    private const string ServerId = "SERVER";

    private static (PeerIdentity Identity, TrustedPeer Peer) Peer(PeerKind kind = PeerKind.Guest)
    {
        var identity = PeerIdentity.Create();
        return (identity, new TrustedPeer
        {
            DeviceId = identity.DeviceId,
            PublicKey = identity.PublicKey,
            Name = "peer",
            Kind = kind,
        });
    }

    private static SessionProof Proof(PeerIdentity identity, string challenge, string serverId = ServerId) => new()
    {
        PublicKey = SharingProtocol.ToBase64Url(identity.PublicKey),
        Challenge = challenge,
        Signature = SharingProtocol.ToBase64Url(
            identity.Sign(PeerSessions.SignedBytes(serverId, challenge))),
    };

    [Fact]
    public void A_trusted_peer_that_signs_the_challenge_is_let_in()
    {
        var (identity, peer) = Peer(PeerKind.Account);
        using var _ = identity;
        var sessions = new PeerSessions(ServerId);

        var grant = sessions.Redeem(Proof(identity, sessions.Issue().Challenge), [peer]);

        Assert.NotNull(grant);
        Assert.Equal(PeerKind.Account, grant!.Kind);
        Assert.Equal(peer.DeviceId, sessions.Peer(grant.Token, [peer])!.DeviceId);
    }

    [Fact]
    public void A_key_nobody_here_trusts_is_not_let_in()
    {
        var (identity, _) = Peer();
        using var _key = identity;
        var sessions = new PeerSessions(ServerId);

        Assert.Null(sessions.Redeem(Proof(identity, sessions.Issue().Challenge), []));
    }

    [Fact]
    public void A_challenge_is_answered_once()
    {
        var (identity, peer) = Peer();
        using var _ = identity;
        var sessions = new PeerSessions(ServerId);

        var challenge = sessions.Issue().Challenge;

        Assert.NotNull(sessions.Redeem(Proof(identity, challenge), [peer]));
        Assert.Null(sessions.Redeem(Proof(identity, challenge), [peer]));
    }

    [Fact]
    public void A_challenge_nobody_issued_is_worth_nothing()
    {
        var (identity, peer) = Peer();
        using var _ = identity;
        var sessions = new PeerSessions(ServerId);

        Assert.Null(sessions.Redeem(Proof(identity, "made-up"), [peer]));
    }

    [Fact]
    public void An_answer_given_to_another_machine_does_not_work_here()
    {
        var (identity, peer) = Peer();
        using var _ = identity;
        var sessions = new PeerSessions(ServerId);

        var challenge = sessions.Issue().Challenge;

        // Signed for a different server: the same peer, the same challenge, and it still fails.
        // That is what stops a machine relaying somebody's proof onwards.
        Assert.Null(sessions.Redeem(Proof(identity, challenge, "ANOTHER"), [peer]));
    }

    [Fact]
    public void A_wrong_answer_burns_the_challenge()
    {
        var (identity, peer) = Peer();
        using var _ = identity;
        var sessions = new PeerSessions(ServerId);

        var challenge = sessions.Issue().Challenge;

        Assert.Null(sessions.Redeem(Proof(identity, challenge, "ANOTHER"), [peer]));
        Assert.Null(sessions.Redeem(Proof(identity, challenge), [peer]));
    }

    [Fact]
    public void A_challenge_goes_stale()
    {
        var (identity, peer) = Peer();
        using var _ = identity;

        var now = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
        var sessions = new PeerSessions(ServerId, () => now);

        var challenge = sessions.Issue().Challenge;
        now += PeerSessions.ChallengeLife + TimeSpan.FromSeconds(1);

        Assert.Null(sessions.Redeem(Proof(identity, challenge), [peer]));
    }

    [Fact]
    public void A_token_runs_out()
    {
        var (identity, peer) = Peer();
        using var _ = identity;

        var now = new DateTime(2026, 8, 10, 12, 0, 0, DateTimeKind.Utc);
        var sessions = new PeerSessions(ServerId, () => now);

        var grant = sessions.Redeem(Proof(identity, sessions.Issue().Challenge), [peer])!;

        now += PeerSessions.TokenLife + TimeSpan.FromSeconds(1);

        Assert.Null(sessions.Peer(grant.Token, [peer]));
    }

    [Fact]
    public void Trust_withdrawn_stops_the_music_now()
    {
        var (identity, peer) = Peer();
        using var _ = identity;
        var sessions = new PeerSessions(ServerId);

        var grant = sessions.Redeem(Proof(identity, sessions.Issue().Challenge), [peer])!;

        Assert.NotNull(sessions.Peer(grant.Token, [peer]));
        Assert.Null(sessions.Peer(grant.Token, []));
    }

    [Fact]
    public void Forgetting_a_computer_takes_its_tokens_with_it()
    {
        var (identity, peer) = Peer();
        using var _ = identity;
        var sessions = new PeerSessions(ServerId);

        var grant = sessions.Redeem(Proof(identity, sessions.Issue().Challenge), [peer])!;
        sessions.Revoke(peer.DeviceId);

        Assert.Null(sessions.Peer(grant.Token, [peer]));
    }

    [Fact]
    public void A_token_nobody_issued_is_nobody()
    {
        var sessions = new PeerSessions(ServerId);

        Assert.Null(sessions.Peer("made-up", []));
        Assert.Null(sessions.Peer(null, []));
    }
}
