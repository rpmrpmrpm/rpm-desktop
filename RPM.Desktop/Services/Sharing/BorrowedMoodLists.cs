using RPM.Core.Library;
using RPM.Core.Sharing;
using RPM.Desktop.Presentation.Main.LeftPane.Models;

namespace RPM.Desktop.Services.Sharing;

/// <summary>
/// The owner's generated lists, turned into rows the borrowed shelf already knows how to draw.
///
/// <para><b>They arrive as lists of ids, which is what a playlist is on a borrowed shelf.</b> This
/// side cannot cut them again. A generated list is made from percentiles over the owner's whole
/// collection, and this machine only has a listing of that collection. So what crosses is a fixed
/// order of records with a name on it. That is a playlist. Folding them into the borrowed playlist
/// tree means the panel, the ordering, the opening and the playback already work for them.</para>
///
/// <para><b>Stored as playlists, drawn as what they are.</b> Each row carries the generated list it
/// was cut as (<see cref="PlaylistCover.Generated"/>). So it shows that list's photograph and frame
/// on every surface, the same picture the owner sees. Without this, each row would be drawn as a
/// made playlist: a grid of four covers, not a photograph.</para>
///
/// <para><b>In the owner's own arrangement.</b> Every list says where it sits
/// (<see cref="PeerMoodList.Folder"/>). So the moods, the mood flavours under each of them and the
/// eras beside them come across in the same shape as the owner's shelf. An owner whose build does not
/// send this field sends no place for its lists. Those lists go into one flat folder.</para>
///
/// <para><b>A flavour hangs under its mood's list, not under a folder with the mood's name.</b> The
/// last step of a flavour's place is the mood's own row (<see cref="SmartPlaylistFace.FolderChain"/>).
/// This matches how the owner's pane files it. If a folder were used, each mood with flavours would
/// show twice: a "Chill" list and a "Chill" folder beside it.</para>
///
/// <para><b>The rows are numbered downwards from zero.</b> The owner's own playlist ids start at one.
/// A generated row is not one of theirs, so it must never take one of those numbers. Folders and
/// lists share this run of numbers.</para>
///
/// <para><b>Sent whole, not trimmed.</b> Which of these records the borrower can reach is not a fact
/// about the list. On the same network they can ask the owner's machine for every one of them. Off
/// the network, only the ones the owner put in the cloud resolve. The rows carry every id, and the
/// player skips the ones it cannot reach, as the phone does.</para>
/// </summary>
public static class BorrowedMoodLists
{
    /// <summary>
    /// The owner's cut as tree rows, ready to go into the borrowed tree beside their own lists.
    /// An empty list gives an empty result. An older build sends no lists at all. That is a shelf
    /// with none, so no empty folder is put on screen.
    /// </summary>
    /// <param name="folderName">The name of the folder for a list that has no place of its own. The
    /// app passes <see cref="SmartPlaylistFace.MoodsFolderName"/>. It is only used for an owner who
    /// sends no arrangement.</param>
    /// <param name="nameOf">Gives the name of each list. It is passed in rather than read here,
    /// because names come from the app's string table and the shaping below does not need it. So a
    /// rearrangement of rows does not need a resource loader. The app passes <see cref="RpmName"/>.</param>
    public static IReadOnlyList<PlaylistTreeRow> AsTree(
        IReadOnlyList<PeerMoodList> lists, string folderName, Func<PeerMoodList, string> nameOf)
    {
        ArgumentNullException.ThrowIfNull(lists);
        ArgumentNullException.ThrowIfNull(nameOf);

        var usable = lists.Where(l => l.RpmIds.Count > 0).ToList();
        if (usable.Count == 0)
            return [];

        var rows = new List<PlaylistTreeRow>(usable.Count + 4);

        // Counting down from zero, for folders and lists together. A folder and a list must not share
        // a number, and neither may take a number that one of the owner's own playlists has.
        long next = 0;
        long Mint() => --next;

        // Every list is numbered before any of them is placed. This lets a flavour name its mood's
        // row, whichever of the two the cut lists first.
        var ids = usable.Select(_ => Mint()).ToArray();

        // The moods' own lists, by kind and by place. A flavour's place is its mood's place plus one
        // step, and that step is the mood's row.
        var wholeAt = new Dictionary<(string Kind, string Place), long>();
        for (var i = 0; i < usable.Count; i++)
        {
            var list = usable[i];
            if (list.Folder.Count > 0 && string.IsNullOrEmpty(list.Genre) && list.Decade == 0)
                wholeAt.TryAdd((list.Kind, PlaceKey(list.Folder)), ids[i]);
        }

        // A folder exists only because a list says it sits in it. Nothing here invents a folder, so an
        // empty heading cannot appear. Lists are met in the order of the owner's cut, which is the
        // order of the owner's own pane.
        var made = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        int sort = 0;

        // Top-level folders sort after the owner's own lists, because the owner made those and this
        // machine did not. Top-level folders keep the cut's order among themselves. Ties at
        // int.MaxValue would leave the moods and the eras in whatever order the tree builds them.
        int top = 0;

        long Place(IReadOnlyList<string> chain)
        {
            long parent = 0;
            var path = "";

            foreach (var name in chain)
            {
                path = path.Length == 0 ? name : path + "\u0000" + name;
                if (made.TryGetValue(path, out var already)) { parent = already; continue; }

                var id = Mint();
                rows.Add(new PlaylistTreeRow(
                    Id: id,
                    ParentId: parent == 0 ? null : parent,
                    Kind: PlaylistNodeKind.Folder,
                    Name: name,
                    // After the owner's own lists, which the owner made and this machine did not. A
                    // folder inside one of these sorts among its siblings, not at the end.
                    Sort: parent == 0 ? int.MaxValue - top++ : sort++,
                    Items: [],
                    Cover: new PlaylistCover.Automatic()));

                made[path] = id;
                parent = id;
            }

            return parent;
        }

        for (var i = 0; i < usable.Count; i++)
        {
            var list = usable[i];

            // An owner who sends no arrangement gets the layout this code always made: everything
            // together under the moods folder. Their build does not know where its lists sit, so no
            // place is guessed here.
            var chain = list.Folder.Count > 0 ? list.Folder : [folderName];

            // A flavour whose mood's own list was sent hangs under that list. A flavour whose mood
            // was not sent (too short to be offered, or empty) keeps the step as a folder. That folder
            // is the only place left that says which mood the flavour belongs to.
            var parent = !string.IsNullOrEmpty(list.Genre) && list.Decade == 0 && list.Folder.Count > 1
                         && wholeAt.TryGetValue((list.Kind, PlaceKey(list.Folder.Take(list.Folder.Count - 1))),
                                                out var mood)
                ? mood
                : Place(chain);

            rows.Add(new PlaylistTreeRow(
                Id: ids[i],
                ParentId: parent == 0 ? null : parent,
                Kind: PlaylistNodeKind.Playlist,
                Name: nameOf(list),
                Sort: sort++,
                Items: [.. list.RpmIds.Select(rpmId =>
                    new PlaylistTreeEntry(PlaylistItemKind.Song, rpmId, RelPath: null, RefPlaylistId: null))],
                Cover: IdOf(list) is { } generated
                    ? new PlaylistCover.Generated(generated)
                    : new PlaylistCover.Automatic()));
        }

        return rows;
    }

