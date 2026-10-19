namespace RPM.Core.Splitting;

/// <summary>
/// One track as the editor holds it: its times, its name, where its boundaries came from, and whether
/// it is ticked for recognition. It also has an <see cref="Id"/> of its own.
///
/// <para>The id is the key. A track's position changes while a record is being cut up. If you drop one
/// track, every track after it moves up a place. So a position cannot name a track. The id stays the
/// same for every edit that does not destroy the track. This keeps a row, a highlight and a playing
/// preview attached to the right piece of music.</para>
/// </summary>
public sealed record EditorTrack(
    int Id,
    double Start,
    double End,
    string Title,
    string Artist,
    BoundarySource Source,
    bool Ticked)
{
    public double Length => End - Start;

    public TrackSpan Span => new(Start, End);
}

/// <summary>
/// The whole split screen as one value: the tracks, where the cursor is, what is selected, which
/// track is being worked on, and which one is playing.
///
/// <para>Every edit is a method here that returns the next state, or <c>null</c> when the edit is
/// refused. For example, there is nothing to split at the cursor, or a selection is too short to be a
/// track. Nothing is changed in place, and nothing is announced. The screen applies a new state and
/// redraws itself from it. So the picture, the grid and the plan cannot drift apart. They are three
/// views of one value. Separate copies of the same facts, kept in step by hand at each call site,
/// cause bugs. Examples are a row that shows the wrong track, or a play button that does not clear.</para>
///
/// <para>Times only. Nothing here knows about XAML, playback devices or files. It is arithmetic over
/// <see cref="TrackRegions"/>, so it can be tested fully without any of those.</para>
/// </summary>
public sealed record SplitEditorState
{
    public static readonly SplitEditorState Empty = new();

    /// <summary>The tracks, in order, non-overlapping. Every edit keeps them that way.</summary>
    public IReadOnlyList<EditorTrack> Tracks { get; init; } = [];

    /// <summary>Length of the whole recording. Every edit is clamped to this far edge.</summary>
    public double TotalSeconds { get; init; }

    /// <summary>Where the next split goes, and where a play starts from.</summary>
    public double Cursor { get; init; }

    /// <summary>The stretch marked out on the waveform, or null.</summary>
    public (double From, double To)? Selection { get; init; }

    /// <summary>The track being worked on. It is highlighted in both views, and the split, combine and
    /// drop tools act on it. 0 when the cursor is in the space between two tracks.</summary>
    public int CurrentId { get; init; }

    /// <summary>The track a preview is playing, or 0 when what is playing is not a whole track (a
    /// selection, or a stretch from the cursor onward).</summary>
    public int PlayingId { get; init; }

    /// <summary>The stretch that the preview belongs to. Moving the needle inside it is a seek. Moving
    /// it outside means that a different stretch is being previewed.</summary>
    public (double From, double To)? PlayRegion { get; init; }

    /// <summary>The shortest track that an edit may make. A person who places a cut by eye sees the
    /// interlude they mean to keep, so this is one second. The detector uses a minute. That is right
    /// for a guess, but wrong for a decision.</summary>
    public double MinLength { get; init; } = TrackRegions.MinManualSeconds;

    /// <summary>The next identity to hand out. This is private. Ids are the one thing a caller may not
    /// choose, or two tracks could claim to be the same track.</summary>
    private int NextId { get; init; } = 1;

    // ── reading it ──

    public int Count => Tracks.Count;

    public IReadOnlyList<TrackSpan> Spans => Tracks.Select(t => t.Span).ToList();

    public int IndexOf(int id)
    {
        for (int i = 0; i < Tracks.Count; i++)
            if (Tracks[i].Id == id)
                return i;
        return -1;
    }

    public EditorTrack? Track(int id)
    {
        int index = IndexOf(id);
        return index < 0 ? null : Tracks[index];
    }

    public int CurrentIndex => IndexOf(CurrentId);

    public EditorTrack? Current => Track(CurrentId);

    public EditorTrack? Playing => Track(PlayingId);

    /// <summary>The track a time falls in, or -1 in the space between two.</summary>
    public int IndexAt(double seconds) => TrackRegions.IndexAt(Spans, seconds);

    public bool HasSelection => Selection is { } s && s.To - s.From > 0.05;

