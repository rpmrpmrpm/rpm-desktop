using System.Text;

using Microsoft.UI.Xaml;

using RPM.Core.Import;
using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Main.Library.Models;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// The album page hears about changes to the record under it.
///
/// <para>Everything this page draws is read once, when the record is opened. A heart pressed in the
/// track pane on the left, a sleeve chosen in the cover window, or a name fixed in the metadata editor
/// each writes to the same shelf this page was drawn from. Without a notice, the page would keep
/// showing what the shelf said when it was opened. The wall behind the page also shows hearts, and the
/// pane on the left shows its own.</para>
///
/// <para><b>Three kinds of change, three kinds of answer.</b> Which answer is right depends on what the
/// change is about, not on what it costs:</para>
///
/// <list type="bullet">
/// <item>A song's heart is one flag on one line. It is applied to the LINE for that file, found by file
/// path and never by position. It is right whatever else has changed, and it does nothing when the song
/// is not on this record.</item>
/// <item>A record's heart is one flag on this page's own key. The key is read again from the shelf, not
/// painted from the flag, because <c>ApplyMeta</c> also draws the rating and the Discogs item.</item>
/// <item>Anything else (a sleeve, a name, a tag, a track deleted) has no shape. The page is re-read
/// whole. The check that keeps this affordable is <see cref="ShelfFace"/>.</item>
/// </list>
///
/// <para><b>The ordering map for this file.</b> There are four independent arrivals: the two heart
/// events (UI thread, from whichever surface was pressed), <c>LibraryService.Changed</c> (any thread,
/// raised by cloud sync), <c>CloudSyncService.AlbumArtChanged</c> (a worker thread), and the reader,
/// who can open another record between any two of them.</para>
///
/// <list type="number">
/// <item><b>A heart against a page that has moved on.</b> Safe. The write is keyed by the song's file
/// path and by the folder, not by an index into a list. Writing <c>Lines[i].IsFavorite = x</c> here
/// would make the same code wrong. Verified by reading the two handlers below: neither writes by
/// position.</item>
/// <item><b>A re-read against the reader opening another record.</b> Safe. Both run on the UI thread,
/// and the re-read reads <c>_folder</c> when it runs, so it is always about the record on screen. At
/// worst it is one turn late, and it is never about the wrong album.</item>
/// <item><b>A re-read against the four catalogue loads.</b> Safe by construction. A re-read that leaves
/// the record's own words alone does not start those loads and does not cancel them, so a load in
/// flight stays about the record it was asked for. A re-read that CHANGES those words (a name fixed in
/// the editor) cancels and asks again, the same as opening the record does.</item>
/// <item><b>Two changes arriving at once.</b> Safe. The face is compared and stamped again inside one
/// UI thread turn, so the second change sees the stamp the first one wrote. Not verified for a
/// dispatcher that merges queued work. In that case the two changes would give one re-read, not
/// two.</item>
/// </list>
/// </summary>
public sealed partial class AlbumDetailView
{
    /// <summary>
    /// Subscribes this page to the shelf for as long as the page exists. Called once, from the
    /// constructor, beside the page's other subscriptions.
    ///
    /// <para>Not on Loaded, and not off on Unloaded. The page is hidden by visibility rather than
    /// unloaded, so an Unloaded that never fires would mean an unsubscribe that never happens. A Loaded
    /// that fires more than once would subscribe twice. The subscriptions are removed when the view
    /// really leaves the tree, which is when the window closes.</para>
    /// </summary>
    private void WatchShelf()
    {
        var library = Library;
        var cloud = Cloud;

        library.TrackFavoriteChanged += OnTrackHearted;
        library.AlbumFavoriteChanged += OnAlbumHearted;
        library.Changed += OnShelfChanged;
        cloud.AlbumArtChanged += OnCloudArt;

        Unloaded += (_, _) =>
        {
            library.TrackFavoriteChanged -= OnTrackHearted;
            library.AlbumFavoriteChanged -= OnAlbumHearted;
            library.Changed -= OnShelfChanged;
            cloud.AlbumArtChanged -= OnCloudArt;
        };
    }

