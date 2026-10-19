using System.Globalization;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RPM.Core.Metadata.Providers;
using RPM.Core.Net;
using RPM.Fingerprint.Catalog;
using RPM.Desktop.Services.Platform;

namespace RPM.Desktop.Services.Catalog;

/// <summary>
/// What the free encyclopedias say about the record now playing. Each answer is fetched once and then
/// remembered.
///
/// <para>Two caches sit in front of the lookup. One is a dictionary in memory. Playing an album calls this
/// once per track, and every track after the first has the same artist and record. The other is a small
/// folder on disk. An artist's biography does not change between launches, and a pane that waits four
/// seconds on a rate-limited service each time the app opens would be a pane people learn to keep
/// closed.</para>
///
/// <para><b>A lookup that FAILED is kept nowhere</b>, not on disk and not in memory. It comes back to the
/// caller as <see cref="Asked{T}.Unanswered"/>, never as an empty dossier. A failure is not "nothing is
/// known". Being offline is a condition of the moment, so a failure must not be shown as "No information
/// was found for this album".</para>
///
/// <para>A lookup that came back EMPTY is written, on a much shorter clock. The two cases are different.
/// One means nobody answered. The other is a real answer: the catalogues have never heard of this record.
/// That is true of every home recording, every bootleg, and every band whose name MusicBrainz spells
/// differently. If these empty answers were not kept, the app would ask the same unknown record the same
/// rate-limited questions on every launch. The short clock also means that an artist added to the
/// catalogues next week is found next week, not next month.</para>
/// </summary>
public sealed partial class TrackInfoService
{
    /// <summary>Long enough that a session's listening costs nothing, short enough that an article
    /// rewritten upstream turns up eventually. The artwork cache uses the same figure.</summary>
    private static readonly TimeSpan DiskTtl = TimeSpan.FromDays(30);

    /// <summary>How long "the catalogues do not know this one" is kept. Days, rather than the month an
    /// article is kept. It is a negative answer, and a record that becomes known should not stay unknown
    /// here for a month.</summary>
    private static readonly TimeSpan BlankTtl = TimeSpan.FromDays(5);

    /// <summary>Wikipedia and Wikidata, and nothing else. See the constructor. Built by Wire, like every
    /// other client that leaves this network. This also fits the catalogue guard: a URL built by hand and
    /// sent through here is refused rather than quietly requested.</summary>
    private static readonly HttpClient Http =
        RPM.Core.Net.Wire.Outside(RPM.Core.Net.Destination.Unaddressed, TimeSpan.FromSeconds(15));

    /// <summary>Wikipedia asks for a real user agent as a courtesy. This is the same string the app uses to
    /// identify itself everywhere else.</summary>
    private const string UserAgent = "rpm/1.0 ( https://rpmrpmrpm.com )";

    private readonly EncyclopediaLookup _lookup;
    private readonly IRpmMetadataApi _api;
    private readonly string _cacheDir;

    /// <summary>Answers only. A failure is never put here: see the class note.</summary>
    private readonly Dictionary<string, TrackDossier> _memory = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Requests in flight, keyed the same way as the caches. When shuffling, the first three
    /// tracks of an album arrive within a second of each other, and they all ask the same
    /// question.</summary>
    private readonly Dictionary<string, Task<Asked<TrackDossier>>> _running = new(StringComparer.OrdinalIgnoreCase);

    public TrackInfoService(IRpmMetadataApi api)
        // The catalogue lookup belongs to the API. Only the API may reach a catalogue, and CatalogueReach
        // refuses the request from here. What remains here reads Wikipedia and Wikidata. These stay on this
        // side, because which article to read depends on the reader's own language.
        : this(api, EncyclopediaLookup.Live(Http, UserAgent), Path.Combine(LocalData.Dir, "trackinfo"))
    {
    }

    /// <summary>For the tests: the encyclopedia and the folder stated rather than built.</summary>
    internal TrackInfoService(IRpmMetadataApi api, EncyclopediaLookup lookup, string cacheDir)
    {
        _api = api;
        _lookup = lookup;
        _cacheDir = cacheDir;
        Directory.CreateDirectory(_cacheDir);
        DropOtherVersions();
    }

