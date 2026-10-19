namespace RPM.Core.Library;

/// <summary>
/// The mood playlists a listener made (<see cref="MoodRecipe"/>): an ordinary playlist filled from a mood's band, and
/// the recipe it was filled from kept as a library note under the playlist's id.
///
/// <para><b>A note, not a column.</b> The recipe belongs to this library, as the notes do, and a schema version for
/// one small value would copy the whole library on the next launch to migrate it. Playlist ids are AUTOINCREMENT and
/// never handed out twice, so a note left behind by a deleted playlist can never be read as another playlist's.</para>
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>The note a playlist's recipe is kept under, followed by the playlist's id.</summary>
    public const string MoodRecipeKeyPrefix = "mood_recipe:";

    /// <summary>The recipe <paramref name="nodeId"/> was filled from, or null for a playlist made any other way.</summary>
    public MoodRecipe? MoodRecipeOf(long nodeId) => MoodRecipe.FromJson(GetMeta(MoodRecipeKeyPrefix + nodeId));

    /// <summary>The songs <paramref name="recipe"/> can draw on, best first, less the acts and records this mood
    /// has been told to stop offering. Its count is what the window shows while the recipe is being made.</summary>
    public MoodPool MoodRecipePool(MoodRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        return SessionMoods.ListFor(recipe.Kind).Matching(recipe).Allowed(RecommendRefusalsSaid(), recipe.Kind);
    }

    /// <summary>The songs one filling of <paramref name="recipe"/> holds, in the order they play, as relative paths.</summary>
    public IReadOnlyList<string> MoodRecipeSongs(MoodRecipe recipe) =>
        MoodRecipePool(recipe).ForRecipe(MoodRecipe.Songs, actsNamed: recipe.Artists.Count > 0, MusicalKeysOf);

    /// <summary>The records one filling of <paramref name="recipe"/> draws on, each once, in the order their first song
    /// plays: what the cover step offers to lay under the frame.</summary>
    public IReadOnlyList<(string Folder, string Album, string Artist)> MoodRecipeRecords(MoodRecipe recipe)
    {
        var songs = MoodRecipeSongs(recipe);
        if (songs.Count == 0)
            return [];
        var byPath = new Dictionary<string, (string Folder, string Album, string Artist)>(StringComparer.Ordinal);
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT shelf.value, tracks.folder, tracks.album, COALESCE(tracks.album_artist, tracks.artist) " +
                "FROM json_each($paths) shelf JOIN tracks ON tracks.relative_path = shelf.value";
            cmd.Parameters.AddWithValue("$paths", System.Text.Json.JsonSerializer.Serialize(songs));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                byPath[reader.GetString(0)] = (
                    reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3));
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return [.. songs.Where(byPath.ContainsKey).Select(p => byPath[p]).Where(r => r.Folder.Length > 0 && seen.Add(r.Folder))];
    }

    /// <summary>Makes a playlist at the root named <paramref name="name"/>, filled from <paramref name="recipe"/>.</summary>
    /// <returns>The new playlist's id.</returns>
    public long CreateMoodPlaylist(string name, MoodRecipe recipe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var id = CreatePlaylist(null, name.Trim());
        FillMoodPlaylist(id, recipe);
        return id;
    }

    /// <summary>Renames <paramref name="nodeId"/> and fills it again from <paramref name="recipe"/>, in place of the
    /// songs it held. Its id, cover, folder and cloud state stay as they were.</summary>
    public void RefillMoodPlaylist(long nodeId, string name, MoodRecipe recipe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        RenamePlaylistNode(nodeId, name.Trim());
        using (_gate.Take())
        {
            if (!IsPlaylistLocked(nodeId))
                return;
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM pl_item WHERE playlist_id = $id";
            cmd.Parameters.AddWithValue("$id", nodeId);
            cmd.ExecuteNonQuery();
        }
        FillMoodPlaylist(nodeId, recipe);
    }

    private void FillMoodPlaylist(long nodeId, MoodRecipe recipe)
    {
        // The recipe first: a playlist with a recipe and no songs can be opened in the window and filled again,
        // one with songs and no recipe cannot.
        SetMeta(MoodRecipeKeyPrefix + nodeId, recipe.ToJson());
        var songs = MoodRecipeSongs(recipe);
        var ids = RpmIdsOf(songs);
        AddSongsToPlaylist(nodeId, songs.Select(p => (ids.GetValueOrDefault(p), p)), AlreadyInPlaylist.AddAnother);
    }

    /// <summary>The rpm id of each of <paramref name="paths"/> that has one, so the playlist keys its songs the way a
    /// hand-made one does. A path with none is keyed by the path alone, which <see cref="AddSongsToPlaylist"/> takes.</summary>
    private Dictionary<string, string?> RpmIdsOf(IReadOnlyList<string> paths)
    {
        var found = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (paths.Count == 0)
            return found;
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "SELECT shelf.value, tracks.rpm_id FROM json_each($paths) shelf " +
                "JOIN tracks ON tracks.relative_path = shelf.value";
            cmd.Parameters.AddWithValue("$paths", System.Text.Json.JsonSerializer.Serialize(paths));
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                found[reader.GetString(0)] = reader.IsDBNull(1) ? null : reader.GetString(1);
        }
        return found;
    }
}
