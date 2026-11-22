using RPM.Core.Analysis;
using RPM.Core.Diagnostics;
using RPM.Core.Import;
using RPM.Core.Library;
using RPM.Core.Metadata.Providers;
using RPM.Core.Threading;

namespace RPM.Desktop.Presentation.Main.Models;

/// <summary>
/// After an import: what the queue's catalogue fill did not reach before the records left the queue.
///
/// <para><b>Why it exists.</b> The fill (MainModel.CatalogueFill) asks about one record a second, in the background,
/// after music is added. An import pressed straight away, or the setup wizard's import, files records the fill has
/// not reached yet. Those records would reach the library with no explicit flag and no disc picture, although the
/// catalogue had both. So for every album folder an import filed, whose record the fill had not answered for, this
/// pass asks the same two questions once.</para>
///
/// <para><b>What it writes.</b> The explicit flag goes into the files, that field only, and the library rows are read
/// again, the way a tag edit in the app is written (<see cref="AdvisoryFill.Write"/>). A song whose flag was set in
/// the queue is left alone, and so is the song on the deck. The disc picture goes into the album folder as
/// disc.png, unless the album already has one (<see cref="FolderArtWriter.WriteFoundDisc"/>). The cloud sync then
/// sends both, as it does any edit.</para>
///
/// <para><b>Governed and quiet.</b> One album is one unit of work that asks the governor first, in the network lane,
/// in the last wave: nobody waits for it. It never holds up the import, which is finished before it starts. Signed
/// out, offline or refused, it stops without a word. It follows the same setting as the fill.</para>
/// </summary>
public sealed partial class MainModel
{
    /// <summary>One album folder an import filed into.</summary>
    /// <param name="Folder">The folder, relative to the library root, with forward slashes.</param>
    /// <param name="Answered">Whether the queue's fill answered for the record before the import.</param>
    /// <param name="Leave">Songs (by relative path) whose explicit flag was set in the queue.</param>
    private sealed record FollowUpAlbum(string Folder, bool Answered, IReadOnlySet<string> Leave);

    private readonly Queue<FollowUpAlbum> _followUps = new();
    private bool _followUpRunning;
    private Services.Shell.Sweep? _followUpSweep;

    /// <summary>A second between albums, for the same reason as the fill's (see <see cref="FillBreath"/>).</summary>
    private static readonly TimeSpan FollowUpBreath = TimeSpan.FromSeconds(1);

    /// <summary>Hands the albums of one import to the follow-up. A follow-up already running takes them on at the
    /// end, rather than a second worker doubling the requests. Safe from any thread.</summary>
    private void StartImportFollowUp(IEnumerable<FollowUpAlbum> albums)
    {
        if (!_settings.ImportFillFromCatalogue || !_library.IsOpen || _library.Root is not { } root)
            return;

        var db = _library.Db;
        lock (_followUps)
        {
            foreach (var album in albums.Where(a => !a.Answered))
                _followUps.Enqueue(album);
            if (_followUps.Count == 0 || _followUpRunning)
                return;
            _followUpRunning = true;
        }

        _followUpSweep ??= App.Services.GetRequiredService<Services.Shell.Governor>().Pass(
            "import follow-up", Services.Shell.Lane.Network, Services.Shell.Wave.Last,
            notBefore: Services.Shell.Phase.Ready);
        var sweep = _followUpSweep;
        _ = Task.Run(() => FollowUpAsync(db, root, sweep));
    }

