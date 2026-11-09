namespace RPM.Core.Migration;

/// <summary>What rpm holds about a track that an import found again: its rating, favourite, date added
/// and last played (UTC ticks, 0 meaning none).</summary>
public sealed record RpmSide(int Rating, bool Favorite, long FavoritedUtcTicks, long AddedUtcTicks, long LastPlayedUtcTicks);

/// <summary>The details to keep after the merge (UTC ticks, 0 meaning none).</summary>
public sealed record MergedDetails(int Rating, bool Favorite, long FavoritedUtcTicks, long AddedUtcTicks, long LastPlayedUtcTicks);

/// <summary>
/// Merges what another app says about a track with what rpm already holds about it. Plays are not
/// here: they are the source library's own device rows (<see cref="SourceLibrary.DeviceId"/>), which
/// add to rpm's.
/// </summary>
public static class MigrationMerge
{
    /// <summary>
    /// <list type="bullet">
    /// <item>Rating: rpm's if it has one, otherwise the source's.</item>
    /// <item>Favourite: a favourite if either says so. rpm's date stays when rpm already had the mark.
    /// The other apps do not say when a song was loved, so a mark that comes only from the source is
    /// dated when the song joined that library, the earliest it can have been made.</item>
    /// <item>Date added: the earlier of the two known dates.</item>
    /// <item>Last played: the later of the two.</item>
    /// </list>
    /// </summary>
    public static MergedDetails Merge(RpmSide rpm, SourceTrack source)
    {
        ArgumentNullException.ThrowIfNull(rpm);
        ArgumentNullException.ThrowIfNull(source);

        var sourceAdded = source.Added?.UtcTicks ?? 0;
        var sourceLast = source.LastPlayed?.UtcTicks ?? 0;

        var favorite = rpm.Favorite || source.Favorite;
        var favoritedAt = rpm.Favorite
            ? rpm.FavoritedUtcTicks
            : source.Favorite ? (sourceAdded > 0 ? sourceAdded : DateTime.UtcNow.Ticks) : 0;

        return new MergedDetails(
            Rating: rpm.Rating > 0 ? rpm.Rating : Math.Clamp(source.Rating, 0, 10),
            Favorite: favorite,
            FavoritedUtcTicks: favoritedAt,
            AddedUtcTicks: Earlier(rpm.AddedUtcTicks, sourceAdded),
            LastPlayedUtcTicks: Math.Max(rpm.LastPlayedUtcTicks, sourceLast));
    }

    private static long Earlier(long a, long b) => a <= 0 ? b : b <= 0 ? a : Math.Min(a, b);
}
