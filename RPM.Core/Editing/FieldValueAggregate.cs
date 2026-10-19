namespace RPM.Core.Editing;

/// <summary>
/// Summarises one field's values across the tracks a mass-edit is about to touch. When every track
/// already holds the same value the control shows that one value (a plain text box); when they differ
/// the control offers the distinct values to pick from or lets a new one be typed (an editable combo).
/// Values are trimmed and an empty/whitespace tag collapses to "", so a blank counts as a value.
/// </summary>
public sealed record FieldValueAggregate(
    bool AllSame,
    string CommonValue,
    IReadOnlyList<string> DistinctValues)
{
    /// <summary>Aggregates <paramref name="values"/> (one per track). <see cref="AllSame"/> is true when
    /// they all agree, including all-empty; <see cref="CommonValue"/> is that shared value (or "").
    /// <see cref="DistinctValues"/> is the non-empty values in first-seen order, deduped case-insensitively,
    /// for the pick-or-type list shown when they diverge.</summary>
    public static FieldValueAggregate Summarize(IEnumerable<string?> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var distinctAll = new List<string>();   // includes the empty value, to decide AllSame
        foreach (var value in values)
        {
            var v = (value ?? "").Trim();
            if (seen.Add(v))
                distinctAll.Add(v);
        }

        bool allSame = distinctAll.Count <= 1;
        string common = distinctAll.Count == 1 ? distinctAll[0] : "";
        var options = distinctAll.Where(v => v.Length > 0).ToList();
        return new FieldValueAggregate(allSame, common, options);
    }
}
