using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace RPM.Core.Sharing;

/// <summary>Where one borrowed track's bytes come from: a file on this disk, or the cloud copy of a track
/// that this machine has offloaded. It is never both, and never neither. A track that is in neither place
/// is not in the catalogue.</summary>
/// <param name="FilePath">The file, when it is on this disk.</param>
/// <param name="RedirectUrl">A presigned cloud URL, when it is not. The borrower is sent there rather than
/// having the bytes pulled down and pushed out again. The owner's machine does not hold them, and relaying
/// them would pay for the same bytes twice on a line that is often the slowest part of the house.</param>
public readonly record struct TrackBytes(string? FilePath, string? RedirectUrl);

/// <summary>
/// The library, served to the computers that this one shares with.
///
/// <para>This uses a <see cref="TcpListener"/> and hand-written HTTP. It has the same shape as
/// <see cref="Audio.CastServer"/>, for the same reason: six routes do not need a framework reference and a
/// hosting model. This class adds <c>Range</c>, because a borrowed track has to be able to seek. It also
/// adds a token, because a library must not be handed to whoever is on a public wifi network.</para>
///
/// <para>This class does not decide anything. Who is trusted, what is in the catalogue, and where a track's
/// bytes are all come in as callbacks from the app. This class owns the socket, the parsing, and the rule
/// that nothing except the greeting is answered without a token.</para>
/// </summary>
public sealed class LibraryServer : IDisposable
{
    /// <summary>
    /// The shared listing, for a peer that has proved who it is. Or the statement that the copy the peer
    /// already holds is current.
    /// </summary>
    /// <param name="have">The version the borrower says it holds, or null when it holds none. <b>This is not
    /// an optional parameter.</b> Answering it first is what makes an unchanged collection cheap. The listing
    /// is not built, so there are no twenty thousand rows to throw away. It is required, so no source can
    /// forget to check it.</param>
    public delegate Task<CatalogAnswer> CatalogSource(
        TrustedPeer peer, string? have, CancellationToken cancellation);

    /// <summary>
    /// What a <see cref="CatalogSource"/> came back with: the listing, or nothing to send.
    ///
    /// <para>This is a type rather than a nullable catalogue, because null already means something else at
    /// every other hook here. <see cref="TrackSource"/> and <see cref="ImageSource"/> return null for "this
    /// machine does not have that", which is a 404. "Nothing to send because you are already current" is a
    /// 204. The two must not look the same.</para>
    ///
    /// <para><see cref="Unchanged"/> is only correct when the source has established that the listing it
    /// would build carries the version the borrower named. If that is said wrongly, the result is not just a
    /// slow shelf but a wrong one. The borrower keeps what it has, and nothing on either machine shows that
    /// the two disagree.</para>
    /// </summary>
    public readonly record struct CatalogAnswer
    {
        private CatalogAnswer(PeerCatalog? catalog) => Catalog = catalog;

        /// <summary>The listing to send, or null to send none.</summary>
        public PeerCatalog? Catalog { get; }

        /// <summary>The borrower's copy is the current one.</summary>
        public static CatalogAnswer Unchanged => new((PeerCatalog?)null);

        /// <summary>Send this listing. It is still checked against what the borrower holds, so a source that
        /// does not track versions may always answer with one.</summary>
        public static CatalogAnswer Listing(PeerCatalog catalog) =>
            new(catalog ?? throw new ArgumentNullException(nameof(catalog)));
    }

    /// <summary>Where one track's bytes are, or null when this machine does not have that track.</summary>
    public delegate Task<TrackBytes?> TrackSource(TrustedPeer peer, string rpmId, CancellationToken cancellation);

    /// <summary>A picture, and what kind it is. Null when there is none.</summary>
    public delegate Task<(byte[] Bytes, string ContentType)?> ImageSource(
        TrustedPeer peer, string key, CancellationToken cancellation);

    private const int MaxRequestBytes = 16 * 1024;
    private const int MaxBodyBytes = 8 * 1024;

