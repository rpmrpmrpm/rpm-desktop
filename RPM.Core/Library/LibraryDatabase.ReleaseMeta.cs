using RPM.Core.Metadata;
using RPM.Core.Metadata.Providers;

namespace RPM.Core.Library;

/// <summary>
/// What a catalogue said about one release, in the catalogue's own words and in RPM's.
///
/// <para><b>Not the user's answer, and not editable.</b> A record's genre and its style are fields the
/// user sets, one value each, stored in the file's own tags. This is different: everything Discogs or
/// MusicBrainz filed the release under, kept whole, so that a generated list can ask for the ballads
/// and the acoustic takes at once. Users never type a list like this, and nothing should ask them to.</para>
/// </summary>
/// <param name="Provider">Which catalogue answered: <see cref="DiscogsJson.ProviderId"/>, or
/// <see cref="LibraryDatabase.ReleaseGroupProvider"/> for MusicBrainz's answer about a release group.</param>
/// <param name="ReleaseId">That catalogue's own id. It has no meaning without <paramref name="Provider"/>
/// beside it.</param>
/// <param name="RawGenres">What the provider called the kind of music, semicolon-separated, in its
/// own order.</param>
/// <param name="RawStyles">The narrower labels, where the provider draws that distinction.</param>
/// <param name="Votes">How many people voted for each of <paramref name="RawGenres"/>, aligned with
/// it. Null for Discogs, whose styles are an editor's and carry no count.</param>
/// <param name="MappedGenre">One of <see cref="Genres.All"/>, or null where nothing recognised.</param>
/// <param name="MappedStyles">Names from <see cref="Subgenres.All"/>, semicolon-separated. An empty
/// string means the provider was asked and none of its labels is on our list. That is an answer.</param>
/// <param name="VocabVersion">The <see cref="Subgenres.Version"/> that did the mapping.</param>
/// <param name="FetchedUtcTicks">When the provider was asked.</param>
public sealed record ReleaseMeta(
    string Provider,
    string ReleaseId,
    string? RawGenres,
    string? RawStyles,
    string? Votes,
    string? MappedGenre,
    string? MappedStyles,
    int VocabVersion,
    long FetchedUtcTicks)
{
    /// <summary>The mapped styles as a list. Empty for a release that was asked and answered nothing
    /// RPM knows. A release nobody has asked about gives the same empty list. Only the stored row
    /// tells the two apart: <see cref="LibraryDatabase.ReadReleaseMeta"/> returns null for a release
    /// nobody has asked about.</summary>
    public IReadOnlyList<string> Styles => LibraryDatabase.SplitStyles(MappedStyles);
}

public sealed partial class LibraryDatabase
{
    /// <summary>
    /// The provider name a MusicBrainz answer about a RELEASE GROUP is filed under.
    ///
    /// <para>Its own name rather than <c>musicbrainz</c>, because the id is a release-group MBID and
    /// a release MBID is a different thing that would collide with it in the same key space. Discogs
    /// answers about a pressing. This name is for the album above its pressings, which is usually the
    /// more useful answer, and there are always fewer release groups than releases.</para>
    /// </summary>
    public const string ReleaseGroupProvider = "musicbrainz-release-group";

    /// <summary>How a list is written into one column. A style may hold a comma: "Folk, World, &amp;
    /// Country" is a real label, so a comma cannot be the separator.</summary>
    private const string StyleSeparator = "; ";

    internal static IReadOnlyList<string> SplitStyles(string? joined)
        => string.IsNullOrWhiteSpace(joined)
            ? []
            : [.. joined.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static string Join(IEnumerable<string>? values)
        => values is null ? "" : string.Join(StyleSeparator, values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()));

    /// <summary>
    /// Writes down what a catalogue said about a release, and what RPM's lists make of it.
    ///
    /// <para>Both halves are written together, on purpose. A row whose raw words and mapping came from
    /// two different calls could hold a mapping of something the raw words no longer say.</para>
    ///
    /// <para>An empty answer is still an answer and is still written. The row stops the same release
    /// being asked about on every pass, and a provider that files a record under nothing has still
    /// told us something.</para>
    /// </summary>
    /// <param name="provider">The catalogue, as its own id.</param>
    /// <param name="releaseId">Its id for the release.</param>
    /// <param name="genres">What it called the kind of music.</param>
    /// <param name="styles">Its narrower labels, where it draws that distinction. A provider with one
    /// list passes it as both, which is what <see cref="Genres.MapAll"/> expects.</param>
    /// <param name="votes">Vote counts aligned with <paramref name="genres"/>, or null.</param>
    public void SetReleaseMeta(
        string provider,
        string releaseId,
        IReadOnlyList<string>? genres,
        IReadOnlyList<string>? styles,
        IReadOnlyList<int>? votes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);

