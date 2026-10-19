namespace RPM.Core.Library;

/// <summary>The store, and the one door into the part of it that can be narrowed.</summary>
public partial class LibraryDatabase
{
    /// <summary>
    /// The import queue as the user sees it: the store, narrowed by a filter.
    ///
    /// <para>Every read and every tick that CAN be narrowed is defined here, and nowhere else. There is no
    /// way to reach one without stating the narrowing. That is the point of this type. An optional
    /// narrowing parameter is easy to forget, and the compiler does not ask for it. Forgetting it acts on
    /// rows the user cannot see, which is the worst way to fail. So the narrowing is required here.</para>
    ///
    /// <para>Facts about the whole queue stay on <see cref="LibraryDatabase"/>. Examples are the number
    /// of rows, the counts for the chips, and the extensions and sources that exist. These are about
    /// everything on purpose. A chip that counted only the rows already on screen could never be turned
    /// on.</para>
    ///
    /// <para>Create one for each action and do not store it. It keeps the narrowing as it was when the
    /// action started, which is what an action should act on.</para>
    /// </summary>
    public sealed class QueueView
    {
        private readonly LibraryDatabase _db;

        internal QueueView(LibraryDatabase db, QueueFilter? filter)
        {
            _db = db;
            Filter = filter ?? QueueFilter.None;
        }

        /// <summary>The narrowing of this view. <see cref="QueueFilter.None"/> means the whole queue. It is
        /// passed explicitly, never left out.</summary>
        public QueueFilter Filter { get; }

        // ── reading rows ──────────────────────────────────────────────────────────────────────────────

        /// <summary>The ticked rows. Every action acts on these.</summary>
        public IReadOnlyList<QueueTrack> CheckedRows() => _db.QueueCheckedRowsIn(Filter);

        /// <summary>The ticked rows that can be filed. Import puts these into the library.</summary>
        public IReadOnlyList<QueueTrack> ImportableRows() => _db.QueueImportableRowsIn(Filter);

        /// <summary>Every row under a tree group: an album, or a whole artist when
        /// <paramref name="album"/> is null. Rows are in the order the records play.</summary>
        public IReadOnlyList<QueueTrack> RowsInGroup(string artist, string? album)
            => _db.QueueRowsInGroupIn(artist, album, Filter);

        /// <summary>The cloud placeholders under a group. These are the rows that a download can act
        /// on.</summary>
        public IReadOnlyList<QueueTrack> DatalessRowsInGroup(string artist, string? album)
            => _db.QueueDatalessRowsInGroupIn(artist, album, Filter);

        /// <summary>One page of a group's rows, for the virtualised tree.</summary>
        public IReadOnlyList<QueueTrack> GroupPage(string artist, string album, int offset, int limit)
            => _db.QueueGroupPageIn(artist, album, offset, limit, Filter);

        // ── the shape of the tree ─────────────────────────────────────────────────────────────────────

        /// <summary>The artist and album skeleton, with counts and tick states. One read builds the
        /// tree.</summary>
        public IReadOnlyList<QueueGroup> Groups() => _db.QueueGroupsIn(Filter);

        /// <summary>Artist/album/track-count only, for callers that need no tick states.</summary>
        public IReadOnlyList<(string Artist, string Album, int TrackCount)> GroupCounts()
            => _db.QueueGroupCountsIn(Filter);

        /// <summary>The tick state of a group: the number of ticked rows, and the total number of
        /// rows.</summary>
        public (int Checked, int Total) GroupCheckState(string artist, string? album)
            => _db.QueueGroupCheckStateIn(artist, album, Filter);

        /// <summary>Every group's tick state in one read.</summary>
        public IReadOnlyList<(string Artist, string Album, int Checked, int Total)> GroupCheckStates()
            => _db.QueueGroupCheckStatesIn(Filter);

        // ── ticking ───────────────────────────────────────────────────────────────────────────────────

        /// <summary>Ticks or unticks everything this view is showing. "Select all" means the rows on
        /// screen.</summary>
        public void CheckAll(bool isChecked) => _db.QueueCheckAllIn(isChecked, Filter);

        /// <summary>Ticks or unticks every row this view shows under one tree group.</summary>
        public void SetCheckedInGroup(string artist, string? album, bool isChecked)
            => _db.QueueSetCheckedInGroupIn(artist, album, isChecked, Filter);

        /// <summary>Removes the ticks from rows this view is not showing, so the ticked set is always a
        /// subset of what is on screen. Returns how many ticks were removed.</summary>
        public int UncheckHidden() => _db.QueueUncheckHiddenIn(Filter);
    }
}
