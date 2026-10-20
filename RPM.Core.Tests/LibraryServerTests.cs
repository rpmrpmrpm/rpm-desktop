using System.Net;
using System.Net.Http;
using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// Two RPMs, one socket: the server this machine runs and the client the other one uses, talking to
/// each other over a real loopback connection.
///
/// <para>What is worth an end-to-end test rather than a unit one is the part where being wrong is
/// invisible: a stranger being served a catalogue, a token that opens a route it should not, and
/// <c>Range</c>. A server that ignores Range still plays music, and fails only when somebody
/// drags the seek bar.</para>
/// </summary>
public class LibraryServerTests : IDisposable
{
    private readonly PeerIdentity _server = PeerIdentity.Create();
    private readonly PeerIdentity _client = PeerIdentity.Create();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rpm-share-" + Guid.NewGuid().ToString("N"));

    /// <summary>The serving machine's pairing board, read by the pairing tests for what its screen
    /// would show.</summary>
    private readonly PairingBoard _board = new(PairingTimes.Standard, () => DateTime.UtcNow);

    private static readonly PeerTrack Track = new()
    {
        RpmId = "11111111-2222-3333-4444-555555555555",
        RelativePath = "Boards of Canada/Geogaddi/01 Ready Lets Go.flac",
        Title = "Ready Lets Go",
        Artist = "Boards of Canada",
        Album = "Geogaddi",
        TrackNumber = 1,
        DurationSeconds = 60,
    };

    private TrustedPeer Trusted() => new()
    {
        DeviceId = _client.DeviceId,
        PublicKey = _client.PublicKey,
        Name = "the other computer",
        Kind = PeerKind.Account,
    };