    /// <summary>
    /// A song marked or unmarked anywhere in the app.
    ///
    /// <para>Applied to the line for that FILE. A page showing another record has no such line, so it
    /// does nothing. There is no "is this my album" test here, because the file path already answers
    /// it.</para>
    /// </summary>
    private void OnTrackHearted(string filePath, bool favorite) => OnUi(() =>
    {
        if (TrackList.ItemsSource is not IEnumerable<AlbumTrackItem> lines)
            return;

        foreach (var line in lines)
            if (line.Info is { } info
                && string.Equals(info.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                line.IsFavorite = favorite;
    });

    /// <summary>A whole record marked somewhere else, such as the full-screen player or the shelf
    /// behind this page. The mark is not painted from the flag that arrived. The meta is read again,
    /// because the key this page draws is one of three things <c>ApplyMeta</c> writes.</summary>
    private void OnAlbumHearted(string folder, bool favorite) => OnUi(() =>
    {
        if (_folder.Length == 0
            || !string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase)
            || !Library.IsOpen)
            return;

        ApplyMeta(Library.Shelf.GetAlbumMeta(_folder));
    });

    /// <summary>A sleeve or a disc label chosen for a record on another of the user's devices and
    /// synced here. Nothing is compared. A picture that has just been written is the change, and the
    /// page redraws for it the same way it would for one chosen on this device.</summary>
    private void OnCloudArt(string folder) => OnUi(() =>
    {
        if (Visibility == Visibility.Visible
            && _folder.Length > 0
            && string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase))
            Load(_folder, AlbumOpen.Again);
    });

    /// <summary>
    /// Something touched the shelf. Almost always it is not this record.
    ///
    /// <para><c>LibraryService.Changed</c> is the one notice the shelf raises, and everything raises
    /// it: an import, a playlist written, every tick of a cloud sync. Re-reading the page on each one
    /// would take the sleeve down and put it back several times a minute, even for a record nobody
    /// edited. So the question is not "did something change" but "does this record still look the way
    /// this page drew it". See <see cref="ShelfFace"/>.</para>
    ///
    /// <para>Nothing happens while the page is not on screen. The page can skip the work because a
    /// record is read from the shelf every time it is opened (see MainPage.OpenAlbum). A change made
    /// while the page was away is already in that read when the page comes back.</para>
    /// </summary>
    private void OnShelfChanged() => OnUi(() =>
    {
        if (Visibility != Visibility.Visible || _folder.Length == 0 || !Library.IsOpen)
            return;

        if (ShelfFace(_folder) is { } face && face != _face)
            Load(_folder, AlbumOpen.Again);
    });

    /// <summary>The separator between the fields of a face. It is a character no tag can hold, so two
    /// records cannot flatten to the same string by having a separator of their own in the right
    /// place.</summary>
    private const char Sep = '\u001f';

    /// <summary>
    /// The record as the shelf describes it now, reduced to exactly what this page DRAWS and flattened
    /// to one comparable string. That is the songs it holds, their titles and tags, and the sleeve in
    /// the folder.
    ///
    /// <para>It is a face, not a version number, because nothing keeps a version. The writes come from
    /// a metadata editor, an import, a cover window and a sync. Asking each of them to stamp the record
    /// it touched would mean four places to forget. This asks the shelf the question the page actually
    /// has, and the answer holds whenever it is asked.</para>
    ///
    /// <para>What is NOT in it is as deliberate as what is. The heart, the rating and the Discogs link
    /// are album meta. They arrive through their own paths, so a rating set on the wall must not take
    /// the sleeve down. The catalogues' answers are not the shelf's business at all.</para>
    ///
    /// <para>Null when the record has no songs left. This is the one answer that is not a change to
    /// draw. The page is being closed by whoever emptied it.</para>
    /// </summary>
    private string? ShelfFace(string folder)
    {
        var library = Library;
        if (!library.IsOpen || library.ShelfRoot is not { } root)
            return null;

        var tracks = library.Shelf.FolderTracks(folder).Select(t => t.ToTrackInfo(root)).ToList();
        return tracks.Count == 0 ? null : FaceOf(AlbumTracklist.InDiscOrder(tracks), root, folder);
    }

