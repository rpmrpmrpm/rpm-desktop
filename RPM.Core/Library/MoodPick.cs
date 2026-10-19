namespace RPM.Core.Library;

/// <summary>
/// Which generated list the home banner shows: the mood that the hour suggests, cut to the kind of
/// music this listener plays at that hour.
///
/// <para><b>Two questions, each answered where it is known.</b> <see cref="MoodClock"/> says which
/// mood suits the hour. That is a general guess about people. The listening profile says which genres
/// this listener plays in that part of the day. That is a fact about them. The two are kept apart. A
/// listener who plays jazz late gets Jazz Club at midnight, and a listener who plays techno gets
/// Slow Machine.</para>
///
/// <para><b>And one day in seven, something else from the same group.</b> A banner that gives the same
/// answer every day is ignored after a while, because people stop looking at it. So on one day in
/// <see cref="OtherDayIn"/> the pick steps down its own ranking. Each such day steps further, so what
/// shows up is another cut of the same mood, not the same alternative every week. The day is chosen by
/// the date only, so the list does not change while someone is looking at it.</para>
/// </summary>
public static class MoodPick
{
    /// <summary>How often the banner shows something other than the usual answer: one day in seven.
    /// That is often enough that a month shows four such days, and rare enough that the card still
    /// looks like the usual one.</summary>
    public const int OtherDayIn = 7;

    /// <summary>
    /// The list to show, or null if this shelf offers none of the moods.
    /// </summary>
    /// <param name="moods">The moods that suit the hour, best first: <see cref="MoodClock.Order"/>.</param>
    /// <param name="genres">What this listener plays at this part of the day, most played first:
    /// <see cref="ListeningProfile.GenresAt"/>. Empty is normal. A new collection has no listening
    /// history, so the whole shelf's cut of the mood is shown.</param>
    /// <param name="offered">What this collection actually offers:
    /// <c>LibraryDatabase.OfferedGeneratedLists</c>. A cut that is not offered is not used, whatever the
    /// listening says, because it has too little music to be worth a row.</param>
    /// <param name="dayNumber">The day, as a number that goes up by one each day. It decides which days
    /// are the one in seven, and which alternative is shown.</param>
    public static SmartPlaylistId? For(
        IReadOnlyList<SmartPlaylistKind> moods,
        IReadOnlyList<GenreShare> genres,
        IReadOnlyCollection<SmartPlaylistId> offered,
        int dayNumber)
    {
        ArgumentNullException.ThrowIfNull(moods);
        ArgumentNullException.ThrowIfNull(genres);
        ArgumentNullException.ThrowIfNull(offered);

        foreach (var mood in moods)
        {
            var group = Group(mood, genres, offered);
            if (group.Count == 0)
                continue;

            return group[Which(group.Count, dayNumber)];
        }

        return null;
    }

    /// <summary>
    /// Every list this shelf offers for one mood, best first: the cuts of the mood that this listener
    /// plays, in order of play, followed by the whole shelf's cut.
    /// </summary>
    /// <remarks>
    /// The whole shelf's cut is last, not first. "Chill" is every quiet record on the shelf, and "Jazz
    /// Club" is the quiet jazz. A listener who plays jazz at night is better served by the smaller list.
    /// The whole shelf's cut stays in the group anyway. A listener with no genres still needs something
    /// to be offered, and it is what the one day in seven moves to when a mood has only one cut worth
    /// having.
    /// </remarks>
    public static IReadOnlyList<SmartPlaylistId> Group(
        SmartPlaylistKind mood,
        IReadOnlyList<GenreShare> genres,
        IReadOnlyCollection<SmartPlaylistId> offered)
    {
        ArgumentNullException.ThrowIfNull(genres);
        ArgumentNullException.ThrowIfNull(offered);

        var group = new List<SmartPlaylistId>();

        foreach (var share in genres)
        {
            if (share.Genre is not { Length: > 0 } genre)
                continue;

            var cut = SmartPlaylistId.Within(mood, genre);
            if (offered.Contains(cut))
                group.Add(cut);
        }

        var whole = SmartPlaylistId.WholeShelf(mood);
        if (offered.Contains(whole))
            group.Add(whole);

        return group;
    }

    /// <summary>
    /// Which of the group to show today.
    /// </summary>
    /// <remarks>
    /// Zero on six days in seven. On the seventh day it steps down the ranking. On each such day after
    /// that it steps one further, wrapping round the group. A listener who plays four kinds of music at
    /// night sees the second on one such day, the third on the next, and the fourth after that. A group
    /// of one has nowhere to step, so it keeps its only member. This follows from the arithmetic below,
    /// and is not a special case.
    /// </remarks>
    public static int Which(int groupSize, int dayNumber)
    {
        if (groupSize <= 1)
            return 0;

        // Math.Floor, so a negative day number still steps forwards rather than mirroring.
        var day = (int)Math.Floor(dayNumber / (double)OtherDayIn);
        if (dayNumber - (day * OtherDayIn) != 0)
            return 0;

        var step = ((day % (groupSize - 1)) + (groupSize - 1)) % (groupSize - 1);
        return 1 + step;
    }
}
