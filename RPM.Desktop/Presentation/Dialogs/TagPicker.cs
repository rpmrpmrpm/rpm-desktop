namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// Where a tag list's entries actually sit, once there is a Clear row in front of them.
///
/// <para>Four windows edit tags: the mass edit, the album editor, the per-file editor and the import
/// workbench's tag panel. Each of them works out a position itself, from the top of a list whose first
/// entry is not one of the answers. That kind of sum is right in three places and one out in the
/// fourth. Being one out means writing a different genre from the one somebody picked. So the sum
/// lives here, with no control near it, where it can be checked on its own.</para>
/// </summary>
internal static class TagPicker
{
    /// <summary>The stored token that an entry stands for. Entry 0 is Clear, which stands for an empty
    /// value. No selection is also empty. Neither of these is a value that somebody typed.</summary>
    public static string ValueAt(IReadOnlyList<TagFieldOption> options, int index)
        => index <= 0 || index > options.Count ? "" : options[index - 1].Value;

    /// <summary>The entry for a stored token. An empty token is the Clear row. An unknown token selects
    /// nothing. Every other token is at its options index plus one, because the Clear row comes
    /// first.</summary>
    public static int IndexOf(IReadOnlyList<TagFieldOption> options, string? value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;

        for (var i = 0; i < options.Count; i++)
        {
            if (string.Equals(options[i].Value, value, StringComparison.OrdinalIgnoreCase))
                return i + 1;
        }
        return -1;
    }

    /// <summary>Reads the value typed into a list. Text equal to the Clear label, in the current
    /// language, is read as empty. A real value that equals that label is read as empty too.</summary>
    public static string ValueOfText(string? text, string clearLabel)
    {
        var said = text ?? "";
        return string.Equals(said, clearLabel, StringComparison.Ordinal) ? "" : said;
    }

    /// <summary>Where a value that is not in the list goes when it is shown: directly after the Clear
    /// row, at the top of the answers.</summary>
    public const int FirstAnswer = 1;
}
