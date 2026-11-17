namespace RPM.Core.Library;

public sealed partial class LibraryDatabase
{
    /// <summary>
    /// The songs whose title, artist, record or record artist holds <paramref name="asked"/>, ignoring case,
    /// at most <paramref name="most"/> of them, in the order the records are filed.
    /// </summary>
    /// <remarks>
    /// The library search's song group. The rule is the phones' <c>SongSearch.matches</c>: the record's
    /// artist is asked as well as the track's, because a compilation credits somebody else on each track.
    /// Unlike <see cref="Query"/>, the file name is not asked: a search for words is a search for what the
    /// songs are called, and a folder named after the rip would match every track in it.
    ///
    /// <para><b>Capped in the query.</b> One letter matches most of a collection, and a list of thirty
    /// thousand rows is not an answer anybody reads. The caller says so when it shows the cap.</para>
    /// </remarks>
    public IReadOnlyList<LibraryTrack> SearchSongs(string asked, int most)
    {
        var words = (asked ?? "").Trim();
        if (words.Length == 0 || most <= 0)
            return [];

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.Parameters.AddWithValue("$q", "%" + EscapeLike(words) + "%");
            cmd.Parameters.AddWithValue("$most", most);

            var reachable = ReachTerm("cloud_state");
            var matches = """
                          (title LIKE $q ESCAPE '\' OR artist LIKE $q ESCAPE '\'
                            OR album LIKE $q ESCAPE '\' OR album_artist LIKE $q ESCAPE '\')
                          """;

            cmd.CommandText =
                $"SELECT * FROM tracks WHERE {(reachable.Length == 0 ? "" : reachable + " AND ")}{matches} "
                + "ORDER BY relative_path LIMIT $most";

            using var reader = cmd.ExecuteReader();
            var found = new List<LibraryTrack>();
            while (reader.Read())
                found.Add(ReadTrack(reader));
            return found;
        }
    }
}