    /// <summary>
    /// Which generated list one of them is, or null for a kind this build does not know.
    ///
    /// <para>Null, and not a guess. The owner may be on a newer build. The list still plays and keeps
    /// the owner's name for it, and it is drawn as a made playlist. A picture chosen for the wrong
    /// kind would say the list is a different one.</para>
    /// </summary>
    public static SmartPlaylistId? IdOf(PeerMoodList list)
        => Enum.TryParse<SmartPlaylistKind>(list.Kind, ignoreCase: false, out var kind) && Enum.IsDefined(kind)
            ? new SmartPlaylistId(kind, string.IsNullOrEmpty(list.Genre) ? null : list.Genre, list.Decade)
            : null;

    /// <summary>A place as one string, for looking a mood's list up by where it sits. The separator is
    /// one no folder name can contain.</summary>
    private static string PlaceKey(IEnumerable<string> chain) => string.Join("\u0000", chain);

    /// <summary>
    /// What this app calls one of them.
    ///
    /// <para>The name this app gives the same list, not one made up here. A borrower who opens "Slow
    /// Burn" on somebody else's shelf and "Slow Burn" on their own is looking at the same idea. Two
    /// names would make it look like two ideas. A kind this build does not know is shown with the
    /// name the owner gave it, not dropped, because a list with an unfamiliar name still plays.</para>
    /// </summary>
    public static string RpmName(PeerMoodList list)
        => SmartPlaylistFace.Name(list.Kind, list.Genre, list.Decade);
}
