using RPM.Core.Library;

using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The sentence under a mood list's name, worded.
///
/// <para><b>Two screens write it, and they must word it the same way.</b> The playlist page prints it under
/// the list's title. The home screen prints it under the list's card. A list that described itself one way
/// in the pane and another way on the way in would read as two lists. <see cref="MoodBlurb"/> decides which
/// genres are worth naming. That is arithmetic, and it lives in the core with its own tests. This class only
/// holds the wording. The wording is localized, so it belongs to the interface.</para>
///
/// <para><b>Only the moods get one.</b> The lists about listening say what they are in their own titles.
/// "Most played" needs no explanation. Writing one would explain a label to someone who has just read it.
/// A mood is different. "Chill" is only a word, and the listener cannot see from the name which part of
/// their own collection it drew from.</para>
/// </summary>
public static class MoodBlurbText
{
    /// <summary>
    /// The sentence for one list, or null where that kind has none.
    /// </summary>
    /// <param name="id">Which list. Every kind other than the five moods returns null.</param>
    /// <param name="tags">The raw genre tag of each song in the list, in list order. They are folded
    /// through <see cref="MoodBlurb.Spoken"/> here. So a list that holds "Deep House" and "Techno" is one
    /// electronic list, not two rare ones. Ignored for the sleep list, which names no genres.</param>
    public static string? For(SmartPlaylistId id, IEnumerable<string?> tags)
    {
        // Sleep names no genres. It is the one mood that is cut across all of them. A list of three genres
        // would describe the shelf, not the list.
        if (id.Kind == SmartPlaylistKind.Sleep)
            return Loc.Get("Playlists_BlurbSleep");

        var key = id.Kind switch
        {
            SmartPlaylistKind.Calm => "Playlists_BlurbCalm",
            SmartPlaylistKind.Energetic => "Playlists_BlurbEnergetic",
            SmartPlaylistKind.Danceable => "Playlists_BlurbDanceable",
            SmartPlaylistKind.Focus => "Playlists_BlurbFocus",
            _ => null,
        };

        if (key is null)
            return null;

        var spoken = MoodBlurb.Spoken(tags);
        return spoken.Count == 0 ? null : Loc.Format(key, Listed(spoken));
    }

    /// <summary>"rock", "rock and pop", "rock, pop and jazz". The last join is a localized template
    /// because only English puts the word there.</summary>
    public static string Listed(IReadOnlyList<string> names) => names switch
    {
        null or { Count: 0 } => "",
        { Count: 1 } => names[0],
        _ => Loc.Format("Playlists_GenreAnd", string.Join(", ", names.Take(names.Count - 1)), names[^1]),
    };
}
