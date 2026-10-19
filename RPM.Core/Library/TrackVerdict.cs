namespace RPM.Core.Library;

/// <summary>
/// Whether a song fits the kind of list the library builds. It is the listener's answer to "was this
/// a good pick", and nothing else.
///
/// <para><b>This is not the favourite flag. Never merge the two.</b> <c>tracks.favorite</c> says the
/// listener loves the song. It drives "Favourite songs", "Loved and forgotten" and "Played not loved".
/// This value says something about the pick: the generated list offered the song, and the listener
/// says it belongs there or does not. A song can be a favourite and still a poor fit for a calm list,
/// and one value cannot say both. Liking a song here does not put a heart on it, and hearting a song
/// says nothing about where it fits.</para>
///
/// <para><b>One value, not two flags.</b> Liked and turned down cannot both be true. Two booleans
/// would allow that state, so every writer would need a rule to prevent it. With one value the state
/// cannot be reached. <c>LibraryDatabase.SetTrackVerdict</c> is the only way to write it, and it
/// writes both columns in one statement.</para>
///
/// <para><b>No answer is not a refusal.</b> That is why there are three values and not two. Most songs
/// have not been judged, and a generated list may offer them. Only a song the listener has turned
/// down must be kept out.</para>
/// </summary>
public enum TrackVerdict
{
    /// <summary>Nobody has said whether the song belongs. Most rows are like this.</summary>
    None,

    /// <summary>
    /// The listener said the song belongs.
    ///
    /// <para>A liked song stays in the lists it qualifies for, even where a cap or a narrowing would
    /// have removed it. The listener has already said it fits. An app that removed it anyway would
    /// overrule them with arithmetic. Four places honour this: <c>MoodList.InOneEra</c>'s decade
    /// window, <c>MoodList.Preferring</c>'s style narrowing, and both caps in
    /// <c>MoodPool.Window</c>.</para>
    ///
    /// <para><b>What it does not do.</b> It never adds a song to a list the song does not qualify for.
    /// Liking a loud record does not make it calm. It never moves a song up the ranking. It only stops
    /// something else from removing it. Like a dislike, it applies to one kind of list only.</para>
    /// </summary>
    Liked,

    /// <summary>
    /// The listener said the song does not belong.
    ///
    /// <para><b>Stronger than a skip. That is its purpose.</b> The play log already records skips, and
    /// <c>KeepSkipping</c> reads them. A skip is ambiguous, though: a phone rang, the song was wrong
    /// for the moment, or someone walked past the keyboard. A dislike is the listener saying so on
    /// purpose. It is acted on once: this kind of list never offers the song again.</para>
    ///
    /// <para><b>This kind of list only.</b> A song turned down in Chill is removed from Chill and from
    /// every genre and decade row cut from it. Dancefloor, Focus and Sleep still offer it as before.
    /// So the stored row is keyed by the kind of list as well as the path. See
    /// <c>LibraryDatabase.SetTrackVerdict</c>, which writes and deletes by both, and
    /// <c>LibraryDatabase.NotTurnedDown</c>, which checks by both.</para>
    /// </summary>
    Disliked,
}