    /// <summary>
    /// Empties the folder when its answers were written by an older lookup.
    ///
    /// <para>Changing the key alone would stop the old answers being READ, because the name of every file
    /// is a hash of its key. But nothing would ever delete them, and a folder of files that can never be
    /// opened again is not a cache. A stamp beside them records which lookup wrote them. So each change of
    /// <see cref="CacheVersion"/> clears the previous version exactly once.</para>
    ///
    /// <para>This runs here, before anything can ask a question. If it ran in the background, a sweep
    /// alongside the first lookups would race to delete the answers they are writing.</para>
    /// </summary>
    private void DropOtherVersions()
    {
        try
        {
            var stamp = Path.Combine(_cacheDir, "version.txt");
            if (File.Exists(stamp)
                && string.Equals(File.ReadAllText(stamp).Trim(), CacheVersion, StringComparison.Ordinal))
                return;

            foreach (var stale in Directory.EnumerateFiles(_cacheDir, "*.json"))
            {
                try
                {
                    File.Delete(stale);
                }
                catch
                {
                    // The file is held open by something, or it is already gone. Its key carries the
                    // version, so it is never read either way. This is tidying, not correctness.
                }
            }

            File.WriteAllText(stamp, CacheVersion);
        }
        catch
        {
            // There is no folder, no permission, or the disk refused the write. The cache is an
            // optimisation, so the app opens without one.
        }
    }

    /// <summary>
    /// The dossier for one record. Answers from memory at once where it can. Otherwise it goes to the API,
    /// which takes seconds rather than milliseconds for a record nobody has asked about yet. The catalogue
    /// behind it is rate-limited, and a cold lookup makes several calls. The server caches the answer for
    /// everyone, so the slow lookup happens once, not once per listener.
    /// </summary>
    /// <param name="album">Null or blank for a loose track. The artist is then looked up alone.</param>
    /// <param name="tracks">How many songs the record has, where the tags say so. It decides nothing here,
    /// but the lookup uses it to tell an album from a single with the same name. It is deliberately NOT
    /// part of the key: it describes the record, which the key already names.</param>
    /// <param name="ct">Stops THIS caller waiting, and nothing else. The request is shared by everyone
    /// asking the same question, and it runs to the end, so the answer is kept for the next caller.</param>
    /// <returns>The dossier. This is <see cref="TrackDossier.Empty"/> when the catalogues know nothing
    /// about the record. It is <see cref="Asked{T}.Unanswered"/> when the API or Wikipedia did not answer.
    /// A caller must not show the second as the first.</returns>
    public async Task<Asked<TrackDossier>> GetAsync(
        string? artist, string? album, int tracks = 0, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return Asked.Answer(TrackDossier.Empty);

        var key = CacheKey(artist, album);

        Task<Asked<TrackDossier>>? fetch;
        lock (_memory)
        {
            if (_memory.TryGetValue(key, out var hit))
                return Asked.Answer(hit);

            _running.TryGetValue(key, out fetch);
        }

        if (fetch is null)
        {
            if (ReadDisk(key) is { } fromDisk)
            {
                lock (_memory) _memory[key] = fromDisk;
                return Asked.Answer(fromDisk);
            }

            fetch = Share(key, () => FetchAsync(key, artist, album, tracks));
        }

        return await fetch.WaitAsync(ct);
    }

    /// <summary>
    /// The request in flight for <paramref name="key"/>: the one already running, or a new one.
    ///
    /// <para>The entry is added before the request starts, and removed before its answer is handed over.
    /// Both steps are under the lock. A request that finishes without awaiting anything, such as one with
    /// no API configured, fails at once. If it could remove its entry before the entry was added, every
    /// later caller would join a finished failure for the rest of the run.</para>
    /// </summary>
    private Task<Asked<TrackDossier>> Share(string key, Func<Task<Asked<TrackDossier>>> start)
    {
        TaskCompletionSource<Asked<TrackDossier>> slot;
        lock (_memory)
        {
            if (_running.TryGetValue(key, out var running))
                return running;

            slot = new TaskCompletionSource<Asked<TrackDossier>>(TaskCreationOptions.RunContinuationsAsynchronously);
            _running[key] = slot.Task;
        }

        _ = Run();
        return slot.Task;

        async Task Run()
        {
            Asked<TrackDossier> answer;
            try
            {
                answer = await start();
            }
            catch (Exception ex)
            {
                answer = Asked.Failed<TrackDossier>($"{ex.GetType().Name}: {ex.Message}");
            }

            lock (_memory)
                _running.Remove(key);

            slot.SetResult(answer);
        }
    }

