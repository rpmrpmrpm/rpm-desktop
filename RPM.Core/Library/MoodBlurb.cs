using RPM.Core.Metadata;

namespace RPM.Core.Library;

/// <summary>
/// The sentence under a mood playlist's name: what it sounds like, and what kind of music is in it.
///
/// <para><b>Only the moods get one.</b> The lists about listening already say what they are in their
/// own titles. "Most played" needs no explanation, and writing one would explain a label the reader has
/// just read. A mood is different. "Chill" is a word, and which part of somebody's own collection it
/// drew from is a fact they cannot see from the name.</para>
///
/// <para><b>Pure, and it returns parts rather than a sentence.</b> The wording is localized, so it
/// belongs to the interface. What is decided here is only which genres are worth naming, and what they
/// are called in running text. The view puts the words around them.</para>
/// </summary>
public static class MoodBlurb
{
    /// <summary>How many kinds of music a blurb will name.
    ///
    /// <para>Three. One kind makes a mood list look narrower than it is. Five is a list of genres with a
    /// sentence wrapped around it, and by then the reader is counting rather than reading.</para>
    /// </summary>
    public const int MostGenres = 3;

    /// <summary>The fewest songs of a genre before it is worth naming.
    ///
    /// <para>A twentieth of the list. Naming a genre that one song in fifty belongs to describes the
    /// exception rather than the playlist. A reader who then cannot find that genre is right to think the
    /// sentence was wrong.</para>
    /// </summary>
    public const double LeastShare = 0.05;

    /// <summary>The kinds of music worth naming in the sentence, commonest first, as they would be spoken.
    /// </summary>
    /// <param name="genres">What each song on the list is tagged, in list order. These are raw tags. They
    /// go through <see cref="Genres.Match"/> here, so a list holding "Deep House" and "Techno" counts as
    /// one electronic list rather than two rare ones.</param>
    public static IReadOnlyList<string> Spoken(IEnumerable<string?> genres)
    {
        ArgumentNullException.ThrowIfNull(genres);

        var counted = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        foreach (var tag in genres)
        {
            total++;
            if (Genres.Match(tag) is not { } canonical || canonical == Genres.Unknown)
                continue;
            counted[canonical] = counted.GetValueOrDefault(canonical) + 1;
        }

        if (total == 0)
            return [];

        return [.. counted.Where(g => g.Value >= total * LeastShare)
                          .OrderByDescending(g => g.Value)
                          .ThenBy(g => g.Key, StringComparer.Ordinal)
                          .Take(MostGenres)
                          .Select(g => Genres.Spoken(g.Key))];
    }
}
