namespace RPM.Core.Splitting;

/// <summary>A track as a pair of times on the recording. Editing the waveform edits these two
/// numbers.</summary>
public readonly record struct TrackSpan(double Start, double End)
{
    public double Length => End - Start;

    public bool Contains(double seconds) => seconds >= Start && seconds <= End;
}

/// <summary>
/// The arithmetic behind the split editor, kept out of the UI so it can be reasoned about and tested
/// on its own.
///
/// <para>Tracks are <b>regions</b>, not a run of shared boundaries. A record's tracks have silence
/// between them that belongs to neither. The needle is still in the groove, but nothing there is
/// music. So a track owns its own start and its own end, and the gap between two tracks is real.
/// An album file usually has no gaps. Its tracks touch each other, and there the two edges that meet
/// are one boundary. That is the whole rule: <b>edges that coincide move together, and edges that do
/// not coincide move alone</b>. Dragging the end of track 3 in a gapless rip also moves the start of
/// track 4. Dragging it on a record eats into the silence and leaves track 4 where it is.</para>
///
/// <para>Nothing cascades. A drag stops at the next track and does not push it along. With direct
/// manipulation, only the thing under the pointer may move. Otherwise the user cannot place a cut
/// without checking what happened three tracks later.</para>
/// </summary>
public static class TrackRegions
{
    /// <summary>Two edges this close are the same boundary. This is a little more than one video frame.
    /// It is fine enough that a hand-placed cut a tenth of a second away is still its own edge. It is
    /// coarse enough to survive the rounding that a time in "m:ss" text goes through.</summary>
    public const double LinkEpsilon = 0.02;

    /// <summary>The shortest track that a hand edit may make. The detector uses a minute, because a
    /// one-minute gap in a detection is almost always a false split. A person who places a cut by eye
    /// can see a twenty-second interlude, and means it.</summary>
    public const double MinManualSeconds = 1.0;