    private async Task FollowUpAsync(LibraryDatabase db, string root, Services.Shell.Sweep sweep)
    {
        using var stop = new CancellationTokenSource();
        var ct = stop.Token;
        int flagged = 0, discs = 0;
        var drained = false;

        try
        {
            while (true)
            {
                FollowUpAlbum next;
                lock (_followUps)
                {
                    if (!_followUps.TryDequeue(out next!))
                    {
                        _followUpRunning = false;
                        drained = true;
                        break;
                    }
                }

                using var slot = await sweep.Next(ct);
                if (!slot.Go || !Still() || !_settings.ImportFillFromCatalogue)
                    break;

                var (asked, ok) = await FollowUpOneAsync(db, root, next, ct);
                flagged += ok.Flagged;
                discs += ok.Discs;
                if (!asked.Reached)
                    break;   // the API did not answer; the rest would get the same silence

                if (asked.Network && !await Cancellable.DelayAsync(FollowUpBreath, ct))
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            // The library closed during the pass. Nothing is half-written: one file is one write.
        }
        catch (Exception ex)
        {
            Faults.Note("import", "following up an import", ex);
        }
        finally
        {
            // Stopped early: what is left is dropped, and the next import starts a new round. A worker that drained
            // the queue has already handed over, and a new round may be running by now.
            if (!drained)
            {
                lock (_followUps)
                {
                    _followUps.Clear();
                    _followUpRunning = false;
                }
            }
            if (flagged > 0 || discs > 0)
                Said.Line("import", $"follow-up: {flagged} songs flagged explicit, {discs} disc pictures");
        }

        bool Still()
        {
            if (_library.IsOpen && ReferenceEquals(_library.Db, db))
                return true;
            stop.Cancel();
            return false;
        }
    }

    /// <summary>The follow-up for one album folder. Returns whether the network was used and reached, and what
    /// was written.</summary>
    private async Task<((bool Network, bool Reached) Asked, (int Flagged, int Discs) Done)> FollowUpOneAsync(
        LibraryDatabase db, string root, FollowUpAlbum album, CancellationToken ct)
    {
        var rows = db.QueryUnderFolder(album.Folder)
            .Where(r => string.Equals(FolderOf(r.RelativePath), album.Folder, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (rows.Count == 0)
            return ((false, true), (0, 0));

        var network = false;
        int flagged = 0, discs = 0;
        var abs = Path.Combine(root, album.Folder.Replace('/', Path.DirectorySeparatorChar));

        // The disc picture, when the album has none. The import already carried any that came with the files.
        if (FolderArtWriter.FindDiscImage(abs) is null)
        {
            var first = rows[0];
            var artist = string.IsNullOrWhiteSpace(first.AlbumArtist) ? first.Artist : first.AlbumArtist;
            var title = first.Album is { } a ? AlbumResolver.NormalizeAlbum(a) : null;
            if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
            {
                network = true;
                var bytes = await App.Services.GetRequiredService<Services.Art.ArtworkService>()
                    .DiscArtBytesAsync(artist, title, ct);
                if (bytes is { Length: > 0 } && FolderArtWriter.WriteFoundDisc(abs, bytes))
                    discs++;
            }
        }

        // The explicit flag, from the release the album is linked to or the one the catalogue is sure of.
        if (Catalog.Search.Providers.FirstOrDefault(p => p.Id == RpmMetadataProvider.ProviderId)
                is { IsAvailable: true } catalogue)
        {
            var meta = db.GetAlbumMeta(album.Folder);
            var releaseId = meta.ReleaseProvider == RpmMetadataProvider.ProviderId ? meta.ReleaseId : null;
            if (AdvisoryFill.AlbumOf(rows, releaseId) is { } record)
            {
                network = true;
                if (!(await CatalogueFill.FindAsync(catalogue, record, ct)).Answers(out var release, out _))
                    return ((true, false), (flagged, discs));

                if (release is not null)
                {
                    var plan = AdvisoryFill.Plan(rows, release, album.Leave);
                    flagged = AdvisoryFill.Write(db, root, plan, path => _playback.HoldsOpen(path));
                }
            }
        }

        return ((network, true), (flagged, discs));

        static string FolderOf(string relativePath)
        {
            var slash = relativePath.LastIndexOf('/');
            return slash < 0 ? "" : relativePath[..slash];
        }
    }
}
