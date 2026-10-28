namespace RPM.Core.Library;

public sealed partial class LibraryDatabase
{
    /// <summary>One album that no release of a catalogue is linked to, as the link pass reads it.</summary>
    /// <param name="Artist">The album artist, or the track artist where none is tagged, as most of its tracks say.</param>
    /// <param name="Album">The album's name, as most of its tracks say.</param>
    /// <param name="Lengths">Every track's length in seconds, in play order: discs in turn, tracks in number.</param>
    /// <param name="Titles">Every track's title, in the same order.</param>
    /// <param name="HasStyle">Whether anything already gives it a style: a track's style field, a genre tag that
    /// lists several, or styles stored for the folder.</param>
    public sealed record AlbumToLink(
        string Folder, string Artist, string Album, IReadOnlyList<double> Lengths, bool HasStyle,
        IReadOnlyList<string> Titles);

    /// <summary>
    /// Every album that is not linked to a release of <paramref name="provider"/>, the albums with no style first,
    /// since those are the ones a link gives the most.
    /// </summary>
    public IReadOnlyList<AlbumToLink> AlbumsNotLinkedTo(string provider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT t.folder,
                       COALESCE(NULLIF(TRIM(t.album_artist), ''), COALESCE(t.artist, '')),
                       COALESCE(t.album, ''),
                       COALESCE(t.duration_seconds, 0),
                       COALESCE(t.subgenre, ''), COALESCE(t.genre, ''), COALESCE(a.styles, ''),
                       COALESCE(t.title, '')
                FROM tracks t
                LEFT JOIN album_meta a ON a.folder = t.folder
                WHERE NOT (COALESCE(a.release_provider, '') = $provider AND TRIM(COALESCE(a.release_id, '')) <> '')
                  AND TRIM(COALESCE(a.discogs_release_id, '')) = ''
                ORDER BY t.folder, COALESCE(t.disc_number, 0), COALESCE(t.track_number, 0), t.file_name
                """;
            cmd.Parameters.AddWithValue("$provider", provider);

            var albums = new List<AlbumToLink>();
            using var reader = cmd.ExecuteReader();

            string? folder = null;
            var artists = new Dictionary<string, int>(StringComparer.Ordinal);
            var names = new Dictionary<string, int>(StringComparer.Ordinal);
            var lengths = new List<double>();
            var titles = new List<string>();
            var hasStyle = false;

            while (reader.Read())
            {
                // The folder column compares without case, so one folder can be spelled two ways across its rows
                // ("Year of the Cat", "Year Of The Cat"), and the rows come sorted together.
                var at = reader.GetString(0);
                if (!string.Equals(at, folder, StringComparison.OrdinalIgnoreCase))
                {
                    Close();
                    folder = at;
                }

                Count(artists, reader.GetString(1));
                Count(names, reader.GetString(2));
                lengths.Add(reader.GetDouble(3));
                titles.Add(reader.GetString(7));
                hasStyle |= reader.GetString(4).Trim().Length > 0
                            || reader.GetString(5).IndexOfAny([',', ';', '|']) >= 0
                            || reader.GetString(6).Trim().Length > 0;
            }
            Close();

            return [.. albums.OrderBy(a => a.HasStyle).ThenBy(a => a.Folder, StringComparer.OrdinalIgnoreCase)];

            void Close()
            {
                if (folder is not null && lengths.Count > 0)
                    albums.Add(new AlbumToLink(folder, Most(artists), Most(names), [.. lengths], hasStyle, [.. titles]));
                artists.Clear();
                names.Clear();
                lengths.Clear();
                titles.Clear();
                hasStyle = false;
            }
        }

        static void Count(Dictionary<string, int> counts, string value)
        {
            if (value.Trim() is { Length: > 0 } v)
                counts[v] = counts.GetValueOrDefault(v) + 1;
        }

        static string Most(Dictionary<string, int> counts) =>
            counts.Count == 0 ? "" : counts.MaxBy(p => p.Value).Key;
    }
}
