using RPM.Core.Library;

namespace RPM.Core.Cloud;

/// <summary>One generated list as it goes to the cloud: what it is, and the records in it, in order.</summary>
/// <param name="Kind">The mood, as <see cref="SmartPlaylistKind"/> spells it.</param>
/// <param name="Genre">The shelf it is narrowed to, or null for the whole list.</param>
/// <param name="Decade">The first year of the decade it is narrowed to, or zero for all of them.</param>
/// <param name="TrackIds">RPM ids, best first. This is a RUN of <see cref="Shows"/>-sized lists and
/// not one list. The phone plays a slice of them in this order.</param>
public sealed record MoodListPayload(string Kind, string? Genre, int Decade, IReadOnlyList<string> TrackIds)
{
    /// <summary>
    /// What this app calls the list, or null where nobody has named it.
    ///
    /// <para><b>It is not built here, on purpose.</b> The name lives in one table on the desktop
    /// (<c>SmartPlaylistFace.Name</c>), for example "Slow Burn" for the calm rock or "1970s" for a
    /// decade. A second copy in Swift would drift, and the same list would read as two different
    /// things on two screens. Core has the records and the sender has the words, so the sender adds
    /// them (<c>MoodListPushService</c>) and the phone shows what it is given.</para>
    ///
    /// <para>Null is an ordinary answer and not a fault: a cut pushed by an older build carries no
    /// names, and a reader that meets one falls back to the genre or the mood it already knows. This
    /// is the same fallback that the desktop's own table has for a pair that nobody wrote a title
    /// for.</para>
    /// </summary>
    public string? Title { get; init; }

    /// <summary>
    /// Where the list sits in the tree that cut it, outermost first: "Moods", or "Moods" then the
    /// mood, or "Decades". Empty where the publisher did not say.
    ///
    /// <para><b>It is published and not worked out at the far end, on purpose.</b> If every reader
    /// derived a place from the kind, the genre and the decade, each would derive a different one. For
    /// example, the borrowed shelf would put the eras and the mood flavours together in one flat
    /// folder, and the phone would guess a shape of its own. A cut is a shelf as somebody arranged it,
    /// and the arrangement is part of what is being shared.</para>
    ///
    /// <para>Filled by the sender, which is the head that HAS the tree. See
    /// <c>PublishedMoodLists.Dressed</c>. Empty is an ordinary answer: a cut from an older build says
    /// nothing about where its lists go, and a reader falls back to placing them itself.</para>
    /// </summary>
    public IReadOnlyList<string> Folder { get; init; } = [];

    /// <summary>
    /// What this list is called on the wire: stable, and the same answer for the same list on every
    /// machine and after every re-cut.
    ///
    /// <para>It is needed because a generated list has no row of its own to be identified by. It is not
    /// a <c>pl_node</c>, and nothing in the cloud is stored under it. Yet two things have to be able to
    /// name it: a reader that keeps the row somebody opened, and the store that keeps its PICTURE at an
    /// address made from this id. It is derived from what the list IS, so it survives a re-cut with
    /// different records in it.</para>
    ///
    /// <para>Empty where the publisher did not say, which is what a cut from an older build carries.</para>
    /// </summary>
    public string? Id { get; init; }

    /// <summary>
    /// How many of the records above make ONE list. The rest are the later readings.
    ///
    /// <para><b>This is what lets the phone refresh.</b> It cannot cut a list itself, because the
    /// arithmetic is percentiles over a collection that it does not have. So it is sent a run of
    /// <see cref="SmartPlaylists.ReadingsSent"/> lists' worth and shows a slice this long, and moves
    /// the slice on when somebody asks for a new list. There is one song per record across the whole
    /// run, so every slice is a different list.</para>
    ///
    /// <para>Zero, or any value at least as long as the run, means the run IS the list and there is
    /// nothing to step to. This is the case for a cut from an older build, or for a kind with no pool
    /// behind it. A reader that meets it shows everything it was sent and offers no refresh.</para>
    /// </summary>
    public int Shows { get; init; }
}

/// <summary>A whole cut, with the version of the arithmetic that made it.</summary>
public sealed record MoodListsPayload(int Recipe, IReadOnlyList<MoodListPayload> Lists);

