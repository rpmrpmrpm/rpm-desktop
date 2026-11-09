namespace RPM.Core.Library;

/// <summary>
/// What an import from another music app (MusicBee, iTunes, Jellyfin, Plex, Navidrome, playlist
/// files) writes into the library beside the tracks themselves.
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>
    /// Moves the date a track joined the library earlier, never later. An import from another app
    /// knows when the track joined that library, which is the earlier and truer date. Zero is ignored.
    /// </summary>
    public void SetAddedEarlier(long trackId, long utcTicks)
    {
        if (utcTicks <= 0)
            return;

        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                "UPDATE tracks SET added_utc_ticks = $at WHERE id = $id AND (added_utc_ticks = 0 OR added_utc_ticks > $at)";
            cmd.Parameters.AddWithValue("$at", utcTicks);
            cmd.Parameters.AddWithValue("$id", trackId);
            cmd.ExecuteNonQuery();
        }
    }
}
