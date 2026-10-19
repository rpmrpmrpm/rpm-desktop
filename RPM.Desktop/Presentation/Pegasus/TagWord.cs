namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// Reads a tag the way a tagger wrote it.
///
/// <para>Two things this room does for a particular record depend on whether a text field that a person
/// typed says a certain thing: showing a mirror ball (<see cref="MirrorBall"/>) and switching on the New
/// Year effect (<see cref="NewYear"/>). The test cannot be strict about case or about the punctuation
/// used to separate words. It also cannot be loose enough to match "Abbath" when it means "ABBA".</para>
///
/// <para>The rule lives in one place because both checks ask the same question. Two copies of such a
/// rule would drift apart.</para>
/// </summary>
internal static class TagWord
{
    /// <summary>Every character a tagger might put between two words. Splitting on all of them makes
    /// "Nu-Disco", "Funk/Soul" and "ABBA feat. Frida" read as their separate words.</summary>
    private static readonly char[] Separators =
        [' ', '\t', ',', ';', '/', '\\', '|', '&', '+', '-', '_', '(', ')', '[', ']', '·', '•'];

    /// <summary>Whether one of the words in <paramref name="text"/> is <paramref name="word"/>, as a whole
    /// word and never part of a longer one. This is the strict check for a field that can hold a list
    /// ("Funk/Soul", "ABBA feat. Frida"). A substring match would match "Discovery" for "disco" and
    /// "Abbath" for "ABBA".</summary>
    internal static bool Names(string? text, string word)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        foreach (var part in text.Split(Separators,
                                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(part, word, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>Whether <paramref name="text"/> is <paramref name="phrase"/> and nothing else, ignoring the
    /// spaces around it and the case it was typed in. Use it for a field that holds one value, such as a
    /// song title. The word check above would match "Happy New Year's Day" for "Happy New Year".</summary>
    internal static bool Is(string? text, string phrase)
        => !string.IsNullOrWhiteSpace(text)
        && string.Equals(text.Trim(), phrase, StringComparison.OrdinalIgnoreCase);
}
