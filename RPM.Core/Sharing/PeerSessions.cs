using System.Security.Cryptography;
using System.Text;

namespace RPM.Core.Sharing;

/// <summary>
/// Who is allowed to read this library right now, and for how long.
///
/// <para>A peer proves that it holds the key it is trusted by. It signs a challenge that this machine
/// made a moment ago. It then gets a token that lasts half an hour. Every route except the greeting needs
/// that token. So a stranger on the network who has never been paired gets a name and a track count from
/// this machine, and nothing else.</para>
///
/// <para>The challenge is signed together with the id of the machine that issued it. So an answer given
/// to one RPM is worth nothing to another. A challenge can be used once, and it expires after a minute.
/// It must be answered at once or not at all.</para>
/// </summary>
public sealed class PeerSessions
{
    /// <summary>How long a token stands. Long enough that an album plays through without a second
    /// handshake. Short enough that trust withdrawn while nothing is happening takes effect on its
    /// own.</summary>
    public static readonly TimeSpan TokenLife = TimeSpan.FromMinutes(30);

    public static readonly TimeSpan ChallengeLife = TimeSpan.FromMinutes(1);

    /// <summary>Challenges held at once. Anyone on the network may ask for one, so without a limit a
    /// machine could fill this one's memory with them. Past the limit the oldest is dropped. A peer that
    /// answers promptly is not affected.</summary>
    public const int MaxChallenges = 256;

    /// <summary>What a signature is made over: the purpose, the machine that asked, and the challenge.
    /// The purpose string stops a signature made here from having any meaning in another exchange that
    /// also signs bytes.</summary>
    private const string Purpose = "rpm-share-session";

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _challenges = new(StringComparer.Ordinal);

    /// <summary>The challenges in the order they were issued, so the oldest is the one dropped. It can hold
    /// challenges already answered or expired; those are skipped.</summary>
    private readonly Queue<string> _issued = new();
    private readonly Dictionary<string, (TrustedPeer Peer, DateTime Until)> _tokens = new(StringComparer.Ordinal);

    private readonly string _deviceId;
    private readonly Func<DateTime> _now;

    /// <param name="deviceId">This machine's device id, which every challenge is bound to.</param>
    /// <param name="now">The clock, so the expiry rules can be tested without waiting for them.</param>
    public PeerSessions(string deviceId, Func<DateTime>? now = null)
    {
        _deviceId = deviceId ?? throw new ArgumentNullException(nameof(deviceId));
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>Something for a peer to sign. Remembered until it is used or it expires.</summary>
    public SessionChallenge Issue()
    {
        var challenge = SharingProtocol.ToBase64Url(RandomNumberGenerator.GetBytes(24));

        lock (_gate)
        {
            Sweep();
            while (_challenges.Count >= MaxChallenges && _issued.TryDequeue(out var oldest))
                _challenges.Remove(oldest);
            _challenges[challenge] = _now() + ChallengeLife;
            _issued.Enqueue(challenge);
        }

        return new SessionChallenge { Challenge = challenge, ExpiresIn = (int)ChallengeLife.TotalSeconds };
    }

    /// <summary>
    /// Turns a signed challenge into a token, or into nothing.
    ///
    /// <para>The result is nothing for every way this can fail. The challenge was never issued, or it was
    /// already used, or it expired. The key is not trusted here. The key's id does not match the id it is
    /// trusted under. Or the signature does not check out. These all give one answer on purpose. A caller
    /// that could tell them apart would have a way to learn which keys this machine trusts.</para>
    /// </summary>
    public SessionGrant? Redeem(SessionProof proof, IReadOnlyList<TrustedPeer> trusted)
    {
        ArgumentNullException.ThrowIfNull(trusted);
        if (proof is null)
            return null;

        if (SharingProtocol.FromBase64Url(proof.PublicKey) is not { Length: > 0 } publicKey ||
            SharingProtocol.FromBase64Url(proof.Signature) is not { Length: > 0 } signature)
            return null;

        lock (_gate)
        {
            Sweep();

            // Taken out whether or not the rest of this works. A challenge is answered once. A wrong answer
            // must not leave it lying there to be tried again.
            if (!_challenges.Remove(proof.Challenge, out var expires) || expires <= _now())
                return null;
        }

        if (PeerTrust.Match(trusted, publicKey) is not { } peer)
            return null;

        if (!PeerIdentity.Verify(publicKey, SignedBytes(_deviceId, proof.Challenge), signature))
            return null;

        var token = SharingProtocol.ToBase64Url(RandomNumberGenerator.GetBytes(32));
        lock (_gate)
            _tokens[token] = (peer, _now() + TokenLife);

        return new SessionGrant
        {
            Token = token,
            ExpiresIn = (int)TokenLife.TotalSeconds,
            Kind = peer.Kind,
        };
    }

    /// <summary>The peer a token belongs to, or null when the token is unknown, expired, or trust has since
    /// been withdrawn.</summary>
    public TrustedPeer? Peer(string? token, IReadOnlyList<TrustedPeer> trusted)
    {
        ArgumentNullException.ThrowIfNull(trusted);
        if (string.IsNullOrEmpty(token))
            return null;

        lock (_gate)
        {
            Sweep();
            if (!_tokens.TryGetValue(token, out var session) || session.Until <= _now())
                return null;

            // Checked on every request, not only when the token is issued. "Stop sharing with that
            // computer" has to mean the music stops now, not in half an hour.
            return PeerTrust.Match(trusted, session.Peer.PublicKey);
        }
    }

    /// <summary>Drops every token belonging to one peer. This is what "forget this computer" does.</summary>
    public void Revoke(string deviceId)
    {
        lock (_gate)
        {
            foreach (var (token, session) in _tokens.ToList())
            {
                if (string.Equals(session.Peer.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                    _tokens.Remove(token);
            }
        }
    }

    /// <summary>What a peer signs to prove it holds its key. Public because the other end has to build
    /// exactly the same bytes.</summary>
    public static byte[] SignedBytes(string serverDeviceId, string challenge) =>
        Encoding.UTF8.GetBytes($"{Purpose}\n{serverDeviceId}\n{challenge}");

    private void Sweep()
    {
        var now = _now();
        foreach (var (challenge, expires) in _challenges.ToList())
            if (expires <= now) _challenges.Remove(challenge);
        while (_issued.TryPeek(out var first) && !_challenges.ContainsKey(first))
            _issued.Dequeue();

        foreach (var (token, session) in _tokens.ToList())
            if (session.Until <= now) _tokens.Remove(token);
    }
}