    private LibraryServer Serving(
        byte[]? audio = null,
        IReadOnlyList<TrustedPeer>? trusted = null,
        LibraryServer.CatalogSource? catalog = null)
    {
        Directory.CreateDirectory(_folder);
        var file = Path.Combine(_folder, "track.flac");
        File.WriteAllBytes(file, audio ?? [1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);

        var server = new LibraryServer(_server, _board)
        {
            HasRoom = () => true,
            Name = "Alex's iMac",
            TrackCount = () => 1,
            Trusted = () => trusted ?? [Trusted()],
            // Answers with the listing every time and tracks no versions of its own, which is the
            // case LibraryServer's own check exists for: the 204 in these tests comes from there.
            OnCatalog = catalog ?? ((_, _, _) => Task.FromResult(LibraryServer.CatalogAnswer.Listing(new PeerCatalog
            {
                Version = PeerCatalog.VersionOf([Track], [], [], []),
                Name = "Alex's iMac",
                Tracks = [Track],
            }))),
            OnTrack = (_, id, _) => Task.FromResult<TrackBytes?>(
                id == Track.RpmId ? new TrackBytes(file, null) : null),
            OnArt = (_, key, _) => Task.FromResult<(byte[], string)?>(
                key == "sleeve" ? ("JPEG"u8.ToArray(), "image/jpeg") : null),
            OnDisc = (_, id, _) => Task.FromResult<(byte[], string)?>(
                id == Track.RpmId ? ("PNG"u8.ToArray(), "image/png") : null),
        };

        server.Start();
        return server;
    }

    private PeerClient Borrower(LibraryServer server, string account = "") =>
        new(_client, "127.0.0.1", server.Port, _server.PublicKey, () => account);

    /// <summary>An HttpClient that trusts the serving machine's certificate and nothing else.
    /// The tests use it where they read a URL directly rather than through
    /// <see cref="PeerClient"/>.</summary>
    private HttpClient Reader(bool followRedirects = true) =>
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = followRedirects,
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    PeerCertificate.Matches(certificate, _server.PublicKey),
            },
        });

    [Fact]
    public async Task A_machine_says_who_it_is_before_anybody_has_proved_anything()
    {
        using var server = Serving();
        using var client = Borrower(server);

        var hello = await client.HelloAsync();

        Assert.NotNull(hello);
        Assert.Equal(_server.DeviceId, hello!.DeviceId);
        Assert.Equal("Alex's iMac", hello.Name);
        Assert.Equal(1, hello.TrackCount);
        Assert.Equal(_server.PublicKey, SharingProtocol.FromBase64Url(hello.PublicKey));
    }

    [Fact]
    public async Task A_trusted_computer_reads_the_catalog()
    {
        using var server = Serving();
        using var client = Borrower(server);

        var catalog = await client.CatalogAsync();

        Assert.NotNull(catalog);
        Assert.Equal("Geogaddi", Assert.Single(catalog!.Tracks).Album);
        Assert.Equal(PeerKind.Account, client.Kind);
    }

    [Fact]
    public async Task A_stranger_reads_nothing()
    {
        using var server = Serving(trusted: []);
        using var client = Borrower(server);

        Assert.NotNull(await client.HelloAsync());
        Assert.Null(await client.TokenAsync());
        Assert.Null(await client.CatalogAsync());
    }

    [Fact]
    public async Task A_catalog_that_has_not_changed_is_not_sent_again()
    {
        using var server = Serving();
        using var client = Borrower(server);

        var first = await client.CatalogAsync();

        Assert.Null(await client.CatalogAsync(first!.Version));
    }

    /// <summary>
    /// The version the borrower holds reaches the source, and a source that says "you are current"
    /// is believed without a listing being built. This is the whole reason the hook takes
    /// <c>have</c>: if the check ran after the listing was built from the entire collection, a poll
    /// from a machine with nothing to learn would cost the same as one from a machine with everything
    /// to learn.
    /// </summary>
    [Fact]
    public async Task A_borrower_that_is_current_is_told_so_without_a_listing_being_built()
    {
        var built = 0;
        var version = PeerCatalog.VersionOf([Track], [], [], []);

        using var server = Serving(catalog: (_, have, _) =>
        {
            if (string.Equals(have, version, StringComparison.Ordinal))
                return Task.FromResult(LibraryServer.CatalogAnswer.Unchanged);

            built++;
            return Task.FromResult(LibraryServer.CatalogAnswer.Listing(new PeerCatalog
            {
                Version = version,
                Name = "Alex's iMac",
                Tracks = [Track],
            }));
        });
        using var client = Borrower(server);

        var first = await client.CatalogAsync();
        Assert.NotNull(first);

        Assert.Null(await client.CatalogAsync(first!.Version));
        Assert.Null(await client.CatalogAsync(first.Version));

        Assert.Equal(1, built);
    }

    [Fact]
    public async Task A_catalog_that_did_change_comes_across()
    {
        using var server = Serving();
        using var client = Borrower(server);

        Assert.NotNull(await client.CatalogAsync("a-version-from-last-week"));
    }

    [Fact]
    public async Task A_borrowed_track_plays_from_a_url_a_decoder_can_open()
    {
        var audio = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();
        using var server = Serving(audio);
        using var client = Borrower(server);

        var url = await client.TrackUrlAsync(Track.RpmId);
        Assert.NotNull(url);

        using var http = Reader();
        var body = await http.GetByteArrayAsync(url);

        Assert.Equal(audio, body);
    }

    [Fact]
    public async Task A_borrowed_track_seeks()
    {
        var audio = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();
        using var server = Serving(audio);
        using var client = Borrower(server);

        var url = await client.TrackUrlAsync(Track.RpmId);

        using var http = Reader();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Range", "bytes=1000-1099");

        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal("bytes 1000-1099/5000", response.Content.Headers.ContentRange!.ToString());
        Assert.Equal(audio[1000..1100], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task The_last_bytes_of_a_track_come_back_which_is_how_a_container_index_is_read()
    {
        var audio = Enumerable.Range(0, 5000).Select(i => (byte)(i % 251)).ToArray();
        using var server = Serving(audio);
        using var client = Borrower(server);

        var url = await client.TrackUrlAsync(Track.RpmId);

        using var http = Reader();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Range", "bytes=-100");

        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(audio[4900..], await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_track_this_machine_offloaded_sends_the_borrower_to_the_cloud_copy()
    {
        using var server = Serving();
        server.OnTrack = (_, _, _) =>
            Task.FromResult<TrackBytes?>(new TrackBytes(null, "https://example.invalid/signed"));

        using var client = Borrower(server);
        var url = await client.TrackUrlAsync(Track.RpmId);

        using var http = Reader(followRedirects: false);
        using var response = await http.GetAsync(url);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("https://example.invalid/signed", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task A_track_this_machine_does_not_have_is_a_404_rather_than_a_wrong_track()
    {
        using var server = Serving();
        using var client = Borrower(server);

        var token = await client.TokenAsync();
        using var http = Reader();
        using var response = await http.GetAsync(
            PeerClient.MediaUrl(client.BaseUrl, SharingProtocol.TrackRoute, "not-a-track", token!));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Audio_is_not_served_without_a_token()
    {
        using var server = Serving();
        using var client = Borrower(server);

        using var http = Reader();
        using var response = await http.GetAsync(
            $"{client.BaseUrl}{SharingProtocol.TrackRoute}{Track.RpmId}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_made_up_token_is_nobody()
    {
        using var server = Serving();
        using var client = Borrower(server);

        using var http = Reader();
        using var response = await http.GetAsync(
            $"{client.BaseUrl}{SharingProtocol.TrackRoute}{Track.RpmId}?t=have-this-one");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Album_art_crosses_the_network_once()
    {
        using var server = Serving();
        using var client = Borrower(server);

        var art = await client.ImageAsync(SharingProtocol.ArtRoute, "sleeve");

        Assert.Equal("JPEG"u8.ToArray(), art);
        Assert.Null(await client.ImageAsync(SharingProtocol.ArtRoute, "no-such-sleeve"));
    }

    /// <summary>The disc image, which is not the cover: a borrowed album's disc.png is on the
    /// machine that holds it, and without this route the borrower would draw the app's own disc for
    /// every album on a shared shelf. It has its own route, so an album with a cover and no disc art
    /// (most albums) answers the two requests separately.</summary>
    [Fact]
    public async Task The_record_itself_crosses_the_network_too()
    {
        using var server = Serving();
        using var client = Borrower(server);

        Assert.Equal("PNG"u8.ToArray(), await client.ImageAsync(SharingProtocol.DiscRoute, Track.RpmId));
        Assert.Null(await client.ImageAsync(SharingProtocol.DiscRoute, "not-a-track"));
    }

    /// <summary>An older peer knows no disc route, and a machine holding no disc art answers exactly as
    /// it does: 404, which the borrower reads as "draw your own disc". The wire version does not
    /// matter here.</summary>
    [Fact]
    public async Task A_machine_that_serves_no_disc_art_is_not_an_error()
    {
        using var server = Serving();
        server.OnDisc = null;
        using var client = Borrower(server);

        Assert.Null(await client.ImageAsync(SharingProtocol.DiscRoute, Track.RpmId));
    }

    [Fact]
    public async Task Trust_withdrawn_while_the_music_plays_stops_the_next_read()
    {
        var trusted = new List<TrustedPeer> { Trusted() };
        using var server = Serving(trusted: trusted);
        using var client = Borrower(server);

        var url = await client.TrackUrlAsync(Track.RpmId);
        Assert.NotNull(url);

        trusted.Clear();
        server.Revoke(_client.DeviceId);

        using var http = Reader();
        using var response = await http.GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>What the serving machine's screen shows for the client's request.</summary>
    private Pairing Asked() => _board.Find(_client.DeviceId)!;

    private async Task<PairOffer> AskAsync(PeerClient client, string name = "laptop", string face = "")
    {
        var (offer, delivery) = await client.PairAsync(name, face, PairingCode.Nonce(), CancellationToken.None);
        Assert.Equal(PairDelivery.Offered, delivery);
        return offer!;
    }

    /// <summary>What the asking machine does once the offer is in: sends its nonce for the first
    /// round, which puts the digits on the serving machine's screen.</summary>
    private static async Task<PeerClient.PairingAsker> JoinAsync(PeerClient client, PairOffer offer)
    {
        var asker = new PeerClient.PairingAsker(client);
        var joined = await asker.JoinAsync(offer.Id, offer.Status, CancellationToken.None);
        Assert.True(joined!.Shown);
        return asker;
    }

    /// <summary>A typed code, as the asking machine's run sends it: through the asker, then joining
    /// the round the answer shows when a wrong code started a new one.</summary>
    private static async Task<PairStatus?> TypeAsync(PeerClient.PairingAsker asker, string id, string code)
    {
        var status = await asker.TypeAsync(id, code, CancellationToken.None);
        if (status is not null && await asker.JoinAsync(id, status, CancellationToken.None) is { } joined)
            return joined;
        return status;
    }

    /// <summary>Six digits that are not <paramref name="code"/>.</summary>
    private static string Not(string code) =>
        ((int.Parse(code, System.Globalization.CultureInfo.InvariantCulture) + 1) % 1_000_000)
            .ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task The_machine_that_was_asked_shows_the_digits_and_the_asking_one_types_them()
    {
        using var server = Serving(trusted: []);
        Pairing? paired = null;
        server.OnPaired = pairing => paired = pairing;

        using var client = Borrower(server);
        var offer = await AskAsync(client, "Anna's laptop", "Anna");

        Assert.Equal(PairStatusKind.ShowingCode, offer.Status.State);
        Assert.NotEmpty(offer.Id);

        // No digits on that screen until the asking machine has sent its nonce for the round.
        Assert.Equal("", Asked().Code);
        var asker = await JoinAsync(client, offer);

        var asked = Asked();
        Assert.Equal(PairingState.ShowingCode, asked.State);
        Assert.Matches("^[0-9]{6}$", asked.Code);
        Assert.Equal("Anna's laptop", asked.Peer.Name);

        // The person, alongside the machine. What the question on this screen is headed with, and
        // the only name available for it: nothing can be looked up about a peer that is not paired.
        Assert.Equal("Anna", asked.Peer.Face);

        // The digits off their screen, typed here. That is the whole of the yes; nothing was pressed
        // at the other end.
        var status = await TypeAsync(asker, offer.Id, asked.Code);
        Assert.Equal(PairStatusKind.Accepted, status!.State);
        Assert.Equal(_client.DeviceId, paired?.DeviceId);
    }

    /// <summary>
    /// The asking machine has both keys and its own nonce. If it got the answering machine's nonce
    /// before committing to what was typed, it could compute the digits and pair without anybody
    /// reading the other screen. Every value in the offer, and in the answer that puts the digits on
    /// screen, is tried as that nonce against the commitment the offer carries; none may open it.
    /// </summary>
    [Fact]
    public async Task Nothing_before_the_commitment_gives_the_asking_machine_the_other_nonce()
    {
        using var server = Serving(trusted: []);
        using var client = Borrower(server);
        var offer = await AskAsync(client);
        var asker = new PeerClient.PairingAsker(client);
        var joined = await asker.JoinAsync(offer.Id, offer.Status, CancellationToken.None);

        var promised = offer.Status.Commit;
        Assert.NotEqual("", promised);
        Assert.Equal("", joined!.Reveal);

        var values = Strings(System.Text.Json.JsonSerializer.SerializeToElement(offer, SharingJson.Default.PairOffer))
            .Concat(Strings(System.Text.Json.JsonSerializer.SerializeToElement(joined, SharingJson.Default.PairStatus)));
        foreach (var value in values)
        {
            if (SharingProtocol.FromBase64Url(value) is not { Length: > 0 } bytes)
                continue;

            Assert.NotEqual(promised, SharingProtocol.ToBase64Url(PairingCode.CommitNonce(bytes)));
        }

        static IEnumerable<string> Strings(System.Text.Json.JsonElement element) => element.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Object => element.EnumerateObject().SelectMany(p => Strings(p.Value)),
            System.Text.Json.JsonValueKind.String => [element.GetString()!],
            _ => [],
        };
    }

    [Fact]
    public async Task A_wrong_code_leaves_the_question_standing()
    {
        using var server = Serving(trusted: []);
        using var client = Borrower(server);
        var offer = await AskAsync(client);

        var asker = await JoinAsync(client, offer);
        var first = Asked().Code;

        var wrong = await TypeAsync(asker, offer.Id, Not(first));
        Assert.Equal(PairStatusKind.ShowingCode, wrong!.State);
        Assert.Equal(2, wrong.TriesLeft);

        // Still standing: a mistyped digit is not somebody saying no. The screen shows new digits for
        // the new round, and those are the ones that count.
        Assert.Equal(2, wrong.Round);
        Assert.True(wrong.Shown);
        var right = await TypeAsync(asker, offer.Id, Asked().Code);
        Assert.Equal(PairStatusKind.Accepted, right!.State);
    }

    [Fact]
    public async Task Guessing_at_the_digits_ends_the_pairing()
    {
        using var server = Serving(trusted: []);
        using var client = Borrower(server);
        var offer = await AskAsync(client);
        var asker = await JoinAsync(client, offer);

        await TypeAsync(asker, offer.Id, Not(Asked().Code));
        await TypeAsync(asker, offer.Id, Not(Asked().Code));
        var code = Asked().Code;
        var third = await TypeAsync(asker, offer.Id, Not(code));
        Assert.Equal(PairStatusKind.TooManyTries, third!.State);

        // And the right code is worth nothing afterwards: six digits with unlimited guesses are not
        // six digits.
        var late = await TypeAsync(asker, offer.Id, code);
        Assert.Equal(PairStatusKind.TooManyTries, late!.State);
    }

    [Fact]
    public async Task A_decline_there_wakes_the_status_call_here()
    {
        using var server = Serving(trusted: []);
        using var client = Borrower(server);
        var offer = await AskAsync(client);

        var held = client.PairStatusAsync(offer.Id, offer.Status.Rev, CancellationToken.None);
        await Task.Delay(100);
        Assert.False(held.IsCompleted);

        _board.Decline(_client.DeviceId);

        var status = await held.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(PairStatusKind.Declined, status!.State);
    }

    [Fact]
    public async Task A_withdraw_takes_the_code_off_the_screen()
    {
        using var server = Serving(trusted: []);
        using var client = Borrower(server);
        var offer = await AskAsync(client);

        Assert.True(await client.PairWithdrawAsync(offer.Id, CancellationToken.None));

        Assert.Equal(PairingState.Withdrawn, Asked().State);
    }

    [Fact]
    public async Task Stopping_the_server_ends_the_code_on_its_screen()
    {
        var server = Serving(trusted: []);
        using var client = Borrower(server);
        await AskAsync(client);

        server.Dispose();

        Assert.Equal(PairingState.Lost, Asked().State);
    }

    [Fact]
    public async Task An_id_nobody_issued_is_answered_gone()
    {
        using var server = Serving(trusted: []);
        using var client = Borrower(server);

        var status = await client.PairStatusAsync("made-up", 1, CancellationToken.None);

        var asker = new PeerClient.PairingAsker(client);
        var round = new PairStatus { State = PairStatusKind.ShowingCode, Round = 1, Commit = SharingProtocol.ToBase64Url([1]) };
        var joined = await asker.JoinAsync("made-up", round, CancellationToken.None);
        var typed = await asker.TypeAsync("made-up", "123456", CancellationToken.None);

        Assert.Equal(PairStatusKind.Gone, status!.State);
        Assert.Equal(PairStatusKind.Gone, joined!.State);
        Assert.Equal(PairStatusKind.Gone, typed!.State);
    }

    [Fact]
    public async Task A_machine_whose_five_places_are_taken_says_so()
    {
        using var server = Serving(trusted: []);
        server.HasRoom = () => false;

        using var client = Borrower(server);
        var offer = await AskAsync(client);

        Assert.Equal(PairStatusKind.Full, offer.Status.State);
        Assert.Empty(offer.Id);
        Assert.Null(_board.Find(_client.DeviceId));
    }

    /// <summary>A build before pairing 2 sends the typed code in the clear, which is the exchange a
    /// machine in the middle could read. Its request is refused and puts nothing on this
    /// screen.</summary>
    [Fact]
    public async Task A_request_from_a_build_before_pairing_2_is_refused()
    {
        using var server = Serving(trusted: []);
        using var http = Reader();

        var old = new PairRequest
        {
            PublicKey = SharingProtocol.ToBase64Url(_client.PublicKey),
            Name = "laptop",
            Nonce = SharingProtocol.ToBase64Url(PairingCode.Nonce()),
            Pairing = 0,
        };
        using var response = await http.PostAsync(
            $"https://127.0.0.1:{server.Port}{SharingProtocol.PairRoute}",
            System.Net.Http.Json.JsonContent.Create(old, SharingJson.Default.PairRequest));

        Assert.Equal(HttpStatusCode.UpgradeRequired, response.StatusCode);
        Assert.Null(_board.Find(_client.DeviceId));
    }

    [Fact]
    public async Task Ten_wrong_codes_across_requests_and_the_next_request_is_told_to_wait()
    {
        using var server = Serving(trusted: []);
        using var client = Borrower(server);

        for (var wrong = 0; wrong < PairingTimes.Standard.WrongCodesAllowed;)
        {
            var offer = await AskAsync(client);
            var asker = await JoinAsync(client, offer);
            for (var tries = 0; tries < PairingTimes.Standard.Tries && wrong < PairingTimes.Standard.WrongCodesAllowed; tries++, wrong++)
                await TypeAsync(asker, offer.Id, Not(Asked().Code));
        }

        var refused = await AskAsync(client);

        Assert.Equal(PairStatusKind.Resting, refused.Status.State);
        Assert.Empty(refused.Id);
    }

    [Fact]
    public async Task A_machine_that_is_not_taking_requests_says_no_rather_than_nothing()
    {
        using var server = Serving(trusted: []);
        server.HasRoom = null;

        using var client = Borrower(server);
        var offer = await AskAsync(client);

        Assert.Equal(PairStatusKind.Declined, offer.Status.State);
    }

    [Fact]
    public async Task Both_machines_asking_at_once_leaves_one_of_them_asking()
    {
        using var server = Serving(trusted: []);

        // The serving machine is already asking the client.
        _board.Begin(new PairingPeer(_client.DeviceId, "laptop", "", _client.PublicKey));

        using var client = Borrower(server);
        var (offer, delivery) = await client.PairAsync("laptop", "", PairingCode.Nonce(), CancellationToken.None);

        if (PairingCode.AsksFirst(_server.DeviceId, _client.DeviceId))
        {
            // The serving machine asks first: the client's request is refused, and only the client's
            // screen will show a code.
            Assert.Equal(PairDelivery.Crossed, delivery);
            Assert.Equal(PairingState.Sending, Asked().State);
        }
        else
        {
            // The client asks first: the serving machine's own request turns into the client's
            // question on its screen.
            Assert.Equal(PairDelivery.Offered, delivery);
            Assert.Equal(PairingState.ShowingCode, Asked().State);
            Assert.NotEmpty(offer!.Id);
        }
    }

    [Fact]
    public async Task Pairing_alone_does_not_open_the_library()
    {
        using var server = Serving(trusted: []);
        using var client = Borrower(server);
        var offer = await AskAsync(client);
        var asker = await JoinAsync(client, offer);
        Assert.Equal(PairStatusKind.Accepted, (await TypeAsync(asker, offer.Id, Asked().Code))!.State);

        // The yes is a person's answer; what it produces is a row in the other machine's book. Until
        // that row is there, this peer is a stranger, which is what stops "asked nicely" from being
        // a way in.
        Assert.Null(await client.CatalogAsync());
    }

    [Fact]
    public async Task A_media_url_opens_its_own_track_and_nothing_else()
    {
        using var server = Serving();
        using var client = Borrower(server);

        var token = await client.TokenAsync();
        using var http = Reader();

        // The decoder does not check certificates, so a track URL is the one thing here that could be
        // read off the wire. It has to be worth as little as possible: this track, and nothing else.
        var url = await client.TrackUrlAsync(Track.RpmId);
        Assert.DoesNotContain(token!, url!, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync(url)).StatusCode);

        // The same handle and ticket on another track, on a picture, on the listing and on the account.
        var query = new Uri(url!).Query;
        foreach (var other in new[]
                 {
                     $"{SharingProtocol.TrackRoute}22222222-3333-4444-5555-666666666666",
                     $"{SharingProtocol.ArtRoute}sleeve",
                     SharingProtocol.CatalogRoute,
                     SharingProtocol.AccountRoute,
                 })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync($"{client.BaseUrl}{other}{query}")).StatusCode);
        }
    }

    [Fact]
    public async Task The_token_itself_in_a_url_opens_nothing()
    {
        using var server = Serving();
        using var client = Borrower(server);

        var token = await client.TokenAsync();
        using var http = Reader();

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await http.GetAsync($"{client.BaseUrl}{SharingProtocol.TrackRoute}{Track.RpmId}?t={token}")).StatusCode);
    }

    [Fact]
    public async Task A_borrower_says_which_account_it_is_in_only_inside_its_session_proof()
    {
        using var server = Serving();
        (TrustedPeer Peer, string Account)? said = null;
        server.OnSession = (peer, account) => said = (peer, account);

        using var client = Borrower(server, account: "user-7");
        Assert.NotNull(await client.TokenAsync());

        Assert.Equal(_client.DeviceId, said?.Peer.DeviceId);
        Assert.Equal("user-7", said?.Account);
    }

    [Fact]
    public async Task A_machine_answering_in_the_librarys_place_is_refused_before_a_word()
    {
        using var server = Serving();

        // The address is right and the port is right; the key is somebody else's. Which is exactly
        // what a machine sitting on the same network and answering first looks like.
        using var impostor = PeerIdentity.Create();
        using var client = new PeerClient(_client, "127.0.0.1", server.Port, impostor.PublicKey, () => "");

        Assert.Null(await client.HelloAsync());
        Assert.Null(await client.CatalogAsync());
    }

    [Fact]
    public async Task The_library_is_served_over_tls()
    {
        using var server = Serving();
        using var client = Borrower(server);

        Assert.StartsWith("https://", client.BaseUrl, StringComparison.Ordinal);

        // A reader that checks nothing still gets a TLS connection: what is refused above is the
        // wrong key, not the missing authority.
        using var plain = new HttpClient(new SocketsHttpHandler
        {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            },
        });

        var hello = await plain.GetAsync($"{client.BaseUrl}{SharingProtocol.HelloRoute}");
        Assert.Equal(HttpStatusCode.OK, hello.StatusCode);
    }

    [Fact]
    public async Task The_certificate_carries_the_machines_own_key()
    {
        using var server = Serving();

        byte[]? presented = null;
        using var handler = new SocketsHttpHandler
        {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    presented = certificate?.GetPublicKey();
                    return PeerCertificate.Matches(certificate, _server.PublicKey);
                },
            },
        };

        using var http = new HttpClient(handler);
        await http.GetAsync($"https://127.0.0.1:{server.Port}{SharingProtocol.HelloRoute}");

        // The key inside the certificate IS the device key, which is what makes "trust this peer"
        // and "trust this connection" one statement rather than two.
        Assert.NotNull(presented);
    }

    public void Dispose()
    {
        _server.Dispose();
        _client.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>A connection that says nothing is closed. Without this a machine on the network could hold
    /// connections open until this app ran out of sockets.</summary>
    [Fact]
    public async Task A_connection_that_sends_nothing_is_closed()
    {
        Directory.CreateDirectory(_folder);
        var server = new LibraryServer(_server, _board) { RequestWait = TimeSpan.FromMilliseconds(300) };
        server.Start();
        try
        {
            using var silent = new System.Net.Sockets.TcpClient();
            await silent.ConnectAsync(IPAddress.Loopback, server.Port);
            var stream = silent.GetStream();

            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var read = await stream.ReadAsync(new byte[1], limit.Token);

            Assert.Equal(0, read);
        }
        finally
        {
            server.Stop();
        }
    }
}
