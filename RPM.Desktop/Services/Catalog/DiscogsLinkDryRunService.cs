using System.Text;
using RPM.Core.Library;
using RPM.Core.Metadata.Providers;
using RPM.Core.Threading;
using RPM.Fingerprint.Catalog;

namespace RPM.Desktop.Services.Catalog;

/// <summary>
/// A dry run of linking every album to its Discogs release: what would be linked, and how sure it is, written to a
/// report and nowhere else.
///
/// <para><b>Why a dry run first.</b> The style sweep (<see cref="DiscogsStyleSweepService"/>) reads styles only for
/// albums already linked, because a search that picks the wrong record writes the wrong styles. Linking the rest
/// means searching, and a wrong link also shows the wrong catalogue number and barcode on the album page. So the
/// matching (<see cref="ReleaseLinker"/>) runs over the whole library first, and the listener reads what it would
/// do before anything is written.</para>
///
/// <para><b>Off unless asked.</b> It runs only when <c>RPM_DISCOGS_LINK_DRYRUN</c> names a folder for the report,
/// a switch a Release build does not read. It never writes to the library.</para>
///
/// <para><b>Per album:</b> Discogs is searched for digital releases of the artist's album first, since the files
/// most likely came from one, and the closest few listings are fetched and measured against the files' track
/// lengths, and an album that is part of a release (one side of a single, an album split over folders) is matched
/// track by track (<see cref="ReleaseLinker.PairEvery"/>). Only when no digital release proves the album is every
/// format searched, and when what came back has another number of tracks, a wider search looks for its other
/// editions. Each request waits <see cref="Breath"/>, Discogs' whole allowance. The report is appended one album at
/// a time, and a run started again skips the albums it already holds.</para>
/// </summary>
public sealed partial class DiscogsLinkDryRunService : IDisposable
{
    public const string Switch = "RPM_DISCOGS_LINK_DRYRUN";

    /// <summary>How long to wait after each request: one second, Discogs' whole sixty a minute for the account. The
    /// app's own lookups share that allowance, so one of them may be refused now and then; see
    /// <see cref="AfterARateLimit"/>.</summary>
    private static readonly TimeSpan Breath = TimeSpan.FromSeconds(1);

    /// <summary>How long to wait after Discogs refuses for the rate: its window is a minute.</summary>
    private static readonly TimeSpan AfterARateLimit = TimeSpan.FromMinutes(1);

    /// <summary>How long to wait when Discogs could not be reached otherwise: no account, or a fault.</summary>
    private static readonly TimeSpan AfterASilence = TimeSpan.FromMinutes(2);

    /// <summary>How many times one album is asked again after a refusal that is not the rate, before its line says
    /// so and the run moves on. A fault that follows one album should not hold up the rest.</summary>
    private const int Tries = 3;

    /// <summary>How many search results are asked for, and how many of those with the album's names have their
    /// listing fetched. Past the first few, the results are other records.</summary>
    private const int Results = 8;
    private const int Listings = 3;

    /// <summary>The wider search for an album whose track count no listing so far shares: more results, and a few
    /// more listings, to find its other editions.</summary>
    private const int EditionResults = 25;
    private const int EditionListings = 4;

    private readonly DiscogsService _discogs;
    private readonly Services.Library.LibraryService _library;
    private readonly Shell.Sweep _sweep;
    private readonly Shell.PassLife _life = new();
    private readonly object _starting = new();
    private CancellationTokenSource? _stop;
    private Task? _worker;

    public DiscogsLinkDryRunService(
        DiscogsService discogs, Services.Library.LibraryService library, Shell.Governor governor)
    {
        _discogs = discogs;
        _library = library;
        _sweep = governor.Pass("discogs link dry run", Shell.Lane.Network, Shell.Wave.Last, notBefore: Shell.Phase.Ready);

        if (RPM.Core.Diagnostics.DevSwitch.Read(Switch) is not { Length: > 0 } folder)
            return;

        _library.Opened += _ => Start(folder);
        _library.StopBeforeClose(_life.CloseAsync);
    }

