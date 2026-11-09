using System.Globalization;
using System.Text.Json;
using RPM.Core.Migration;

namespace RPM.Core.Tests;

/// <summary>
/// Compares a reader's <see cref="SourceLibrary"/> with the sample's <c>expected/*.json</c>. Both are
/// turned into the same plain shape and written with the same serializer, so the comparison is one
/// string compare, and a failure shows the two JSON texts side by side.
/// </summary>
internal static class SourceJson
{
    private sealed record FileDto(string? local, string? remote, string? extension, long? size);
    private sealed record TrackDto(
        string sourceId, FileDto file, string title, string artist, string album, string albumArtist,
        int trackNumber, int discNumber, double durationSeconds, long sizeBytes, int rating, bool favorite,
        int plays, string? lastPlayed, string? added);
    private sealed record PlaylistDto(string sourceId, string name, string[] folder, bool smart, string[] trackIds, string[] unresolved);
    private sealed record SkipDto(string what, string reasonKey);
    private sealed record LibraryDto(string kind, string libraryId, string name, TrackDto[] tracks, PlaylistDto[] playlists, SkipDto[] skipped);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string AssetPath(params string[] parts) =>
        Path.Combine([AppContext.BaseDirectory, "Assets", "Migration", .. parts]);

    public static string Expected(string name)
    {
        var dto = JsonSerializer.Deserialize<LibraryDto>(File.ReadAllText(AssetPath("expected", name + ".json")))!;
        return JsonSerializer.Serialize(dto with
        {
            tracks = [.. dto.tracks.Select(t => t with { lastPlayed = Date(t.lastPlayed), added = Date(t.added) })],
        }, Options);
    }

    public static string Of(SourceLibrary library) => JsonSerializer.Serialize(new LibraryDto(
        library.Kind.ToString().ToLowerInvariant(),
        library.LibraryId,
        library.Name,
        [.. library.Tracks.Select(t => new TrackDto(
            t.SourceId,
            t.File switch
            {
                LocalFile l => new FileDto(l.Path, null, null, null),
                RemoteFile r => new FileDto(null, r.Key, r.Extension, r.Size),
                _ => throw new InvalidOperationException(),
            },
            t.Title, t.Artist, t.Album, t.AlbumArtist, t.TrackNumber, t.DiscNumber, t.DurationSeconds, t.SizeBytes,
            t.Rating, t.Favorite, t.Plays, Date(t.LastPlayed), Date(t.Added)))],
        [.. library.Playlists.Select(p => new PlaylistDto(p.SourceId, p.Name, [.. p.Folder], p.Smart, [.. p.TrackIds], [.. p.Unresolved]))],
        [.. library.Skipped.Select(s => new SkipDto(s.What, s.ReasonKey))]), Options);

    private static string? Date(string? text) =>
        text is null ? null : Date(DateTimeOffset.Parse(text, CultureInfo.InvariantCulture));

    private static string? Date(DateTimeOffset? at) =>
        at?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
