namespace RPM.Core.Metadata;

/// <summary>One style and the shelf it hangs off.</summary>
/// <param name="Name">The style as the app spells it, for example "Deep House" or "Post-Punk".</param>
/// <param name="Genre">One of <see cref="Genres.All"/>. The genre is stored as data and is not worked out
/// from the name. The list is not a tree. For example, <c>Electronic / Dance</c> sits beside <c>House</c>
/// and <c>Techno</c> as the answer for dance music that none of the narrower shelves fits. So the parent of
/// a style cannot be read from the words in its name.</param>
public sealed record Subgenre(string Name, string Genre);

/// <summary>
/// The styles a record can be filed under. Each style hangs off one of <see cref="Genres.All"/>.
///
/// <para>A shelf says what kind of music it is. A style says which part of that kind it is. The two are
/// asked for together and returned together (see <see cref="GenreChoice"/>). A caller that has only the
/// shelf has dropped half of the answer.</para>
///
/// <para><b>This is not a validator.</b> A file may carry any style at all. This class gives the styles the
/// app suggests, picks them out of a catalogue's answer, and never invents one.</para>
///
/// <para>The table itself is generated. See <c>Subgenres.Data.cs</c> and <c>scripts/subgenres.json</c>.
/// Only the lookup code is written by hand.</para>
/// </summary>
public static partial class Subgenres
{
    /// <summary>Every spelling that leads to a style. It is built once, then read for each lookup and for
    /// each keystroke in a style box.</summary>
    private static readonly Dictionary<string, Subgenre> ByKey;

    /// <summary>The styles under one shelf, in offering order, keyed by the shelf.</summary>
    private static readonly Dictionary<string, IReadOnlyList<Subgenre>> ByGenre;

    /// <summary>
    /// Both tables are built in a static CONSTRUCTOR, not in a field initialiser.
    ///
    /// <para><b>The data is in another file.</b> The data (<see cref="All"/>, <see cref="SharedKeys"/>,
    /// <see cref="BlockedKeys"/>) is generated into <c>Subgenres.Data.cs</c>. Field initialisers run in the
    /// order they appear within one file, but the order between the files of a partial class is not fixed.
    /// A field initialiser here once read an empty <see cref="All"/> and threw. The static constructor body
    /// runs after every field initialiser in every part of the class, so the data is always there at that
    /// point.</para>
    /// </summary>
    static Subgenres()
    {
        var shared = new HashSet<string>(SharedKeys, StringComparer.Ordinal);
        var blocked = new HashSet<string>(BlockedKeys, StringComparer.Ordinal);
        var keys = new Dictionary<string, Subgenre>(StringComparer.Ordinal);

        foreach (var style in All)
        {
            var key = GenreText.Fold(style.Name);
            // A style that is under more than one shelf gets no key, so the rest of the tag decides. For
            // example, "singer-songwriter, lo-fi, folk" reaches Folk on its third word. The style is still
            // offered under each of its shelves. The same applies to a style whose spelling matches a
            // blocked word (see BlockedKeys).
            if (key.Length == 0 || shared.Contains(key) || blocked.Contains(key))
                continue;
            keys.TryAdd(key, style);
        }

        ByKey = keys;
        ByGenre = All.GroupBy(s => s.Genre, StringComparer.Ordinal)
                     .ToDictionary(g => g.Key, g => (IReadOnlyList<Subgenre>)[.. g], StringComparer.Ordinal);
    }

    /// <summary>The styles offered under one shelf, in the order the list gives them. Empty for a shelf
    /// with no styles under it, and for a name that is not a shelf at all.</summary>
    public static IReadOnlyList<Subgenre> Under(string? genre)
        => genre is not null && ByGenre.TryGetValue(genre, out var styles) ? styles : [];

    /// <summary>
    /// The style this text names, or null when it names none.
    ///
    /// <para>The whole label is tried first, then its words from left to right. This is how <see
    /// cref="Genres.Match"/> reads a shelf. So "Deep House" is found as a whole and does not stop at
    /// "deep". A tag such as "deep house, electronic" answers on its first entry.</para>
    ///
    /// <para>The blocked words stop the word pass from giving wrong answers. Splitting "Folk, World, &amp;
    /// Country" produces "world". Splitting Windows Media's "Alternative &amp; Punk" produces a word that
    /// folds to the same letters as the style Alternative Punk. Neither result is a style that a person
    /// tagged. These words are skipped here, so the wrong answer is never given.</para>
    /// </summary>
    public static Subgenre? Match(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        foreach (var key in GenreText.KeysOf(value))
            if (ByKey.TryGetValue(key, out var style))
                return style;

        return null;
    }

    /// <summary>Whether this is a style the app knows, spelled in any way at all.</summary>
    public static bool Knows(string? value) => Match(value) is not null;

    /// <summary>The style that one already-folded key names. This is for <see cref="Genres.Read"/>, which
    /// walks the keys of a tag ONCE and asks the styles and the shelves about each key in turn. If each
    /// table were asked about the whole tag in turn instead, "Pop Rap" would be filed under Pop. The shelf
    /// table answers to the second word before the style table is reached.</summary>
    internal static Subgenre? ByFoldedKey(string key)
        => ByKey.TryGetValue(key, out var style) ? style : null;
}
