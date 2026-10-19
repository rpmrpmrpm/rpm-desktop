namespace RPM.Core.Library;

/// <summary>
/// Notes the app keeps about this library, such as markers for one-off passes over its files.
///
/// <para>Not settings. A setting belongs to the person and follows them to whichever library they open
/// next. A note belongs to the library and stays with it. For example, "the one-off pass over these
/// files has run" stops being true when the same person points the app at a different folder.</para>
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>Whether the pass that publishes what this machine knows about which names stand for
    /// one record has been over this library, and at which generation of the pass.</summary>
    public const string RecordingKeysPublishedKey = "recording_keys_published";

    /// <summary>What was noted under this key, or null when nothing was.</summary>
    public string? GetMeta(string key)
    {
        ArgumentNullException.ThrowIfNull(key);

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT value FROM library_meta WHERE key = $key";
            cmd.Parameters.AddWithValue("$key", key);
            var value = cmd.ExecuteScalar();
            return value is DBNull or null ? null : Convert.ToString(value);
        }
    }

    /// <summary>Notes a value against a key, replacing whatever was there.</summary>
    public void SetMeta(string key, string? value)
    {
        ArgumentNullException.ThrowIfNull(key);

        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO library_meta (key, value, noted_utc_ticks)
                VALUES ($key, $value, $at)
                ON CONFLICT(key) DO UPDATE SET
                    value = excluded.value,
                    noted_utc_ticks = excluded.noted_utc_ticks
                """;
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", DateTime.UtcNow.Ticks);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>A note kept as a number, for the generation markers that most of these are. Zero when
    /// there is no note, which is what "never done" reads as.</summary>
    public int GetMetaVersion(string key) =>
        int.TryParse(GetMeta(key), out var version) ? version : 0;
}