    /// <summary>How long a connection gets for the TLS handshake and the request itself. A connection that
    /// sends nothing is closed after this. Without it, a machine on the network could open connections and
    /// leave them silent until this app ran out of sockets. Tests set a shorter one.</summary>
    public TimeSpan RequestWait { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Connections served at once. A client opens at most a few per computer (Wire.LocalSockets),
    /// so this leaves room for several borrowers. A connection over the limit is closed at once.</summary>
    private const int MaxConnections = 64;

    private readonly SemaphoreSlim _connections = new(MaxConnections, MaxConnections);

    /// <summary>What is read from the disk in one go while serving audio. It is big enough that a FLAC does not
    /// cost thousands of reads, and small enough that a peer hanging up is noticed quickly.</summary>
    private const int CopyBuffer = 128 * 1024;

    private readonly object _gate = new();
    private readonly PeerIdentity _identity;
    private readonly PeerSessions _sessions;

    /// <summary>Every pairing this computer is part of. It is shared with the app, which uses the same board.
    /// So a request that arrives here and a request the app sends are settled under one lock.</summary>
    private readonly PairingBoard _pairings;

    /// <summary>This machine's own certificate, made once from its device key. See <see cref="PeerCertificate"/>
    /// for why there is no certificate authority behind it.</summary>
    private readonly X509Certificate2 _certificate;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;

    /// <param name="identity">This install's key. Its device id is what session challenges are bound to, so an
    /// answer given to this machine is worth nothing to any other machine.</param>
    /// <param name="pairings">The board that requests from other computers are written to. This is the app's own
    /// board, never a new one. The crossed-request rule only holds when both directions use the same board.</param>
    public LibraryServer(PeerIdentity identity, PairingBoard pairings)
    {
        _identity = identity ?? throw new ArgumentNullException(nameof(identity));
        _pairings = pairings ?? throw new ArgumentNullException(nameof(pairings));
        _sessions = new PeerSessions(identity.DeviceId);
        _certificate = PeerCertificate.For(identity);
    }

    /// <summary>Who this machine shares with, checked on every request. Withdrawing trust must stop the music at
    /// once. It must not wait for a token to expire.</summary>
    public Func<IReadOnlyList<TrustedPeer>> Trusted { get; set; } = () => [];

    /// <summary>What this machine calls itself in the offer on somebody else's screen.</summary>
    public string Name { get; set; } = "";

    /// <summary>The display name of whoever is signed in here, or empty. It is sent only in a pairing offer, to the
    /// machine that asked, so the person typing the code can see whose computer it is. The greeting, which anybody
    /// on the network may read, does not carry it.</summary>
    public string Face { get; set; } = "";

    /// <summary>
    /// Called when a peer has proved its key and opened a session. It names the peer and the account it said it is
    /// in, inside its signature: the user id, or empty when it is signed out. This is how this machine learns which
    /// account a borrower is in now (<see cref="TrustedPeer.AccountUserId"/>).
    /// </summary>
    public Action<TrustedPeer, string>? OnSession { get; set; }

    /// <summary>
    /// The account signed in here. It is sent only to trusted peers. See <see cref="PeerAccount"/>.
    /// Empty when signed out.
    /// </summary>
    public Func<PeerAccount> Account { get; set; } = () => new PeerAccount();

    /// <summary>How many tracks are on offer. It goes in the greeting.</summary>
    public Func<int> TrackCount { get; set; } = () => 0;

    public CatalogSource? OnCatalog { get; set; }
    public TrackSource? OnTrack { get; set; }
    public ImageSource? OnCover { get; set; }

    /// <summary>The album's own picture of its record (disc.png), asked for by track id, like the cover. If it
    /// is unset, or if it answers null, this machine has no picture for that record. That is the usual case,
    /// and it is a plain 404.</summary>
    public ImageSource? OnDisc { get; set; }

    public ImageSource? OnArt { get; set; }

    /// <summary>Whether this computer can take one more peer. It is checked when a request arrives and again
    /// when its code is used. If it is unset, this computer is not taking requests, and asking gets a plain no.</summary>
    public Func<bool>? HasRoom { get; set; }

    /// <summary>Called when a computer types back the digits this one showed. Writing the peer down is the app's
    /// job. This class only needs to know that it happened. It is raised once per pairing.</summary>
    public Action<Pairing>? OnPaired { get; set; }

    /// <summary>
    /// Called when serving a request goes wrong. It says which route failed, and what happened.
    ///
    /// <para>
    /// This exists because such a fault is hard to find. A machine whose catalogue cannot be built hangs up on
    /// every request for it, silently. Both ends behave as they do when a laptop is closed. The borrower's
    /// shelf simply stops changing and shows nothing, and the owner has no idea that it is serving anyone
    /// badly. A fault on one computer that is invisible on both is the worst thing this feature can do.
    /// </para>
    /// </summary>
    public Action<string, Exception>? OnError { get; set; }

    /// <summary>The port being listened on, or zero when stopped.</summary>
    public int Port { get; private set; }

    public bool IsRunning => _listener is not null;

    /// <summary>Starts listening on an ephemeral port on every interface, and returns that port. The port is
    /// put into the beacon. Nothing else needs to know it.</summary>
    public int Start()
    {
        lock (_gate)
        {
            if (_listener is not null)
                return Port;

            var listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            _listener = listener;
            _cts = new CancellationTokenSource();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;

            var token = _cts.Token;
            _ = Task.Run(() => AcceptAsync(listener, token));
            return Port;
        }
    }

    public void Stop()
    {
        TcpListener? listener;
        CancellationTokenSource? cts;
        lock (_gate)
        {
            listener = _listener; _listener = null;
            cts = _cts; _cts = null;
            Port = 0;
        }

        // A code that can no longer be typed is removed from the screen. The asking computer's next status call
        // then reads Lost, instead of waiting out its own timeout.
        if (listener is not null)
            _pairings.EndAsked();

        try { cts?.Cancel(); } catch { }
        try { listener?.Stop(); } catch { }
        try { cts?.Dispose(); } catch { }
    }

    /// <summary>Removes a peer's tokens as soon as it stops being trusted, rather than leaving them to expire.</summary>
    public void Revoke(string deviceId) => _sessions.Revoke(deviceId);

    private async Task AcceptAsync(TcpListener listener, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cancellation); }
            catch { return; }