    private void Start(string folder)
    {
        lock (_starting)
        {
            if (_worker is { IsCompleted: false } || !_library.IsOpen)
                return;

            _stop?.Dispose();
            _stop = _life.Linked();
            var ct = _stop.Token;
            var db = _library.Db;
            _worker = _life.Run(() => RunAsync(db, folder, ct));
        }
    }

    private async Task RunAsync(LibraryDatabase db, string folder, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "discogs-links.tsv");
            var done = ReadDone(path);

            IReadOnlyList<LibraryDatabase.AlbumToLink> albums;
            using (var page = await _sweep.Next(ct))
            {
                if (!page.Go)
                    return;
                albums = db.AlbumsNotLinkedTo(DiscogsJson.ProviderId);
            }

            var left = albums.Where(a => !done.Contains(a.Folder)).ToList();
            Console.WriteLine($"[discogs-link] dry run: {albums.Count} albums not linked, {left.Count} left to ask, report at {path}");

            if (!File.Exists(path))
                await File.WriteAllTextAsync(path, Header + "\n", Encoding.UTF8, ct);

            var tally = new Dictionary<LinkVerdict, int>();
            foreach (var album in left)
            {
                using var slot = await _sweep.Next(ct);
                if (!slot.Go)
                    return;

                LinkDecision? decision = null;
                var requests = 0;
                IReadOnlyList<string> styles = [];
                var refused = 0;
                while (decision is null)
                {
                    try
                    {
                        (decision, requests, styles) = await AskAsync(album, ct);
                    }
                    catch (DiscogsUnavailableException ex) when (ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!await Cancellable.DelayAsync(AfterARateLimit, ct))
                            return;
                    }
                    catch (DiscogsUnavailableException ex)
                    {
                        // Signed out, or no account connected: the listener can fix that while the app runs, so it
                        // is waited out and not held against the album.
                        if (_discogs.MayAnswer && ++refused >= Tries)
                        {
                            decision = new LinkDecision(LinkVerdict.NoMatch, null, null, $"error: {ex.Message}");
                            break;
                        }
                        Console.WriteLine($"[discogs-link] Discogs is not answering ({ex.Message}); trying again in two minutes");
                        if (!await Cancellable.DelayAsync(AfterASilence, ct))
                            return;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // One album whose answer cannot be read does not stop the run; its line says why.
                        decision = new LinkDecision(LinkVerdict.NoMatch, null, null, $"error: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                tally[decision.Verdict] = tally.GetValueOrDefault(decision.Verdict) + 1;
                await File.AppendAllTextAsync(path, Line(album, decision, requests, styles) + "\n", Encoding.UTF8, ct);
            }

            Console.WriteLine("[discogs-link] dry run done: "
                              + string.Join(", ", tally.Select(p => $"{p.Value} {p.Key}")));
        }
        catch (OperationCanceledException)
        {
            // The library closed or the app is shutting down. The report holds every album finished so far.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[discogs-link] dry run stopped: {ex}");
        }
    }

