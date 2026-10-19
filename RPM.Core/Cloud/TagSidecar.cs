using System.Text.Json;
using System.Text.Json.Serialization;
using RPM.Core.Metadata;
using RPM.Core.Models;

namespace RPM.Core.Cloud;

/// <summary>Everything about a track that the canonical blob leaves out on purpose: the user's tags, the
/// cover art, and the id that ties the file to its library row.
///
/// <para>The blob is shared, so it cannot carry any of this. One user's title would show up for everyone
/// else. The sidecar is the private half. It is stored per user at <c>u/{userId}/{rpmId}.meta.json</c>,
/// and it is what makes a restore give back the file the user had, rather than an anonymous stream of
/// audio. The audio comes back bit-identical. The tag container is rebuilt from this record, not copied.
/// </para>
///
/// <para>Art travels inline. System.Text.Json writes the bytes as base64. An album's twelve tracks
/// therefore carry twelve copies of the same cover. That could be de-duplicated in the blob store later,
/// but for now it is not worth the extra round trip.
/// </para>
/// </summary>
public sealed partial record TagSidecar
{
    /// <summary>The version of this record's shape. It is bumped when the shape changes, so that an
    /// older sidecar can still be read. An older sidecar lacks the fields that a newer version added.
    /// Those fields read as zero or null. A sidecar from before version 5 has no favourite mark, so no
    /// track in it is a favourite. Every tag that the file can hold must be carried here, because a
    /// field missing from this record is a field that a downloaded file comes back without (see
    /// <see cref="ToTrackInfo"/>). The favourite mark is the one value here that is not read from the
    /// file. See <see cref="With"/>.</summary>
    public int Version { get; init; } = 5;

    /// <summary>Where the file belongs under the library root, with forward slashes. A restore puts the
    /// file back here.</summary>
    public required string RelativePath { get; init; }

    /// <summary>Lower-case extension, including the dot. It says which container the blob is.</summary>
    public required string Extension { get; init; }

    /// <summary>The id in the file's own tags. A restore writes it back, so the file returns as the same
    /// track.</summary>
    public string? RpmId { get; init; }

    public double DurationSeconds { get; init; }

    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? AlbumArtist { get; init; }
    public string? Album { get; init; }
    public string? Genre { get; init; }

    /// <summary>Which part of its genre, for example "Deep House" under House. It is carried like the
    /// genre above it. Without it, a track that went to the cloud and came back would return with only
    /// half of its genre.</summary>
    public string? Subgenre { get; init; }

    public uint TrackNumber { get; init; }
    public uint TrackCount { get; init; }
    public uint DiscNumber { get; init; }
    public uint DiscCount { get; init; }
    public string? DiscSubtitle { get; init; }
    public uint Year { get; init; }
    public string? Composers { get; init; }
    public string? Conductor { get; init; }
    public string? Comment { get; init; }
    public string? Lyrics { get; init; }
    public string? Grouping { get; init; }
    public uint Bpm { get; init; }
    public string? Copyright { get; init; }
    public string? Publisher { get; init; }
    public string? RemixedBy { get; init; }
    public string? Isrc { get; init; }
    public string? MusicalKey { get; init; }

    /// <summary>Two-letter country of the pressing. A sidecar from before this field existed does not
    /// have it, so it reads as null.</summary>
    public string? ReleaseCountry { get; init; }

    /// <summary>Whether the lyrics are explicit. A sidecar that does not carry this value reads as
    /// <see cref="ContentAdvisory.Unknown"/>, which is the honest answer for it.</summary>
    public ContentAdvisory Advisory { get; init; }

    // ── sort names, the classical block, the pressing, the ids ──
    //
    // Tags that TagLib has no property for are kept here too. A restore rebuilds the tag container from
    // this record and nothing else. A field missing here is a field the user loses in the round trip.
    // A test checks that this record and TrackInfo have the same fields.

    public string? TitleSort { get; init; }
    public string? ArtistSort { get; init; }
    public string? AlbumArtistSort { get; init; }
    public string? ComposerSort { get; init; }

    public string? Work { get; init; }
    public string? MovementName { get; init; }
    public uint MovementNumber { get; init; }
    public uint MovementCount { get; init; }
    public string? Lyricist { get; init; }
    public string? Arranger { get; init; }
    public string? Ensemble { get; init; }
    public string? Soloists { get; init; }

    public string? Barcode { get; init; }
    public string? CatalogNumber { get; init; }

    /// <summary>The disc ID of the CD that the track came from. It is kept with the rest of the pressing
    /// details, so that a phone, which has no files to read tags from, can still say which record it
    /// holds.</summary>
    public string? MusicBrainzDiscId { get; init; }

