namespace RPM.Core.Metadata;

/// <summary>
/// How a genre or a style is read from a tag: folded to letters, and cut into the words that a lookup tries.
///
/// <para><b>It is a separate class because it must hold no state.</b> <see cref="Genres"/> takes its shelves
/// from <see cref="Subgenres"/>, and <see cref="Subgenres"/> needs this class to fold text into its keys. If the
/// folding lived in either of those classes, each would set up static fields that reach into the other while it
/// is still being set up. Whether that code sees a built table or an empty one would then depend on the order the
/// compiler put two partial files in. This class has no static state, so it cannot be half set up, and the cycle
/// cannot happen.</para>
/// </summary>
internal static class GenreText
{
    /// <summary>What splits a compound shelf label into words. The list's own labels are cut on their slashes
    /// only. Splitting "Hip-Hop / Rap" on hyphens as well would file every trip hop record under rap.</summary>
    internal static readonly char[] Separators = ['/', ',', '&', ';', '|'];

    /// <summary>What splits a label into words when something is being looked up. This is wider than
    /// <see cref="Separators"/>, because a style is written as a phrase. "Post-Punk" and "Hard Rock" must find
    /// Punk and Rock, and neither is a slashed compound.</summary>
    internal static readonly char[] WordBreaks = ['/', ',', '&', ';', '|', ' ', '-', '_'];

    /// <summary>Punctuation, case and accents are removed, so "Hip-Hop", "hip hop" and "HipHop" are one word,
    /// and so are "Opéra Comique" and "Opera Comique". The text is decomposed first, which splits "é" into an
    /// "e" and an accent mark, and the mark is not a letter, so it goes with the punctuation.</summary>
    internal static string Fold(string value)
        => new([.. value.Normalize(System.Text.NormalizationForm.FormD)
                       .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    /// <summary>
    /// How a label is looked up: the whole label first, then its words from left to right.
    ///
    /// <para>Whole first is what makes "Hip-Hop" find Hip-Hop / Rap, and not stop at "hip". It also makes "Pop
    /// Rap" a rap record rather than a pop one. Left to right is what makes "Folk, World, &amp; Country" a folk
    /// record. The first word of a compound label is the one that says what the record is.</para>
    /// </summary>
    internal static IEnumerable<string> KeysOf(string value, char[] breaks)
    {
        yield return Fold(value);
        if (value.IndexOfAny(breaks) < 0)
            yield break;
        foreach (var part in value.Split(breaks, StringSplitOptions.RemoveEmptyEntries))
            if (Fold(part) is { Length: > 0 } key)
                yield return key;
    }

    /// <summary>
    /// The keys a lookup tries, in order: the whole label, then each phrase in it, then each word.
    ///
    /// <para>The phrase pass is in the middle, and that is why it exists. A tag is often a list, such as "cloud
    /// rap, hip hop, trap". Cutting it on its commas and its spaces in one go leaves only single words. So "cloud
    /// rap" never forms, and the record gets a shelf with no style. Cutting on the commas alone first offers "cloud
    /// rap" as a whole, which is a style, so the record gets both.</para>
    ///
    /// <para>Whole before phrase before word, because the more of the label a key uses, the more it means. "Pop
    /// Rap" as a whole is a rap style. Its last word alone is only a shelf.</para>
    /// </summary>
    internal static IEnumerable<string> KeysOf(string value)
    {
        yield return Fold(value);

        if (value.IndexOfAny(Separators) >= 0)
            foreach (var phrase in value.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
                if (Fold(phrase) is { Length: > 0 } key)
                    yield return key;

        if (value.IndexOfAny(WordBreaks) >= 0)
            foreach (var word in value.Split(WordBreaks, StringSplitOptions.RemoveEmptyEntries))
                if (Fold(word) is { Length: > 0 } key)
                    yield return key;
    }
}
