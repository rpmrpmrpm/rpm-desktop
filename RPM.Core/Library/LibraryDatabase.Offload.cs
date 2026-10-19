using RPM.Core.Cloud;

namespace RPM.Core.Library;

/// <summary>A count of tracks and their total size in bytes. Most questions about what is on this disk
/// come down to these two numbers.</summary>
public readonly record struct TrackTally(int Count, long Bytes)
{
    public static readonly TrackTally Nothing = new(0, 0);

    public bool Any => Count > 0;
}

/// <summary>
/// The bookkeeping for tracks that are in the cloud and not on this disk. It separates two kinds:
/// tracks that this install had on its disk (<em>this</em> machine's own), and tracks that came from
/// the catalogue.
///
/// <para>
/// A row that is cloud-only and <c>cloud_adopted = 0</c> is a track this install had on its disk and
/// no longer has. A row that is cloud-only and <c>cloud_adopted = 1</c> came from the catalogue and was
/// never on this disk. Restoring files from the cloud must use only the first set. Otherwise a laptop
/// would download the other machine's entire library onto itself.
/// </para>
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>
    /// Tracks this install put in the cloud whose local files are gone. The user deleted them to make
    /// room, or they were never restored after a drive was replaced.
    ///
    /// <para>This is not the same question as "what is cloud-only". That also includes the tracks the
    /// user's other machines hold. This machine never had those, so it should not download them.</para>
    /// </summary>
    public IReadOnlyList<LibraryTrack> QueryOffloadedHere(int limit = 100_000)
        => QueryOwn(CloudState.CloudOnly, limit);

    /// <summary>How many tracks this machine no longer has a file for, and how much disk space those
    /// files used.</summary>
    public TrackTally OffloadedHereTally() => TallyOwn(CloudState.CloudOnly);

    /// <summary>
    /// Tracks the library shows that have never been on this disk. These are catalogue rows that another
    /// of the user's installs put in the cloud.
    ///
    /// <para>This is the other half of "what is not on this computer". The bytes are the size of the
    /// other machine's files. They show what downloading these tracks would cost, not what this disk
    /// has saved.</para>
    /// </summary>
    public TrackTally FromElsewhereTally()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*), COALESCE(SUM(size_bytes), 0) FROM tracks WHERE cloud_adopted = 1";
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? new TrackTally(reader.GetInt32(0), reader.GetInt64(1)) : TrackTally.Nothing;
        }
    }

    private IReadOnlyList<LibraryTrack> QueryOwn(CloudState state, int limit)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT * FROM tracks
                WHERE cloud_state = $s AND cloud_adopted = 0
                ORDER BY id LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$s", (int)state);
            cmd.Parameters.AddWithValue("$limit", limit);

            using var reader = cmd.ExecuteReader();
            var results = new List<LibraryTrack>();
            while (reader.Read())
                results.Add(ReadTrack(reader));
            return results;
        }
    }

    private TrackTally TallyOwn(CloudState state)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT COUNT(*), COALESCE(SUM(size_bytes), 0) FROM tracks
                WHERE cloud_state = $s AND cloud_adopted = 0
                """;
            cmd.Parameters.AddWithValue("$s", (int)state);

            using var reader = cmd.ExecuteReader();
            return reader.Read() ? new TrackTally(reader.GetInt32(0), reader.GetInt64(1)) : TrackTally.Nothing;
        }
    }
}