    /// <summary>The dossier if it is already known, without asking anybody. The pane paints this on the
    /// frame the track changes, so a record played twice does not flash through its empty state
    /// first.</summary>
    public TrackDossier? Cached(string? artist, string? album)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return null;

        var key = CacheKey(artist, album);
        lock (_memory)
        {
            if (_memory.TryGetValue(key, out var hit))
                return hit;
        }

        var fromDisk = ReadDisk(key);
        if (fromDisk is not null)
            lock (_memory) _memory[key] = fromDisk;
        return fromDisk;
    }

    /// <summary>
    /// Everyone the catalogues have under this card's name, for a reader who says it is the wrong act.
    ///
    /// <para>This is asked for here, not carried on every dossier, because most cards do not need it. An
    /// artist identified through the record the user owns is not guessed at, so a name search would spend a
    /// rate-limited request on a question nobody asked. The list is asked for once and then kept. It is
    /// written into the standing answer under its own key, so a reader who opens the list, closes it, and
    /// opens it again does not wait twice.</para>
    ///
    /// <para>It is written only into a standing ANSWER. With no answer in hand, a dossier holding only this
    /// list would be kept under the record's key and served as the record's papers. That would say "nothing
    /// is known about this record", built from a list of names.</para>
    /// </summary>
    /// <param name="artist">The name to ask about, and the album it was asked with. Together they form the
    /// key the answer is kept under, so they must be the pair the card's own dossier came from.</param>
    /// <returns>The list, empty when nobody else has the name. It is <see cref="Asked{T}.Unanswered"/> when
    /// the API did not answer. The card must not report that as "no other artists".</returns>
    public async Task<Asked<IReadOnlyList<ArtistProfile>>> ShortlistAsync(
        string artist, string? album, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return Asked.Answer<IReadOnlyList<ArtistProfile>>([]);

        var standing = Cached(artist, album);
        if (standing is { Shortlist.Count: > 0 })
            return Asked.Answer(standing.Shortlist);

        if (!(await _api.GetArtistsNamedAsync(artist, EncyclopediaLookup.MaxArtistsOffered, ct))
                .Answers(out var named, out var why))
            return Asked.Failed<IReadOnlyList<ArtistProfile>>(why);

        IReadOnlyList<ArtistProfile> found = named.Select(EncyclopediaLookup.Profile)
                                                  .Where(p => p is { Mbid.Length: > 0 })
                                                  .Select(p => p!)
                                                  .ToList();

        if (found.Count > 0 && standing is not null)
        {
            var key = CacheKey(artist, album);
            var settled = standing with { ArtistShortlist = found };
            lock (_memory) _memory[key] = settled;
            WriteDisk(key, settled);
        }

        return Asked.Answer(found);
    }

    /// <summary>
    /// What Wikipedia says about one of the app's genres: the text of the genre's page.
    ///
    /// <para><b>It lives here rather than on the page, because this is where the lookup lives.</b> The
    /// encyclopedia reader is built in this class's constructor, with this class's HttpClient and user
    /// agent. Wikipedia asks for a real user agent. The reader is not in the container. A caller that tried
    /// to resolve it got nothing. The card then never appeared, and the failure looked exactly like "the
    /// encyclopedia has nothing on this genre".</para>
    ///
    /// <para><b>Kept for the session, in memory only.</b> A genre's article is a page that a reader opens,
    /// closes and opens again. Asking twice in one evening is a request nobody needed. It is not written to
    /// the disk cache beside the dossiers. That cache is keyed by artist and album and holds a
    /// <see cref="TrackDossier"/>. An entry of a different shape would be a second thing every reader of
    /// that folder has to handle. The cost is one request per genre per launch.</para>
    ///
    /// <para>Null when there is no article, and null when Wikipedia did not answer. The page draws no card
    /// for either, and it does not say why. Only the "no article" result is kept. A failed request is not
    /// kept, or a genre's column would stay empty until the app restarted.</para>
    /// </summary>
    public async Task<EncyclopediaArticle?> GenreArticleAsync(string? genre, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(genre))
            return null;

        lock (_genreArticles)
            if (_genreArticles.TryGetValue(genre, out var kept))
                return kept;

        if (!(await _lookup.GenreArticleAsync(genre, ct)).Answers(out var article, out _))
            return null;

        lock (_genreArticles)
            _genreArticles[genre] = article;

        return article;
    }

    /// <summary>The genre articles answered so far, null included. "The encyclopedia has nothing on this"
    /// is an answer, and it is worth not asking twice. A request that did not come back is not an answer,
    /// so it never reaches here.</summary>
    private readonly Dictionary<string, EncyclopediaArticle?> _genreArticles =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The reader's own answer to which artist was meant. It replaces the one the search picked.
    ///
    /// <para>The catalogue cannot settle a name that several acts share. See
    /// <see cref="TrackDossier.Shortlist"/>. Only the person looking at the screen can settle it, so this
    /// method takes that choice. It takes one row off the shortlist and asks the catalogues about that
    /// artist properly. Shortlist rows are search hits, and a search hit has no genres, no outside pages
    /// and no article. The result is written back under the SAME key as the answer it replaces.</para>
    ///
    /// <para>The same key is used on purpose, and the answer is written to disk as well as memory. A
    /// correction that lived only for the session would be made again on every launch, for as long as the
    /// wrong answer had left to run. It would also be made again for every track of the record. Once
    /// settled, the choice holds for the record, for the album it is on, and until the entry expires like
    /// any other.</para>
    ///
    /// <para>The album is not changed, because the record is the same whoever chose the artist. The
    /// correction is written only over a standing ANSWER, for the reason given at
    /// <see cref="ShortlistAsync"/>.</para>
    /// </summary>
    /// <param name="artist">The artist as the tags spell it, and the album with it. Together they are the
    /// key of the answer being corrected. They must be the pair that asked the original question, not
    /// something derived from the result.</param>
    /// <param name="mbid">The chosen artist's MusicBrainz id, from a <see cref="TrackDossier.Shortlist"/>
    /// row.</param>
    /// <returns>The dossier as it now stands. It is unchanged when the id is nobody. It is
    /// <see cref="Asked{T}.Unanswered"/> when the API or Wikipedia did not answer. In either case, a
    /// correction that could not be made leaves the card as it was.</returns>
    public async Task<Asked<TrackDossier>> ChooseArtistAsync(
        string artist, string? album, string mbid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(mbid))
            return Asked.Answer(TrackDossier.Empty);

        var key = CacheKey(artist, album);
        var standing = Cached(artist, album);

        if (!(await _api.GetArtistAsync(mbid, ct)).Answers(out var picked, out var why))
            return Asked.Failed<TrackDossier>(why);

        if (EncyclopediaLookup.Profile(picked) is not { } chosen)
            return Asked.Answer(standing ?? TrackDossier.Empty);

        if (!(await _lookup.ArticlesForAsync(new CatalogPapers(Artist: picked), Languages(), ct))
                .Answers(out var aboutThem, out why))
            return Asked.Failed<TrackDossier>(why);

        // The shortlist is kept after the choice. A reader who picks the wrong one of five must be able to
        // pick again. A corrected card that has lost its list has no button left, and no way back.
        var settled = (standing ?? TrackDossier.Empty) with
        {
            Artist = chosen,
            ArtistArticle = aboutThem.ArtistArticle,
        };

        if (standing is not null)
        {
            lock (_memory) _memory[key] = settled;
            WriteDisk(key, settled);
        }

        return Asked.Answer(settled);
    }

    /// <summary>
    /// Asks the API, then Wikipedia, about one record, and writes the answer down.
    ///
    /// <para><b>Every step either answers or ends the fetch as a failure.</b> A step that did not answer is
    /// never read as one that found nothing. The papers not arriving is not "the catalogues know nothing".
    /// The record's own artist not arriving is not "the record names nobody else". An article not arriving
    /// is not "there is no article". Each of these failures stays a failure, and none of them is written to
    /// disk as an answer.</para>
    ///
    /// <para>The fetch has no cancellation of its own. The request is shared by every caller asking the
    /// same question (see <see cref="Share"/>), and one caller leaving must not end it for the others. The
    /// clients' own time-outs bound it.</para>
    /// </summary>
    private async Task<Asked<TrackDossier>> FetchAsync(string key, string artist, string? album, int tracks)
    {
        // This is one request to the API. The API makes the catalogue calls itself and caches the answer
        // for ninety days, for everyone. So the cost falls on the first listener to play the record, and on
        // nobody after them.
        if (!(await _api.GetPapersAsync(artist, album, tracks, CancellationToken.None))
                .Answers(out var papers, out var why))
            return Unanswered(artist, album, "the papers", why);

        // The two halves of one answer can name two different people. The RECORD is the half worth
        // believing. It was matched on the act's name, a title and a track count. The artist was matched on
        // a name alone. This check happens before the articles are fetched, so the encyclopedia is asked
        // about the act the record names. It costs one extra request, and only when the two halves
        // disagree. See PapersAgree, which has the case that shows why.
        if (PapersAgree.ArtistTheRecordNames(papers) is { Length: > 0 } truer)
        {
            if (!(await _api.GetArtistAsync(truer, CancellationToken.None)).Answers(out var settled, out why))
                return Unanswered(artist, album, "the record's own artist", why);

            Said(artist, album, tracks, papers, truer, settled);
            papers = PapersAgree.WithArtist(papers, settled);
        }
        else
        {
            Said(artist, album, tracks, papers, null, null);
        }

        if (!(await _lookup.ArticlesForAsync(papers, Languages(), CancellationToken.None))
                .Answers(out var dossier, out why))
            return Unanswered(artist, album, "Wikipedia", why);

        // Part of an answer is shown but never kept. The API may say that a source is still answering, or
        // cannot be asked. The record is then asked again the next time it is opened, and that request
        // fills in the rest. If the partial answer were kept, it would stand for thirty days in place of
        // the whole answer.
        if (papers.Partial)
            return Asked.Answer(dossier);

        lock (_memory) _memory[key] = dossier;

        // Written whether or not anything was found. An empty answer is still an answer. The artist may
        // really not be in MusicBrainz. It is also the answer that costs the most to ask again, since an
        // answer that finds nothing has tried every request there is. It keeps its own short clock. See
        // BlankTtl and ReadDisk.
        WriteDisk(key, dossier);

        return Asked.Answer(dossier);
    }

    /// <summary>A fetch that did not get its answer. It is written to the log and returned as a failure.
    /// Read it with <c>rpmscope log --channel papers</c>.</summary>
    private static Asked<TrackDossier> Unanswered(string artist, string? album, string step, string why)
    {
        if (RPM.Core.Diagnostics.Scope.On)
            RPM.Core.Diagnostics.Scope.Said(
                "papers", $"asked=\"{artist}\"/\"{album}\" unanswered at {step}: {why}");

        return Asked.Failed<TrackDossier>($"{step}: {why}");
    }

    /// <summary>Which Wikipedia to read, best first: the user's own language, then English. English is
    /// where the article usually is. It is a fallback, not a preference.</summary>
    private static IReadOnlyList<string> Languages()
    {
        var own = WikipediaJson.BaseLanguage(CultureInfo.CurrentUICulture.Name);
        return own is "" or "en" ? ["en"] : [own, "en"];
    }

    /// <summary>
    /// What the papers said about WHO, written to the log.
    /// </summary>
    /// <remarks>
    /// A card about the wrong act looks exactly like a card about the right one. It has a name, a country
    /// and an article, all internally consistent, and all about somebody else. The only way to see the
    /// problem from outside is to know which of the papers' two halves the act came from, and whether the
    /// halves agreed. This line says so.
    ///
    /// <para>Read it with <c>rpmscope log --channel papers</c>. A line whose <c>record=</c> differs from its
    /// <c>picked=</c> is the case <see cref="PapersAgree"/> handles. A line with an album but an EMPTY
    /// <c>record=</c> means the catalogue did not send the release group's own artist. Then nothing on this
    /// side can tell the right act from the wrong one.</para>
    /// </remarks>
    private static void Said(
        string artist, string? album, int tracks,
        CatalogPapers papers, string? truer, CatalogArtist? settled)
    {
        if (!RPM.Core.Diagnostics.Scope.On)
            return;

        // The line logs the question as well as the answer. Logging only the answer says that a record was
        // not matched, but not why. The title from the tags and the title the catalogue holds are the whole
        // question, and both must be on the line. In one measurement of ten records, eight came back with
        // no album at all.
        RPM.Core.Diagnostics.Scope.Said(
            "papers",
            $"asked=\"{artist}\"/\"{album}\"/{tracks} "
            + $"album=\"{papers.Album?.Title}\" picked={papers.Artist?.Name}/{papers.Artist?.Id} "
            + $"record={papers.Album?.Artist}/{papers.Album?.ArtistId} "
            + $"took={(truer is null ? "the one picked" : settled?.Name + "/" + truer)}");
    }

    // ── the disk cache ──

    /// <summary>
    /// Which version of the lookup wrote an answer down. It is part of every key. Changing how a record is
    /// found throws away everything that was found the old way.
    ///
    /// <para>A cache is a promise that the same question has the same answer. A route that returns
    /// the wrong artist breaks that promise. Those entries are not merely stale. They are WRONG, and they
    /// would stay wrong for the whole of their life, however much the lookup improved. Raise this whenever
    /// the lookup changes in a way that could give a record a better answer than the one already on
    /// disk.</para>
    ///
    /// <para>Each entry must hold every field the current code reads. An artist found by name carries the
    /// shortlist of other matches. An artist carries every Discogs id the catalogue holds for them. An
    /// entry without these fields would be read as having none, so the card would show a wrong result or no
    /// choice at all.</para>
    /// </summary>