    /// <summary>
    /// The same face, built from songs already in hand.
    ///
    /// <para><c>Load</c> uses this one. It uses the rows it has just read, rather than asking the shelf
    /// a second time. Two reads give two answers. A write that lands between them stamps the page with
    /// a face newer than the drawing itself, so the change that write carried would not be seen by the
    /// next notice. One read, one drawing, one face.</para>
    /// </summary>
    private static string FaceOf(IReadOnlyList<TrackInfo> tracks, string root, string folder)
    {
        var face = new StringBuilder();
        foreach (var t in tracks)
            face.Append(t.FilePath).Append(Sep)
                .Append(t.Title).Append(Sep)
                .Append(t.Artist).Append(Sep)
                .Append(t.AlbumArtist).Append(Sep)
                .Append(t.Album).Append(Sep)
                .Append(t.DiscNumber).Append('/').Append(t.TrackNumber).Append(Sep)
                .Append(t.Year).Append(Sep)
                .Append(t.IsExplicit).Append(Sep);

        return face.Append(SleeveFace(root, folder)).ToString();
    }

    /// <summary>
    /// The sleeve in the folder, as a stamp: the file name, its size and when it was last written. It
    /// is not the picture's bytes. This runs on every notice the shelf raises, and reading a megabyte
    /// off the disk to learn that nothing changed is the cost this check exists to avoid.
    ///
    /// <para>The length is included as well as the time. A cover replaced by a different one within the
    /// same second would otherwise look unchanged.</para>
    /// </summary>
    private static string SleeveFace(string root, string folder)
    {
        try
        {
            var full = Path.Combine(root, folder.Replace('/', Path.DirectorySeparatorChar));
            if (FolderArtWriter.FindFolderImage(full) is not { } image)
                return "none";

            var file = new FileInfo(image);
            return $"{image}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            // A folder on a volume that has just gone away. The page keeps the sleeve it has, which is
            // the honest answer for a record whose disk cannot be read.
            return "unreadable";
        }
    }

    /// <summary>The face of the record as this page last drew it, so the next notice has something to
    /// compare against. Written by <c>Load</c>, the only code that draws the page.</summary>
    private string? _face;

    /// <summary>
    /// Runs the work on the UI thread, whichever thread the news arrived on. The two heart events are
    /// raised from the press that caused them, so they are already on the UI thread. The shelf's notice
    /// and the cloud's notice are raised from workers. All four go through this, so no handler is only
    /// sometimes marshalled. A reader would otherwise have to work out which ones are.
    /// </summary>
    private void OnUi(Action work)
    {
        if (DispatcherQueue.HasThreadAccess)
            work();
        else
            DispatcherQueue.TryEnqueue(() => work());
    }
}

/// <summary>
/// Why the album page is being drawn. This decides what the drawing costs.
///
/// <para>It is stated at every call rather than given a default, because the two answers are not
/// degrees of the same thing. One of them makes four network lookups and the other must not. A default
/// here would let "the record was read again because its sleeve changed" quietly ask MusicBrainz again.
/// That is the request this page is most careful about: one a second, and the answer takes seconds to
/// arrive.</para>
/// </summary>
public enum AlbumOpen
{
    /// <summary>The reader opened this record. Everything is drawn and everything is asked: what the
    /// encyclopedias say, what the wall says, and what to play next.</summary>
    Fresh,

    /// <summary>The same record, read again because the shelf under it changed: a name fixed, a sleeve
    /// chosen, or a song deleted. The catalogue lookups are not asked again unless the words they were
    /// asked about have changed. Results that have already come back are kept.</summary>
    Again,
}
