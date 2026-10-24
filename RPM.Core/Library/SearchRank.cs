namespace RPM.Core.Library;

/// <summary>
/// The order a search's answers are listed in, and how many of each kind are kept: the rule of the Android
/// TV app's search (LibrarySearch.best), which Pegasus's search screen follows.
///
/// <para>Within each kind, a name that starts with the whole query comes first, then a name with a word
/// that starts with the query's first word, then the rest. Each group keeps the order the answers arrived
/// in, which is the order the collection lists them. Case is ignored. So "love" lists "Love Me Do" before
/// "Endless Love" before "Glove", and a list cut at its most never drops a name that starts with the
/// query for one that only holds it.</para>
///
/// <para>What matches is not decided here. The caller hands over what already matched, by the rule its
/// own search has (the mirror's substring search, or the artists list's folded names), and this only
/// orders and cuts it.</para>
/// </summary>
public static class SearchRank
{
    /// <summary>The most artists, albums, tracks and radio stations listed, as on the TV.</summary>
    public const int ArtistsMost = 10;
    public const int AlbumsMost = 20;
    public const int TracksMost = 30;
    public const int StationsMost = 20;

    /// <summary>
    /// The best <paramref name="most"/> of <paramref name="found"/> for <paramref name="query"/>, by the
    /// name <paramref name="name"/> gives each one. Nothing for a query that is only spaces.
    /// </summary>
    /// <remarks>The walk stops once the first group is full: nothing found after it can come before
    /// it.</remarks>
    public static List<T> Best<T>(IEnumerable<T> found, Func<T, string?> name, string? query, int most)
    {
        var whole = query?.Trim() ?? "";
        if (whole.Length == 0 || most <= 0)
            return [];

        var first = whole.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var starts = new List<T>();
        var wordStarts = new List<T>();
        var rest = new List<T>();

        foreach (var item in found)
        {
            var text = name(item) ?? "";
            if (text.StartsWith(whole, StringComparison.OrdinalIgnoreCase))
                starts.Add(item);
            else if (text.StartsWith(first, StringComparison.OrdinalIgnoreCase)
                     || text.Contains(" " + first, StringComparison.OrdinalIgnoreCase))
                wordStarts.Add(item);
            else
                rest.Add(item);

            if (starts.Count >= most)
                break;
        }

        return [.. starts.Concat(wordStarts).Concat(rest).Take(most)];
    }
}