        var mapped = Genres.MapAll(genres, styles);

        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO release_meta
                    (provider, release_id, raw_genres, raw_styles, votes,
                     mapped_genre, mapped_styles, vocab_version, fetched_utc_ticks)
                VALUES ($provider, $id, $rawGenres, $rawStyles, $votes,
                        $genre, $styles, $vocab, $fetched)
                ON CONFLICT(provider, release_id) DO UPDATE SET
                    raw_genres = excluded.raw_genres,
                    raw_styles = excluded.raw_styles,
                    votes = excluded.votes,
                    mapped_genre = excluded.mapped_genre,
                    mapped_styles = excluded.mapped_styles,
                    vocab_version = excluded.vocab_version,
                    fetched_utc_ticks = excluded.fetched_utc_ticks
                """;
            cmd.Parameters.AddWithValue("$provider", provider);
            cmd.Parameters.AddWithValue("$id", releaseId);
            cmd.Parameters.AddWithValue("$rawGenres", Join(genres));
            cmd.Parameters.AddWithValue("$rawStyles", Join(styles));
            cmd.Parameters.AddWithValue("$votes",
                votes is null ? DBNull.Value : string.Join(StyleSeparator, votes));
            cmd.Parameters.AddWithValue("$genre", (object?)mapped.Genre ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$styles", Join(mapped.Styles.Select(s => s.Name)));
            cmd.Parameters.AddWithValue("$vocab", Subgenres.Version);
            cmd.Parameters.AddWithValue("$fetched", DateTime.UtcNow.Ticks);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>What one catalogue said about one release, or null where nobody has asked it.</summary>
    public ReleaseMeta? ReadReleaseMeta(string provider, string releaseId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseId);

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT provider, release_id, raw_genres, raw_styles, votes,
                       mapped_genre, mapped_styles, vocab_version, fetched_utc_ticks
                FROM release_meta WHERE provider = $provider AND release_id = $id
                """;
            cmd.Parameters.AddWithValue("$provider", provider);
            cmd.Parameters.AddWithValue("$id", releaseId);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadRow(reader) : null;
        }
    }

    /// <summary>
    /// Every release whose mapping was made by a style list that is now out of date.
    ///
    /// <para>This is why a vocabulary change needs a query, not a new fetch. The raw words are stored in
    /// the row. Only RPM's reading of them is out of date.</para>
    /// </summary>
    public IReadOnlyList<ReleaseMeta> ReleasesToRemap(int limit)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT provider, release_id, raw_genres, raw_styles, votes,
                       mapped_genre, mapped_styles, vocab_version, fetched_utc_ticks
                FROM release_meta
                WHERE vocab_version <> $vocab
                ORDER BY provider, release_id
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$vocab", Subgenres.Version);
            cmd.Parameters.AddWithValue("$limit", Math.Max(0, limit));

            using var reader = cmd.ExecuteReader();
            var rows = new List<ReleaseMeta>();
            while (reader.Read())
                rows.Add(ReadRow(reader));
            return rows;
        }
    }

    /// <summary>
    /// Runs the current style list over every row an older one mapped, and returns how many rows were
    /// remapped. Nothing is fetched: the provider's own words are already stored in each row.
    /// </summary>
    public int RemapReleases(int batch = 500)
    {
        int done = 0;
        while (ReleasesToRemap(batch) is { Count: > 0 } rows)
        {
            foreach (var row in rows)
            {
                SetReleaseMeta(
                    row.Provider,
                    row.ReleaseId,
                    SplitStyles(row.RawGenres),
                    SplitStyles(row.RawStyles),
                    ParseVotes(row.Votes));
                done++;
            }

            // A row that maps to the same answer is still saved with the current version. So the next
            // read cannot return the same rows again, and the loop ends.
            if (rows.Count < batch)
                break;
        }

        return done;

        static IReadOnlyList<int>? ParseVotes(string? joined)
        {
            if (string.IsNullOrWhiteSpace(joined))
                return null;
            var parts = SplitStyles(joined);
            var votes = new List<int>(parts.Count);
            foreach (var part in parts)
                votes.Add(int.TryParse(part, out var n) ? n : 0);
            return votes;
        }
    }

    /// <summary>
    /// The genre and the styles that the catalogues give for each album folder.
    ///
    /// <para><b>Discogs first, MusicBrainz where Discogs had nothing.</b> Discogs draws a styles
    /// distinction and MusicBrainz does not, and narrower is what a mood list needs. A Discogs answer
    /// that was empty falls through to MusicBrainz rather than standing as the last word, because
    /// "asked and told nothing" is not a reason to leave a record undescribed when another catalogue
    /// has an answer.</para>
    ///
    /// <para>Read once and passed around, the same way <see cref="MoodBands.GenreByFolder"/> works. A
    /// lookup per track over a table this size would repeat the same read about seventeen thousand
    /// times.</para>
    /// </summary>
    public IReadOnlyDictionary<string, CatalogueFacts> CatalogueFactsByFolder()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT a.folder,
                       COALESCE(NULLIF(d.mapped_genre,  ''), m.mapped_genre),
                       COALESCE(NULLIF(d.mapped_styles, ''), m.mapped_styles)
                FROM album_meta a
                LEFT JOIN release_meta d
                       ON d.provider = a.release_provider AND d.release_id = a.release_id
                LEFT JOIN release_meta m
                       ON m.provider = $group AND m.release_id = a.release_group_id
                WHERE d.provider IS NOT NULL OR m.provider IS NOT NULL
                """;
            cmd.Parameters.AddWithValue("$group", ReleaseGroupProvider);

            using var reader = cmd.ExecuteReader();
            var byFolder = new Dictionary<string, CatalogueFacts>(StringComparer.OrdinalIgnoreCase);
            while (reader.Read())
            {
                var folder = reader.GetString(0);
                var genre = reader.IsDBNull(1) ? null : reader.GetString(1);
                var styles = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (genre is null && string.IsNullOrWhiteSpace(styles))
                    continue;
                byFolder[folder] = new CatalogueFacts(genre, SplitStyles(styles));
            }

            return byFolder;
        }
    }

    /// <summary>One album whose release nobody has asked a catalogue about: where it is, and the
    /// release it is already linked to.</summary>
    public sealed record ReleaseToAsk(string Folder, string Provider, string ReleaseId);

    /// <summary>
    /// The next linked releases that no catalogue has been asked about.
    ///
    /// <para><b>Only the ones already linked. That is the whole point of the pass.</b> A linked album
    /// is one request by id: exact, cheap, and with no search to get wrong. An album with no link would
    /// need a search per record against the user's own rate limit, and a wrong match there writes a
    /// wrong vocabulary onto a record that had none.</para>
    ///
    /// <para>What makes the pass finite is the row, not the answer. A release asked about and filed
    /// under nothing still has a row, so it does not come back. Results are grouped by the release
    /// rather than listed by the folder, because two folders holding the same record are one question.</para>
    /// </summary>
    public IReadOnlyList<ReleaseToAsk> NextReleasesToAsk(string provider, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT MIN(a.folder), a.release_provider, a.release_id
                FROM album_meta a
                LEFT JOIN release_meta r
                       ON r.provider = a.release_provider AND r.release_id = a.release_id
                WHERE r.provider IS NULL
                  AND a.release_provider = $provider
                  AND a.release_id IS NOT NULL AND TRIM(a.release_id) <> ''
                GROUP BY a.release_provider, a.release_id
                ORDER BY MIN(a.folder)
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$provider", provider);
            cmd.Parameters.AddWithValue("$limit", Math.Max(0, limit));

            using var reader = cmd.ExecuteReader();
            var rows = new List<ReleaseToAsk>();
            while (reader.Read())
                rows.Add(new ReleaseToAsk(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            return rows;
        }
    }

    /// <summary>How many albums are linked to a catalogue, and how many of those have been asked.
    /// Lists built on this use it as their coverage: an album with no answer is simply not narrowed.</summary>
    public ReleaseCoverage CountReleaseCoverage()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT
                  (SELECT COUNT(*) FROM album_meta),
                  (SELECT COUNT(*) FROM album_meta
                    WHERE release_id IS NOT NULL AND TRIM(release_id) <> ''),
                  (SELECT COUNT(*) FROM album_meta a JOIN release_meta r
                      ON r.provider = a.release_provider AND r.release_id = a.release_id),
                  (SELECT COUNT(*) FROM album_meta a JOIN release_meta r
                      ON r.provider = a.release_provider AND r.release_id = a.release_id
                    WHERE r.mapped_styles IS NOT NULL AND TRIM(r.mapped_styles) <> '')
                """;

            using var reader = cmd.ExecuteReader();
            return reader.Read()
                ? new ReleaseCoverage(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3))
                : new ReleaseCoverage(0, 0, 0, 0);
        }
    }

    private static ReleaseMeta ReadRow(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        r.GetString(0),
        r.GetString(1),
        r.IsDBNull(2) ? null : r.GetString(2),
        r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.IsDBNull(5) ? null : r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6),
        r.GetInt32(7),
        r.GetInt64(8));
}

/// <summary>What the catalogues say about one album: the genre they file it under, and every style RPM
/// recognises among their answers.</summary>
/// <param name="Genre">One of <see cref="Genres.All"/>, or null where no catalogue named one.</param>
/// <param name="Styles">Names from <see cref="Subgenres.All"/>. Empty is an ordinary answer.</param>
public sealed record CatalogueFacts(string? Genre, IReadOnlyList<string> Styles);

/// <summary>How far the catalogues have got through a collection.</summary>
/// <param name="Albums">Every album with a row of its own.</param>
/// <param name="Linked">Those linked to a catalogue release.</param>
/// <param name="Asked">Those whose release has been asked about.</param>
/// <param name="WithStyles">Those whose release came back with at least one style RPM knows.</param>
public sealed record ReleaseCoverage(int Albums, int Linked, int Asked, int WithStyles);
