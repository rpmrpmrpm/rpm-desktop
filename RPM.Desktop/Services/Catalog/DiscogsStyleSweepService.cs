using RPM.Core.Library;
using RPM.Core.Metadata.Providers;
using RPM.Core.Threading;

namespace RPM.Desktop.Services.Catalog;

/// <summary>
/// Reads the styles of every album that is already linked to a Discogs release, one album at a time.
///
/// <para><b>What a style is for.</b> The app's genre list has one broad word per shelf, on purpose (see
/// <c>Genres</c>). There are about thirty genres, not four hundred. <c>MoodFlavour</c> is the one place
/// that needs the finer detail. It decides which records inside a shelf a list is named after. It has been
/// matching words against whatever a tagger typed. Discogs separates a release's genres from its styles,
/// and styles come from a controlled list: "Deep House" is always spelled that way.</para>
///
/// <para><b>Why a pass and not a lookup.</b> The album page already stores the styles of any record whose
/// page is opened (see <c>AlbumDetailView.LoadReleaseFactsAsync</c>). So this pass only covers records
/// nobody has opened. It is not urgent. Nothing on screen waits for it, and every request uses the user's
/// OWN Discogs account.</para>
///
/// <para><b>Only records that are already linked.</b> A linked album is one request, by id, and the result
/// is exact. An album with no link would need a search per record, and a search that picks the wrong
/// pressing writes the wrong styles onto a record. Widening this to a search is a later decision, to be
/// made once the linked albums are done and their value is clear.</para>
///
/// <para><b>Nothing is written when nobody was reached.</b> A Discogs that is not connected, or is
/// refusing, leaves the album untouched, so the next run asks again. Writing "no styles" for a record that
/// was never checked is the one mistake here that nothing would correct. An empty result is saved only
/// after Discogs has answered (see <c>SetAlbumStyles</c>).</para>
/// </summary>
public sealed partial class DiscogsStyleSweepService : IDisposable
{
    private readonly DiscogsService _discogs;
    private readonly Services.Library.LibraryService _library;

    private CancellationTokenSource? _stop;
    private Task? _worker;

    public DiscogsStyleSweepService(
        DiscogsService discogs,
        Services.Library.LibraryService library,
        Shell.Governor governor)
    {
        _discogs = discogs;
        _library = library;
        _sweep = governor.Pass(
            "discogs styles", Shell.Lane.Network, Shell.Wave.Last, notBefore: Shell.Phase.Ready);
        _library.Opened += _ => Start();

        // The sweep holds the library between its requests. It is stopped and waited for before the library
        // is closed (see Shell.PassLife).
        _library.StopBeforeClose(_life.CloseAsync);
    }

    /// <summary>What this pass asks before each unit of work. See Governor. Nothing in the background
    /// starts by itself.</summary>
    private readonly Shell.Sweep _sweep;


    /// <summary>
    /// How long to wait between albums.
    /// </summary>
    /// <remarks>Two seconds. Discogs allows sixty requests a minute to an authenticated caller. This pass
    /// makes one request per album, so using half that rate leaves room for the other requests the app
    /// makes to Discogs while a person is using it. The album page they are looking at comes
    /// first.</remarks>
    private static readonly TimeSpan Breath = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait after an album that could not be reached. Two minutes. The cause is not
    /// this record: either no account is connected, or the service is refusing. Asking again at once only
    /// earns a longer refusal.</summary>
    private static readonly TimeSpan AfterASilence = TimeSpan.FromMinutes(2);

    /// <summary>How many albums are read at a time. This is a page, not the whole list, because the shelf
    /// can change during a pass that runs for a while.</summary>
    private const int Page = 40;