/// <summary>
/// Turns one session's cut into what the phone reads.
///
/// <para><b>The phone shows these and does not compute them.</b> The arithmetic is percentiles over
/// a whole collection, a preference table and a decade window, and it changes often. If it were
/// implemented once here and once in Swift, the two would drift, and nobody would see it until the
/// same list held different records on the two screens. So the desktop, which is the only head that
/// has the collection, cuts and pushes, and the phone shows what it is given.</para>
///
/// <para><b>It is built from ONE reading of the shelf.</b> Every list here is asked of the same
/// <see cref="LibraryDatabase.SessionMoods"/>, which is cut once per session behind a lock so that
/// no two lists disagree about which records are calm. A push assembled from two readings would
/// undo that on the way out.</para>
///
/// <para><b>A track with no RPM id is left out and not sent as a blank.</b> The id is what the
/// phone asks for the audio by, and a row without one names nothing that the phone could play.</para>
/// </summary>
public static class MoodListPush
{
    /// <summary>
    /// Which arithmetic cut this.
    ///
    /// <para>Raised by hand when a change would put different records in the same list (an edge
    /// moved, a flavour line rewritten, a cohort rule changed). It is not a schema version and nothing
    /// branches on it. It travels so that a cut can be told apart from a later one made differently,
    /// and so that a phone showing an old cut can say which one it is showing.</para>
    ///
    /// <para>What the current value means: the reading that each mood list starts at is one number for
    /// each LIST and not one for the whole collection, so opening one list does not move the songs in
    /// the others (<c>LibraryDatabase.MoodWindow</c>). A list is sent as a RUN of several readings,
    /// with <see cref="MoodListPayload.Shows"/> saying how much of it is one list, so that the phone
    /// can step its own window.</para>
    ///
    /// <para>The run is ORDERED one reading at a time. <c>MoodFlow.Ordered</c> re-sequences and
    /// normalises across everything it is handed, so ordering the whole run at once would make the
    /// first reading fifty songs drawn from all over the run, in an order that belongs to the two
    /// hundred, and the phone and the computer would show different songs under one name. Ordered one
    /// reading at a time, the first reading of a run IS the list the computer shows, song for song and
    /// in the same order. Which songs are in the run does not change: the picking still runs across
    /// the whole of it.</para>
    /// </summary>
    public const int Recipe = 4;

    /// <summary>
    /// Every list the collection offers, with the songs in it.
    /// </summary>
    /// <param name="db">The library. Read only.</param>
    /// <param name="kinds">Which kinds to offer, in order. These are the caller's own shelf, never a
    /// borrowed one. They are passed explicitly and have no default: a push that quietly offered
    /// everything would be a push whose contents nobody chose.</param>
    /// <param name="nowUtcTicks">One reading of the clock for the whole cut, so that two lists whose
    /// rules have a window in them cannot answer from two different moments.</param>
    /// <remarks>The lists come out of here unnamed and unplaced. The words and the tree belong to the
    /// interface and not to the database, and the sender adds them. See <c>PublishedMoodLists</c>.
    /// </remarks>
    public static MoodListsPayload Build(
        LibraryDatabase db, IReadOnlyList<SmartPlaylistKind> kinds, long nowUtcTicks)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(kinds);

        var lists = new List<MoodListPayload>();

        foreach (var id in db.OfferedGeneratedLists(kinds))
        {
            // A RUN and not one list: the phone is sent more than it shows, so that it can take its own
            // window when somebody presses refresh there, without asking anybody to cut anything. How many
            // readings is set for each kind and is not one number: four readings of a kind that shows two
            // hundred is more than the store takes on one list, and a cut is PUT whole, so that one list
            // would refuse every list beside it. See SmartPlaylists.ReadingsSentFor.
            var ids = db.QuerySmartPlaylistRun(id, nowUtcTicks, SmartPlaylists.ReadingsSentFor(id.Kind))
                        .Select(t => t.RpmId)
                        .Where(rpmId => !string.IsNullOrWhiteSpace(rpmId))
                        .Select(rpmId => rpmId!)
                        .ToList();

            // The minimum that the pane requires of its own rows, applied to what will CROSS and not to what
            // this computer holds. A reader shows what it is sent, so a list that goes up short would be a
            // row that the phone draws and the computer does not: a mood of one record that still has a name.
            //
            // Counted after the RPM ids are taken out, because that is the list the phone gets. A shelf of
            // four hundred records with nine of them in the cloud is nine songs over there, whatever the row
            // on this screen says.
            if (ids.Count < SmartPlaylists.LeastToShow(id))
                continue;

            lists.Add(new MoodListPayload(id.Kind.ToString(), id.Genre, id.Decade, ids)
            {
                // What one list is out of the run above. Sent and not worked out at the far end: the ceiling
                // is per kind and it comes from this head's table.
                Shows = SmartPlaylists.MostSongs(id.Kind),
            });
        }

