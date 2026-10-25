namespace RPM.Core.Library;

/// <summary>One rung of the ladder of letters beside an artists list: the letter, and the place in the list
/// of the first act filed under it.</summary>
public readonly record struct ArtistLetter(char Letter, int First);

/// <summary>
/// The letters an artists list in name order runs through, for the ladder down the side of Pegasus's
/// artists list (as on the Android TV app, FOBO c86660c2e and ANDR 1128831).
///
/// <para>Each act files under the letter <see cref="ArtistWall.Initial"/> gives it: accents folded, "#" for a
/// name that starts with a digit, a symbol or a script with no letter of its own. The ladder holds only the
/// letters the list has, so every rung leads somewhere: a collection with no Q has no Q on its ladder.</para>
///
/// <para><b>A to Z, then "#".</b> The list is already in that order (<see cref="ArtistSort"/>'s name order and
/// <see cref="ArtistWall.Build"/> sort by the letter first). The letters are put in that order here as well,
/// so the ladder reads alphabetically whatever order it is handed, and each letter points at the first act
/// the list files under it.</para>
/// </summary>
public static class ArtistLetters
{
    /// <summary>The letters <paramref name="list"/> runs through, A to Z and then "#", each with the place of
    /// its first act. Empty for an empty list.</summary>
    /// <param name="letterOf">The letter one entry files under: <see cref="ArtistWallItem.Initial"/>.</param>
    public static IReadOnlyList<ArtistLetter> Of<T>(IReadOnlyList<T> list, Func<T, char> letterOf)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(letterOf);

        var first = new Dictionary<char, int>();
        for (var i = 0; i < list.Count; i++)
            first.TryAdd(letterOf(list[i]), i);

        return first
            .OrderBy(pair => pair.Key == '#' ? 1 : 0)
            .ThenBy(pair => pair.Key)
            .Select(pair => new ArtistLetter(pair.Key, pair.Value))
            .ToList();
    }
}