    /// <summary>Searches, fetches the closest listings and decides for one album. Digital releases are asked first;
    /// every format only when no digital release proves the album.</summary>
    private async Task<(LinkDecision Decision, int Requests, IReadOnlyList<string> Styles)> AskAsync(
        LibraryDatabase.AlbumToLink album, CancellationToken ct)
    {
        var requests = 0;
        var fetched = new Dictionary<string, LinkCandidate>(StringComparer.Ordinal);
        var styles = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var title = StripNotes(album.Album);

        // Digital first, then every format. Then, when what came back has the album's names but another number of
        // tracks, a wider search for its other editions: the deluxe or the expanded one is often further down.
        // A digital release this album is only part of does not end the search: a whole release in another format
        // is the closer edition, and the decision prefers it.
        var decision = await RoundAsync(ReleaseMedia.Digital, Results, Listings);
        if (decision.Verdict != LinkVerdict.Link || decision.Partial)
            decision = await RoundAsync(ReleaseMedia.Any, Results, Listings);
        if (decision.Verdict == LinkVerdict.Unsure && decision.Match?.Basis == RPM.Core.Splitting.MatchBasis.TotalOnly)
            decision = await RoundAsync(ReleaseMedia.Any, EditionResults, EditionListings);

        var bestStyles = decision.Best is { } best && styles.TryGetValue(best.Release.Ref.Id, out var s) ? s : [];
        return (decision, requests, bestStyles);

        async Task<LinkDecision> RoundAsync(ReleaseMedia media, int results, int listings)
        {
            var json = await _discogs.SearchAsync(title, album.Artist, media, results, ct: ct)
                       ?? throw new DiscogsUnavailableException("not signed in, or no account connected");
            requests++;
            await Cancellable.DelayAsync(Breath, ct);

            var named = DiscogsJson.ParseSearch(json, results)
                .Where(r => !fetched.ContainsKey(r.Ref.Id)
                            && ReleaseLinker.NamesAgree(album.Artist, album.Album, r.Artist, r.Title))
                .Take(listings)
                .ToList();

            foreach (var release in named)
            {
                var listing = await _discogs.ReleaseAsync(release.Ref.Id, ct)
                              ?? throw new DiscogsUnavailableException("not signed in, or no account connected");
                requests++;
                await Cancellable.DelayAsync(Breath, ct);

                fetched[release.Ref.Id] = new LinkCandidate(release, DiscogsJson.ParseRelease(listing, release.Ref.Id));
                styles[release.Ref.Id] = DiscogsJson.ParseReleaseFacts(listing).Styles;
            }

            return ReleaseLinker.Decide(album.Artist, album.Album, album.Lengths, [.. fetched.Values], album.Titles);
        }
    }

    /// <summary>The album's name without its edition notes, for the search: Discogs files "Read My Lips" and not
    /// "Read My Lips (Deluxe Version)".</summary>
    private static string StripNotes(string album)
    {
        var plain = System.Text.RegularExpressions.Regex.Replace(album, @"[\(\[][^\)\]]*[\)\]]", " ");
        plain = System.Text.RegularExpressions.Regex.Replace(plain, @"\s+[-–—]\s+(?:single|ep)\s*$", "",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        plain = string.Join(' ', plain.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return plain.Length > 0 ? plain : album;
    }

    private const string Header =
        "verdict\tfolder\tartist\talbum\ttracks\thas_style\trelease_id\trelease_artist\trelease_title\tyear\tformat\t"
        + "score\tbasis\tmean_error_s\tdrift\tlisted_tracks\tpartial\tstyles\treason\trequests\turl";

    private static string Line(
        LibraryDatabase.AlbumToLink album, LinkDecision d, int requests, IReadOnlyList<string> styles)
    {
        var r = d.Best?.Release;
        var m = d.Match;
        string[] cells =
        [
            d.Verdict.ToString(), album.Folder, album.Artist, album.Album, album.Lengths.Count.ToString(),
            album.HasStyle ? "yes" : "no",
            r?.Ref.Id ?? "", r?.Artist ?? "", r?.Title ?? "", r is { Year: > 0 } ? r.Year.ToString() : "", r?.Format ?? "",
            m is null ? "" : m.Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            m?.Basis.ToString() ?? "",
            m is null || double.IsNaN(m.MeanErrorSeconds) ? "" : m.MeanErrorSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
            m is null ? "" : m.DriftFactor.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
            m?.ExpectedCount.ToString() ?? "",
            d.Partial ? "yes" : "",
            string.Join("; ", styles), d.Reason, requests.ToString(),
            r is null ? "" : $"https://www.discogs.com/release/{r.Ref.Id}",
        ];
        return string.Join('\t', cells.Select(c => c.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ')));
    }

    private static HashSet<string> ReadDone(string path)
    {
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return done;
        foreach (var line in File.ReadLines(path).Skip(1))
            if (line.Split('\t') is { Length: > 1 } cells)
                done.Add(cells[1]);
        return done;
    }

    public void Dispose()
    {
        _stop?.Cancel();
        _stop?.Dispose();
        _stop = null;
    }
}
