namespace RPM.Core.Cloud;

/// <summary>
/// Which songs the listener has marked as a favourite, and the one place where anything asks.
///
/// <para>
/// The mark is a field on a catalogue row, and every screen reads it there: the heart on a track
/// line, the heart on the now playing sheet, and the generated favourites list. But a CloudTrack is
/// a record, and the copy that a screen holds was taken when its catalogue was read. A queue built
/// when an album was opened carries the marks as they were then. If marking a song on one screen
/// left every copy already handed out unchanged, the other screens would keep drawing a hollow
/// heart for a loved song, and a filled heart for a song that was not.
/// </para>
///
/// <para>
/// So the mark is taken off the track and asked for by id. This is the only answer to that
/// question, and it is true whenever it is asked, not just for one turn of the dispatcher.
/// </para>
///
/// <para>
/// <b>There are two layers, and the top layer makes the answer independent of arrival order.</b> A
/// mark is a push and then, later, a catalogue that contains it. Meanwhile a read of the whole
/// catalogue is often already in flight (a phone refreshes its listing behind the wall it has just
/// drawn) and will answer with a listing that was true before the tap. So a mark made here is held
/// OVER whatever catalogue arrives, and is dropped only when a catalogue comes back that says the
/// same thing. Whichever order the push and the read arrive in, the listener's own last word stands.
/// </para>
/// </summary>
public sealed class FavoriteMarks
{
    /// <summary>Guards both collections. They are read whenever a heart is drawn, and written by a tap
    /// and by every catalogue that arrives, which can happen on different threads.</summary>
    private readonly object _gate = new();

    /// <summary>The marked songs of the catalogue in hand, and the stamp each one carried. Only the
    /// marked ones: an unmarked song is an absence, which is what the catalogue says about the other
    /// thousands. Replaced whole by <see cref="Read"/>, never edited.</summary>
    private Dictionary<string, long> _catalog = new(StringComparer.Ordinal);

    /// <summary>What has been marked or unmarked HERE since that catalogue was read, and when. Kept
    /// only until a catalogue comes back that agrees.</summary>
    private readonly Dictionary<string, (bool On, long At)> _mine = new(StringComparer.Ordinal);

    /// <summary>Raised whenever any mark changes: a tap, or a catalogue that arrives with a mark made
    /// somewhere else. It is raised on the thread that made the change. A listener that draws must
    /// return to its own thread itself.</summary>
    public event Action? Changed;

    /// <summary>
    /// Reads the marks from a catalogue that has just arrived.
    ///
    /// <para>What this device has said since then stands over it, unless the catalogue agrees. A
    /// listing read before a tap must not undo the tap, and a listing that has caught up leaves this
    /// device with nothing more to say. A mark that the listing disagrees with is not resolved either
    /// way: the push that made it may not be in this listing yet, and the listener's last word is the
    /// better guess until a listing shows otherwise.</para>
    /// </summary>
    public void Read(IEnumerable<CloudTrack> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        var catalog = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var track in tracks)
        {
            if (track.Tags?.Favorite == true && !string.IsNullOrEmpty(track.RpmId))
                catalog[track.RpmId] = track.Tags.FavoritedAt;
        }

        lock (_gate)
        {
            _catalog = catalog;

            foreach (var id in _mine.Where(mine => catalog.ContainsKey(mine.Key) == mine.Value.On)
                                    .Select(mine => mine.Key)
                                    .ToList())
            {
                _mine.Remove(id);
            }
        }

        Changed?.Invoke();
    }

    /// <summary>Marks a song, or clears the mark. This is the listener's own word, and it outranks the
    /// catalogue until one arrives that says the same thing.</summary>
    /// <param name="at">When it was marked, for the order of the favourites list. Zero when the mark
    /// is being cleared, because an unmarked song is in no list to be ordered.</param>
    public void Note(string rpmId, bool favorite, long at)
    {
        ArgumentException.ThrowIfNullOrEmpty(rpmId);

        lock (_gate)
            _mine[rpmId] = (favorite, favorite ? at : 0);

        Changed?.Invoke();
    }

    /// <summary>Whether this song is loved. The question every heart asks.</summary>
    public bool IsMarked(string rpmId)
    {
        if (string.IsNullOrEmpty(rpmId))
            return false;

        lock (_gate)
            return _mine.TryGetValue(rpmId, out var mine) ? mine.On : _catalog.ContainsKey(rpmId);
    }

    /// <summary>
    /// The mark and its stamp together: what a caller has to put back when a push it made is refused.
    ///
    /// <para>Take it before the push and do not work it out afterwards. "The other state" is not the
    /// same thing as what was there, and a song marked twice from two screens would otherwise be put
    /// back to a state it was never in.</para>
    /// </summary>
    public (bool On, long At) MarkOf(string rpmId)
    {
        if (string.IsNullOrEmpty(rpmId))
            return (false, 0);

        lock (_gate)
        {
            if (_mine.TryGetValue(rpmId, out var mine))
                return mine;

            return _catalog.TryGetValue(rpmId, out var at) ? (true, at) : (false, 0);
        }
    }

    /// <summary>When this song was marked, for the order of the generated list (newest first). This
    /// device's stamp is used where it has one: a song marked a moment ago belongs at the top of that
    /// list and not at the bottom under a catalogue that has not been read since.</summary>
    public long MarkedAt(string rpmId) => MarkOf(rpmId).At;

    /// <summary>Throws everything away, on signing out. The marks belong to the library they were made
    /// in, and the next listener to hold this phone must not be told which songs the last one loved.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _catalog = new Dictionary<string, long>(StringComparer.Ordinal);
            _mine.Clear();
        }

        Changed?.Invoke();
    }
}
