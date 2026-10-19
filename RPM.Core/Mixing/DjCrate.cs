namespace RPM.Core.Mixing;

/// <summary>
/// The crate: the records the DJ has brought to the set. It holds what is left to play and what has
/// already been played. The DJ picks the next record from it. The analyser keeps filling in what it
/// learns about the records not yet reached. That is why the crate is thread-safe, and why analysis may
/// finish after the set has started. A record that is not analysed when its turn comes gets a plain
/// blend instead of a beat-match.
/// </summary>
public sealed class DjCrate
{
    private readonly object _gate = new();
    private readonly List<MixTrack> _remaining;
    private readonly List<MixTrack> _played = [];

    /// <param name="plan">Whether the DJ picks what comes next. Set false to keep the crate's own
    /// order. A playlist played on a phone keeps the order it was written in, because the person who
    /// wrote it chose that order. The mixing is the same either way: the same cue points, the same
    /// tempo pull and the same changeover. Only the choice of the next record is given up.</param>
    public DjCrate(IEnumerable<MixTrack> tracks, bool plan = true)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        _remaining = [.. Mixable(tracks)];
        _plan = plan;
    }

    /// <summary>
    /// The records a set can use: everything under <see cref="TransitionPlanner.MaxMixableSeconds"/>.
    ///
    /// <para>Longer items are not tracks. They are recorded sets, one side of a live album, or a full
    /// mix. They have no outro to leave on and no intro to arrive over. Their tempo reading is the
    /// average of many different records. They are dropped here, because every changeover into or out
    /// of one would be planned from numbers that describe nothing.</para>
    ///
    /// <para>If that rule removes every record, the long items are kept. An empty crate would mean the
    /// set never starts. The rule decides what to leave out of a choice, so when there is no choice
    /// there is nothing to leave out.</para>
    /// </summary>
    private static IReadOnlyList<MixTrack> Mixable(IEnumerable<MixTrack> tracks)
    {
        var all = tracks.ToList();
        var mixable = all
            .Where(t => t.DurationSeconds <= 0 || t.DurationSeconds <= TransitionPlanner.MaxMixableSeconds)
            .ToList();

        return mixable.Count > 0 ? mixable : all;
    }

    private readonly bool _plan;

    /// <summary>How many records are left unplayed.</summary>
    public int RemainingCount { get { lock (_gate) return _remaining.Count; } }

    public bool IsEmpty => RemainingCount == 0;

    /// <summary>
    /// How many of the records left have been read.
    ///
    /// <para>This tells whether the mix can move on. The planner does not reach for an unread record
    /// while a read one is left (see <see cref="DjSetPlanner.Next"/>). So one analysed record means the
    /// next changeover can be a beat-match. None means the next record can only be blended. The
    /// transport reads this through <see cref="Dj.IsNextReady"/>.</para>
    /// </summary>
    public int AnalyzedRemaining
    {
        get { lock (_gate) return _remaining.Count(t => t.IsAnalyzed); }
    }

    /// <summary>Everything left, in crate order. This is a snapshot, so it is safe to
    /// enumerate.</summary>
    public IReadOnlyList<MixTrack> Remaining { get { lock (_gate) return [.. _remaining]; } }

    /// <summary>The whole set as it will be played so far: what has gone, in order.</summary>
    public IReadOnlyList<MixTrack> Played { get { lock (_gate) return [.. _played]; } }

    /// <summary>
    /// Puts another record in the crate, at the back.
    ///
    /// <para>
    /// A crate is normally filled once, from a library whose files already exist. A phone's crate is
    /// different. Records arrive over the network while the set is playing. A record named before its
    /// audio has arrived would open an empty file. The DJ reads that as "this record is gone" and skips
    /// it, so a set built this way runs out after whatever was downloaded when it started.
    /// </para>
    ///
    /// <para>Ignored when the record is already in the crate or has been played. A fetcher that offers
    /// the same file twice therefore cannot make the set play it twice.</para>
    /// </summary>
    public void Add(MixTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);

        lock (_gate)
        {
            if (_remaining.Any(t => Same(t, track)) || _played.Any(t => Same(t, track)))
                return;

            _remaining.Add(track);
        }
    }

    /// <summary>
    /// The analyser has finished with a record. Swaps the crate's copy for the analysed one. Does
    /// nothing when the record has already been played, because its analysis is not needed.
    /// </summary>
    public void Update(MixTrack analyzed)
    {
        ArgumentNullException.ThrowIfNull(analyzed);
        lock (_gate)
        {
            int at = _remaining.FindIndex(t => string.Equals(t.Path, analyzed.Path, StringComparison.Ordinal));
            if (at >= 0)
                _remaining[at] = analyzed;
        }
    }

    /// <summary>
    /// Takes the best follow-on to <paramref name="playing"/> out of the crate, or null when the crate
    /// is empty. <paramref name="playingBpm"/> is the tempo actually coming out of the speakers. The
    /// next record has to match that tempo, not the tempo printed on the outgoing one.
    /// </summary>
    /// <param name="playingIntensity">How hard the playing record is hitting at the point where it is
    /// being mixed out. Null uses the track's average.</param>
    /// <param name="except"> Records that a person has turned down for this changeover, by path. See
    /// <see cref="Dj.RejectCued"/>.
    ///
    /// <para>Ignored when it would leave nothing to choose from. A rejection means "not this one, find
    /// me another". On the last record in the crate there is no other one. Honouring the rejection
    /// there would stop the set, and a set that stops is not what anybody asked for.</para>
    /// </param>
    public MixTrack? TakeNext(
        MixTrack? playing, double playingBpm, double? playingIntensity = null,
        IReadOnlyCollection<string>? except = null)
    {
        lock (_gate)
        {
            var next = Choose(playing, playingBpm, playingIntensity, except);
            if (next is null)
                return null;

            _remaining.Remove(next);
            _played.Add(next);
            return next;
        }
    }

    /// <summary>
    /// The same choice as <see cref="TakeNext"/>, but the record stays in the crate. This is what the
    /// DJ would play next.
    ///
    /// <para>Use it to show the answer before the answer is acted on. It must use the same arithmetic
    /// on the same inputs, not a second opinion that looks similar. That is why this method and
    /// <see cref="TakeNext"/> share one choice, with the removal done outside it. A screen that shows a
    /// record the set will not reach for is worse than a screen that shows nothing.</para>
    /// </summary>
    public MixTrack? PeekNext(
        MixTrack? playing, double playingBpm, double? playingIntensity = null,
        IReadOnlyCollection<string>? except = null)
    {
        lock (_gate)
            return Choose(playing, playingBpm, playingIntensity, except);
    }

    /// <summary>Picks the record that follows <paramref name="playing"/>. Called with <c>_gate</c> held
    /// by both public callers above.</summary>
    private MixTrack? Choose(
        MixTrack? playing, double playingBpm, double? playingIntensity,
        IReadOnlyCollection<string>? except)
    {
        if (_remaining.Count == 0)
            return null;

        var from = _remaining;
        if (except is { Count: > 0 })
        {
            var left = _remaining.Where(t => !except.Contains(t.Path)).ToList();
            if (left.Count > 0)
                from = left;
        }

        return _plan
            ? DjSetPlanner.Next(playing, playingBpm, from, playingIntensity) ?? from[0]
            : from[0];
    }

    /// <summary>
    /// Puts a record back in the crate unplayed. The DJ had cued it and then changed the plan. This
    /// happens when the set is skipped forward from a point where the choice does not fit. The record
    /// goes back at the front, so it stays a strong candidate without being forced.
    /// </summary>
    /// <param name="atFront">False puts it at the back instead. That is where a record that someone
    /// turned down belongs: still in the set, but not the obvious next record.</param>
    public void PutBack(MixTrack track, bool atFront = true)
    {
        ArgumentNullException.ThrowIfNull(track);
        lock (_gate)
        {
            _played.RemoveAll(t => string.Equals(t.Path, track.Path, StringComparison.Ordinal));
            if (_remaining.Any(t => string.Equals(t.Path, track.Path, StringComparison.Ordinal)))
                return;

            if (atFront)
                _remaining.Insert(0, track);
            else
                _remaining.Add(track);
        }
    }

    /// <summary>Records are the same record when they are the same file. This is the identity the crate
    /// matches on. See <see cref="Update"/>.</summary>
    private static bool Same(MixTrack a, MixTrack b)
        => string.Equals(a.Path, b.Path, StringComparison.Ordinal);

    /// <summary>Puts every played record back and restores the original order. This starts the set
    /// over, rather than building a new crate.</summary>
    public void Rewind(IEnumerable<MixTrack> original)
    {
        ArgumentNullException.ThrowIfNull(original);
        lock (_gate)
        {
            _remaining.Clear();
            _remaining.AddRange(original);
            _played.Clear();
        }
    }
}