        return new MoodListsPayload(Recipe, lists);
    }

    /// <summary>
    /// The same cut with one list out of each run: the first <see cref="MoodListPayload.Shows"/>
    /// records, and no more.
    ///
    /// <para><b>For a reader that does not window a run.</b> The phone is sent the whole run and takes
    /// its own slice. A borrowed shelf on another computer draws what it is handed as one playlist
    /// (<c>BorrowedMoodLists</c>), and if it were handed the run it would draw a Chill of two hundred
    /// songs where the owner has fifty. So the run is trimmed on the way to anybody who cannot step
    /// through it, and the cut is not taken twice.</para>
    ///
    /// <para>A list whose <see cref="MoodListPayload.Shows"/> is zero or covers the whole list is passed
    /// through unchanged: there is no run to trim.</para>
    /// </summary>
    public static MoodListsPayload OneListEach(MoodListsPayload cut)
    {
        ArgumentNullException.ThrowIfNull(cut);

        return cut with
        {
            Lists = [.. cut.Lists.Select(list =>
                list.Shows > 0 && list.TrackIds.Count > list.Shows
                    ? list with { TrackIds = [.. list.TrackIds.Take(list.Shows)] }
                    : list)],
        };
    }

    /// <summary>
    /// The cut without the lists that only its owner is sent (<see cref="SmartPlaylists.OwnerOnly"/>),
    /// for anything that goes to another person. Those lists are cut from the owner's listening.
    /// </summary>
    public static MoodListsPayload ForOthers(MoodListsPayload cut)
    {
        ArgumentNullException.ThrowIfNull(cut);

        var ownerOnly = SmartPlaylists.OwnerOnly.Select(k => k.ToString()).ToHashSet(StringComparer.Ordinal);
        return cut with { Lists = [.. cut.Lists.Where(list => !ownerOnly.Contains(list.Kind))] };
    }

    /// <summary>
    /// Whether two cuts say the same thing: the same lists, in the same order, naming the same records
    /// in the same order.
    ///
    /// <para>This is written out because a <c>record</c>'s own equality would not answer it. The lists
    /// hang off the payload as an <see cref="IReadOnlyList{T}"/>, which compares by reference, so two
    /// builds of an unchanged shelf would come back "different" every time. That is the opposite of
    /// what a publisher deciding whether to send anything needs to hear.</para>
    ///
    /// <para><see cref="MoodListPayload.Shows"/> IS compared: it says how much of what was sent is one
    /// list, so a cut that names the same records and splits them into lists of a different length is a
    /// different answer on the screen that reads it.</para>
    ///
    /// <para>The names, the tree and the ids are not compared, because they are not in a cut. They are
    /// added at the boundary from tables in this build (<c>PublishedMoodLists</c>), so two cuts that
    /// name the same records get the same names.</para>
    /// </summary>
    public static bool Same(MoodListsPayload a, MoodListsPayload b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (ReferenceEquals(a, b)) return true;
        if (a.Recipe != b.Recipe || a.Lists.Count != b.Lists.Count) return false;

        for (var i = 0; i < a.Lists.Count; i++)
        {
            var (one, other) = (a.Lists[i], b.Lists[i]);

            if (one.Kind != other.Kind || one.Genre != other.Genre || one.Decade != other.Decade)
                return false;
            if (one.Shows != other.Shows)
                return false;
            if (!one.TrackIds.SequenceEqual(other.TrackIds))
                return false;
        }

        return true;
    }

    /// <summary>
    /// The id a list is known by wherever it goes: a name made from what the list IS.
    ///
    /// <para><b>It is made and not minted</b>, because nothing stores these to hand an id out. The same
    /// list has to answer to the same name on the computer that cut it, on a phone reading it back and
    /// on the store holding its picture, and it has to go on doing so after a re-cut has changed every
    /// record in it. So it is a hash of the three things that say which list this is.</para>
    ///
    /// <para><b>It is a GUID, and not for tidiness.</b> The store's own id check is
    /// <c>Guid.TryParseExact</c>, which stands between a caller and writing an object at a path of their
    /// choosing, so an id that is not a GUID cannot address a picture at all.</para>
    ///
    /// <para>MD5 is used because this is a name and not a secret: what is wanted is the same sixteen
    /// bytes for the same three fields on every machine, for ever.</para>
    /// </summary>
    public static Guid IdOf(string kind, string? genre, int decade)
    {
        var bytes = System.Security.Cryptography.MD5.HashData(
            System.Text.Encoding.UTF8.GetBytes($"rpm-mood\u0000{kind}\u0000{genre}\u0000{decade}"));

        return new Guid(bytes);
    }
}
