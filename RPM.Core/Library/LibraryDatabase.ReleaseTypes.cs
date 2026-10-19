namespace RPM.Core.Library;

/// <summary>Reading the release type the files carry, a record at a time. See <see cref="ReleaseType"/>.</summary>
public sealed partial class LibraryDatabase
{
    /// <summary>
    /// The release type of every record whose files carry one, by album folder. Where a record's tracks disagree, the
    /// type most of them carry wins, and the order of <see cref="ReleaseTypes.All"/> breaks a tie, so two reads of one
    /// shelf always agree. A record whose files carry no type is absent, which is what lets the artist page fall back
    /// to the title for it.
    /// </summary>
    public IReadOnlyDictionary<string, ReleaseType> QueryReleaseTypesByFolder()
    {
        var counted = new Dictionary<string, Dictionary<ReleaseType, int>>(StringComparer.OrdinalIgnoreCase);

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT folder, release_type, COUNT(*)
                FROM tracks
                WHERE release_type IS NOT NULL
                GROUP BY folder, release_type
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (ReleaseTypes.FromStored(reader.GetString(1)) is not { } type)
                    continue;

                var folder = reader.GetString(0);
                if (!counted.TryGetValue(folder, out var types))
                    counted[folder] = types = [];
                types[type] = types.GetValueOrDefault(type) + (int)reader.GetInt64(2);
            }
        }

        return counted.ToDictionary(
            entry => entry.Key,
            entry => entry.Value
                .OrderByDescending(t => t.Value)
                .ThenBy(t => ReleaseTypes.All.ToList().IndexOf(t.Key))
                .First().Key,
            StringComparer.OrdinalIgnoreCase);
    }
}