#if DEBUG
    /// <summary>
    /// Puts an answer into this session's memory without asking anybody. This is for the design harness
    /// only. See
    /// <see cref="Presentation.Gallery.GalleryShelfLibrary"/>. Its shelf is a catalogue of invented
    /// records, and its artists are ones no encyclopedia will have heard of.
    ///
    /// <para>The answer is kept in session memory, not in the disk cache, on purpose. What the harness
    /// writes is not an answer about the world. It must not outlive the run that wrote it, or be served in
    /// a later run about a real artist who happens to share the name.</para>
    /// </summary>
    internal void Remember(string? artist, string? album, TrackDossier dossier)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return;

        lock (_memory)
        {
            _memory[CacheKey(artist, album)] = dossier;
        }
    }
#endif

    /// <summary>Answers written before the API sent the record's own artist can name the wrong act. For a
    /// record whose two halves named different people, the act that a name search found is stored, with its
    /// country, its links and an article about the wrong person. Changing this version drops those
    /// answers.</summary>
    private const string CacheVersion = "v9";

    private static string CacheKey(string artist, string? album) =>
        $"{CacheVersion} {artist.Trim()} {album?.Trim() ?? ""}".ToLowerInvariant();

    /// <summary>A file name a file system will take, from a key that can hold anything a tag can.</summary>
    private string PathFor(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant();
        return Path.Combine(_cacheDir, hash + ".json");
    }

    private TrackDossier? ReadDisk(string key)
    {
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path))
                return null;

            var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age > DiskTtl)
                return null;

            var dossier = JsonSerializer.Deserialize(File.ReadAllText(path), TrackInfoJson.Default.TrackDossier);

            // There are two clocks. A dossier with content is kept for the month. One that found nothing is
            // kept for a few days, after which the record is asked about again.
            return dossier is { IsEmpty: true } && age > BlankTtl ? null : dossier;
        }
        catch
        {
            return null;   // A row in an older shape, or a half-written file. Ask again.
        }
    }

    private void WriteDisk(string key, TrackDossier dossier)
    {
        try
        {
            File.WriteAllText(PathFor(key), JsonSerializer.Serialize(dossier, TrackInfoJson.Default.TrackDossier));
        }
        catch { /* the cache is an optimisation; failing to write one is not a failure */ }
    }
    /// <summary>The cached dossier's shape, as the source generator describes it. A published build is
    /// Native AOT, which serializes from this and never by reflection.</summary>
    [JsonSerializable(typeof(TrackDossier))]
    private sealed partial class TrackInfoJson : JsonSerializerContext;

}
