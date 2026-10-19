namespace RPM.Core.Library;

/// <summary>
/// The songs and records the listener has loved: the per-track favourite flag and the favourite
/// albums that the wall and the list can be narrowed to. The generated playlist made from the
/// favourite songs is with the other generated ones (<see cref="SmartPlaylistKind"/>).
///
/// <para>A favourite belongs to the row, never to the file. Writing it into the tags would put one
/// listener's taste into every copy of that file, and it would be lost the first time someone
/// else's tagger rewrote the tags.</para>
/// </summary>
public sealed partial class LibraryDatabase
{
    // ── one song ──

    /// <summary>
    /// Marks (or unmarks) the song at <paramref name="relativePath"/> as a favourite, stamping the time
    /// it was said. Clearing sets the stamp back to zero, so a song loved twice is ordered
    /// newest-first by the second time and not by the first.
    /// </summary>
    /// <para>The row is stamped as edited here as well, which puts it in front of the next
    /// catalogue pass. A favourite is not written into the file, so nothing else about the row changes
    /// when one is made, and without the stamp the mark would stay on this computer for ever. See
    /// <see cref="Cloud.CloudSyncPlan.ChangedHere"/>.</para>
    /// <returns>False when no row has that path, for example a file previewed from outside the library.</returns>
    public bool SetTrackFavorite(string relativePath, bool favorite, long atUtcTicks)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE tracks SET
                    favorite = $fav, favorited_utc_ticks = $at, metadata_edited_utc_ticks = $now
                WHERE relative_path = $path
                """;
            cmd.Parameters.AddWithValue("$fav", favorite ? 1 : 0);
            cmd.Parameters.AddWithValue("$at", favorite ? atUtcTicks : 0);
            cmd.Parameters.AddWithValue("$now", atUtcTicks);
            cmd.Parameters.AddWithValue("$path", relativePath);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// Writes whether a song FITS the kind of list the library builds. This is the ONE door, and the
    /// reason <see cref="TrackVerdict"/> is a value and not two flags.
    ///
    /// <para><b>It does not touch the favourite flag, and that separation is the whole point.</b> A
    /// favourite is a statement about the song; this is a statement about the pick. A record can be a
    /// favourite and a bad fit for a calm list at the same time. If the two were combined, pressing
    /// "like" would silently mark the song as a favourite, which is a different claim from the one
    /// the listener made.</para>
    ///
    /// <para>Both of its own columns are written in one statement, always, so there is no moment and no
    /// failure in which a song is both liked and turned down. The alternative is a rule ("clear the
    /// other one") written wherever either is set, and a rule written in six places holds in
    /// five.</para>
    ///
    /// <para>The row is stamped as edited here as well, which puts it in front of the next
    /// catalogue pass. A verdict is not written into the file, so nothing else about the row changes
    /// when one is made, and without the stamp the mark would stay on this computer for ever. See
    /// <see cref="Cloud.CloudSyncPlan.ChangedHere"/>.</para>
    ///
    /// <para><b>It is local and does not travel.</b> The store carries a favourite flag and has no
    /// column for either of these, so a song judged here is not judged anywhere else. This is stated
    /// here on purpose: a signal that silently failed to sync would look like a feature that does not
    /// work.</para>
    /// </summary>
    /// <returns>False when no row has that path, for example a file previewed from outside the library.</returns>
    public bool SetTrackVerdict(
        string relativePath, SmartPlaylistKind kind, TrackVerdict verdict, long atUtcTicks)
    {
        using (_gate.Take())
        {
            // The track row is what says the path is ours to judge, and it also carries the edit
            // stamp. It is updated first, so a path that the mirror does not hold returns false
            // without leaving a verdict row behind for a song this library has never seen.
            using var stamp = _connection.CreateCommand();
            stamp.CommandText =
                "UPDATE tracks SET metadata_edited_utc_ticks = $now WHERE relative_path = $path";
            stamp.Parameters.AddWithValue("$now", atUtcTicks);
            stamp.Parameters.AddWithValue("$path", relativePath);
            if (stamp.ExecuteNonQuery() == 0)
                return false;

            using var cmd = _connection.CreateCommand();

            // Taking a verdict back removes the row and does not store None. A row that says nothing
            // still has to be read to find that out. Every generated list asks this table "is there
            // one", and an absent row answers that fastest and most plainly.
            cmd.CommandText = verdict == TrackVerdict.None
                ? "DELETE FROM track_verdict WHERE relative_path = $path AND kind = $kind"
                : """
                  INSERT INTO track_verdict (relative_path, kind, verdict, said_utc_ticks)
                  VALUES ($path, $kind, $verdict, $at)
                  ON CONFLICT(relative_path, kind) DO UPDATE SET
                      verdict = excluded.verdict, said_utc_ticks = excluded.said_utc_ticks
                  """;
            cmd.Parameters.AddWithValue("$path", relativePath);
            cmd.Parameters.AddWithValue("$kind", (int)kind);
            if (verdict != TrackVerdict.None)
            {
                cmd.Parameters.AddWithValue("$verdict", (int)verdict);
                cmd.Parameters.AddWithValue("$at", atUtcTicks);
            }
            cmd.ExecuteNonQuery();
            return true;
        }
    }

    /// <summary>
    /// What the listener has said about one song.
    ///
    /// <para>It is read when it is asked for and not remembered. A verdict can be written from the
    /// transport, from a track line, from the album view and from the full-screen player, and a value
    /// read when the consumer needs it is right whoever wrote it.</para>
    ///
    /// <para>It returns <see cref="TrackVerdict.None"/> for a path the mirror does not have, which is
    /// the same answer it gives for a song nobody has an opinion about. A caller that needs the two
    /// apart should ask <see cref="HoldsTrack"/>.</para>
    /// </summary>
    public TrackVerdict TrackVerdictOf(string relativePath, SmartPlaylistKind kind)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                "SELECT verdict FROM track_verdict WHERE relative_path = $path AND kind = $kind";
            cmd.Parameters.AddWithValue("$path", relativePath);
            cmd.Parameters.AddWithValue("$kind", (int)kind);

            // No row means nobody has said anything, which is true of almost every pair. The absence
            // IS the answer.
            return cmd.ExecuteScalar() is long said ? (TrackVerdict)said : TrackVerdict.None;
        }
    }

    /// <summary>
    /// Every song the listener has said BELONGS, and the lists they said it of.
    ///
    /// <para>It is read in one pass and handed to <see cref="MoodShelf.Build"/>, because a like is
    /// asked about every song in a cut, and asking per song would be one query per row. The refusals
    /// are not here: they are asked in SQL, on the list's own query, where the rows can be excluded
    /// before they are ever read (see <c>LibraryDatabase.NotTurnedDown</c>).</para>
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlySet<SmartPlaylistKind>> LikedByKind()
    {
        lock (_readGate)
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                $"SELECT relative_path, kind FROM track_verdict WHERE verdict = {(int)TrackVerdict.Liked}";

            var byPath = new Dictionary<string, HashSet<SmartPlaylistKind>>(StringComparer.OrdinalIgnoreCase);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(0);
                if (!byPath.TryGetValue(path, out var kinds))
                    byPath[path] = kinds = [];
                kinds.Add((SmartPlaylistKind)reader.GetInt64(1));
            }

            return byPath.ToDictionary(
                p => p.Key, p => (IReadOnlySet<SmartPlaylistKind>)p.Value, StringComparer.OrdinalIgnoreCase);
        }
    }

    // ── an act, or a record ──

    /// <summary>
    /// Tells one kind of list to stop offering everything by an act, or everything on a record, or
    /// lets it offer them again.
    ///
    /// <para><b>It is the same statement as a dislike, about a bigger subject.</b> A song turned down
    /// is one song. This says "not this act, not in this kind of list", which is what a listener
    /// actually wants to say when the third song by the same band comes up in a list they did not ask
    /// it to be in. It is acted on in the same way and in the same places (see <c>NotTurnedDown</c>
    /// and <see cref="RecommendRefusals"/>), so there is one idea here and not two.</para>
    ///
    /// <para><b>It is keyed by the subject and not by the songs, which is what lets it outlive
    /// them.</b> Writing a verdict onto every track of a record would refuse only the songs that are
    /// here today. The album's second disc imported next week, or the record bought again after being
    /// deleted, would come back and offer itself. A refusal is about the act or the record, so it is
    /// stored as one.</para>
    ///
    /// <para><b>It applies to ONE kind of list</b>, for the reason given in
    /// <see cref="SetTrackVerdict"/>: the listener is saying this act does not belong in THIS kind of
    /// list. The other lists offer them as before.</para>
    ///
    /// <para>Nothing is stamped as edited, unlike the marks above: there is no track row to stamp, and
    /// the store has no column for this. It is local and does not travel, exactly as a verdict does.</para>
    /// </summary>
    /// <param name="subject">The folder for a record, the credit for an act. It is trimmed here, so a
    /// subject written with a stray space still matches the one that a query folds.</param>
    public void SetRecommendRefusal(
        RecommendScope scope, string subject, SmartPlaylistKind kind, bool refused, long atUtcTicks)
    {
        ArgumentNullException.ThrowIfNull(subject);
        subject = subject.Trim();
        if (subject.Length == 0)
            return;   // an empty name is not an act, and an empty folder is not a record

        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();

            // Taking it back deletes the row and does not store "allowed". A row that says nothing is
            // still a row, and this table is keyed by a subject that the collection may no longer hold.
            cmd.CommandText = refused
                ? """
                  INSERT INTO recommend_refusal (scope, subject, kind, said_utc_ticks)
                  VALUES ($scope, $subject, $kind, $at)
                  ON CONFLICT(scope, subject, kind) DO UPDATE SET said_utc_ticks = excluded.said_utc_ticks
                  """
                : "DELETE FROM recommend_refusal WHERE scope = $scope AND subject = $subject AND kind = $kind";

            cmd.Parameters.AddWithValue("$scope", (int)scope);
            cmd.Parameters.AddWithValue("$subject", subject);
            cmd.Parameters.AddWithValue("$kind", (int)kind);
            if (refused)
                cmd.Parameters.AddWithValue("$at", atUtcTicks);
            cmd.ExecuteNonQuery();
        }

        // The held answer is now wrong. It is dropped after the write and outside the write gate, so
        // the next reader rebuilds it from the table and not from anything remembered here.
        using (_cuts.Take())
            _refusalsSaid = null;
    }

    /// <summary>
    /// Every act and record the listener has refused, in one read.
    ///
    /// <para>It is read in one pass and handed round, as <see cref="LikedByKind"/> is. The pool asks
    /// this about every song it holds, and a query per song would be a round trip per row.</para>
    /// </summary>
    public RecommendRefusals RecommendRefusalsSaid()
    {
        using (_cuts.Take())
            if (_refusalsSaid is { } held)
                return held;

        var read = ReadRecommendRefusals();

        using (_cuts.Take())
            return _refusalsSaid ??= read;
    }

    /// <summary>
    /// What the table said, held until somebody changes it.
    ///
    /// <para><b>It is held because of who asks and how often.</b> Every mood list composes its pool
    /// against the refusals, and the playlist pane composes eighty-four of them in a row. Each ask
    /// would take the reading connection and run a query. That was not visible while composition ran
    /// inside a gate the caller already held (the take was reentrant and free). It became visible
    /// when composition moved out of that gate: 69 waits and 16.5 seconds of queueing in one
    /// start-up, for a table with one row in it.</para>
    ///
    /// <para>It is dropped by <see cref="SetRecommendRefusal"/>, which is the only thing that writes
    /// the table, so a refusal made at the menu is gone from the next list composed. It is not held
    /// for the whole session like the mood cut beside it, because what a listener has just refused
    /// must leave the lists at once.</para>
    /// </summary>
    private RecommendRefusals? _refusalsSaid;

    /// <inheritdoc cref="RecommendRefusalsSaid"/>
    private RecommendRefusals ReadRecommendRefusals()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT scope, subject, kind FROM recommend_refusal";

            var said = new List<(RecommendScope, string, SmartPlaylistKind)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                said.Add((
                    (RecommendScope)reader.GetInt64(0),
                    reader.GetString(1),
                    (SmartPlaylistKind)reader.GetInt64(2)));

            return said.Count == 0 ? RecommendRefusals.None : new RecommendRefusals(said);
        }
    }

    /// <summary>
    /// Takes another machine's word for one song's mark: the same write, without the edit stamp.
    ///
    /// <para>The stamp says "this computer changed something and has not sent it". A mark that
    /// arrived FROM the store counts as already sent, and stamping it would make this machine push
    /// the store's own value straight back on the next pass: two installs arguing about a
    /// favourite that neither of them touched.</para>
    /// </summary>
    public void AcceptTrackFavorite(string relativePath, bool favorite, long favoritedUtcTicks)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE tracks SET favorite = $fav, favorited_utc_ticks = $at
                WHERE relative_path = $path
                """;
            cmd.Parameters.AddWithValue("$fav", favorite ? 1 : 0);
            cmd.Parameters.AddWithValue("$at", favorite ? favoritedUtcTicks : 0);
            cmd.Parameters.AddWithValue("$path", relativePath);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Whether one song is a favourite. False for a path the library does not hold.</summary>
    public bool IsTrackFavorite(string relativePath)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT favorite FROM tracks WHERE relative_path = $path LIMIT 1";
            cmd.Parameters.AddWithValue("$path", relativePath);
            var value = cmd.ExecuteScalar();
            return value is not null and not DBNull && Convert.ToInt64(value) != 0;
        }
    }

    /// <summary>
    /// Which of <paramref name="relativePaths"/> are favourites. It is one query for a whole tracklist
    /// and not a round trip per line, which is what opening a record's drawer would otherwise cost.
    /// </summary>
    public IReadOnlySet<string> FavoriteTracksAmong(IEnumerable<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);

        var wanted = new HashSet<string>(relativePaths, StringComparer.OrdinalIgnoreCase);
        var favorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
            return favorites;

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT relative_path FROM tracks WHERE favorite = 1";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(0);
                if (wanted.Contains(path))
                    favorites.Add(path);
            }
        }
        return favorites;
    }

    // ── whole records ──

    /// <summary>
    /// Every album folder the user has marked as a favourite. The wall and the album list narrow
    /// themselves to these when the heart in the header is on.
    ///
    /// <para>It is read straight from <c>album_meta</c>, so a folder that no longer holds anything is
    /// listed too. The views intersect it with the shelf they are showing, which drops it.</para>
    /// </summary>
    public IReadOnlySet<string> FavoriteAlbumFolders()
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT folder FROM album_meta WHERE is_favorite = 1";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                folders.Add(reader.GetString(0));
        }
        return folders;
    }
}
