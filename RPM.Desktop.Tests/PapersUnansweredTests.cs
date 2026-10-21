using System.Net;
using System.Net.Http;
using RPM.Core.Metadata.Providers;
using RPM.Core.Net;
using RPM.Desktop.Services.Account;
using RPM.Desktop.Services.Catalog;
using RPM.Fingerprint.Catalog;
using RPM.Fingerprint.External;

namespace RPM.Desktop.Tests;

/// <summary>
/// A failure is not "nothing".
///
/// <para>The papers path must not turn a failure into an empty answer. When the API client gets a 503,
/// or Wikipedia does not answer, the result is an unanswered question, not empty papers. If an empty
/// answer were written to disk, a record opened offline would show "No information was found for this
/// album" for days, and one opened during a short Wikipedia outage would keep no article for weeks.</para>
/// </summary>
public class PapersUnansweredTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "rpm-papers-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private int Written => Directory.Exists(_dir) ? Directory.GetFiles(_dir, "*.json").Length : 0;

    // ── the API client ──

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(answer(request));
        }
    }

    private RpmMetadataApi Api(Func<HttpRequestMessage, HttpResponseMessage> answer)
        => new(TestSessions.Auth(_dir),
               new HttpClient(new Stub(answer)) { BaseAddress = new Uri("https://api.example.com/") });

    private static HttpResponseMessage Status(HttpStatusCode code, string body = "")
        => new(code) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    [Fact]
    public async Task A_server_error_is_unanswered_and_not_empty_papers()
    {
        var asked = await Api(_ => Status(HttpStatusCode.ServiceUnavailable)).GetPapersAsync("Talk Talk", "Spirit of Eden", 6);

        Assert.IsType<Asked<CatalogPapers>.Unanswered>(asked);
    }

    [Fact]
    public async Task No_network_is_unanswered()
    {
        var asked = await Api(_ => throw new HttpRequestException("no route to host"))
            .GetPapersAsync("Talk Talk", "Spirit of Eden", 6);

        Assert.IsType<Asked<CatalogPapers>.Unanswered>(asked);
    }

    [Fact]
    public async Task No_api_configured_is_unanswered()
    {
        var api = new RpmMetadataApi(TestSessions.Auth(_dir), http: null);

        Assert.IsType<Asked<CatalogPapers>.Unanswered>(await api.GetPapersAsync("Talk Talk", null, 0));
        Assert.IsType<Asked<CatalogArtist?>.Unanswered>(await api.GetArtistAsync("mb-tt"));
        Assert.IsType<Asked<IReadOnlyList<CatalogArtist>>.Unanswered>(await api.GetArtistsNamedAsync("Talk Talk", 5));
    }

    /// <summary>The server answers "the catalogues know nothing" with 200 and empty papers. That is an
    /// answer.</summary>
    [Fact]
    public async Task Empty_papers_are_an_answer()
    {
        var asked = await Api(_ => Status(HttpStatusCode.OK, "{}")).GetPapersAsync("Nobody", "Demo", 4);

        Assert.True(Assert.IsType<Asked<CatalogPapers>.Answered>(asked).Value.IsEmpty);
    }

    /// <summary>On the papers route a 404 is not the server's answer (it never sends one), so it is a
    /// failure. On the artist route it is the answer "no catalogue knows this id".</summary>
    [Fact]
    public async Task A_404_is_nobody_only_where_the_server_says_so()
    {
        var api = Api(_ => Status(HttpStatusCode.NotFound));

        Assert.IsType<Asked<CatalogPapers>.Unanswered>(await api.GetPapersAsync("Talk Talk", null, 0));
        Assert.Null(Assert.IsType<Asked<CatalogArtist?>.Answered>(await api.GetArtistAsync("mb-nobody")).Value);
    }

    /// <summary>A time-out arrives as a cancellation nobody asked for, and is a failure. A cancellation
    /// the caller asked for is thrown.</summary>
    [Fact]
    public async Task A_time_out_is_unanswered_and_a_cancel_is_thrown()
    {
        var timedOut = await Api(_ => throw new TaskCanceledException("timed out"))
            .GetPapersAsync("Talk Talk", null, 0);
        Assert.IsType<Asked<CatalogPapers>.Unanswered>(timedOut);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Api(_ => Status(HttpStatusCode.OK, "{}")).GetPapersAsync("Talk Talk", null, 0, cancelled.Token));
    }

    // ── the service ──

    /// <summary>The API as the service sees it: each answer stated per test.</summary>
    private sealed class Catalogue : IRpmMetadataApi
    {
        public Func<Asked<CatalogPapers>> Papers { get; set; } = () => Asked.Answer(CatalogPapers.Empty);
        public Func<string, Asked<CatalogArtist?>> Artist { get; set; } = _ => Asked.Answer<CatalogArtist?>(null);
        public Func<Asked<IReadOnlyList<CatalogArtist>>> Named { get; set; } =
            () => Asked.Answer<IReadOnlyList<CatalogArtist>>([]);

        /// <summary>Held until released, for the tests about two callers sharing one request.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public int PapersAsked;

        public bool IsAvailable => true;

        public async Task<Asked<CatalogPapers>> GetPapersAsync(
            string artist, string? album, int tracks, CancellationToken ct = default)
        {
            Interlocked.Increment(ref PapersAsked);
            if (Gate is { } gate)
                await gate.Task;
            return Papers();
        }

        public Task<Asked<CatalogArtist?>> GetArtistAsync(string mbid, CancellationToken ct = default)
            => Task.FromResult(Artist(mbid));

        public Task<Asked<IReadOnlyList<CatalogArtist>>> GetArtistsNamedAsync(
            string name, int limit, CancellationToken ct = default)
            => Task.FromResult(Named());

        public Task<Asked<IReadOnlyList<CatalogRelease>>> SearchAsync(string text, string? artist, int limit,
            ReleaseMedia media, string? catalogNumber, string? country, uint? year, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Asked<CatalogReleaseDetail?>> GetReleaseAsync(string releaseId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Asked<CatalogPlays>> GetPlaysAsync(string artist, string album, int tracks, string? releaseMbid,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Asked<CatalogDisc?>> LookupDiscAsync(string discId, string? toc, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Asked<string?>> GetCoverUrlAsync(string releaseId, int maxSize, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<Asked<CatalogSongCover>> GetSongCoverAsync(string artist, string title, int size, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    /// <summary>Wikidata and Wikipedia: every request answered by <paramref name="answer"/>, where null
    /// is a status that was not a success.</summary>
    private static EncyclopediaLookup Wiki(Func<string, string?> answer)
        => new((url, _) => Task.FromResult(answer(url)));

    private static readonly EncyclopediaLookup WikiUnused =
        Wiki(url => throw new InvalidOperationException("Wikipedia was asked: " + url));

    private TrackInfoService Service(Catalogue api, EncyclopediaLookup? wiki = null)
        => new(api, wiki ?? WikiUnused, _dir);

    private static CatalogPapers Record(string? artistId = "mb-tt", string? recordArtistId = "mb-tt") => new(
        Artist: new CatalogArtist("Talk Talk", Id: artistId, WikidataId: "Q1"),
        Album: new CatalogAlbum("Spirit of Eden", Artist: "Talk Talk", ArtistId: recordArtistId));

    private const string Sitelink = """{"entities":{"Q1":{"sitelinks":{"enwiki":{"title":"Talk Talk"}}}}}""";
    private const string Article = """{"query":{"pages":{"1":{"title":"Talk Talk","extract":"An English band."}}}}""";

    [Fact]
    public async Task Papers_that_did_not_arrive_are_kept_nowhere_and_asked_for_again()
    {
        var api = new Catalogue { Papers = () => Asked.Failed<CatalogPapers>("HTTP 503") };
        var service = Service(api);

        Assert.IsType<Asked<TrackDossier>.Unanswered>(await service.GetAsync("Talk Talk", "Spirit of Eden", 6));
        Assert.Null(service.Cached("Talk Talk", "Spirit of Eden"));
        Assert.Equal(0, Written);

        api.Papers = () => Asked.Answer(CatalogPapers.Empty);
        Assert.IsType<Asked<TrackDossier>.Answered>(await service.GetAsync("Talk Talk", "Spirit of Eden", 6));
        Assert.Equal(2, api.PapersAsked);
    }

    /// <summary>"The catalogues know nothing" is an answer, and it is written down.</summary>
    [Fact]
    public async Task Papers_that_say_nothing_are_kept()
    {
        var api = new Catalogue();
        var service = Service(api);

        Assert.True(Assert.IsType<Asked<TrackDossier>.Answered>(await service.GetAsync("Nobody", "Demo", 4)).Value.IsEmpty);
        Assert.Equal(1, Written);

        await service.GetAsync("Nobody", "Demo", 4);
        Assert.Equal(1, api.PapersAsked);
    }

    /// <summary>The record names a different act, and asking about that act fails. Keeping the papers as
    /// they came would match the act on its name alone. The second question exists to replace that
    /// match.</summary>
    [Fact]
    public async Task The_records_own_artist_not_arriving_keeps_nothing()
    {
        var api = new Catalogue
        {
            Papers = () => Asked.Answer(Record(artistId: "mb-wrong", recordArtistId: "mb-tt")),
            Artist = _ => Asked.Failed<CatalogArtist?>("HTTP 503"),
        };
        var service = Service(api);

        Assert.IsType<Asked<TrackDossier>.Unanswered>(await service.GetAsync("Talk Talk", "Spirit of Eden", 6));
        Assert.Null(service.Cached("Talk Talk", "Spirit of Eden"));
        Assert.Equal(0, Written);
    }

    [Fact]
    public async Task Wikipedia_not_answering_keeps_nothing()
    {
        var api = new Catalogue { Papers = () => Asked.Answer(Record()) };
        var service = Service(api, Wiki(url => url.Contains("wikidata") ? Sitelink : null));

        Assert.IsType<Asked<TrackDossier>.Unanswered>(await service.GetAsync("Talk Talk", "Spirit of Eden", 6));
        Assert.Null(service.Cached("Talk Talk", "Spirit of Eden"));
        Assert.Equal(0, Written);
    }

    [Fact]
    public async Task A_whole_answer_is_kept()
    {
        var api = new Catalogue { Papers = () => Asked.Answer(Record()) };
        var service = Service(api, Wiki(url => url.Contains("wikidata") ? Sitelink : Article));

        var dossier = Assert.IsType<Asked<TrackDossier>.Answered>(await service.GetAsync("Talk Talk", "Spirit of Eden", 6)).Value;

        Assert.Equal("An English band.", dossier.ArtistArticle?.Extract);
        Assert.NotNull(service.Cached("Talk Talk", "Spirit of Eden"));
        Assert.Equal(1, Written);
    }

    /// <summary>Two callers share one request, and the one that leaves does not end it for the other. The
    /// cancellation of the first caller must not be passed into the shared request. Otherwise a record
    /// changing under the first caller would answer the second caller with an empty dossier.</summary>
    [Fact]
    public async Task A_caller_that_leaves_does_not_end_the_request_for_the_rest()
    {
        var api = new Catalogue { Gate = new TaskCompletionSource(), Papers = () => Asked.Answer(CatalogPapers.Empty) };
        var service = Service(api);

        using var leaving = new CancellationTokenSource();
        var first = service.GetAsync("Nobody", "Demo", 4, leaving.Token);
        var second = service.GetAsync("Nobody", "Demo", 4);

        leaving.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        api.Gate.SetResult();
        Assert.IsType<Asked<TrackDossier>.Answered>(await second);
        Assert.Equal(1, api.PapersAsked);
        Assert.NotNull(service.Cached("Nobody", "Demo"));
    }

    /// <summary>A request that fails without awaiting anything must not leave itself behind as the
    /// request in flight: every later caller would join the finished failure.</summary>
    [Fact]
    public async Task A_failure_that_finishes_at_once_is_asked_again()
    {
        var api = new Catalogue { Papers = () => Asked.Failed<CatalogPapers>("no api url configured") };
        var service = Service(api);

        await service.GetAsync("Talk Talk", null);
        api.Papers = () => Asked.Answer(CatalogPapers.Empty);

        Assert.IsType<Asked<TrackDossier>.Answered>(await service.GetAsync("Talk Talk", null));
        Assert.Equal(2, api.PapersAsked);
    }

    [Fact]
    public async Task A_shortlist_that_did_not_arrive_is_unanswered_and_not_no_other_artists()
    {
        var api = new Catalogue { Named = () => Asked.Failed<IReadOnlyList<CatalogArtist>>("HTTP 503") };

        Assert.IsType<Asked<IReadOnlyList<ArtistProfile>>.Unanswered>(
            await Service(api).ShortlistAsync("Ground Zero", null));
    }

    /// <summary>With no standing answer for the record, a list of names is not written as its papers.
    /// It would be served as "nothing is known about this record".</summary>
    [Fact]
    public async Task A_shortlist_with_no_standing_answer_is_not_written_as_the_records_papers()
    {
        var api = new Catalogue
        {
            Named = () => Asked.Answer<IReadOnlyList<CatalogArtist>>(
                [new CatalogArtist("Ground Zero", Id: "mb-1"), new CatalogArtist("Ground Zero", Id: "mb-2")]),
        };
        var service = Service(api);

        var found = Assert.IsType<Asked<IReadOnlyList<ArtistProfile>>.Answered>(
            await service.ShortlistAsync("Ground Zero", null)).Value;

        Assert.Equal(2, found.Count);
        Assert.Null(service.Cached("Ground Zero", null));
        Assert.Equal(0, Written);
    }

    [Fact]
    public async Task A_choice_that_did_not_arrive_changes_nothing()
    {
        var api = new Catalogue { Artist = _ => Asked.Failed<CatalogArtist?>("HTTP 503") };
        var service = Service(api);
        await service.GetAsync("Ground Zero", null);

        Assert.IsType<Asked<TrackDossier>.Unanswered>(await service.ChooseArtistAsync("Ground Zero", null, "mb-2"));
        Assert.True(service.Cached("Ground Zero", null)?.IsEmpty);
    }
}
