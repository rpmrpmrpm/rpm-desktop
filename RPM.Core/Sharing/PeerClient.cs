using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace RPM.Core.Sharing;

/// <summary>
/// This end of the conversation: how a machine reads somebody else's library.
///
/// <para>Holds one peer's address and the token that lets it read. When the old token runs out, it gets
/// a new token by itself. A token that expires in the middle of a record should not be something that
/// anybody using this class has to handle.</para>
/// </summary>
public sealed partial class PeerClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly PeerIdentity _identity;

    /// <summary>The key the other machine must hold. The connection is pinned to it. A pairing commitment
    /// is bound to it.</summary>
    private readonly byte[] _pinnedKey;
    private readonly object _gate = new();

    private string? _token;
    private DateTime _tokenUntil;

    /// <summary>The margin taken off a token's life, so that a token is never handed out moments before it
    /// stops working.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Talks to one peer.
    /// </summary>
    /// <param name="identity">This install's key, which is what a session is proved with.</param>
    /// <param name="host">Where that machine answered from.</param>
    /// <param name="port">And on which port.</param>
    /// <param name="publicKey">
    /// The key that machine must turn out to be holding. It comes from the trust book for a peer already
    /// shared with, or from the beacon for a peer being asked. The connection is refused outright if the
    /// certificate on the wire does not carry that key. So a machine answering in the library's place gets
    /// a closed socket, not a token.
    ///
    /// <para>This parameter is required. There is no way to pass "any certificate". If it were optional,
    /// a caller that left it out would accept whatever answered. Every caller has a key: the book's, or
    /// the beacon's, which the device id is derived from.</para>
    /// </param>
    public PeerClient(PeerIdentity identity, string host, int port, byte[] publicKey)
        : this(identity, host, port, publicKey, Pinned(publicKey))
    {
    }

    /// <summary>For tests. Talks through <paramref name="handler"/>, which decides what is believed.
    /// <paramref name="publicKey"/> is the key a pairing commitment is bound to.</summary>
    internal PeerClient(PeerIdentity identity, string host, int port, byte[] publicKey, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(handler);

        _identity = identity;
        _pinnedKey = publicKey;

        // This is https on a local network, with a certificate that nobody issued. See PeerCertificate.
        // The name in the URL is only an address, so it is never checked. The key is.
        BaseUrl = $"https://{Bracket(host)}:{port}";

        // No timeout on the client itself. One of these requests is a question held open on another
        // person's screen for two minutes. The rest should give up in twenty seconds. A deadline per call
        // says which is which. One timeout on the client cannot.
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>
    /// A handler that believes exactly one certificate: the one carrying the peer's own key.
    ///
    /// <para>The chain, the name and the dates are deliberately not checked. There is no authority on a
    /// home network to have issued anything. A certificate that carries the key that the six digits were
    /// computed over is the only thing worth checking.</para>
    /// </summary>
    private static HttpMessageHandler Pinned(byte[] publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        return PeerCertificate.Handler(publicKey);
    }

    /// <summary>How long a machine on the same network gets before it is written off.</summary>
    private static readonly TimeSpan Quick = TimeSpan.FromSeconds(20);

    /// <summary>A whole collection can be tens of thousands of tracks. It crosses a local network in
    /// moments, but not in twenty seconds on a bad one.</summary>
    private static readonly TimeSpan Listing = TimeSpan.FromMinutes(2);

    private static CancellationTokenSource Deadline(TimeSpan life, CancellationToken cancellation)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cts.CancelAfter(life);
        return cts;
    }

    /// <summary>Where this peer answers, e.g. <c>http://192.168.1.24:51733</c>.</summary>
    public string BaseUrl { get; }

    /// <summary>What the peer says it is, before anything has been proved. Null when it did not answer at
    /// all. That is the ordinary state of a machine that has been shut down.</summary>
    /// <remarks>
    /// <para>Every request here answers a failure the same way: with nothing. Closed is one of the
    /// failures. A client can be disposed while a request it is carrying is still in flight. This client is
    /// shared between everything that reads a peer. The pairing's client is closed when the dialog goes
    /// away while a question is still on the other screen. Both are reasonable things to do, and both
    /// happen in the middle of a call. Then the library throws <see cref="ObjectDisposedException"/> from
    /// several frames below anything that looks like networking.</para>
    /// <para>The exception is caught rather than prevented, because the answer is the same either way: that
    /// machine is not going to tell us. Who may close a client while somebody is using it is settled where
    /// the clients are kept. See <c>SharingService.Borrow</c>.</para>
    /// </remarks>
    public async Task<PeerHello?> HelloAsync(CancellationToken cancellation = default)
    {
        try
        {
            using var deadline = Deadline(Quick, cancellation);
            return await _http.GetFromJsonAsync(
                BaseUrl + SharingProtocol.HelloRoute, SharingJson.Default.PeerHello, deadline.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                 or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Asks a machine to share, and comes back as soon as it has said who it is and where the pairing
    /// stands.
    /// </summary>
    /// <param name="name">What to call this machine on their screen.</param>
    /// <param name="face">The display name of whoever is at this machine, or empty. This is required rather
    /// than defaulted. If it were forgotten, the question on their screen would show only a hostname, and
    /// nothing would say that anything was missing.</param>
    /// <param name="accountFingerprint">This machine's account fingerprint, or empty.</param>
    /// <param name="nonce">This machine's half of the code.</param>
    /// <returns>The offer when they answered with one. <see cref="PairDelivery.Crossed"/> when they are asking
    /// this machine at the same moment and ask first. <see cref="PairDelivery.Undelivered"/> when nothing
    /// usable came back.</returns>
    public async Task<(PairOffer? Offer, PairDelivery Delivery)> PairAsync(
        string name, string face, string accountFingerprint, byte[] nonce,
        CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(nonce);

        var request = new PairRequest
        {
            PublicKey = SharingProtocol.ToBase64Url(_identity.PublicKey),
            Name = name ?? "",
            Face = face ?? "",
            Account = accountFingerprint ?? "",
            Nonce = SharingProtocol.ToBase64Url(nonce),
            Pairing = SharingProtocol.Pairing,
        };

        try
        {
            using var deadline = Deadline(Quick, cancellation);
            using var response = await _http.PostAsJsonAsync(
                BaseUrl + SharingProtocol.PairRoute, request, SharingJson.Default.PairRequest, deadline.Token);

            if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
                return (null, PairDelivery.Crossed);

            if (!response.IsSuccessStatusCode)
                return (null, PairDelivery.Undelivered);

            var offer = await response.Content.ReadFromJsonAsync(SharingJson.Default.PairOffer, deadline.Token);
            return offer is null ? (null, PairDelivery.Undelivered) : (offer, PairDelivery.Offered);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                 or ObjectDisposedException)
        {
            return (null, PairDelivery.Undelivered);
        }
    }

    /// <summary>
    /// A held status call on one pairing. The other machine answers when the pairing's revision is no
    /// longer <paramref name="rev"/>. Otherwise it answers after its own hold, which is shorter than this
    /// call's deadline.
    /// </summary>
    /// <returns>The status, or null when the call failed. <see cref="PairStatusKind.Gone"/> is an answer,
    /// not a failure. That machine has no such pairing.</returns>
    public async Task<PairStatus?> PairStatusAsync(string id, long rev, CancellationToken cancellation)
    {
        var url = $"{BaseUrl}{SharingProtocol.PairStatusRoute}?id={Uri.EscapeDataString(id ?? "")}" +
                  $"&rev={rev.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

        try
        {
            using var deadline = Deadline(Quick, cancellation);
            using var response = await _http.GetAsync(url, deadline.Token);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadFromJsonAsync(SharingJson.Default.PairStatus, deadline.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                 or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Sends this side's nonce for one round. Null when the call failed. Private: only
    /// <see cref="PairingAsker"/> decides when it may go.</summary>
    private Task<PairStatus?> PostNonceAsync(string id, int round, byte[] nonce, CancellationToken cancellation) =>
        PostPairAsync(SharingProtocol.PairNonceRoute,
            new PairNonce { Id = id ?? "", Round = round, Nonce = SharingProtocol.ToBase64Url(nonce) },
            SharingJson.Default.PairNonce, cancellation);

    /// <summary>Sends the commitment to what somebody typed, for one round. Null when the call failed. The
    /// answer reveals the other machine's nonce for the round once it has accepted this commitment.</summary>
    private Task<PairStatus?> PostCommitAsync(string id, int round, byte[] commitment, CancellationToken cancellation) =>
        PostPairAsync(SharingProtocol.PairCommitRoute,
            new PairCommit { Id = id ?? "", Round = round, Commit = SharingProtocol.ToBase64Url(commitment) },
            SharingJson.Default.PairCommit, cancellation);

    /// <summary>Opens the commitment. An empty <paramref name="opening"/> says that this side's check of the
    /// revealed nonce failed. Null when the call failed.</summary>
    private Task<PairStatus?> PostOpenAsync(string id, int round, byte[] opening, CancellationToken cancellation) =>
        PostPairAsync(SharingProtocol.PairOpenRoute,
            new PairOpen { Id = id ?? "", Round = round, Opening = SharingProtocol.ToBase64Url(opening) },
            SharingJson.Default.PairOpen, cancellation);

    private async Task<PairStatus?> PostPairAsync<T>(
        string route, T body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken cancellation)
    {
        try
        {
            using var deadline = Deadline(Quick, cancellation);
            using var response = await _http.PostAsJsonAsync(BaseUrl + route, body, type, deadline.Token);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadFromJsonAsync(SharingJson.Default.PairStatus, deadline.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                 or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>Tells the other machine that this one cancelled. False when it could not be told. Its lease
    /// ends the pairing there anyway.</summary>
    public async Task<bool> PairWithdrawAsync(string id, CancellationToken cancellation)
    {
        try
        {
            using var deadline = Deadline(Quick, cancellation);
            using var response = await _http.PostAsJsonAsync(
                BaseUrl + SharingProtocol.PairWithdrawRoute, new PairWithdraw { Id = id ?? "" },
                SharingJson.Default.PairWithdraw, deadline.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                 or ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// A token, fetched if there is not a good one in hand.
    ///
    /// <para>Null when this peer will not have us. That happens when the pairing was undone on their side, or
    /// when their machine has forgotten this one. That is an answer, not a failure. The borrowed library goes
    /// quiet and says why.</para>
    /// </summary>
    public async Task<string?> TokenAsync(CancellationToken cancellation = default)
    {
        lock (_gate)
        {
            if (_token is { } held && DateTime.UtcNow < _tokenUntil - Margin)
                return held;
        }

        try
        {
            using var deadline = Deadline(Quick, cancellation);

            var challenge = await _http.GetFromJsonAsync(
                BaseUrl + SharingProtocol.SessionRoute, SharingJson.Default.SessionChallenge, deadline.Token);
            if (challenge is null)
                return null;

            // The signature is made together with the peer's own device id. That is the id in the URL we are
            // talking to. So an answer given to this machine cannot be replayed to another.
            var peerId = await DeviceIdAsync(deadline.Token);
            if (peerId is null)
                return null;

            var signature = _identity.Sign(PeerSessions.SignedBytes(peerId, challenge.Challenge));

            var proof = new SessionProof
            {
                PublicKey = SharingProtocol.ToBase64Url(_identity.PublicKey),
                Challenge = challenge.Challenge,
                Signature = SharingProtocol.ToBase64Url(signature),
            };

            var response = await _http.PostAsJsonAsync(
                BaseUrl + SharingProtocol.SessionRoute, proof, SharingJson.Default.SessionProof, deadline.Token);
            if (!response.IsSuccessStatusCode)
                return null;

            var grant = await response.Content.ReadFromJsonAsync(
                SharingJson.Default.SessionGrant, deadline.Token);
            if (grant is null || string.IsNullOrEmpty(grant.Token))
                return null;

            lock (_gate)
            {
                _token = grant.Token;
                _tokenUntil = DateTime.UtcNow.AddSeconds(Math.Max(grant.ExpiresIn, 60));
                Kind = grant.Kind;
            }

            return grant.Token;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                 or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>How this peer is serving us: as one of the same user's machines, or as a guest. It is set
    /// when a token is granted.</summary>
    public PeerKind Kind { get; private set; } = PeerKind.Guest;

    /// <summary>
    /// Which account that machine is signed into, or null when it will not say. That happens when it is
    /// signed out, or when it is no longer sharing with this one.
    /// </summary>
    public async Task<PeerAccount?> AccountAsync(CancellationToken cancellation = default)
    {
        if (await TokenAsync(cancellation) is not { } token)
            return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + SharingProtocol.AccountRoute);
        request.Headers.Add("Authorization", "Bearer " + token);

        try
        {
            using var deadline = Deadline(Quick, cancellation);
            using var response = await _http.SendAsync(request, deadline.Token);
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadFromJsonAsync(
                SharingJson.Default.PeerAccount, deadline.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                 or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>
    /// The peer's shared collection, or null when nothing has changed since <paramref name="have"/>. That is
    /// the answer on most visits.
    /// </summary>
    public async Task<PeerCatalog?> CatalogAsync(string? have = null, CancellationToken cancellation = default)
    {
        if (await TokenAsync(cancellation) is not { } token)
            return null;

        var url = BaseUrl + SharingProtocol.CatalogRoute;
        if (!string.IsNullOrEmpty(have))
            url += "?have=" + Uri.EscapeDataString(have);

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", "Bearer " + token);

        try
        {
            using var deadline = Deadline(Listing, cancellation);
            using var response = await _http.SendAsync(request, deadline.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                return null;
            if (!response.IsSuccessStatusCode)
                return null;

            return await response.Content.ReadFromJsonAsync(SharingJson.Default.PeerCatalog, deadline.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                 or ObjectDisposedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where a borrowed track's bytes are, as a URL that a decoder can open. Null when this peer will not
    /// have us. That is the same answer as a file that is not there.
    /// </summary>
    public async Task<string?> TrackUrlAsync(string rpmId, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(rpmId) || await TokenAsync(cancellation) is not { } token)
            return null;

        return $"{BaseUrl}{SharingProtocol.TrackRoute}{Uri.EscapeDataString(rpmId)}?t={Uri.EscapeDataString(token)}";
    }

    /// <summary>A picture from the peer, by whichever route names that kind of picture. Null when there is
    /// none, or the peer cannot be reached.</summary>
    public async Task<byte[]?> ImageAsync(string route, string key, CancellationToken cancellation = default)
    {
        if (string.IsNullOrWhiteSpace(key) || await TokenAsync(cancellation) is not { } token)
            return null;

        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"{BaseUrl}{route}{Uri.EscapeDataString(key)}");
        request.Headers.Add("Authorization", "Bearer " + token);

        try
        {
            using var deadline = Deadline(Quick, cancellation);
            using var response = await _http.SendAsync(request, deadline.Token);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsByteArrayAsync(deadline.Token)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                 or ObjectDisposedException)
        {
            return null;
        }
    }

    private string? _deviceId;

    /// <summary>The peer's device id, remembered after the first greeting. The session signature is bound to
    /// it, and it does not change while the connection does.</summary>
    private async Task<string?> DeviceIdAsync(CancellationToken cancellation)
    {
        if (_deviceId is { } known)
            return known;

        var hello = await HelloAsync(cancellation);
        if (hello is null)
            return null;

        _deviceId = hello.DeviceId;
        return _deviceId;
    }

    /// <summary>An IPv6 address must be in brackets to be used in a URL. IPv4 addresses and host names pass
    /// through unchanged.</summary>
    private static string Bracket(string host) =>
        host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[') ? $"[{host}]" : host;

    public void Dispose() => _http.Dispose();
}

/// <summary>What became of a request to pair.</summary>
public enum PairDelivery
{
    /// <summary>The other machine answered with an offer. The offer says where the pairing stands.</summary>
    Offered,

    /// <summary>The other machine is asking this one at the same moment, and it asks first (409). Its request
    /// is on its way here.</summary>
    Crossed,

    /// <summary>Nothing usable came back.</summary>
    Undelivered,
}
