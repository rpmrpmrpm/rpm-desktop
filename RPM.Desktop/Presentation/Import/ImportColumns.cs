namespace RPM.Desktop.Presentation.Import;

/// <summary>
/// Which of the import grid's optional columns are down.
///
/// <para>Stored as the columns that are hidden, not the ones that are shown. A column added in a later
/// version then appears switched on for everyone, instead of missing from every saved list. The ids
/// are the columns' <c>Tag</c> values, because a header is a translated string and cannot be what a
/// setting remembers.</para>
/// </summary>
public static class ImportColumns
{
    /// <summary>Reads the setting. Unknown and empty ids are dropped. An id that matches no column cannot
    /// be shown, so keeping it would hide nothing and could confuse the next write.</summary>
    public static HashSet<string> Parse(string? setting) =>
        new((setting ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>Writes the setting. Null when nothing is hidden, so "everything is up" is stored as
    /// having no opinion rather than as an empty string.</summary>
    public static string? Format(IEnumerable<string> hidden)
    {
        var ids = hidden.Where(id => !string.IsNullOrWhiteSpace(id))
                        .Select(id => id.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
        return ids.Count == 0 ? null : string.Join(",", ids);
    }
}
