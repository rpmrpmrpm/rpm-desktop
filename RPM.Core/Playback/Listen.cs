namespace RPM.Core.Playback;

/// <summary>
/// Whether something that was played counts as having been listened to.
///
/// <para>
/// Moving quickly through an album touches each track for a second or two. If those count as plays,
/// "most played" shows the tracks that were skipped past fastest. That is the opposite of what the feature
/// is for. The error is hard to spot, because the numbers look normal.
/// </para>
///
/// <para>
/// The rule is the one that music scrobbling services use. A listen counts when half the track has been
/// heard, or four minutes, whichever comes first. Half makes a two-minute single and a twenty-minute side
/// both fair. The four-minute cap stops a long side from needing ten minutes to count. Tracks under thirty
/// seconds never count, such as an interlude or a spoken intro, because half of such a track is too little
/// to judge.
/// </para>
/// </summary>
public static class Listen
{
    /// <summary>Shorter than this and it is not a track anybody plays on purpose.</summary>
    public const double ShortestSeconds = 30;

    /// <summary>
    /// Heard for less than this, the listen is not recorded at all, neither as a play nor as a skip.
    ///
    /// <para>The listening log grows with use, not with the size of the collection, so only useful entries
    /// should go in it. Scrolling down an album wall ends a listen on each track after a second or two.
    /// Those listens show the pointer moving, not a choice about the music, and many of them in a minute
    /// would fill the log. Five seconds is past the point where someone has heard the track start.</para>
    /// </summary>
    public const double NoticedSeconds = 5;

    /// <summary>
    /// A gap longer than this ends a listening session. Half an hour is long enough for a short break, and
    /// short enough that a morning and an evening count as two sessions.
    /// </summary>
    public const double SessionGapMinutes = 30;

    /// <summary>
    /// Whether a finished listen is worth a row in the log.
    ///
    /// <para>This is wider than <see cref="Counts"/> on purpose. A listen that did not count is the
    /// evidence for "keep skipping", so discarding it would throw away the only sign that someone dislikes
    /// a track. What is discarded is a quick skip past a track (see <see cref="NoticedSeconds"/>), and
    /// tracks too short to judge either way.</para>
    /// </summary>
    public static bool WorthRecording(double heardSeconds, double lengthSeconds)
        => lengthSeconds >= ShortestSeconds && heardSeconds >= NoticedSeconds;

    /// <summary>However long the record, this much of it is a listen.</summary>
    public const double EnoughSeconds = 4 * 60;

    /// <summary>
    /// Whether <paramref name="heardSeconds"/> of a track <paramref name="lengthSeconds"/> long is a play.
    /// </summary>
    /// <param name="heardSeconds">Audio actually delivered, not the playback position. A seek moves the
    /// position but not this value, so this value shows what was heard. A track played to its end has the
    /// two nearly equal. A track that failed silently has no audio delivered. That is why an album that
    /// played through in silence does not become the most played record in the library.</param>
    /// <param name="lengthSeconds">Zero when the catalogue has not given a length, for example a row that
    /// has not been backfilled. Nothing counts then. Guessing a length would count a quick skip past a
    /// track whose duration is unknown, and those are the rows a phone sees first.</param>
    public static bool Counts(double heardSeconds, double lengthSeconds)
    {
        if (lengthSeconds < ShortestSeconds) return false;
        if (heardSeconds <= 0) return false;

        return heardSeconds >= Math.Min(lengthSeconds / 2, EnoughSeconds);
    }
}
