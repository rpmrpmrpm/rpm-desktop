namespace RPM.Core.Analysis;

/// <summary>Case-insensitive substring matching for the review-page search box.
/// A blank/whitespace query matches everything; otherwise the trimmed query must be a
/// substring of at least one of the supplied fields.</summary>
public static class TrackTextSearch
{
    public static bool Matches(string? query, params string?[] fields)
    {
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q))
            return true;
        foreach (var field in fields)
            if (!string.IsNullOrEmpty(field) && field.Contains(q, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