    /// <summary>The id at a position, or 0. This turns what the waveform reports (indexes) into what
    /// everything else here uses (identities).</summary>
    public int IdAt(int index) => index >= 0 && index < Tracks.Count ? Tracks[index].Id : 0;

    // ── the cursor, the selection, the highlight ──

    // Each of these returns the same state when nothing actually moved. So the screen can tell a real
    // change from a repeat by reference alone. A scrub reports a hundred cursor positions a second,
    // and most of them are the position it already has.

    public SplitEditorState WithCursor(double seconds)
    {
        double at = Math.Clamp(seconds, 0, Math.Max(0, TotalSeconds));
        return Math.Abs(at - Cursor) < 1e-9 ? this : this with { Cursor = at };
    }

    public SplitEditorState WithSelection((double From, double To)? selection)
    {
        var span = selection is { } s && s.To < s.From ? (s.To, s.From) : selection;
        return span.Equals(Selection) ? this : this with { Selection = span };
    }

    public SplitEditorState WithCurrent(int id)
    {
        int next = IndexOf(id) >= 0 ? id : 0;
        return next == CurrentId ? this : this with { CurrentId = next };
    }

    public SplitEditorState WithCurrentIndex(int index) => WithCurrent(IdAt(index));

    public SplitEditorState WithPlaying(int id, (double From, double To)? region)
    {
        int next = IndexOf(id) >= 0 ? id : 0;
        return next == PlayingId && region.Equals(PlayRegion)
            ? this
            : this with { PlayingId = next, PlayRegion = region };
    }

    public SplitEditorState NotPlaying =>
        PlayingId == 0 && PlayRegion is null ? this : this with { PlayingId = 0, PlayRegion = null };

    // ── names and ticks ──

    public SplitEditorState Rename(int id, string? title, string? artist)
    {
        int index = IndexOf(id);
        if (index < 0)
            return this;
        var track = Tracks[index];
        title ??= "";
        artist ??= "";
        if (track.Title == title && track.Artist == artist)
            return this;
        return Replace(index, track with { Title = title, Artist = artist });
    }

    public SplitEditorState Tick(int id, bool ticked)
    {
        int index = IndexOf(id);
        if (index < 0 || Tracks[index].Ticked == ticked)
            return this;
        return Replace(index, Tracks[index] with { Ticked = ticked });
    }

    public SplitEditorState TickAll(bool ticked) =>
        this with { Tracks = Tracks.Select(t => t.Ticked == ticked ? t : t with { Ticked = ticked }).ToList() };

    public IReadOnlyList<EditorTrack> TickedTracks => Tracks.Where(t => t.Ticked).ToList();

    public bool AllTicked => Tracks.Count > 0 && Tracks.All(t => t.Ticked);

    private SplitEditorState Replace(int index, EditorTrack track)
    {
        var list = Tracks.ToList();
        list[index] = track;
        return this with { Tracks = list };
    }

    // ── the edits ───────────────────────────────────────────────────────────────────────────
    //
    // Each returns the next state, or null when the edit does not apply. Refusing by returning null,
    // instead of applying part of the edit, lets the screen say why nothing happened.

    /// <summary>
    /// Moves one edge of one track: a marker drag, or a time typed into the grid. Names and
    /// identities do not change. This changes where a track is, not which track it is. The edge that
    /// moved takes <paramref name="source"/>. So does the neighbour's edge, if the two were one
    /// boundary. A drag or a typed time gives a hand-made boundary. A release's own length, applied to
    /// the row, gives an album-timed boundary.
    /// </summary>
    public SplitEditorState? MoveEdge(int index, bool isEnd, double seconds,
        BoundarySource source = BoundarySource.Manual)
    {
        if (index < 0 || index >= Tracks.Count)
            return null;

        var spans = Spans.ToList();
        bool linked = isEnd ? TrackRegions.EndIsLinked(spans, index) : TrackRegions.StartIsLinked(spans, index);
        TrackRegions.MoveEdge(spans, index, isEnd, seconds, MinLength, TotalSeconds);

        int partner = isEnd ? index + 1 : index - 1;
        var list = new List<EditorTrack>(Tracks.Count);
        for (int i = 0; i < Tracks.Count; i++)
        {
            var track = Tracks[i] with { Start = spans[i].Start, End = spans[i].End };
            if (i == index || (linked && i == partner))
                track = track with { Source = source };
            list.Add(track);
        }
        return this with { Tracks = list };
    }

