using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Migration;

/// <summary>A track the import gave up on. <paramref name="What"/> is the track as a person names it,
/// <paramref name="ReasonKey"/> a <c>Migration_*</c> string key, <paramref name="Detail"/> the source
/// path where the reason needs one.</summary>
public sealed record NotImportedTrack(string SourceId, string What, string ReasonKey, string? Detail);

/// <summary>Everything the report shows at the end of an import: every track the source listed is in
/// one of the counts, and every track that was not imported is listed with its reason.</summary>
public sealed record MigrationReport(
    string SourceName,
    MigrationCounts Counts,
    IReadOnlyList<NotImportedTrack> NotImported,
    IReadOnlyList<string> LowerQualityInRpm,
    IReadOnlyList<PlaylistOutcome> Playlists,
    IReadOnlyList<SourceSkip> Skipped);

public static class MigrationReports
{
    public static MigrationReport Build(SourceLibrary library, LibraryDatabase.MigrationRecord record, IReadOnlyList<PlaylistOutcome> playlists)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(playlists);

        var bySource = library.Tracks.GroupBy(t => t.SourceId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var notImported = record.Rows(MigrationTrackState.NotImported)
            .Where(r => r.Attempts >= CloudRetrySchedule.MaxAttempts)
            .Select(r => new NotImportedTrack(r.SourceId, What(bySource.GetValueOrDefault(r.SourceId), r.SourceId), r.Reason ?? MigrationReason.ReadFailed, r.Detail))
            .ToList();
        var lower = record.Rows(MigrationTrackState.AlreadyInRpm)
            .Where(r => r.Reason == MigrationReason.LowerQualityInRpm && r.Detail is not null)
            .Select(r => r.Detail!)
            .ToList();

        return new MigrationReport(library.Name, record.Counts(), notImported, lower, playlists, library.Skipped);
    }

    private static string What(SourceTrack? track, string sourceId) => track is null
        ? sourceId
        : track.Artist.Length > 0 ? $"{track.Artist} - {track.Title}" : track.Title;
}