    public string? Media { get; init; }
    /// <summary>The record's release type by its stored name ("Album", "Ep", "Live"...), or null where nobody
    /// has said. Carried so the phone can shelve an artist's records the way the desktop does.</summary>
    public string? ReleaseType { get; init; }
    public uint OriginalYear { get; init; }

    public string? MusicBrainzRecordingId { get; init; }
    public string? MusicBrainzReleaseId { get; init; }
    public string? MusicBrainzReleaseGroupId { get; init; }
    public string? MusicBrainzArtistId { get; init; }
    public string? MusicBrainzReleaseArtistId { get; init; }

    // ── what the file is, as opposed to what its tags say it holds ──
    //
    // Read from the stream by <see cref="TagReader"/>. They are carried here for the same reason as the
    // rest of the catalogue: a phone that draws an album has no file to read. Zero means "not known".
    // An older sidecar does not have these properties. A lossy codec really has no bit depth, so zero
    // is the correct value there.

    /// <summary>Discrete channels in the stream: 2 for nearly everything, 6 for a 5.1 mix.</summary>
    public int Channels { get; init; }

    public int SampleRate { get; init; }

    /// <summary>Bit depth, where the format has one. Zero for the lossy codecs, which do not.</summary>
    public int BitsPerSample { get; init; }

    public int BitrateKbps { get; init; }

    /// <summary>What the decoder calls the stream. This is the one thing that tells Apple Lossless from
    /// AAC, because both live in a .m4a file. See <see cref="Metadata.AudioFormat.Codec"/>.</summary>
    public string? CodecDescription { get; init; }

    /// <summary>Front-cover bytes, or null when the file had none.</summary>
    public byte[]? Art { get; init; }
    public string? ArtMime { get; init; }

    // ── the listener's own mark ──
    //
    // This is not a tag, and it is deliberately not written into the file. The blob is shared between
    // everybody who has the same encoding, and a favourite belongs to one person. It is kept here, in the
    // private half, so that a library rebuilt from the cloud (on a second computer, or after a restore)
    // still shows the songs that its owner marked as favourites.

    /// <summary>Whether the owner of this sidecar has made the song a favourite.</summary>
    public bool Favorite { get; init; }

    /// <summary>When they said so (UTC ticks), or zero.</summary>
    public long FavoritedUtcTicks { get; init; }