    /// <summary>Cuts the track at a time into two. The front half is the same track and keeps its name.
    /// The back half is new, has no name, and becomes the track being worked on.</summary>
    public SplitEditorState? SplitAt(double seconds)
    {
        var spans = Spans.ToList();
        int at = TrackRegions.SplitAt(spans, seconds, MinLength);
        if (at < 0)
            return null;

        var list = Tracks.ToList();
        list[at - 1] = list[at - 1] with { End = spans[at - 1].End, Source = BoundarySource.Manual };
        list.Insert(at, new EditorTrack(NextId, spans[at].Start, spans[at].End, "", "",
            BoundarySource.Manual, Ticked: false));
        return (this with { Tracks = list, NextId = NextId + 1, CurrentId = NextId }).Settle();
    }

    /// <summary>Joins a track to the one after it, including the silence between them. The first
    /// track's name stands, since a second title would have nowhere to go.</summary>
    public SplitEditorState? Combine(int index)
    {
        if (index < 0 || index + 1 >= Tracks.Count)
            return null;

        var list = Tracks.ToList();
        list[index] = list[index] with { End = list[index + 1].End, Source = BoundarySource.Manual };
        list.RemoveAt(index + 1);
        return (this with { Tracks = list, CurrentId = list[index].Id }).Settle();
    }

    /// <summary>Throws a track away. The audio it covered is in no file at all. This is the right tool
    /// for a detected lead-in, a locked groove or a side of applause.</summary>
    public SplitEditorState? Drop(int index)
    {
        if (index < 0 || index >= Tracks.Count)
            return null;

        var list = Tracks.ToList();
        list.RemoveAt(index);
        // The position stays, so a run of deletions works on one spot instead of jumping around.
        int current = list.Count == 0 ? 0 : list[Math.Min(index, list.Count - 1)].Id;
        return (this with { Tracks = list, CurrentId = current }).Settle();
    }

    /// <summary>Makes the marked stretch a track of its own. This is for a song that the detector
    /// missed. The stretch's audio is taken back from the tracks that covered it.</summary>
    public SplitEditorState? Carve(double from, double to)
    {
        var spans = Spans.ToList();
        int at = TrackRegions.Carve(spans, from, to, MinLength);
        if (at < 0)
            return null;

        var next = Rebuild(spans);
        return (next with { CurrentId = next.IdAt(at), Selection = null }).Settle();
    }

    /// <summary>Removes every boundary that the selection covers. The tracks it touches become one. This
    /// is the tool for a side that a noisy passage split into six pieces.</summary>
    public SplitEditorState? ClearBoundariesIn(double from, double to)
    {
        var spans = Spans.ToList();
        if (!TrackRegions.RemoveBoundariesIn(spans, from, to))
            return null;

        var next = Rebuild(spans);
        int at = Math.Max(0, TrackRegions.IndexAt(spans, from));
        return (next with { CurrentId = next.IdAt(at), Selection = null }).Settle();
    }

    /// <summary>
    /// Lays a new set of spans over the tracks. Names are carried across by one rule that is right in
    /// every case: <b>a name follows the track that still starts where it started</b>. Two joined
    /// tracks keep the first one's name. A track carved in two keeps its name on the front half. A
    /// piece that starts somewhere new is a new track, with a new id, no name and hand-made boundaries.
    /// A track whose end also stayed put keeps where its boundaries came from as well.
    /// </summary>
    private SplitEditorState Rebuild(IReadOnlyList<TrackSpan> after)
    {
        var owners = TrackRegions.MatchByStart(Spans, after);
        var list = new List<EditorTrack>(after.Count);
        var claimed = new HashSet<int>();
        int nextId = NextId;

        for (int i = 0; i < after.Count; i++)
        {
            // One old track cannot become two. The first piece that starts where the old track started
            // gets it.
            var old = owners[i] >= 0 && claimed.Add(owners[i]) ? Tracks[owners[i]] : null;
            if (old is null)
            {
                list.Add(new EditorTrack(nextId++, after[i].Start, after[i].End, "", "",
                    BoundarySource.Manual, Ticked: false));
                continue;
            }
            bool sameEnd = Math.Abs(old.End - after[i].End) < TrackRegions.LinkEpsilon;
            list.Add(old with
            {
                Start = after[i].Start,
                End = after[i].End,
                Source = sameEnd ? old.Source : BoundarySource.Manual,
            });
        }
        return this with { Tracks = list, NextId = nextId };
    }

