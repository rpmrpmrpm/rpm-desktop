namespace RPM.Core.Library;

/// <summary>
/// What the library's search was used for, newest first: the rules, with the keeping left to each head.
/// </summary>
/// <remarks>
/// The iPhone's <c>SearchHistory</c> and the Android phone's are the same rules; change all three together.
/// A search is remembered once the box has been left alone for <see cref="Settled"/>: the person stopped
/// typing and is reading the results. The half-typed steps of one search ("radi", then "radiohead") are one
/// entry, not two.
/// </remarks>
public static class SearchHistory
{
    /// <summary>The most searches kept.</summary>
    public const int Most = 10;

    /// <summary>How long the box must be left alone before what is in it counts as a search to remember.</summary>
    public static readonly TimeSpan Settled = TimeSpan.FromSeconds(2);

    /// <summary>
    /// <paramref name="kept"/> with <paramref name="asked"/> at the top. A search that only adds to or takes
    /// from the newest entry replaces it. One shorter than two characters is not remembered.
    /// </summary>
    public static IReadOnlyList<string> Remember(IReadOnlyList<string> kept, string asked)
    {
        var clean = (asked ?? "").Trim().Replace('\n', ' ').Replace('\r', ' ');
        if (clean.Length < 2)
            return kept;

        var list = kept.Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
        if (list.Count > 0
            && (clean.StartsWith(list[0], StringComparison.OrdinalIgnoreCase)
                || list[0].StartsWith(clean, StringComparison.OrdinalIgnoreCase)))
            list.RemoveAt(0);

        list.RemoveAll(e => string.Equals(e, clean, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, clean);
        return list.Take(Most).ToList();
    }

    /// <summary><paramref name="kept"/> without <paramref name="asked"/>.</summary>
    public static IReadOnlyList<string> Forget(IReadOnlyList<string> kept, string asked) =>
        kept.Where(e => e != asked).ToList();
}
