namespace RPM.Core.Library;

/// <summary>
/// A generated list that is not there yet, and why.
/// </summary>
/// <param name="Id">Which list.</param>
/// <param name="Gate">How far from being worth showing it is. <see cref="FeatureGate.Need"/> is what
/// the row says, because the three blockers want three different things from the listener.</param>
public readonly record struct LockedList(SmartPlaylistId Id, FeatureGate Gate);

/// <summary>Which of the lists that are not there yet are worth saying so about.
///
/// <para><b>The problem this solves is not which lists are empty, but how many of them to show.</b>
/// <see cref="SmartPlaylists.LeastToShow"/> already decides whether a list may draw. A list under its
/// floor is not drawn, so the listener sees a short pane and cannot tell whether that is all the app can
/// do or all their collection can answer. Saying so helps. Saying so about
/// every candidate would be worse: with the moods broken out by genre and by decade there are more than
/// forty candidates, and a pane of rows that are not there says less than a pane with three good rows in
/// it.</para>
/// </summary>
public static class LockedLists
{
    /// <summary>How many locked rows are drawn at once.
    ///
    /// <para>Three. That is enough to show that the lists are earned rather than absent, and few enough
    /// that the locked rows never outnumber the real ones on a shelf that has any. The value is a
    /// judgement, not a measured number. If it reads as too many on a small collection, change it here. It
    /// is set in no other place.</para>
    /// </summary>
    public const int AtOnce = 3;

    /// <summary>The locked lists worth drawing, closest to opening first.
    /// </summary>
    /// <param name="counts">How many songs each candidate list holds right now. Lists absent from this hold
    /// none. This is the same count the totals pass already computes for the lists that DO draw, so nothing
    /// extra is read to answer it.</param>
    /// <param name="measured">How many tracks the measuring pass has finished, and how many there are.
    /// While the pass is still walking, a mood list is blocked by the pass rather than by the collection.
    /// The two want different things said: one asks for more music, the other asks only for time.</param>
    /// <returns>At most <see cref="AtOnce"/>, ordered by how close each is. A list that is already open is
    /// never among them.</returns>
    public static IReadOnlyList<LockedList> Choose(
        IReadOnlyDictionary<SmartPlaylistId, int> counts,
        MeasuringProgress measured)
    {
        ArgumentNullException.ThrowIfNull(counts);

        var shut = new List<LockedList>();

        foreach (var (id, have) in counts)
        {
            int want = SmartPlaylists.LeastToShow(id);
            var gate = new FeatureGate(Need(id, measured), have, want);
            if (!gate.Open)
                shut.Add(new LockedList(id, gate));
        }

        // Closest first, by how much of the way there the collection is rather than by how many are
        // missing: "seven of ten" is nearer than "forty of sixty" although it is missing three and the
        // other is missing twenty.
        return [.. shut
            .OrderByDescending(l => l.Gate.Fraction)
            .ThenBy(l => l.Gate.ToGo)
            .ThenBy(l => l.Id.Kind)
            .Take(AtOnce)];
    }

    /// <summary>
    /// What is actually holding this list up.
    ///
    /// <para>The measuring pass takes precedence for the lists that depend on it. A mood list on a
    /// collection the pass has barely started is not short of music. Telling the listener to import
    /// more music when the app has not finished measuring what they already have would send them to
    /// work that changes nothing.</para>
    /// </summary>
    private static GateNeed Need(SmartPlaylistId id, MeasuringProgress measured) =>
        SmartPlaylists.NeedsMeasuring(id.Kind) && !measured.Enough
            ? GateNeed.Measured
            : id.Kind switch
            {
                // These read the listening log. More music will not fill them; playing something will.
                SmartPlaylistKind.OnRepeat
                    or SmartPlaylistKind.Rediscover
                    or SmartPlaylistKind.LateNight
                    or SmartPlaylistKind.FirstListens
                    or SmartPlaylistKind.KeepSkipping
                    or SmartPlaylistKind.MostPlayed
                    or SmartPlaylistKind.RecentlyPlayed
                    or SmartPlaylistKind.LovedAndForgotten
                    or SmartPlaylistKind.PlayedNotLoved
                    or SmartPlaylistKind.RightNow => GateNeed.Plays,

                _ => GateNeed.SongsInList,
            };
}

/// <summary>
/// How far the measuring pass has got through the collection.
/// </summary>
/// <param name="Measured">Tracks it has finished.</param>
/// <param name="Total">Tracks there are.</param>
public readonly record struct MeasuringProgress(int Measured, int Total)
{
    /// <summary>Whether the pass has covered enough of the collection that a mood list's emptiness is
    /// about the collection rather than about the pass.
    ///
    /// <para>Nine tenths, not all of it. The pass always leaves some files it cannot read. A check for full
    /// completion would blame the pass for every empty mood list on any collection with one broken
    /// file.</para>
    /// </summary>
    public bool Enough => Total <= 0 || Measured >= Total * 0.9;

    public double Fraction => Total <= 0 ? 1.0 : Math.Clamp((double)Measured / Total, 0.0, 1.0);
}