    /// <summary>The sidecar's shape, and how it is written.</summary>
    /// <remarks>On the context rather than a JsonSerializerOptions instance: a published build is
    /// Native AOT, which serializes from generated metadata and not by reflection.</remarks>
    [JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(TagSidecar))]
    private sealed partial class TagSidecarJson : JsonSerializerContext;

    /// <summary>Reads the full set of tags from a file, art included, into a sidecar. Reading the art is
    /// the only expensive part. That is why this does not just copy from a TrackInfo that was already
    /// read.
    /// </summary>
    public static TagSidecar FromFile(string path, string libraryRoot)
    {
        var info = TagReader.Read(path);
        var art = info.HasArt ? TagReader.LoadArt(path) : null;

        return new TagSidecar
        {
            RelativePath = Path.GetRelativePath(libraryRoot, path).Replace(Path.DirectorySeparatorChar, '/'),
            Extension = info.Extension,
            RpmId = info.RpmId,
            DurationSeconds = info.DurationSeconds,
            Title = info.Title,
            Artist = info.Artist,
            AlbumArtist = info.AlbumArtist,
            Album = info.Album,
            Genre = info.Genre,
            Subgenre = info.Subgenre,
            TrackNumber = info.TrackNumber,
            TrackCount = info.TrackCount,
            DiscNumber = info.DiscNumber,
            DiscCount = info.DiscCount,
            DiscSubtitle = info.DiscSubtitle,
            Year = info.Year,
            Composers = info.Composers,
            Conductor = info.Conductor,
            Comment = info.Comment,
            Lyrics = info.Lyrics,
            Grouping = info.Grouping,
            Bpm = info.Bpm,
            Copyright = info.Copyright,
            Publisher = info.Publisher,
            RemixedBy = info.RemixedBy,
            Isrc = info.Isrc,
            MusicalKey = info.MusicalKey,
            ReleaseCountry = info.ReleaseCountry,
            Advisory = info.Advisory,
            TitleSort = info.TitleSort,
            ArtistSort = info.ArtistSort,
            AlbumArtistSort = info.AlbumArtistSort,
            ComposerSort = info.ComposerSort,
            Work = info.Work,
            MovementName = info.MovementName,
            MovementNumber = info.MovementNumber,
            MovementCount = info.MovementCount,
            Lyricist = info.Lyricist,
            Arranger = info.Arranger,
            Ensemble = info.Ensemble,
            Soloists = info.Soloists,
            Barcode = info.Barcode,
            CatalogNumber = info.CatalogNumber,
            MusicBrainzDiscId = info.MusicBrainzDiscId,
            Media = info.Media,
            ReleaseType = info.ReleaseType is { } releaseType ? Library.ReleaseTypes.ToStored(releaseType) : null,
            OriginalYear = info.OriginalYear,
            MusicBrainzRecordingId = info.MusicBrainzRecordingId,
            MusicBrainzReleaseId = info.MusicBrainzReleaseId,
            MusicBrainzReleaseGroupId = info.MusicBrainzReleaseGroupId,
            MusicBrainzArtistId = info.MusicBrainzArtistId,
            MusicBrainzReleaseArtistId = info.MusicBrainzReleaseArtistId,
            Channels = info.Channels ?? 0,
            SampleRate = info.SampleRate ?? 0,
            BitsPerSample = info.BitsPerSample ?? 0,
            BitrateKbps = info.BitrateKbps ?? 0,
            CodecDescription = info.CodecDescription,
            Art = art?.Bytes,
            ArtMime = art?.Mime,
        };
    }

    /// <summary>The same sidecar with the library's corrections written over it. This is for a track
    /// whose file is not on this machine.
    ///
    /// <para>The sidecar is what a restore rebuilds the file's tags from. It is normally read again from
    /// that file whenever it is written. A track that lives only in the cloud has no file to read, so an
    /// edit made here would reach the catalogue (what everything <em>draws</em>) and never reach the
    /// sidecar (what a download would eventually <em>write</em>). A record brought back later would then
    /// arrive with the old tags.
    /// </para>
    ///
    /// <para>Only what the mirror knows is written. The art, the lyrics, the ISRC and everything else
    /// that the row does not carry are left exactly as they were. This is a correction, not a rewrite
    /// from a thinner source.</para>
    /// </summary>
    public TagSidecar With(Library.LibraryTrack row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return this with
        {
            RelativePath = row.RelativePath,
            Title = row.Title ?? Title,
            Artist = row.Artist ?? Artist,
            AlbumArtist = row.AlbumArtist ?? AlbumArtist,
            Album = row.Album ?? Album,
            Genre = row.Genre ?? Genre,
            Subgenre = row.Subgenre ?? Subgenre,
            TrackNumber = row.TrackNumber > 0 ? row.TrackNumber : TrackNumber,
            DiscNumber = row.DiscNumber > 0 ? row.DiscNumber : DiscNumber,
            DiscCount = row.DiscCount > 0 ? row.DiscCount : DiscCount,
            DiscSubtitle = row.DiscSubtitle ?? DiscSubtitle,
            Year = row.Year > 0 ? row.Year : Year,
            // The classical fields that the mirror holds. The catalogue push sends these too. Without
            // them here, a download would get the old composer and work back.
            Composers = row.Composer ?? Composers,
            Work = row.Work ?? Work,
            MovementName = row.MovementName ?? MovementName,
            MovementNumber = row.MovementNumber is > 0 ? (uint)row.MovementNumber.Value : MovementNumber,
            // Unknown in the row means "not known here", the same as a blank title, so the sidecar
            // keeps its own value.
            Advisory = row.Advisory is { } advisory && advisory != ContentAdvisory.Unknown ? advisory : Advisory,
            // These are taken as stated, both ways. False is a real answer here, unlike a blank title.
            // The row is where the mark is made. The sidecar only keeps it.
            Favorite = row.IsFavorite,
            FavoritedUtcTicks = row.FavoritedUtcTicks,
        };
    }

    /// <summary>The same sidecar carrying one row's favourite mark. A sidecar built from the FILE cannot
    /// know this mark, because no container carries it. It is used where a sidecar is written for a
    /// track that this machine holds. The cloud-only path goes through <see cref="With"/>, which does
    /// this and the tags together.</summary>
    public TagSidecar WithFavorite(bool favorite, long favoritedUtcTicks)
        => this with { Favorite = favorite, FavoritedUtcTicks = favoritedUtcTicks };

    /// <summary>Whether this sidecar already says what the library's row says. This means that an edit
    /// which changed nothing does not rewrite a megabyte of JSON on every sync run.</summary>
    public bool Matches(Library.LibraryTrack row)
    {
        ArgumentNullException.ThrowIfNull(row);

        return string.Equals(RelativePath, row.RelativePath, StringComparison.Ordinal)
            && Same(Title, row.Title) && Same(Artist, row.Artist)
            && Same(AlbumArtist, row.AlbumArtist) && Same(Album, row.Album)
            && Same(Genre, row.Genre) && Same(Subgenre, row.Subgenre)
            && Same(DiscSubtitle, row.DiscSubtitle)
            && Number(TrackNumber, row.TrackNumber) && Number(DiscNumber, row.DiscNumber)
            && Number(DiscCount, row.DiscCount) && Number(Year, row.Year)
            && Same(Composers, row.Composer) && Same(Work, row.Work) && Same(MovementName, row.MovementName)
            && Number(MovementNumber, row.MovementNumber is > 0 ? (uint)row.MovementNumber.Value : 0)
            && Number((uint)Advisory, (uint)(row.Advisory ?? ContentAdvisory.Unknown));

        // Blank in the row means "not known here", never "erase it". This is the same rule that the
        // catalogue push follows, so the two cannot disagree about what an empty field means.
        static bool Same(string? mine, string? theirs)
            => string.IsNullOrWhiteSpace(theirs) || string.Equals(mine, theirs, StringComparison.Ordinal);

        static bool Number(uint mine, uint theirs) => theirs == 0 || mine == theirs;
    }

    /// <summary>Stamps this sidecar's tags onto <paramref name="filePath"/>, the freshly downloaded
    /// blob, which has none. <see cref="TagReader.WriteAll"/> writes every field as stated. So a field
    /// that the user had cleared stays cleared, and the RPM id goes back in with the rest.
    /// </summary>
    public void ApplyTo(string filePath)
        => TagReader.WriteAll(filePath, ToTrackInfo(filePath));

    /// <summary>The sidecar as the TrackInfo a writer wants, with the art queued as pending.</summary>
    public TrackInfo ToTrackInfo(string filePath) => new()
    {
        FilePath = filePath,
        Extension = Extension,
        DurationSeconds = DurationSeconds,
        // Not a tag, so nothing is written into the file for it. It is carried so that a restore can put
        // the mark back on the row it rebuilds. See the favourite property above.
        IsFavorite = Favorite,
        Title = Title,
        Artist = Artist,
        AlbumArtist = AlbumArtist,
        Album = Album,
        Genre = Genre,
        Subgenre = Subgenre,
        TrackNumber = TrackNumber,
        TrackCount = TrackCount,
        DiscNumber = DiscNumber,
        DiscCount = DiscCount,
        DiscSubtitle = DiscSubtitle,
        Year = Year,
        Composers = Composers,
        Conductor = Conductor,
        Comment = Comment,
        Lyrics = Lyrics,
        Grouping = Grouping,
        Bpm = Bpm,
        Copyright = Copyright,
        Publisher = Publisher,
        RemixedBy = RemixedBy,
        Isrc = Isrc,
        MusicalKey = MusicalKey,
        ReleaseCountry = ReleaseCountry,
        Advisory = Advisory,
        TitleSort = TitleSort,
        ArtistSort = ArtistSort,
        AlbumArtistSort = AlbumArtistSort,
        ComposerSort = ComposerSort,
        Work = Work,
        MovementName = MovementName,
        MovementNumber = MovementNumber,
        MovementCount = MovementCount,
        Lyricist = Lyricist,
        Arranger = Arranger,
        Ensemble = Ensemble,
        Soloists = Soloists,
        Barcode = Barcode,
        CatalogNumber = CatalogNumber,
        MusicBrainzDiscId = MusicBrainzDiscId,
        Media = Media,
        ReleaseType = Library.ReleaseTypes.FromStored(ReleaseType),
        OriginalYear = OriginalYear,
        MusicBrainzRecordingId = MusicBrainzRecordingId,
        MusicBrainzReleaseId = MusicBrainzReleaseId,
        MusicBrainzReleaseGroupId = MusicBrainzReleaseGroupId,
        MusicBrainzArtistId = MusicBrainzArtistId,
        MusicBrainzReleaseArtistId = MusicBrainzReleaseArtistId,
        RpmId = RpmId,
        // Null art means "the file had none", and WriteAll must then not invent any art. An empty
        // array would mean "remove the art". On a blob that has no art, that gives the same outcome.
        PendingArt = Art,
        PendingArtMime = ArtMime,
        HasArt = Art is { Length: > 0 },
        Source = MetadataSource.Tags,
    };

    public string ToJson() => JsonSerializer.Serialize(this, TagSidecarJson.Default.TagSidecar);

    public static TagSidecar? FromJson(string json)
    {
        try { return JsonSerializer.Deserialize(json, TagSidecarJson.Default.TagSidecar); }
        catch { return null; }
    }
}
