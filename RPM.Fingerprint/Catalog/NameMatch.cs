using System.Text;

namespace RPM.Fingerprint.Catalog;

/// <summary>
/// Whether two names are the same name, allowing for how differently one gets written.
///
/// <para>Needed wherever a catalogue's answer has to be checked against what was asked. Those searches
/// are fuzzy by design. They score every candidate and hand back the closest, never nothing, so
/// "something came back" says only that the index is not empty. The question that matters is whether
/// what came back is the thing that was asked for, and that cannot be asked with string equality: a tag
/// says "Earth Wind and Fire" and the catalogue says "Earth, Wind &amp; Fire", and they are one band.</para>
/// </summary>
public static class NameMatch
{
    /// <summary>Whether these are the same name. False when either is missing, and false when both
    /// reduce to nothing, because two strings of only punctuation are not a match.</summary>
    public static bool Same(string? one, string? other)
    {
        if (one is not { Length: > 0 } || other is not { Length: > 0 })
            return false;

        var squashed = Squash(one);
        return squashed.Length > 0 && squashed == Squash(other);
    }

    /// <summary>
    /// A name with everything that varies taken out of it: case, spacing, punctuation, and the word
    /// "and", which is dropped rather than kept so that it matches the ampersand it stands in for.
    /// </summary>
    public static string Squash(string? name)
    {
        if (name is not { Length: > 0 })
            return "";

        var squashed = new StringBuilder(name.Length);
        var word = new StringBuilder(8);

        foreach (var c in name + " ")
        {
            if (char.IsLetterOrDigit(c))
            {
                word.Append(char.ToLowerInvariant(c));
                continue;
            }

            // Compared as a string on purpose: StringBuilder.Equals(string) binds to object.Equals in
            // C# and answers reference equality, which is false for every word there has ever been.
            if (word.Length > 0 && !string.Equals(word.ToString(), "and", StringComparison.Ordinal))
                squashed.Append(word);
            word.Clear();
        }

        return squashed.ToString();
    }
}
