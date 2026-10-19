namespace RPM.Core.Library;

/// <summary>
/// What counts as a song, for the purpose of having a mood at all.
///
/// <para>This is separate from what the meter can measure. The two are different questions.
/// <see cref="Analysis.MoodMeter"/> refuses what it cannot describe. Under a few seconds there are
/// not enough frames for a spread to mean anything. This class refuses what is not worth describing:
/// a forty-second interlude, a spoken intro, a lock groove, or applause between two live tracks.
/// Both answers are true about the audio, and neither kind of track belongs in a playlist that
/// someone plays.
/// </para>
/// </summary>
public static class Moods
{
    /// <summary>
    /// The shortest a track can run and still be given a mood.
    ///
    /// <para>One minute. A real short song is kept, such as a punk single, a two-chorus radio edit or
    /// most of a grindcore record. Nothing that people would call a song is turned away. Shorter
    /// tracks are the material between songs: skits, intros, interludes, needle-drop noise and the
    /// count-in on a rehearsal tape. A large shelf can hold a great many of them.</para>
    ///
    /// <para>The filter also applies before analysis. A track this short is not decoded for its mood
    /// at all. Its arousal numbers, such as those over forty seconds of applause, would measure
    /// something nobody wants measured. They would also pull the bands that the distribution is cut
    /// from towards the sound of a room.</para>
    ///
    /// <para><b>A track whose length is not known is treated as too short</b>, because the database
    /// stores an unread duration as zero. This is the safe choice: nothing that might be a lock groove
    /// is described. But it is silent. A library whose durations never load would have no moods and
    /// no visible reason. The log line written at the start of each pass shows how many tracks are
    /// still to be worked through. On such a library that number reads zero, and that is the sign.</para>
    /// </summary>
    public const double LeastSeconds = 60;
}