    /// <summary>Makes the state consistent after an edit has removed something. A highlight or a preview
    /// that points at a track that no longer exists is cleared. The cursor is kept within the
    /// recording. Every structural edit ends here, so no caller has to remember to do this.</summary>
    private SplitEditorState Settle()
    {
        var state = this;
        if (state.CurrentId != 0 && state.IndexOf(state.CurrentId) < 0)
            state = state with { CurrentId = 0 };
        if (state.PlayingId != 0 && state.IndexOf(state.PlayingId) < 0)
            state = state with { PlayingId = 0, PlayRegion = null };

        double cursor = Math.Clamp(state.Cursor, 0, Math.Max(0, state.TotalSeconds));
        if (Math.Abs(cursor - state.Cursor) > 1e-9)
            state = state with { Cursor = cursor };
        return state;
    }

    // ── the plan, in and out ──

    /// <summary>Takes a detected plan (or a re-detected or renamed one) as the tracks to edit. Every
    /// track gets a new id. This is a different reading of the record, so nothing from the old one (a
    /// highlight, a preview or a tick) carries over.</summary>
    public SplitEditorState FromPlan(SplitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        int nextId = NextId;
        var list = plan.Tracks
            .Select(t => new EditorTrack(nextId++, t.StartSeconds, t.EndSeconds, t.Title ?? "",
                t.Artist ?? "", t.Source, Ticked: false))
            .ToList();

        // The place in the record is kept where a track still exists there. Re-detecting while working
        // on the fourth track leaves you on the fourth track, not back at the top. If nothing was being
        // worked on, nothing is now. A fresh detection highlights no row.
        int index = CurrentIndex;
        int current = list.Count == 0 || index < 0 ? 0 : list[Math.Min(index, list.Count - 1)].Id;
        return (this with
        {
            Tracks = list,
            NextId = nextId,
            TotalSeconds = plan.TotalSeconds > 0 ? plan.TotalSeconds : TotalSeconds,
            CurrentId = current,
            Selection = null,
            PlayingId = 0,
            PlayRegion = null,
        }).Settle();
    }

    /// <summary>The tracks as the tracks of a plan. They are numbered by position. A track that an edit
    /// shortened below one second is dropped, so no sliver is written out.</summary>
    public IReadOnlyList<SplitTrack> ToPlanTracks()
    {
        var list = new List<SplitTrack>(Tracks.Count);
        foreach (var track in Tracks)
        {
            if (track.Length < 1)
                continue;
            list.Add(new SplitTrack(
                track.Start, track.End,
                string.IsNullOrWhiteSpace(track.Title) ? null : track.Title,
                string.IsNullOrWhiteSpace(track.Artist) ? null : track.Artist,
                (uint)(list.Count + 1),
                Confidence: 1.0,
                track.Source));
        }
        return list;
    }

    /// <summary>Each track's length, in order. A candidate release's listing is scored against
    /// these.</summary>
    public IReadOnlyList<double> Lengths => Tracks.Select(t => t.Length).ToList();

    /// <summary>
    /// Where an adopted release's tracks would fall on this recording: its lengths laid end to end,
    /// starting at the first track's start. These are marks to read against, never cuts to take.
    /// Adopting a release names the record. It does not re-time it. A catalogue's seconds are rounded
    /// and belong to another pressing, while the boundaries here were measured on this transfer.
    /// </summary>
    /// <remarks>The guides start at the first track, not at zero. A recorded side does not begin where
    /// the music begins, because the needle drop and the lead-in groove come first. A listing that
    /// gives no time for a track cannot say where the next one starts. So the guides stop there,
    /// instead of continuing from a guess.</remarks>
    public IReadOnlyList<double> AlbumGuides(IReadOnlyList<double>? albumLengths)
    {
        if (albumLengths is not { Count: > 0 } lengths || Tracks.Count == 0)
            return [];

        double at = Tracks[0].Start;
        var guides = new List<double>(lengths.Count + 1) { at };
        foreach (var length in lengths)
        {
            if (length <= 0)
                break;
            at += length;
            if (at > TotalSeconds)
                break;
            guides.Add(at);
        }
        return guides;
    }
}
