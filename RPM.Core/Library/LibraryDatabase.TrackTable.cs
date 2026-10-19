using Microsoft.Data.Sqlite;

namespace RPM.Core.Library;

/// <summary>One column of the data table, sorted one way.</summary>
public readonly record struct TrackTableSort(string Column, bool Descending);

/// <summary>How a column's filter text is matched against the column.</summary>
public enum TextMatchMode
{
    StartsWith,
    Contains,
    EndsWith,
}

/// <summary>
/// What a column is narrowed to: a way of matching and the text to match. There is no way to build
/// one from empty text. Empty text means no filter at all, not a filter that matches nothing, and the
/// two give very different results: an empty table, or every row.
/// </summary>
public sealed record TextMatch
{
    private TextMatch(TextMatchMode mode, string text)
    {
        Mode = mode;
        Text = text;
    }

    public TextMatchMode Mode { get; }

    /// <summary>The text to match, trimmed and never empty.</summary>
    public string Text { get; }

    /// <summary>The match for this text, or null when there is no text to match.</summary>
    public static TextMatch? Of(TextMatchMode mode, string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : new TextMatch(mode, text.Trim());
}

/// <summary>One column of the data table, narrowed to the rows whose text matches.</summary>
public readonly record struct TrackTableFilter(string Column, TextMatch Match);

/// <summary>
/// What the library's data table is asking for: the header's search, the columns it is sorted by, and
/// the columns it is filtered on. Everything the view can do to the collection in one value, so the
/// count, the page and the filter flyout's own list all answer the same question.
/// </summary>
public sealed record TrackTableQuery(
    string? Search = null,
    IReadOnlyList<TrackTableSort>? Sorts = null,
    IReadOnlyList<TrackTableFilter>? Filters = null);

/// <summary>
/// The columns the data table may sort and filter on, by their name in the mirror. A whitelist rather
/// than a check on the string: these names are concatenated into SQL, and the only safe way to build a
/// clause out of a name is to have written the name yourself.
/// </summary>
public static class TrackTableColumns
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "title", "artist", "album_artist", "album", "genre",
        "work", "movement_name", "movement_no", "composer",
        "track_number", "disc_number", "disc_count", "disc_subtitle", "year",
        "duration_seconds", "plays", "last_played_utc_ticks", "added_utc_ticks",
        "extension", "codec_description", "bitrate_kbps", "sample_rate", "bits_per_sample",
        "channels", "size_bytes", "cloud_state", "folder", "file_name", "relative_path",
        "rpm_id", "modified_utc_ticks",
    };

    /// <summary>The column's name if the table is allowed to touch it, else null.</summary>
    public static string? Sql(string? name)
        => name is not null && Allowed.Contains(name) ? name : null;
}

