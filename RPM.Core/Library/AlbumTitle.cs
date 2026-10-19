namespace RPM.Core.Library;

/// <summary>A record's name with the bracketed part taken off the end. "Short N' Sweet (Deluxe)" is a
/// record called Short N' Sweet, in its Deluxe edition. The album page shows the two parts on separate
/// lines.
///
/// <para><b>The full title is kept as well, which is why this is a type and not a method.</b> A page that
/// kept only the two halves would show the record under a different name wherever it used the name, such as
/// a review heading or a tooltip. So the split returns all three values, the original included. A caller
/// that needs the name as it was tagged uses <see cref="Full"/>. Rebuilding it from the parts would get the
/// spacing wrong.</para>
///
/// <para><b>Only from the end.</b> A bracketed group in the middle or at the start is part of the name.
/// "(What's the Story) Morning Glory?" is not a record called Morning Glory? in a "What's the Story"
/// edition. Taggers, catalogues and shops put the edition at the end. The split removes groups from the
/// end, repeatedly, so a title with two of them, such as "... (Deluxe) [Remastered]", loses both.</para>
///
/// <para><b>Only when the brackets hold an edition.</b> The record "The Guide (Wommat)" is really called
/// that. The bracket is part of its name in Wolof, not an edition. Moving it to a second line would rename
/// the record on its own heading. An edition is defined by <see cref="TitleAsides.IsAside"/>, the same test
/// the tracklists use to fade brackets. That keeps both views in agreement about what a bracket
/// means.</para>
/// </summary>
public sealed record AlbumTitle
{
    private AlbumTitle(string full, string name, IReadOnlyList<string> editions)
    {
        Full = full;
        Name = name;
        Editions = editions;
    }

    /// <summary>The title exactly as it was tagged, brackets and all.</summary>
    public string Full { get; }

    /// <summary>The title with the trailing bracketed groups removed. It is never empty when
    /// <see cref="Full"/> is not: a title that is only brackets keeps them.
    /// </summary>
    public string Name { get; }

    /// <summary>What was inside those brackets, in reading order, without the brackets. It is empty for
    /// most records, so the page can leave the line out instead of drawing a blank one.
    /// </summary>
    public IReadOnlyList<string> Editions { get; }

    public static AlbumTitle Split(string? title)
    {
        var full = title ?? "";
        var name = full.TrimEnd();
        var editions = new List<string>();

        while (Peel(name) is { } peeled)
        {
            // A title that is only a bracketed phrase, such as Sigur Rós's "( )", has no edition. Its name
            // is punctuation. Removing the group would leave an empty heading.
            if (peeled.Before.Length == 0)
                break;

            editions.Insert(0, peeled.Inside);
            name = peeled.Before;
        }

        return new AlbumTitle(full, name, editions);
    }

    /// <summary>One trailing bracketed group: what is left of the title, and what was inside. Null when
    /// the title does not end in one.
    ///
    /// <para>The closing bracket is counted, not the first opening one. So a group with brackets inside it,
    /// such as "(Deluxe (2019))", is removed whole. Matching the first opener would leave a stray bracket
    /// on the name.</para>
    /// </summary>
    private static (string Before, string Inside)? Peel(string title)
    {
        if (title.Length == 0)
            return null;

        var (open, close) = title[^1] switch
        {
            ')' => ('(', ')'),
            ']' => ('[', ']'),
            _ => ('\0', '\0'),
        };
        if (close == '\0')
            return null;

        var depth = 0;
        for (var i = title.Length - 1; i >= 0; i--)
        {
            if (title[i] == close)
                depth++;
            else if (title[i] == open && --depth == 0)
            {
                var inside = title[(i + 1)..^1].Trim();

                // Not every trailing bracket is an edition. "(Wommat)" is part of a name, and "()" is a
                // tagging error. Both stay in the title. Removing them would rename the record on the
                // heading that must show its name correctly.
                return TitleAsides.IsAside(inside) ? (title[..i].TrimEnd(), inside) : null;
            }
        }

        // A bracket that is never closed, such as "Album (Deluxe", is part of the title.
        return null;
    }
}
