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
            identity.Sign(PeerSessions.SignedBytes(serverId, challenge, ""))),
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

    /// <summary>Anyone on the network may ask for a challenge, so only so many are held. The oldest goes
    /// first, and a peer that answers its own promptly is not affected.</summary>
    [Fact]
    public void Only_so_many_challenges_are_held_and_the_oldest_goes_first()
    {
        var (identity, peer) = Peer();
        using var _ = identity;
        var sessions = new PeerSessions(ServerId, () => new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc));

        var oldest = sessions.Issue().Challenge;
        for (var i = 0; i < PeerSessions.MaxChallenges; i++)
            sessions.Issue();
        var newest = sessions.Issue().Challenge;

        Assert.Null(sessions.Redeem(Proof(identity, oldest), [peer]));
        Assert.NotNull(sessions.Redeem(Proof(identity, newest), [peer]));
    }

    [Fact]
    public void The_account_a_peer_names_is_kept_with_its_token()
    {
        var (identity, peer) = Peer(PeerKind.Account);
        using var _ = identity;
        var sessions = new PeerSessions(ServerId);

        var challenge = sessions.Issue().Challenge;
        var proof = new SessionProof
        {
            PublicKey = SharingProtocol.ToBase64Url(identity.PublicKey),
            Challenge = challenge,
            Signature = SharingProtocol.ToBase64Url(identity.Sign(PeerSessions.SignedBytes(ServerId, challenge, "user-7"))),
            Account = "user-7",
        };

        var grant = sessions.Redeem(proof, [peer]);
        Assert.Equal("user-7", sessions.Account(grant!.Token));
    }

    /// <summary>The account is inside the signature, so a machine in the middle cannot change it.</summary>
    [Fact]
    public void An_account_changed_after_signing_is_refused()
    {
        var (identity, peer) = Peer(PeerKind.Account);
        using var _ = identity;
        var sessions = new PeerSessions(ServerId);

        var challenge = sessions.Issue().Challenge;
        var proof = new SessionProof
        {
            PublicKey = SharingProtocol.ToBase64Url(identity.PublicKey),
            Challenge = challenge,
            Signature = SharingProtocol.ToBase64Url(identity.Sign(PeerSessions.SignedBytes(ServerId, challenge, "user-7"))),
            Account = "user-mine",
        };

        Assert.Null(sessions.Redeem(proof, [peer]));
    }

    [Fact]
    public void A_ticket_opens_its_own_route_and_id_while_the_token_lives()
    {
        var now = new DateTime(2026, 10, 20, 12, 0, 0, DateTimeKind.Utc);
        var (identity, peer) = Peer();
        using var _ = identity;
        var sessions = new PeerSessions(ServerId, () => now);

        var token = sessions.Redeem(Proof(identity, sessions.Issue().Challenge), [peer])!.Token;
        var handle = PeerSessions.Handle(token);
        var ticket = PeerSessions.Ticket(token, SharingProtocol.TrackRoute, "track-1");

        Assert.Equal(peer.DeviceId,
            sessions.PeerForTicket(handle, ticket, SharingProtocol.TrackRoute, "track-1", [peer])!.DeviceId);

        // Another id, another route, a made-up handle, and trust withdrawn.
        Assert.Null(sessions.PeerForTicket(handle, ticket, SharingProtocol.TrackRoute, "track-2", [peer]));
        Assert.Null(sessions.PeerForTicket(handle, ticket, SharingProtocol.CoverRoute, "track-1", [peer]));
        Assert.Null(sessions.PeerForTicket("nobody", ticket, SharingProtocol.TrackRoute, "track-1", [peer]));
        Assert.Null(sessions.PeerForTicket(handle, ticket, SharingProtocol.TrackRoute, "track-1", []));

        // The token runs out, and its tickets with it.
        now += PeerSessions.TokenLife;
        Assert.Null(sessions.PeerForTicket(handle, ticket, SharingProtocol.TrackRoute, "track-1", [peer]));
    }

    [Fact]
    public void Forgetting_a_computer_takes_its_tickets_with_it()
    {
        var (identity, peer) = Peer();
        using var _ = identity;
        var sessions = new PeerSessions(ServerId);

        var token = sessions.Redeem(Proof(identity, sessions.Issue().Challenge), [peer])!.Token;
        var ticket = PeerSessions.Ticket(token, SharingProtocol.TrackRoute, "track-1");
        sessions.Revoke(peer.DeviceId);

        Assert.Null(sessions.PeerForTicket(
            PeerSessions.Handle(token), ticket, SharingProtocol.TrackRoute, "track-1", [peer]));
    }

    /// <summary>The phones make handles and tickets too (PeerWire.swift, PeerWire.kt). Their tests check these
    /// same values, so a change here must change them too.</summary>
    [Fact]
    public void Handles_and_tickets_match_the_phones()
    {
        Assert.Equal("ufGG_y6PQoD1A0zS7jm0xg", PeerSessions.Handle("token-1"));
        Assert.Equal("pfexHfz9I4_UxxVAiaRSMw", PeerSessions.Ticket("token-1", SharingProtocol.TrackRoute, "abc"));
        Assert.Equal("rpm-share-session\nSERVER\nCH\nuser-7",
            System.Text.Encoding.UTF8.GetString(PeerSessions.SignedBytes("SERVER", "CH", "user-7")));
    }
}
