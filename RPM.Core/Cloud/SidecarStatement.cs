using System.Text.Json;
using RPM.Core.Models;

namespace RPM.Core.Cloud;

/// <summary>
/// A sidecar together with the names of the fields its JSON states.
///
/// <para>A field the JSON leaves out is "not known there". A field it states as empty (null, an empty
/// string or zero) is "empty there". Once the JSON is read into a <see cref="TagSidecar"/> the two look the
/// same, so the names are kept beside it. A sidecar written by this app from a file leaves out every empty
/// text field and states every number. One written by the web app's editors keeps what it read and states
/// every field it emptied.</para>
///
/// <para>This is what lets an edit made somewhere else reach a file on this disk, an emptied field
/// included, without clearing a field that an older sidecar never carried.</para>
/// </summary>
public sealed record SidecarStatement(TagSidecar Sidecar, IReadOnlySet<string> Stated)
{
    /// <summary>The sidecar in <paramref name="json"/>, or null when it is not a sidecar this app can
    /// read.</summary>
    public static SidecarStatement? Read(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        if (TagSidecar.FromJson(json) is not { } sidecar)
            return null;

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            var stated = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                stated.Add(property.Name);
            return new SidecarStatement(sidecar, stated);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The tags this sidecar states, written over <paramref name="info"/>. A tag it does not
    /// state keeps the value in <paramref name="info"/>. The picture is not handled here, see
    /// <see cref="ArtOver"/>.</summary>
    public TrackInfo Over(TrackInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        foreach (var (name, take) in Tags)
            if (Stated.Contains(name))
                info = take(info, Sidecar);
        return info;
    }

    /// <summary>
    /// The picture to write into a file whose embedded front cover is <paramref name="current"/>: the
    /// sidecar's picture when it differs, an empty array when the sidecar states that there is none and the
    /// file has one, and null when the file is to keep what it has (the sidecar does not state a picture, or
    /// states the same one).
    /// </summary>
    public byte[]? ArtOver(byte[]? current)
    {
        if (!Stated.Contains(nameof(TagSidecar.Art)))
            return null;

        if (Sidecar.Art is not { Length: > 0 } wanted)
            return current is { Length: > 0 } ? [] : null;

        return current is not null && current.AsSpan().SequenceEqual(wanted) ? null : wanted;
    }

    /// <summary>
    /// Every field of <see cref="TagSidecar"/> that is a tag in the file, by its name in the JSON, and how
    /// it is written over a <see cref="TrackInfo"/>. A test checks that every field of the sidecar is
    /// either here or in <see cref="NotTags"/>, so a field added to the sidecar cannot be left out of
    /// both.
    /// </summary>
    internal static readonly (string Name, Func<TrackInfo, TagSidecar, TrackInfo> Take)[] Tags =
    [
        (nameof(TagSidecar.Title), (i, s) => i with { Title = Text(s.Title) }),
        (nameof(TagSidecar.Artist), (i, s) => i with { Artist = Text(s.Artist) }),
        (nameof(TagSidecar.AlbumArtist), (i, s) => i with { AlbumArtist = Text(s.AlbumArtist) }),
        (nameof(TagSidecar.Album), (i, s) => i with { Album = Text(s.Album) }),
        (nameof(TagSidecar.Genre), (i, s) => i with { Genre = Text(s.Genre) }),
        (nameof(TagSidecar.Subgenre), (i, s) => i with { Subgenre = Text(s.Subgenre) }),
        (nameof(TagSidecar.TrackNumber), (i, s) => i with { TrackNumber = s.TrackNumber }),
        (nameof(TagSidecar.TrackCount), (i, s) => i with { TrackCount = s.TrackCount }),
        (nameof(TagSidecar.DiscNumber), (i, s) => i with { DiscNumber = s.DiscNumber }),
        (nameof(TagSidecar.DiscCount), (i, s) => i with { DiscCount = s.DiscCount }),
        (nameof(TagSidecar.DiscSubtitle), (i, s) => i with { DiscSubtitle = Text(s.DiscSubtitle) }),
        (nameof(TagSidecar.Year), (i, s) => i with { Year = s.Year }),
        (nameof(TagSidecar.Composers), (i, s) => i with { Composers = Text(s.Composers) }),
        (nameof(TagSidecar.Conductor), (i, s) => i with { Conductor = Text(s.Conductor) }),
        (nameof(TagSidecar.Comment), (i, s) => i with { Comment = Text(s.Comment) }),
        (nameof(TagSidecar.Lyrics), (i, s) => i with { Lyrics = Text(s.Lyrics) }),
        (nameof(TagSidecar.Grouping), (i, s) => i with { Grouping = Text(s.Grouping) }),
        (nameof(TagSidecar.Bpm), (i, s) => i with { Bpm = s.Bpm }),
        (nameof(TagSidecar.Copyright), (i, s) => i with { Copyright = Text(s.Copyright) }),
        (nameof(TagSidecar.Publisher), (i, s) => i with { Publisher = Text(s.Publisher) }),
        (nameof(TagSidecar.RemixedBy), (i, s) => i with { RemixedBy = Text(s.RemixedBy) }),
        (nameof(TagSidecar.Isrc), (i, s) => i with { Isrc = Text(s.Isrc) }),
        (nameof(TagSidecar.MusicalKey), (i, s) => i with { MusicalKey = Text(s.MusicalKey) }),
        (nameof(TagSidecar.ReleaseCountry), (i, s) => i with { ReleaseCountry = Text(s.ReleaseCountry) }),
        (nameof(TagSidecar.Advisory), (i, s) => i with { Advisory = s.Advisory }),
        (nameof(TagSidecar.TitleSort), (i, s) => i with { TitleSort = Text(s.TitleSort) }),
        (nameof(TagSidecar.ArtistSort), (i, s) => i with { ArtistSort = Text(s.ArtistSort) }),
        (nameof(TagSidecar.AlbumArtistSort), (i, s) => i with { AlbumArtistSort = Text(s.AlbumArtistSort) }),
        (nameof(TagSidecar.ComposerSort), (i, s) => i with { ComposerSort = Text(s.ComposerSort) }),
        (nameof(TagSidecar.Work), (i, s) => i with { Work = Text(s.Work) }),
        (nameof(TagSidecar.MovementName), (i, s) => i with { MovementName = Text(s.MovementName) }),
        (nameof(TagSidecar.MovementNumber), (i, s) => i with { MovementNumber = s.MovementNumber }),
        (nameof(TagSidecar.MovementCount), (i, s) => i with { MovementCount = s.MovementCount }),
        (nameof(TagSidecar.Lyricist), (i, s) => i with { Lyricist = Text(s.Lyricist) }),
        (nameof(TagSidecar.Arranger), (i, s) => i with { Arranger = Text(s.Arranger) }),
        (nameof(TagSidecar.Ensemble), (i, s) => i with { Ensemble = Text(s.Ensemble) }),
        (nameof(TagSidecar.Soloists), (i, s) => i with { Soloists = Text(s.Soloists) }),
        (nameof(TagSidecar.Barcode), (i, s) => i with { Barcode = Text(s.Barcode) }),
        (nameof(TagSidecar.CatalogNumber), (i, s) => i with { CatalogNumber = Text(s.CatalogNumber) }),
        (nameof(TagSidecar.MusicBrainzDiscId), (i, s) => i with { MusicBrainzDiscId = Text(s.MusicBrainzDiscId) }),
        (nameof(TagSidecar.Media), (i, s) => i with { Media = Text(s.Media) }),
        (nameof(TagSidecar.ReleaseType), (i, s) => i with { ReleaseType = Library.ReleaseTypes.FromStored(s.ReleaseType) }),
        (nameof(TagSidecar.OriginalYear), (i, s) => i with { OriginalYear = s.OriginalYear }),
        (nameof(TagSidecar.MusicBrainzRecordingId), (i, s) => i with { MusicBrainzRecordingId = Text(s.MusicBrainzRecordingId) }),
        (nameof(TagSidecar.MusicBrainzReleaseId), (i, s) => i with { MusicBrainzReleaseId = Text(s.MusicBrainzReleaseId) }),
        (nameof(TagSidecar.MusicBrainzReleaseGroupId), (i, s) => i with { MusicBrainzReleaseGroupId = Text(s.MusicBrainzReleaseGroupId) }),
        (nameof(TagSidecar.MusicBrainzArtistId), (i, s) => i with { MusicBrainzArtistId = Text(s.MusicBrainzArtistId) }),
        (nameof(TagSidecar.MusicBrainzReleaseArtistId), (i, s) => i with { MusicBrainzReleaseArtistId = Text(s.MusicBrainzReleaseArtistId) }),
    ];

    /// <summary>The fields of <see cref="TagSidecar"/> that are not tags in the file: where the file is
    /// filed and what it is, which belong to this machine and to the audio, the picture (see
    /// <see cref="ArtOver"/>), and the favourite mark, which the catalogue row carries.</summary>
    internal static readonly IReadOnlySet<string> NotTags = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(TagSidecar.Version), nameof(TagSidecar.RelativePath), nameof(TagSidecar.Extension),
        nameof(TagSidecar.RpmId), nameof(TagSidecar.DurationSeconds), nameof(TagSidecar.Channels),
        nameof(TagSidecar.SampleRate), nameof(TagSidecar.BitsPerSample), nameof(TagSidecar.BitrateKbps),
        nameof(TagSidecar.CodecDescription), nameof(TagSidecar.Art), nameof(TagSidecar.ArtMime),
        nameof(TagSidecar.Favorite), nameof(TagSidecar.FavoritedUtcTicks),
    };

    /// <summary>Empty text is no value, as a tag reader gives it.</summary>
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