    /// <summary>The track that <paramref name="seconds"/> falls in, or -1 in a gap or past the
    /// end.</summary>
    public static int IndexAt(IReadOnlyList<TrackSpan> tracks, double seconds)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        for (int i = 0; i < tracks.Count; i++)
            if (tracks[i].Contains(seconds))
                return i;
        return -1;
    }

    /// <summary>The track nearest to <paramref name="seconds"/>: the one it is inside, or else the closer
    /// neighbour across a gap. -1 only when there are no tracks at all.</summary>
    public static int NearestIndex(IReadOnlyList<TrackSpan> tracks, double seconds)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        int inside = IndexAt(tracks, seconds);
        if (inside >= 0 || tracks.Count == 0)
            return inside;

        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < tracks.Count; i++)
        {
            double distance = seconds < tracks[i].Start ? tracks[i].Start - seconds
                : seconds > tracks[i].End ? seconds - tracks[i].End
                : 0;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }

    /// <summary>True when this track's end and the next one's start are the same boundary.</summary>
    public static bool EndIsLinked(IReadOnlyList<TrackSpan> tracks, int index)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        return index >= 0 && index + 1 < tracks.Count
            && Math.Abs(tracks[index + 1].Start - tracks[index].End) <= LinkEpsilon;
    }

    /// <summary>True when this track's start and the previous one's end are the same boundary.</summary>
    public static bool StartIsLinked(IReadOnlyList<TrackSpan> tracks, int index) =>
        EndIsLinked(tracks, index - 1);

    /// <summary>
    /// Moves one edge of one track. This is the single edit that a marker drag makes. A linked edge
    /// carries its partner with it; an unlinked one stops where the neighbour begins. Returns where
    /// the edge actually landed, which is what the drag draws.
    /// </summary>
    public static double MoveEdge(
        List<TrackSpan> tracks, int index, bool isEnd, double seconds, double minLen, double total)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (index < 0 || index >= tracks.Count)
            return seconds;

        minLen = Math.Max(0.05, minLen);
        var track = tracks[index];

        if (isEnd)
        {
            bool linked = EndIsLinked(tracks, index);
            double lower = track.Start + minLen;
            double upper = index + 1 < tracks.Count
                ? (linked ? tracks[index + 1].End - minLen : tracks[index + 1].Start)
                : total;
            double value = Math.Clamp(seconds, lower, Math.Max(lower, Math.Min(upper, total)));

            tracks[index] = track with { End = value };
            if (linked)
                tracks[index + 1] = tracks[index + 1] with { Start = value };
            return value;
        }
        else
        {
            bool linked = StartIsLinked(tracks, index);
            double lower = index > 0
                ? (linked ? tracks[index - 1].Start + minLen : tracks[index - 1].End)
                : 0;
            double upper = track.End - minLen;
            double value = Math.Clamp(seconds, Math.Max(0, lower), Math.Max(Math.Max(0, lower), upper));

            tracks[index] = track with { Start = value };
            if (linked)
                tracks[index - 1] = tracks[index - 1] with { End = value };
            return value;
        }
    }

    /// <summary>
    /// Cuts the track at <paramref name="seconds"/> in two, where the two halves meet. Returns the
    /// index of the new second half. Returns -1 when the point is in a gap or when a piece would be
    /// shorter than <paramref name="minLen"/>.
    /// </summary>
    public static int SplitAt(List<TrackSpan> tracks, double seconds, double minLen)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        int index = IndexAt(tracks, seconds);
        if (index < 0)
            return -1;

        var track = tracks[index];
        if (seconds - track.Start < minLen || track.End - seconds < minLen)
            return -1;

        tracks[index] = track with { End = seconds };
        tracks.Insert(index + 1, new TrackSpan(seconds, track.End));
        return index + 1;
    }

    /// <summary>Joins a track to the one after it, including the silence between them. The boundary
    /// between the two disappears, but the audio does not.</summary>
    public static bool Combine(List<TrackSpan> tracks, int index)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (index < 0 || index + 1 >= tracks.Count)
            return false;

        tracks[index] = new TrackSpan(tracks[index].Start, tracks[index + 1].End);
        tracks.RemoveAt(index + 1);
        return true;
    }

    /// <summary>
    /// Removes every boundary inside <c>[from, to]</c>. The tracks it touches become one, running from
    /// the first one's start to the last one's end. Returns false when the range touches fewer than two
    /// tracks, so there is no boundary to remove.
    /// </summary>
    public static bool RemoveBoundariesIn(List<TrackSpan> tracks, double from, double to)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (to < from)
            (from, to) = (to, from);

        var touched = new List<int>();
        for (int i = 0; i < tracks.Count; i++)
            if (tracks[i].Start <= to && tracks[i].End >= from)
                touched.Add(i);
        if (touched.Count < 2)
            return false;

        int first = touched[0], last = touched[^1];
        tracks[first] = new TrackSpan(tracks[first].Start, tracks[last].End);
        for (int i = last; i > first; i--)
            tracks.RemoveAt(i);
        return true;
    }

    /// <summary>Removes a track outright. The audio it covered belongs to no track afterwards. This is
    /// how to throw away a lead-in that was detected as a track.</summary>
    public static bool Remove(List<TrackSpan> tracks, int index)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (index < 0 || index >= tracks.Count)
            return false;
        tracks.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// Makes a new track out of a stretch that belongs to no track, such as a song the detector missed.
    /// The stretch is marked out on the waveform. Returns where the track landed in the order, or -1
    /// when the stretch is too short or runs into a track that is already there.
    /// </summary>
    public static int Insert(List<TrackSpan> tracks, double from, double to, double minLen)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (to < from)
            (from, to) = (to, from);
        if (to - from < minLen)
            return -1;

        int at = tracks.Count;
        for (int i = 0; i < tracks.Count; i++)
        {
            // Overlapping an existing track is not an insert. The user is editing that track's edges.
            if (tracks[i].Start < to - LinkEpsilon && tracks[i].End > from + LinkEpsilon)
                return -1;
            if (tracks[i].Start >= to - LinkEpsilon)
            {
                at = i;
                break;
            }
        }
        tracks.Insert(at, new TrackSpan(from, to));
        return at;
    }

    /// <summary>
    /// For every span in <paramref name="after"/>, gives the index in <paramref name="before"/> of the
    /// track that still starts at the same instant. Gives -1 for a piece that starts somewhere new.
    ///
    /// <para>A track is the same track when it starts where it started. Two joined tracks keep the
    /// first one's name, because the join starts where that track did. A track split in two keeps its
    /// name on the front half and leaves the back half without one. A stretch carved out of the middle
    /// of a track is new, and this rule reports it as new. One rule gives the right answer in each
    /// case.</para>
    /// </summary>
    public static int[] MatchByStart(IReadOnlyList<TrackSpan> before, IReadOnlyList<TrackSpan> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        var owners = new int[after.Count];
        for (int i = 0; i < after.Count; i++)
        {
            owners[i] = -1;
            for (int j = 0; j < before.Count; j++)
            {
                if (Math.Abs(before[j].Start - after[i].Start) > LinkEpsilon)
                    continue;
                owners[i] = j;
                break;
            }
        }
        return owners;
    }

    /// <summary>
    /// Takes a stretch of the recording out of the track or tracks it is part of, and makes it a track
    /// of its own. This is for a song that the detector did not find, drawn straight onto the waveform.
    /// The tracks it overlaps keep whatever falls outside the stretch. Any piece that ends up shorter
    /// than <paramref name="minLen"/> is dropped, not left as a sliver. Returns the new track's index,
    /// or -1 when the stretch is too short.
    /// </summary>
    public static int Carve(List<TrackSpan> tracks, double from, double to, double minLen)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (to < from)
            (from, to) = (to, from);
        if (to - from < minLen)
            return -1;

        var kept = new List<TrackSpan>(tracks.Count + 2);
        foreach (var track in tracks)
        {
            if (track.End <= from || track.Start >= to)
            {
                kept.Add(track);
                continue;
            }
            if (track.Start < from)
            {
                var head = new TrackSpan(track.Start, from);
                if (head.Length >= minLen)
                    kept.Add(head);
            }
            if (track.End > to)
            {
                var tail = new TrackSpan(to, track.End);
                if (tail.Length >= minLen)
                    kept.Add(tail);
            }
        }
        kept.Add(new TrackSpan(from, to));
        kept.Sort((a, b) => a.Start.CompareTo(b.Start));

        tracks.Clear();
        foreach (var span in kept)
            tracks.Add(span);
        return kept.FindIndex(s => s.Start == from && s.End == to);
    }
}
