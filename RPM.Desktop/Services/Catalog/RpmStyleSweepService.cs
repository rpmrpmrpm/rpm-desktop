using RPM.Core.Analysis;
using RPM.Core.Library;
using RPM.Core.Metadata.Providers;
using RPM.Core.Threading;

namespace RPM.Desktop.Services.Catalog;

/// <summary>
/// Reads what the app's own catalogue says about every album linked through it, one album at a time.
///
/// <para><b>Why there are two passes.</b> <see cref="DiscogsStyleSweepService"/> asks only about Discogs links,
/// and it waits for a connected Discogs account. An album linked through the app's own catalogue was asked about
/// by nobody, so its styles never reached the mood lists. This pass needs no account of the user's.</para>
///
/// <para><b>It fills the catalogue's answer, not the file.</b> The answer goes where the Discogs pass puts its
/// own, against the release. Genre and style in a file are the user's fields, and this does not write
/// them.</para>
/// </summary>
public sealed partial class RpmStyleSweepService : IDisposable
{
    private readonly RpmMetadataProvider _catalogue;
    private readonly Services.Library.LibraryService _library;

    private CancellationTokenSource? _stop;
    private Task? _worker;

    public RpmStyleSweepService(
        IRpmMetadataApi api,
        Services.Library.LibraryService library,
        Shell.Governor governor)
    {
        _catalogue = new RpmMetadataProvider(api);
        _library = library;
        _sweep = governor.Pass(
            "rpm styles", Shell.Lane.Network, Shell.Wave.Last, notBefore: Shell.Phase.Ready);
        _library.Opened += _ => Start();

        // The sweep holds the library between its requests. It is stopped and waited for before the library
        // is closed (see Shell.PassLife).
        _library.StopBeforeClose(_life.CloseAsync);
    }

    /// <summary>What this pass asks before each unit of work. See Governor. Nothing in the background
    /// starts by itself.</summary>
    private readonly Shell.Sweep _sweep;

    /// <summary>How long to wait between albums. The album page someone is looking at comes first.</summary>
    private static readonly TimeSpan Breath = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait after an album that could not be reached. The cause is not this record, and
    /// asking again at once only earns a longer refusal.</summary>
    private static readonly TimeSpan AfterASilence = TimeSpan.FromMinutes(2);

    /// <summary>How many albums are read at a time. This is a page, not the whole list, because the shelf
    /// can change during a pass that runs for a while.</summary>
    private const int Page = 40;

    /// <summary>Starts the pass, or leaves the one already running alone. The library raises <c>Opened</c> for
    /// every open, and a second worker would double the request rate.</summary>
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
                // The catalogue can be out of reach for a while. The pass waits rather than stopping, and the
                // check is here so that it costs one sleeping task and not a refusal for every album.
                if (!_catalogue.IsAvailable)
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
                        albums = db.NextReleasesToAsk(RpmMetadataProvider.ProviderId, Page);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
                    {
                        return;   // The library closed during the pass. This is not an error.
                    }
                }

                // Every linked album has been asked about. A record linked later brings the pass back at the
                // next open.
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

    /// <summary>Asks about one album. Whether the catalogue answered decides how long to wait.</summary>
    private async Task<bool> AskAsync(LibraryDatabase db, LibraryDatabase.ReleaseToAsk album, CancellationToken ct)
    {
        try
        {
            return await ReleaseFactsAsk.AskAsync(db, _catalogue, album, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return false;   // The library closed in the middle of the album.
        }
        catch (Exception)
        {
            // A release that cannot be read does not stop the pass. It is also not an answer about this
            // record, so nothing is written and the pass can come back to it.
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