    /// <summary>Starts the pass, or leaves the one already running alone. Calling it again does nothing,
    /// because the library raises <c>Opened</c> for every open. A second worker would double the request
    /// rate.</summary>
    public void Start()
    {
        // Under a lock, so two Opened events cannot both find no worker and start two.
        lock (_starting)
        {
            if (_worker is { IsCompleted: false })
                return;

            _stop?.Dispose();
            _stop = _life.Linked();
            var ct = _stop.Token;

            if (!_library.IsOpen)
                return;

            // Refused once the library is about to close; the token is cancelled by that close as well.
            var db = _library.Db;
            _worker = _life.Run(() => RunAsync(db, ct));
        }
    }

    private readonly object _starting = new();

    /// <summary>The pass's worker, stopped and waited for before the library is closed.</summary>
    private readonly Shell.PassLife _life = new();

    private async Task RunAsync(LibraryDatabase db, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // There is no signed-in user, or no connected Discogs account. Then there is no question to
                // ask and no page of albums worth reading. The pass waits rather than stopping, because the
                // user can fix both while the app runs. The check is made here, at the top, so a signed-out
                // session costs one sleeping task, not a refusal for every album every two minutes.
                if (!_discogs.MayAnswer)
                {
                    if (!await Cancellable.DelayAsync(AfterASilence, ct))
                        return;
                    continue;
                }

                // The page query is a unit of work and asks the governor first, like any other (see
                // LoudnessService), so it does not run while startup is still loading.
                IReadOnlyList<LibraryDatabase.ReleaseToAsk> albums;
                using (var page = await _sweep.Next(ct))
                {
                    if (!page.Go)
                        return;

                    try
                    {
                        albums = db.NextReleasesToAsk(DiscogsJson.ProviderId, Page);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                    {
                        return;   // The library closed during the pass. This is not an error.
                    }
                }

                // Every linked album has been checked. Nothing checks them again. A release's styles are a
                // fact about a pressing, not a figure that changes. The album page rewrites them anyway
                // when it reads that release.
                if (albums.Count == 0)
                    return;

                foreach (var album in albums)
                {
                    if (ct.IsCancellationRequested)
                        return;

                    using var slot = await _sweep.Next(ct);
                    if (!slot.Go)
                        return;

                    var reached = await AskAsync(db, album, ct);

                    if (!await Cancellable.DelayAsync(reached ? Breath : AfterASilence, ct))
                        return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The library closed, or the app is shutting down. Nothing is left half-written, because one
            // album is one write.
        }
    }

    /// <summary>Asks about one album and writes what came back.</summary>
    /// <returns>Whether Discogs was reached. This decides how long to wait before the next album. It is not
    /// the same as whether the release named any styles.</returns>
    private async Task<bool> AskAsync(LibraryDatabase db, LibraryDatabase.ReleaseToAsk album, CancellationToken ct)
    {
        try
        {
            // Null means Discogs declined to be asked. That is not an answer about this record. Leaving it
            // untouched lets the pass come back to it.
            if (await _discogs.ReleaseAsync(album.ReleaseId, ct) is not { } json)
                return false;

            var facts = DiscogsJson.ParseReleaseFacts(json);

            // The styles are written even when the list is empty. An empty list means Discogs filed the
            // record under a genre with no style. That is an answer, and it stops this release coming back
            // again.
            //
            // The styles are written against the RELEASE, so a corrected link takes the answer with it. Two
            // folders that hold one record ask once. The folder's own column is also written, for as long
            // as the peer catalogue still carries it. See PeerCatalogBuilder.
            db.SetReleaseMeta(DiscogsJson.ProviderId, album.ReleaseId, facts.Genres, facts.Styles);
            db.SetAlbumStyles(album.Folder, facts.Styles);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DiscogsUnavailableException)
        {
            return false;   // No account is connected, or Discogs is refusing. Ask again later.
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return false;   // The library closed in the middle of the album.
        }
        catch (Exception)
        {
            // A release that cannot be read does not stop the pass. It is also not an answer about this
            // record, so leaving it null lets the pass come back to it.
            return false;
        }
    }

    public void Dispose()
    {
        _stop?.Cancel();
        _stop?.Dispose();
        _stop = null;
    }
}