public sealed partial class LibraryDatabase
{
    /// <summary>
    /// The ids of every track the data table's query matches, in the order it wants them.
    ///
    /// <para>Ids, not rows: sorting by a column with no index over a million-row library is a scan and
    /// a sort. Doing that once per page, which is what an ORDER BY … LIMIT … OFFSET page query does,
    /// would sort the whole collection again for every screenful the user scrolls past. The order
    /// is settled once here, and <see cref="QueryTracksByIds"/> then fetches any window of it by
    /// primary key. One 8-byte id per track is about 8 MB for a million tracks, which is the price of
    /// never sorting twice.</para>
    /// </summary>
    public IReadOnlyList<long> QueryTrackIds(TrackTableQuery query)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = $"SELECT id FROM tracks {Where(cmd, query)} {OrderBy(query)}";
            using var reader = cmd.ExecuteReader();
            var ids = new List<long>();
            while (reader.Read())
                ids.Add(reader.GetInt64(0));
            return ids;
        }
    }

    /// <summary>
    /// The rows for <paramref name="ids"/>, handed back in exactly the order they were asked for.
    /// The id list is the sort, and SQLite returns primary-key lookups in its own order.
    /// </summary>
    public IReadOnlyList<LibraryTrack> QueryTracksByIds(IReadOnlyList<long> ids)
    {
        if (ids.Count == 0)
            return [];

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            // Parameters rather than an interpolated id list: a page is a bounded few hundred ids, and
            // a bound integer cannot be anything but an integer.
            var names = new string[ids.Count];
            for (var i = 0; i < ids.Count; i++)
            {
                names[i] = $"$i{i}";
                cmd.Parameters.AddWithValue(names[i], ids[i]);
            }
            cmd.CommandText = $"SELECT * FROM tracks WHERE id IN ({string.Join(",", names)})";

            using var reader = cmd.ExecuteReader();
            var byId = new Dictionary<long, LibraryTrack>(ids.Count);
            while (reader.Read())
            {
                var track = ReadTrack(reader);
                byId[track.Id] = track;
            }

            var ordered = new List<LibraryTrack>(ids.Count);
            foreach (var id in ids)
            {
                // A row deleted between the order being taken and the page being fetched simply is not
                // there; the next refresh re-counts. Dropping it beats a hole in the page.
                if (byId.TryGetValue(id, out var track))
                    ordered.Add(track);
            }
            return ordered;
        }
    }

    /// <summary>The WHERE the search and the filters add up to, or "" when they add up to nothing.</summary>
    private string Where(SqliteCommand cmd, TrackTableQuery query)
    {
        var clauses = Clauses(cmd, query);

        // A borrowed shelf (another computer's library) shows only cloud rows when that computer is
        // not on this network. A signed-out session shows only rows on this disk.
        // See LibraryDatabase.CloudRowsOnly and LibraryDatabase.LocalRowsOnly.
        if (ReachTerm("cloud_state") is { Length: > 0 } reach)
            clauses.Insert(0, reach);

        return clauses.Count == 0 ? "" : "WHERE " + string.Join(" AND ", clauses);
    }

    /// <summary>The search clause and one clause per filtered column.</summary>
    private static List<string> Clauses(SqliteCommand cmd, TrackTableQuery query)
    {
        var clauses = new List<string>();

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            cmd.Parameters.AddWithValue("$q", "%" + EscapeLike(query.Search.Trim()) + "%");
            clauses.Add(
                """
                (file_name LIKE $q ESCAPE '\' OR artist LIKE $q ESCAPE '\'
                  OR title LIKE $q ESCAPE '\' OR album LIKE $q ESCAPE '\'
                  OR album_artist LIKE $q ESCAPE '\')
                """);
        }

        var slot = 0;
        foreach (var filter in query.Filters ?? [])
        {
            // A column the whitelist does not know is skipped. It is not a column the database holds,
            // so no text typed against it can filter anything.
            if (TrackTableColumns.Sql(filter.Column) is not { } sql)
                continue;

            var name = $"$f{slot++}";
            cmd.Parameters.AddWithValue(name, Pattern(filter.Match));

            // LIKE is case-insensitive over ASCII in SQLite, which is what a filter box should be.
            clauses.Add($"{sql} LIKE {name} ESCAPE '\\'");
        }

        return clauses;
    }

    /// <summary>The LIKE pattern a match means. The text is escaped first, so a title with a % or an
    /// _ in it filters on those characters instead of on any run of anything.</summary>
    private static string Pattern(TextMatch match)
    {
        var text = EscapeLike(match.Text);
        return match.Mode switch
        {
            TextMatchMode.StartsWith => text + "%",
            TextMatchMode.EndsWith => "%" + text,
            _ => "%" + text + "%",
        };
    }

    /// <summary>
    /// The ORDER BY the header clicks add up to. It always ends on the rowid. Two tracks that tie on
    /// every sorted column would otherwise come back in whatever order the scan found them, so the
    /// rows could appear in a different order on the next page.
    /// </summary>
    private static string OrderBy(TrackTableQuery query)
    {
        var terms = new List<string>();
        foreach (var sort in query.Sorts ?? [])
        {
            if (TrackTableColumns.Sql(sort.Column) is not { } sql)
                continue;
            terms.Add($"{sql} {(sort.Descending ? "DESC" : "ASC")}");
        }

        // The tie-break is the primary key, not the path. Both settle the order of two tracks that a
        // sort cannot separate, and the user cannot see the difference. The path costs more. An index
        // on a column holds (that column, rowid), so a sort that ends in the rowid matches the index's
        // own order, and SQLite reads it straight from the index. A sort that ends in any other column
        // needs a temp B-tree and a row lookup per row to fetch the path. On a 9,639-track library,
        // ORDER BY title, relative_path takes 43 ms and ORDER BY title, id takes 1 ms. For artist the
        // figures are 24 ms and 1 ms. The rowid tie-break also keeps the cost linear rather than
        // n log n, which matters at the size this table is built for.
        terms.Add("id ASC");
        return "ORDER BY " + string.Join(", ", terms);
    }
}