            if (!_connections.Wait(0))
            {
                client.Dispose();
                continue;
            }

            _ = Task.Run(async () =>
            {
                try { await ServeAsync(client, cancellation); }
                finally { _connections.Release(); }
            }, CancellationToken.None);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellation)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;

                // TLS on the local network, always. The token, the listing and the music all cross a network that
                // other people are on, such as a cafe, a hotel or an office. None of the three should be handed to
                // whoever is sitting on that network.
                await using var raw = client.GetStream();
                await using var stream = new SslStream(raw, leaveInnerStreamOpen: false);

                // The handshake and the request must arrive within RequestWait. Serving the answer is not
                // limited by it: a track can stream for an hour.
                using var arriving = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                arriving.CancelAfter(RequestWait);

                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _certificate,
                    ClientCertificateRequired = false,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,

                    // Nothing to check here. The certificate proves the key, and a moment later the token decides who
                    // may read anything.
                    CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, arriving.Token);

                if (await ReadRequestAsync(stream, arriving.Token) is not { } request)
                    return;

                try
                {
                    await RouteAsync(request, stream, cancellation);
                }
                catch (Exception ex) when (Say(request.Path, ex))
                {
                    // The error is reported and then swallowed. One bad request must not take the listener down. The
                    // filter above does the reporting, so the socket still closes the way the other end expects.
                }
            }
            catch
            {
                // A borrower that hangs up mid-track is the ordinary way a stream ends. A malformed request is a
                // stranger's problem. Neither is worth reporting.
            }
        }
    }

    /// <summary>Reports a failed request and always returns true, so that it can sit in an exception filter
    /// without changing the stack. A borrower hanging up mid-track is not worth reporting, because it is the
    /// ordinary way a stream ends.</summary>
    private bool Say(string path, Exception ex)
    {
        if (ex is OperationCanceledException or IOException or System.Net.Sockets.SocketException)
            return true;

        try { OnError?.Invoke(path, ex); }
        catch { /* If the reporter throws, there is nowhere else to report it. */ }

        return true;
    }

    private async Task RouteAsync(Request request, Stream stream, CancellationToken cancellation)
    {
        var path = request.Path;

        if (request.Method is not ("GET" or "HEAD" or "POST"))
        {
            await WriteStatusAsync(stream, "405 Method Not Allowed", cancellation);
            return;
        }

        // The greeting is the one thing anyone may read. It is how a machine that has never been paired is shown
        // to the person sitting in front of this one.
        if (path == SharingProtocol.HelloRoute)
        {
            await WriteJsonAsync(stream, Hello(), SharingJson.Default.PeerHello, request.Method is "HEAD", cancellation);
            return;
        }

        // Pairing is the one other thing that a machine nobody trusts may do. It is how that machine stops being
        // untrusted. It can only ask. The answer comes from a person at this end.
        if (path == SharingProtocol.PairRoute)
        {
            await ServePairAsync(request, stream, cancellation);
            return;
        }

        // The rest of a pairing is reached by its id, which only the two computers know.
        if (path == SharingProtocol.PairStatusRoute)
        {
            await ServePairStatusAsync(request, stream, cancellation);
            return;
        }

        if (path == SharingProtocol.PairNonceRoute)
        {
            await ServePairNonceAsync(request, stream, cancellation);
            return;
        }

        if (path == SharingProtocol.PairCommitRoute)
        {
            await ServePairCommitAsync(request, stream, cancellation);
            return;
        }

        if (path == SharingProtocol.PairOpenRoute)
        {
            await ServePairOpenAsync(request, stream, cancellation);
            return;
        }

        if (path == SharingProtocol.PairWithdrawRoute)
        {
            await ServePairWithdrawAsync(request, stream, cancellation);
            return;
        }

        if (path == SharingProtocol.SessionRoute)
        {
            if (request.Method is "GET")
            {
                await WriteJsonAsync(stream, _sessions.Issue(), SharingJson.Default.SessionChallenge, headOnly: false, cancellation);
                return;
            }

            var proof = Read(request.Body, SharingJson.Default.SessionProof);
            var trusted = Trusted();
            if (proof is null || _sessions.Redeem(proof, trusted) is not { } grant)
            {
                await WriteStatusAsync(stream, "403 Forbidden", cancellation);
                return;
            }

            if (OnSession is { } said && _sessions.Peer(grant.Token, trusted) is { } proved)
            {
                try { said(proved, _sessions.Account(grant.Token) ?? ""); }
                catch (Exception ex) when (Say(path, ex)) { }
            }

            await WriteJsonAsync(stream, grant, SharingJson.Default.SessionGrant, headOnly: false, cancellation);
            return;
        }

        // The collection itself (its listing, and which account holds it) is sent only to a request that
        // carries its token in a header, over a connection whose certificate the other end has pinned. A media
        // URL carries a ticket for its own route and id instead (PeerSessions.Ticket), so the worst a URL read
        // off the wire can do is open that one track or picture until the token runs out.
        var peer = request.HeaderToken is { } token
            ? _sessions.Peer(token, Trusted())
            : MediaTarget(path) is { } target
                ? _sessions.PeerForTicket(request.Query("s"), request.Query("t"), target.Route, target.Id, Trusted())
                : null;

        if (peer is null)
        {
            await WriteStatusAsync(stream, "401 Unauthorized", cancellation);
            return;
        }

        if (path == SharingProtocol.AccountRoute)
        {
            await WriteJsonAsync(stream, Account(), SharingJson.Default.PeerAccount, request.Method is "HEAD", cancellation);
            return;
        }

        if (path == SharingProtocol.CatalogRoute)
        {
            await ServeCatalogAsync(peer, request, stream, cancellation);
            return;
        }

        if (Tail(path, SharingProtocol.TrackRoute) is { } rpmId)
        {
            await ServeTrackAsync(peer, rpmId, request, stream, cancellation);
            return;
        }

        if (Tail(path, SharingProtocol.CoverRoute) is { } coverId)
        {
            await ServeImageAsync(OnCover, peer, coverId, request, stream, cancellation);
            return;
        }

        if (Tail(path, SharingProtocol.DiscRoute) is { } discId)
        {
            await ServeImageAsync(OnDisc, peer, discId, request, stream, cancellation);
            return;
        }

        if (Tail(path, SharingProtocol.ArtRoute) is { } artKey)
        {
            await ServeImageAsync(OnArt, peer, artKey, request, stream, cancellation);
            return;
        }

        await WriteStatusAsync(stream, "404 Not Found", cancellation);
    }

    /// <summary>
    /// Another computer asks to share. This puts the pairing on the board, which shows the six digits on this
    /// screen, and answers with who this computer is and the pairing's id.
    /// </summary>
    private async Task ServePairAsync(Request request, Stream stream, CancellationToken cancellation)
    {
        if (request.Method is not "POST")
        {
            await WriteStatusAsync(stream, "405 Method Not Allowed", cancellation);
            return;
        }

        if (Read(request.Body, SharingJson.Default.PairRequest) is not { } asking ||
            asking.Version != SharingProtocol.Version ||
            SharingProtocol.FromBase64Url(asking.PublicKey) is not { Length: > 0 } key)
        {
            await WriteStatusAsync(stream, "400 Bad Request", cancellation);
            return;
        }

        // A build that sends the typed code in the clear is not paired with. Its exchange can be sat in by a
        // machine in the middle. That computer sees the request fail and reports that the other computer could
        // not be reached. Updating the other computer fixes this.
        if (asking.Pairing != SharingProtocol.Pairing)
        {
            await WriteStatusAsync(stream, "426 Upgrade Required", cancellation);
            return;
        }

        if (HasRoom is not { } hasRoom)
        {
            await WriteJsonAsync(stream, Offer("", new PairStatus { State = PairStatusKind.Declined }),
                SharingJson.Default.PairOffer, headOnly: false, cancellation);
            return;
        }

        var peer = new PairingPeer(PeerIdentity.IdFor(key), asking.Name ?? "", asking.Face ?? "", key);

        var secrets = new PairingSecrets(key, _identity.PublicKey);
        var (kind, pairing) = _pairings.Incoming(_identity.DeviceId, peer, secrets, SafeRoom(hasRoom));

        switch (kind)
        {
            case PairingBoard.IncomingKind.Crossed:
                // This computer asks them first. Their request is refused, and ours is on its way to them.
                await WriteStatusAsync(stream, "409 Conflict", cancellation);
                return;

            case PairingBoard.IncomingKind.Full:
                await WriteJsonAsync(stream, Offer("", new PairStatus { State = PairStatusKind.Full }),
                    SharingJson.Default.PairOffer, headOnly: false, cancellation);
                return;

            case PairingBoard.IncomingKind.Resting:
                await WriteJsonAsync(stream, Offer("", new PairStatus { State = PairStatusKind.Resting }),
                    SharingJson.Default.PairOffer, headOnly: false, cancellation);
                return;
        }

        var status = _pairings.Status(pairing!.Id) ?? PairStatus.Gone;
        await WriteJsonAsync(stream, Offer(pairing.Id, status),
            SharingJson.Default.PairOffer, headOnly: false, cancellation);
    }

    /// <summary>A held status call. It is answered when the pairing changes, or after the hold ends.</summary>
    private async Task ServePairStatusAsync(Request request, Stream stream, CancellationToken cancellation)
    {
        var id = request.Query("id") ?? "";
        long rev = long.TryParse(request.Query("rev"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)
            ? r : 0;

        var status = await _pairings.WaitAsync(id, rev, cancellation) ?? PairStatus.Gone;
        await WriteJsonAsync(stream, status, SharingJson.Default.PairStatus, headOnly: false, cancellation);
    }

    /// <summary>
    /// The asking computer's nonce for one round. The digits are shown on this screen with it.
    /// </summary>
    private async Task ServePairNonceAsync(Request request, Stream stream, CancellationToken cancellation)
    {
        if (request.Method is not "POST" || Read(request.Body, SharingJson.Default.PairNonce) is not { } sent ||
            SharingProtocol.FromBase64Url(sent.Nonce) is not { Length: > 0 } nonce)
        {
            await WriteStatusAsync(stream, "400 Bad Request", cancellation);
            return;
        }

        var status = _pairings.Join(sent.Id, sent.Round, nonce);
        await WriteJsonAsync(stream, status ?? PairStatus.Gone, SharingJson.Default.PairStatus, headOnly: false, cancellation);
    }

    /// <summary>
    /// The asking computer's commitment to the code typed there. The status answer reveals this round's nonce,
    /// but only after the commitment has been taken.
    /// </summary>
    private async Task ServePairCommitAsync(Request request, Stream stream, CancellationToken cancellation)
    {
        if (request.Method is not "POST" || Read(request.Body, SharingJson.Default.PairCommit) is not { } commit ||
            SharingProtocol.FromBase64Url(commit.Commit) is not { Length: > 0 } commitment)
        {
            await WriteStatusAsync(stream, "400 Bad Request", cancellation);
            return;
        }

        var status = _pairings.Commit(commit.Id, commit.Round, commitment);
        await WriteJsonAsync(stream, status ?? PairStatus.Gone, SharingJson.Default.PairStatus, headOnly: false, cancellation);
    }

    /// <summary>
    /// The asking computer opens its commitment. This is the whole of this side's check. It lives here because
    /// the computer that was asked is the one that knows what it showed. The asking computer has made its own
    /// check by now, comparing the nonce against the commitment in the offer. It sends an empty opening when
    /// that check failed.
    /// </summary>
    private async Task ServePairOpenAsync(Request request, Stream stream, CancellationToken cancellation)
    {
        if (request.Method is not "POST" || Read(request.Body, SharingJson.Default.PairOpen) is not { } open)
        {
            await WriteStatusAsync(stream, "400 Bad Request", cancellation);
            return;
        }

        var opening = SharingProtocol.FromBase64Url(open.Opening) ?? [];
        var (status, accepted) = _pairings.Open(open.Id, open.Round, opening, HasRoom is { } hasRoom && SafeRoom(hasRoom));

        if (accepted is not null)
        {
            try { OnPaired?.Invoke(accepted); }
            catch (Exception ex) when (Say(SharingProtocol.PairOpenRoute, ex)) { }
        }

        await WriteJsonAsync(stream, status ?? PairStatus.Gone, SharingJson.Default.PairStatus, headOnly: false, cancellation);
    }

    /// <summary>The asking computer cancelled.</summary>
    private async Task ServePairWithdrawAsync(Request request, Stream stream, CancellationToken cancellation)
    {
        if (request.Method is not "POST" || Read(request.Body, SharingJson.Default.PairWithdraw) is not { } withdraw)
        {
            await WriteStatusAsync(stream, "400 Bad Request", cancellation);
            return;
        }

        _pairings.WithdrawIncoming(withdraw.Id);
        await WriteStatusAsync(stream, "204 No Content", cancellation);
    }

    private PairOffer Offer(string id, PairStatus status) => new()
    {
        DeviceId = _identity.DeviceId,
        Name = Name,
        Face = Face,
        PublicKey = SharingProtocol.ToBase64Url(_identity.PublicKey),
        Id = id,
        Status = status,
    };

    private static bool SafeRoom(Func<bool> hasRoom)
    {
        try { return hasRoom(); }
        catch { return false; }
    }

    private PeerHello Hello() => new()
    {
        DeviceId = _identity.DeviceId,
        Name = Name,
        PublicKey = SharingProtocol.ToBase64Url(_identity.PublicKey),
        TrackCount = SafeCount(),
    };

    /// <summary>The media route and unescaped id a path names, or null for any other path. A ticket is checked
    /// against exactly these two.</summary>
    private static (string Route, string Id)? MediaTarget(string path)
    {
        foreach (var route in new[]
                 { SharingProtocol.TrackRoute, SharingProtocol.CoverRoute, SharingProtocol.DiscRoute, SharingProtocol.ArtRoute })
        {
            if (Tail(path, route) is { } id)
                return (route, id);
        }
        return null;
    }

    private int SafeCount()
    {
        try { return TrackCount(); }
        catch { return 0; }
    }

    private async Task ServeCatalogAsync(
        TrustedPeer peer, Request request, Stream stream, CancellationToken cancellation)
    {
        if (OnCatalog is not { } source)
        {
            await WriteStatusAsync(stream, "503 Service Unavailable", cancellation);
            return;
        }

        // The borrower says which version it already holds. Unchanged is the common answer: a collection is not
        // edited most days, so most polls cost both ends a few hundred bytes instead of the whole shelf. The
        // version is handed to the source, rather than only checked below, because the source is the only thing
        // that can answer it without building the listing.
        var have = request.Query("have") is { Length: > 0 } asked ? asked : null;

        CatalogAnswer answer;
        try
        {
            answer = await source(peer, have, cancellation);
        }
        catch (Exception ex) when (Say(SharingProtocol.CatalogRoute, ex))
        {
            // Nothing has been written yet, so this can still be reported properly. A borrower that is told 500
            // knows its shelf is stale. One that is hung up on cannot tell this machine from a closed laptop, and
            // it shows a collection that stopped being true days ago.
            await WriteStatusAsync(stream, "500 Internal Server Error", cancellation);
            return;
        }

        if (answer.Catalog is not { } catalog)
        {
            await WriteStatusAsync(stream, "204 No Content", cancellation);
            return;
        }

        // The check is made again on the built listing, on purpose. It is not the same check as the source's.
        // The source answers from what it remembers of the last build. This check answers from the bytes about to
        // go out. A source that tracks nothing can hand back a listing every time, and the network still costs
        // nothing when it matches. A source whose memory says "rebuild" is checked here against the version it
        // actually got.
        if (have is not null && string.Equals(have, catalog.Version, StringComparison.Ordinal))
        {
            await WriteStatusAsync(stream, "204 No Content", cancellation);
            return;
        }

        await WriteJsonAsync(stream, catalog, SharingJson.Default.PeerCatalog, request.Method is "HEAD", cancellation);
    }

    private async Task ServeTrackAsync(
        TrustedPeer peer, string rpmId, Request request, Stream stream, CancellationToken cancellation)
    {
        if (OnTrack is not { } source || await source(peer, rpmId, cancellation) is not { } bytes)
        {
            await WriteStatusAsync(stream, "404 Not Found", cancellation);
            return;
        }

        // A track this machine has offloaded is in the cloud, and the borrower is sent there. The URL is signed by
        // the owner's account, so it is the owner's storage that is read. That is what a borrower being allowed
        // to hear the track means.
        if (bytes.RedirectUrl is { Length: > 0 } url)
        {
            await WriteHeadersAsync(stream, "302 Found", cancellation,
                extra: $"Location: {url}\r\n");
            return;
        }

        if (bytes.FilePath is not { Length: > 0 } file || !File.Exists(file))
        {
            await WriteStatusAsync(stream, "404 Not Found", cancellation);
            return;
        }

        await using var body = new FileStream(
            file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, CopyBuffer, useAsync: true);

        long length = body.Length;
        var range = ByteRange.Parse(request.Header("range"), length);

        var type = ContentTypes.For(Path.GetExtension(file));

        if (range is not { } window)
        {
            await WriteHeadersAsync(stream, "200 OK", cancellation,
                contentType: type, contentLength: length,
                extra: "Accept-Ranges: bytes\r\n");

            if (request.Method is not "HEAD")
                await body.CopyToAsync(stream, CopyBuffer, cancellation);
            return;
        }

        await WriteHeadersAsync(stream, "206 Partial Content", cancellation,
            contentType: type, contentLength: window.Length,
            extra: "Accept-Ranges: bytes\r\n" +
                   $"Content-Range: bytes {window.Start}-{window.End}/{length}\r\n");

        if (request.Method is "HEAD")
            return;

        body.Seek(window.Start, SeekOrigin.Begin);
        await CopyAsync(body, stream, window.Length, cancellation);
    }

    private static async Task ServeImageAsync(
        ImageSource? source, TrustedPeer peer, string key, Request request, Stream stream,
        CancellationToken cancellation)
    {
        if (source is null || await source(peer, key, cancellation) is not { } image ||
            image.Bytes is not { Length: > 0 })
        {
            await WriteStatusAsync(stream, "404 Not Found", cancellation);
            return;
        }

        // A picture asked for by key is the same picture every time, so it crosses the network once.
        await WriteHeadersAsync(stream, "200 OK", cancellation,
            contentType: image.ContentType, contentLength: image.Bytes.Length,
            cache: "public, max-age=31536000, immutable");

        if (request.Method is not "HEAD")
            await stream.WriteAsync(image.Bytes, cancellation);
    }

    private static async Task CopyAsync(Stream from, Stream to, long count, CancellationToken cancellation)
    {
        var buffer = new byte[CopyBuffer];
        while (count > 0)
        {
            int wanted = (int)Math.Min(buffer.Length, count);
            int read = await from.ReadAsync(buffer.AsMemory(0, wanted), cancellation);
            if (read <= 0)
                return;

            await to.WriteAsync(buffer.AsMemory(0, read), cancellation);
            count -= read;
        }
    }

    // ── the little bit of HTTP we speak ──

    private sealed record Request(
        string Method, string Target, string Path,
        Dictionary<string, string> Headers, string? Body)
    {
        public string? Header(string name) => Headers.GetValueOrDefault(name);

        /// <summary>The session token as a header. Everything that is not audio asks this way.</summary>
        public string? HeaderToken =>
            Header("authorization") is { } value &&
            value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                ? value["Bearer ".Length..].Trim()
                : null;

        /// <summary>A query value, unescaped. A media URL carries the token's handle in <c>s</c> and its ticket in
        /// <c>t</c> (<see cref="PeerSessions.PeerForTicket"/>), because a decoder is handed a URL and nothing else.</summary>
        public string? Query(string name)
        {
            int mark = Target.IndexOf('?', StringComparison.Ordinal);
            if (mark < 0)
                return null;

            foreach (var pair in Target[(mark + 1)..].Split('&'))
            {
                int equals = pair.IndexOf('=', StringComparison.Ordinal);
                if (equals < 0)
                    continue;
                if (pair[..equals].Equals(name, StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(pair[(equals + 1)..]);
            }
            return null;
        }
    }

    private static async Task<Request?> ReadRequestAsync(Stream stream, CancellationToken cancellation)
    {
        var buffer = new byte[MaxRequestBytes];
        int filled = 0, headerEnd = -1;

        while (filled < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(filled), cancellation);
            if (read <= 0)
                return null;
            filled += read;

            headerEnd = Find(buffer, filled);
            if (headerEnd >= 0)
                break;
        }

        if (headerEnd < 0)
            return null;

        var head = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        var lines = head.Split("\r\n");
        var start = lines[0].Split(' ');
        if (start.Length < 2)
            return null;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        var method = start[0].ToUpperInvariant();
        var target = start[1];
        var path = target.Split('?', 2)[0];

        int bodyStart = headerEnd + 4;
        var carried = buffer.AsMemory(bodyStart, filled - bodyStart);

        string? body = null;

        // Chunked, because that is what an HTTP client sends when it does not know the body length before it
        // starts writing. .NET's JSON content does this by default. A server that only understands Content-Length
        // reads those requests as having no body at all, and answers a good handshake with "no".
        if (headers.TryGetValue("Transfer-Encoding", out var encoding) &&
            encoding.Contains("chunked", StringComparison.OrdinalIgnoreCase))
        {
            body = await ReadChunkedAsync(stream, carried, cancellation);
        }
        else if (headers.TryGetValue("Content-Length", out var declared) &&
            int.TryParse(declared, NumberStyles.Integer, CultureInfo.InvariantCulture, out int length) &&
            length is > 0 and <= MaxBodyBytes)
        {
            var bytes = new byte[length];
            int have = Math.Min(length, carried.Length);
            carried[..have].CopyTo(bytes);

            while (have < length)
            {
                int read = await stream.ReadAsync(bytes.AsMemory(have), cancellation);
                if (read <= 0)
                    return null;
                have += read;
            }

            body = Encoding.UTF8.GetString(bytes);
        }

        return new Request(method, target, path, headers, body);
    }

    /// <summary>
    /// A chunked body, read to its end. Each chunk is a hexadecimal length, the bytes, and a blank line. A zero
    /// length ends it.
    ///
    /// <para>Bounded by <see cref="MaxBodyBytes"/>, like every other body here. The only things posted to this
    /// server are a couple of hundred bytes of handshake, so a body that keeps coming is someone trying something.</para>
    /// </summary>
    private static async Task<string?> ReadChunkedAsync(
        Stream stream, ReadOnlyMemory<byte> carried, CancellationToken cancellation)
    {
        var pending = new List<byte>(carried.ToArray());
        var body = new List<byte>();

        while (true)
        {
            // The size line, and the chunk itself. Either may already be in hand, or may need another read from
            // the socket.
            int newline;
            while ((newline = IndexOfCrLf(pending, 0)) < 0)
            {
                if (!await FillAsync(stream, pending, cancellation))
                    return null;
            }

            var header = Encoding.ASCII.GetString(pending.ToArray(), 0, newline).Split(';')[0].Trim();
            if (!int.TryParse(header, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int size) ||
                size < 0 || body.Count + size > MaxBodyBytes)
                return null;

            pending.RemoveRange(0, newline + 2);

            if (size == 0)
                return Encoding.UTF8.GetString(body.ToArray());

            while (pending.Count < size + 2)
            {
                if (!await FillAsync(stream, pending, cancellation))
                    return null;
            }

            body.AddRange(pending.GetRange(0, size));
            pending.RemoveRange(0, size + 2);
        }
    }

    private static async Task<bool> FillAsync(Stream stream, List<byte> pending, CancellationToken cancellation)
    {
        var buffer = new byte[1024];
        int read = await stream.ReadAsync(buffer, cancellation);
        if (read <= 0)
            return false;

        pending.AddRange(buffer.AsSpan(0, read).ToArray());
        return true;
    }

    private static int IndexOfCrLf(List<byte> bytes, int from)
    {
        for (int i = from; i + 1 < bytes.Count; i++)
        {
            if (bytes[i] == '\r' && bytes[i + 1] == '\n')
                return i;
        }
        return -1;
    }

    private static int Find(byte[] buffer, int filled)
    {
        for (int i = 0; i + 3 < filled; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    /// <summary>A request body in the shape the route expects, or null when it is not that shape.</summary>
    /// <remarks>The shape is passed as a parameter rather than a type argument. A published build is Native AOT,
    /// where a type argument alone describes nothing. The generated metadata has to be named at the call.</remarks>
    private static T? Read<T>(string? body, JsonTypeInfo<T> shape) where T : class
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try { return JsonSerializer.Deserialize(body, shape); }
        catch (JsonException) { return null; }
    }

    /// <summary>The id or key at the end of a route, unescaped. Null when the path is not that route, or when
    /// what follows it is empty.</summary>
    private static string? Tail(string path, string prefix)
    {
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var tail = Uri.UnescapeDataString(path[prefix.Length..]);
        return tail.Length == 0 ? null : tail;
    }

    private static Task WriteStatusAsync(Stream stream, string status, CancellationToken cancellation) =>
        WriteHeadersAsync(stream, status, cancellation, contentLength: 0);

    /// <summary>One answer, as JSON. The shape is a parameter for the same reason as on <see cref="Read{T}"/>.</summary>
    private static async Task WriteJsonAsync<T>(
        Stream stream, T value, JsonTypeInfo<T> shape, bool headOnly, CancellationToken cancellation)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(value, shape);
        await WriteHeadersAsync(stream, "200 OK", cancellation,
            contentType: "application/json; charset=utf-8", contentLength: json.Length);

        if (!headOnly)
            await stream.WriteAsync(json, cancellation);
    }

    private static async Task WriteHeadersAsync(
        Stream stream, string status, CancellationToken cancellation,
        string? contentType = null, long? contentLength = null,
        string cache = "no-store", string extra = "")
    {
        var head = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append("\r\n")
            .Append("Server: RPM\r\n")
            .Append("Connection: close\r\n")
            .Append("Cache-Control: ").Append(cache).Append("\r\n");

        if (contentType is not null) head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        if (contentLength is long length) head.Append("Content-Length: ").Append(length).Append("\r\n");
        head.Append(extra).Append("\r\n");

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), cancellation);
        await stream.FlushAsync(cancellation);
    }

    public void Dispose()
    {
        Stop();
        _certificate.Dispose();
    }
}

/// <summary>What to call the bytes being served. Only the containers a library can hold are listed, plus one
/// honest fallback. A player that has been told the wrong type is worse off than one that has been told nothing.</summary>
internal static class ContentTypes
{
    public static string For(string? extension) => extension?.ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".m4a" or ".m4b" or ".mp4" => "audio/mp4",
        ".aac" => "audio/aac",
        ".ogg" or ".oga" => "audio/ogg",
        ".opus" => "audio/opus",
        ".wav" => "audio/wav",
        ".aiff" or ".aif" => "audio/aiff",
        ".wma" => "audio/x-ms-wma",
        ".ape" => "audio/x-ape",
        ".wv" => "audio/x-wavpack",
        _ => "application/octet-stream",
    };
}
