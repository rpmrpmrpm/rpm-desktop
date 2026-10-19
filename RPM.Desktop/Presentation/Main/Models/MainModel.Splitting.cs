using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Analysis;
using RPM.Core.Import;
using RPM.Core.Identify;
using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Models;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace RPM.Desktop.Presentation.Main.Models;

/// <summary>One file that holds a whole record. This part gives the split dialog what it needs from the
/// model to cut the file into tracks, and it says what to do with the pieces that the dialog hands back.
/// </summary>
public sealed partial class MainModel
{
    // ── album-file splitting (dialog wiring) ──

    /// <summary>The shared player, used for the split dialog's boundary preview.</summary>
    public PlaybackService Playback => _playback;

    /// <summary>The catalogues that a release can be searched for in. The split dialog's album box uses
    /// them.</summary>
    public CatalogService Catalog { get; }

    /// <summary>The analyzer for the split dialog. The account decides what its Recognize selection may
    /// use. That choice is made here, when the window opens. It is either nothing, or the basic ladder with
    /// the advanced layer behind it. See <see cref="RecognitionReach.ForSplit"/>. Support bought while the
    /// window is open applies the next time the window opens.
    /// </summary>
    /// <param name="vinyl">The file is a recorded record, so silence must be backed up by other evidence
    /// before it counts as a track break. See <see
    /// cref="RPM.Core.Splitting.AlbumSplitAnalyzer.VinylRecording"/>. This is off for an album file,
    /// because the gaps in an album file are part of the album as it was mastered.</param>
    public RPM.Core.Splitting.AlbumSplitAnalyzer CreateSplitAnalyzer(bool vinyl = false)
    {
        // Signed out, every lookup would be refused. So nothing is offered, and the dialog hides Recognize
        // selected. Signed in, the account decides.
        var offer = _identification.SignInRequired
            ? null
            : RecognitionReach.ForSplit(_supporter.Standing, AdvancedLayerAvailable);
        var basic = offer is null ? null : _identification.Backend as ISegmentIdentificationBackend;
        var advanced = offer is { UsesAdvanced: true } ? _identification.Backend as IClipIdentificationBackend : null;

        return new(basic, advanced)
        {
            VinylRecording = vinyl,
            ReleaseLookup = Catalog.SplitReleaseLookup,
        };
    }

    /// <summary>Swaps an album-file row for the per-track rows that a split produced. The album itself
    /// stays in the tree. It is moved to <c>Originals/</c>, not removed. It is also how a bad split is
    /// undone: the cut tracks go to the trash, and the original is cut again.
    /// </summary>
    public void ReplaceWithSplit(TrackRow albumRow, RPM.Core.Splitting.SplitExecution split)
    {
        if (!_library.IsOpen || albumRow.QueueId == 0 || split.NewTracks.Count == 0)
            return;

        var original = _library.Db.QueueRowById(albumRow.QueueId);
        if (original is null)
            return;

        // The split's files sit under the same source tree. So their queue rows are built the same way a
        // scan builds them (folders nested under the source name). Then the album row is swapped out, and
        // the skeleton is rebuilt.
        var tracks = split.NewTracks.ToList();

        // The kept album is read back from disk. So its row has the real file's tags and length, and its
        // split probe. That lets the row offer to cut it again straight away.
        if (PreservedOriginal(split.OriginalMovedTo) is { } kept)
            tracks.Add(kept);

        var newRows = BuildQueueTracks(tracks, original.Source);
        _library.Db.QueueRemove([albumRow.QueueId]);
        _library.Db.QueueInsertMany(newRows);
        _ = _queueSource.RebuildAsync(_queueFilter);
        RaiseQueueChrome();
    }

    private static TrackInfo? PreservedOriginal(string path)
    {
        try
        {
            return File.Exists(path) ? TagReader.Read(path) : null;
        }
        catch
        {
            return null;   // unreadable tags must not lose the split's own rows
        }
    }

    public string NeedsRecognitionText
    {
        get
        {
            if (_queueSource.TotalCount == 0)
                return "";
            var n = _queueSource.NeedsRecognitionCount;
            return n == 0 ? Loc.Get("Review_AllGood") : Loc.Format("Review_NeedRecognition", n);
        }
    }

#if DEBUG
    /// <summary>The first queued row, materialised. Only for the MD_DEBUG_DETAILS dialog
    /// shortcut.</summary>
    public TrackRow? DebugFirstQueueRow()
    {
        if (!_library.IsOpen)
            return null;
        var first = _library.Db.WholeQueue.GroupCounts().FirstOrDefault();
        if (first.Artist is null)
            return null;
        var page = _library.Db.WholeQueue.GroupPage(first.Artist, first.Album, 0, 1);
        return page.Count > 0 ? QueueTreeSource.MaterializeRow(page[0]) : null;
    }
#endif

    public void Dispose()
    {
        _playback.Changed -= OnPlaybackChanged;
        _identification.AvailabilityChanged -= OnIdentificationAvailabilityChanged;
        _library.Changed -= OnLibraryChanged;
        _library.Opened -= OnLibraryOpened;
        _inbox.Arrived -= OnInboxArrived;
        _playback.Stop();
        _libraryAlbums.Dispose();
        _queueSource.Dispose();
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}
