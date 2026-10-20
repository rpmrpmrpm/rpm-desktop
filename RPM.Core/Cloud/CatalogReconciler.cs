using System.Globalization;
using RPM.Core.Library;
using RPM.Core.Models;
using System.Text.Json.Serialization;

namespace RPM.Core.Cloud;

/// <summary>
/// The fields of one track that differ between the library and the cloud, and nothing else.
///
/// <para>Every field is optional, and null means "do not change". The server merges, so a push for a
/// rename carries only the name.</para>
/// </summary>
public sealed record CatalogPush(
    string RpmId,
    string? Title = null,
    string? Artist = null,
    string? Album = null,
    string? RelativePath = null,
    CloudTrackTags? Tags = null)
{
    [JsonIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Title) && string.IsNullOrWhiteSpace(Artist)
        && string.IsNullOrWhiteSpace(Album) && string.IsNullOrWhiteSpace(RelativePath)
        && Tags is null;

    /// <summary>
    /// Whether anything except the path is being sent. If not, the push is not worth sending.
    ///
    /// <para>
    /// The path travels with a correction but never causes one. The store keeps a reasonably current
    /// path (an adopted row sits at it and a restore writes to it), but the path names a file on THIS
    /// machine's disk under this machine's naming template, and nothing at the other end can act on it.
    /// A push made only for a path is still a write, and every write moves the row's stamp, which the
    /// next run reads as a change made elsewhere. <see cref="CloudSyncPlan.Apply"/> does not take the
    /// store's path in the other direction either.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public bool CorrectsAnything =>
        !string.IsNullOrWhiteSpace(Title) || !string.IsNullOrWhiteSpace(Artist)
        || !string.IsNullOrWhiteSpace(Album) || Tags is not null;
}

/// <summary>
/// What a catalogue push did: how many rows the store wrote, and the stamp each of them now carries.
///
/// <para>
/// The stamps matter. Every write moves a row's stamp, and the machine that did the writing must
/// record what it wrote. If it records the stamp from the listing it read BEFORE the push, the next
/// listing reads as somebody else's change, on every following run, for every row it has corrected.
/// See <see cref="CloudSyncPlan.ChangedThere"/>.
/// </para>
///
/// <para>An older server answers with the count alone, and <see cref="Stamps"/> is then empty. The
/// caller falls back to the listing's own stamp.</para>
/// </summary>
public sealed record CatalogPushResult(int Updated, IReadOnlyDictionary<string, long> Stamps)
{
    public static CatalogPushResult Nothing { get; } =
        new(0, new Dictionary<string, long>(StringComparer.Ordinal));
}

/// <summary>
/// What one row would have to be told for the cloud to agree with the library about it.
///
/// <para>
/// <see cref="CloudSyncPlan"/> decides which side is right. This type only says what they disagree
/// about. The answer is used in both directions: a push carries exactly these fields, and a pull is
/// only worth making when there are some.
/// </para>
///
/// <para>
/// <b>Why a comparison and not a dirty flag.</b> A track's metadata is edited from at least six
/// places on the desktop (the track dialog, mass edit, an album's own edit, the naming template
/// re-file, a split, a rip). Each of them writes tags and moves files through code that knows nothing
/// about the cloud. A "needs pushing" bit on each one is six chances to forget, and it silently does
/// nothing when a seventh is written. Reading both sides and pushing the difference cannot be
/// forgotten by a caller that does not know it exists. It needs no schema change, and it also repairs
/// a library that drifted for any other reason.
/// </para>
///
/// <para>
/// Blank never travels. A mirror row with no title means "not known here", not "make it empty". The
/// server treats a blank the same way and refuses to clear a field through this route, so both ends
/// follow the same rule and a half-read row cannot wipe a good one.
/// </para>
/// </summary>
public static class CatalogReconciler
{
    /// <summary>Durations are floating point and come from two different readers. A difference of 0.05 s
    /// or less is the same length. Pushing on it would push every track on every run.</summary>
    private const double DurationTolerance = 0.05;

    /// <summary>What one row would have to be told, or an empty push when the two already agree.</summary>
    /// <param name="album">The album this track is on, as the library has it. The linked pressing
    /// (catalogue number, barcode and so on) is stored there. Null when the caller has none, and then
    /// the row says nothing about which pressing it is.</param>
    public static CatalogPush Differs(LibraryTrack mine, CloudTrack theirs, AlbumMeta? album = null)
    {
        ArgumentNullException.ThrowIfNull(mine);
        ArgumentNullException.ThrowIfNull(theirs);

        var tags = TagsFor(mine, album);

        return new CatalogPush(
            theirs.RpmId,
            Changed(mine.Title, theirs.Title),
            Changed(mine.Artist, theirs.Artist),
            Changed(mine.Album, theirs.Album),
            Changed(mine.RelativePath, theirs.RelativePath),
            SameTags(tags, theirs.Tags) ? null : tags);
    }

    /// <summary>The value to send, or null when there is nothing to say: the two agree, or this side
    /// does not know. A blank would be an instruction to erase, and the library not knowing an album
    /// artist is not a reason to remove the cloud's.</summary>
    private static string? Changed(string? mine, string? theirs)
        => string.IsNullOrWhiteSpace(mine) || string.Equals(mine, theirs, StringComparison.Ordinal)
            ? null
            : mine;

    /// <summary>The catalogue fields as the mirror holds them. Null becomes zero, which the wire reads as
    /// "not known". The mirror gives a null column the same meaning.</summary>
    public static CloudTrackTags TagsFor(LibraryTrack track, AlbumMeta? album = null)
    {
        ArgumentNullException.ThrowIfNull(track);

        return new CloudTrackTags(
            track.DurationSeconds,
            (int)track.TrackNumber,
            0,                            // track count: the mirror does not keep one
            (int)track.DiscNumber,
            (int)track.DiscCount,
            (int)track.Year,
            track.Genre,
            track.AlbumArtist,
            track.Extension,
            track.Channels ?? 0,
            track.SampleRate ?? 0,
            track.BitsPerSample ?? 0,
            track.BitrateKbps ?? 0,
            track.CodecDescription,
            track.Work,
            track.MovementName,
            // Null is "the file has not been read for it" in the mirror. On the wire zero means the same
            // thing, so dropping the difference loses nothing.
            track.MovementNumber ?? 0,
            track.Composer,
            // Negative infinity is the mirror's "this file cannot be measured". It must not go on the wire:
            // zero there means "not known", which is how another machine should read it instead of trying
            // to level by it.
            track.LoudnessLufs is { } lufs && double.IsFinite(lufs) ? lufs : 0,
            track.TruePeak ?? 0,
            track.IsFavorite,
            track.FavoritedUtcTicks,
            // Null in the mirror is "the file has not been read for it". Zero on the wire is Unknown, which
            // is the same answer for anything that draws a badge.
            (int)(track.Advisory ?? ContentAdvisory.Unknown),
            // Which pressing this is. It belongs to the album, not to the track, so it is read from the
            // album's own row and written onto every track of the album. The phone reads a catalogue listing
            // and has no album rows to consult.
            album?.Catno,
            album?.Barcode,
            album?.MusicBrainzReleaseId,
            album?.DiscId,
            // What kind of release it is, as the file's RELEASETYPE tag or the album editor says. Null is
            // "nobody has said", which the wire reads the same way.
            track.ReleaseType is { } type ? ReleaseTypes.ToStored(type) : null);
    }

    /// <summary>
    /// Whether the cloud's catalogue fields already say what the mirror's say.
    ///
    /// <para>Field by field, and only where the mirror has a value. Zero and blank on this side mean
    /// "not known", so they never count as a disagreement. Otherwise a library whose files have no year
    /// would push every track on every run.</para>
    /// </summary>
    private static bool SameTags(CloudTrackTags mine, CloudTrackTags? theirs)
        => TagDifference(mine, theirs) is null;

    /// <summary>
    /// The first catalogue field the two sides disagree about, or null when they agree.
    ///
    /// <para>It returns a name and not a count, because the failure this pass can have is not visible in
    /// a count. A push that repeats for the same row on every run means one side writes a field the
    /// other cannot read back. "brought 1 catalogue row up to date" only says that it happened again.
    /// The name says which field to look at.</para>
    ///
    /// <para>It includes both sides' values, because the name alone does not separate the two ways this
    /// repeats. "here X, there none" means the store does not keep the field: it was sent, it was not
    /// read back, and it will be sent again on every run. "here X, there Y" means two machines disagree.
    /// That is normal and settles itself on the next pass.</para>
    /// </summary>
    public static string? TagDifference(CloudTrackTags mine, CloudTrackTags? theirs)
    {
        ArgumentNullException.ThrowIfNull(mine);

        if (theirs is null) return "tags (the row carries none)";

        if (mine.DurationSeconds > 0
            && Math.Abs(mine.DurationSeconds - theirs.DurationSeconds) > DurationTolerance)
            return Say("duration", mine.DurationSeconds, theirs.DurationSeconds);

        if (!SameNumber(mine.TrackNumber, theirs.TrackNumber))
            return Say("trackNumber", mine.TrackNumber, theirs.TrackNumber);
        if (!SameNumber(mine.DiscNumber, theirs.DiscNumber))
            return Say("discNumber", mine.DiscNumber, theirs.DiscNumber);
        if (!SameNumber(mine.DiscCount, theirs.DiscCount))
            return Say("discCount", mine.DiscCount, theirs.DiscCount);
        if (!SameNumber(mine.Year, theirs.Year)) return Say("year", mine.Year, theirs.Year);
        if (!SameNumber(mine.Channels, theirs.Channels))
            return Say("channels", mine.Channels, theirs.Channels);
        if (!SameNumber(mine.SampleRate, theirs.SampleRate))
            return Say("sampleRate", mine.SampleRate, theirs.SampleRate);
        if (!SameNumber(mine.BitsPerSample, theirs.BitsPerSample))
            return Say("bitsPerSample", mine.BitsPerSample, theirs.BitsPerSample);
        if (!SameNumber(mine.BitrateKbps, theirs.BitrateKbps))
            return Say("bitrateKbps", mine.BitrateKbps, theirs.BitrateKbps);
        if (!SameText(mine.Genre, theirs.Genre)) return Say("genre", mine.Genre, theirs.Genre);
        if (!SameText(mine.AlbumArtist, theirs.AlbumArtist))
            return Say("albumArtist", mine.AlbumArtist, theirs.AlbumArtist);
        if (!SameText(mine.Extension, theirs.Extension))
            return Say("extension", mine.Extension, theirs.Extension);
        if (!SameText(mine.CodecDescription, theirs.CodecDescription))
            return Say("codecDescription", mine.CodecDescription, theirs.CodecDescription);
        if (!SameNumber(mine.MovementNumber, theirs.MovementNumber))
            return Say("movementNumber", mine.MovementNumber, theirs.MovementNumber);
        if (!SameText(mine.Work, theirs.Work)) return Say("work", mine.Work, theirs.Work);
        if (!SameText(mine.MovementName, theirs.MovementName))
            return Say("movementName", mine.MovementName, theirs.MovementName);
        if (!SameText(mine.Composer, theirs.Composer))
            return Say("composer", mine.Composer, theirs.Composer);
        // The pressing is compared differently from the fields above: a value here and none there is
        // worth sending, but a value here and a DIFFERENT one there is not. See SamePressing.
        if (!SamePressing(mine.Catno, theirs.Catno))
            return Say("catno", mine.Catno, theirs.Catno);
        if (!SamePressing(mine.Barcode, theirs.Barcode))
            return Say("barcode", mine.Barcode, theirs.Barcode);
        if (!SamePressing(mine.MusicBrainzReleaseId, theirs.MusicBrainzReleaseId))
            return Say("mbReleaseId", mine.MusicBrainzReleaseId, theirs.MusicBrainzReleaseId);
        if (!SamePressing(mine.MusicBrainzDiscId, theirs.MusicBrainzDiscId))
            return Say("discId", mine.MusicBrainzDiscId, theirs.MusicBrainzDiscId);
        if (!SameLoudness(mine, theirs))
            return Say("loudnessLufs", mine.LoudnessLufs, theirs.HasLoudness ? theirs.LoudnessLufs : null);
        if (!SameText(mine.ReleaseType, theirs.ReleaseType))
            return Say("releaseType", mine.ReleaseType, theirs.ReleaseType);

        // "Not known" on this side is no reason to push, as with the numbers above. A library whose files
        // have no advisory flag (most of them) would otherwise rewrite every row on every run.
        if (!SameNumber(mine.Advisory, theirs.Advisory))
            return Say("advisory", mine.Advisory, theirs.Advisory);

        // Exact comparison, in both directions. Every other field here treats "nothing on this side" as
        // "not known" and lets the cloud keep its value. A favourite always has a definite answer in the
        // mirror, and taking the mark OFF is as much of a change as putting it on.
        return mine.Favorite == theirs.Favorite ? null : Say("favorite", mine.Favorite, theirs.Favorite);
    }

    /// <summary>What a row is being pushed for, in words, for the sync log. Not for a hot path: it
    /// is called once for each row that is actually sent.</summary>
    public static string Explain(CatalogPush push, CloudTrack theirs)
    {
        ArgumentNullException.ThrowIfNull(push);
        ArgumentNullException.ThrowIfNull(theirs);

        var fields = new List<string>();
        if (!string.IsNullOrWhiteSpace(push.Title)) fields.Add(Say("title", push.Title, theirs.Title));
        if (!string.IsNullOrWhiteSpace(push.Artist)) fields.Add(Say("artist", push.Artist, theirs.Artist));
        if (!string.IsNullOrWhiteSpace(push.Album)) fields.Add(Say("album", push.Album, theirs.Album));
        if (!string.IsNullOrWhiteSpace(push.RelativePath))
            fields.Add(Say("path", push.RelativePath, theirs.RelativePath));
        if (push.Tags is { } tags && TagDifference(tags, theirs.Tags) is { } field)
            fields.Add(field);

        return fields.Count == 0 ? "nothing" : string.Join(", ", fields);
    }

    /// <summary>
    /// Whether the cloud already knows how loud this recording is.
    ///
    /// <para>Only where this side has measured it. A library that is still measuring has nothing to say
    /// about most of its rows. Treating that silence as a disagreement would push every track on every
    /// pass, as for every other field here.</para>
    /// </summary>
    private static bool SameLoudness(CloudTrackTags mine, CloudTrackTags theirs)
    {
        if (!mine.HasLoudness)
            return true;

        // A tenth of a dB. Two meters reading the same file can differ by that much, and a tighter
        // comparison would have two machines pushing the row back and forth at each other.
        return theirs.HasLoudness
            && Math.Abs(mine.LoudnessLufs - theirs.LoudnessLufs) <= 0.1;
    }

    /// <summary>What the two sides say about one field, for the sync log.</summary>
    private static string Say(string field, object? mine, object? theirs)
        => $"{field} (here {Value(mine)}, there {Value(theirs)})";

    /// <summary>One side's answer, in words. Blank and zero mean "not known" for every field in this
    /// record except the favourite, so both read as "none". This makes a repeated push easy to read:
    /// a value here and none there is a field the store does not keep.</summary>
    private static string Value(object? value) => value switch
    {
        null => "none",
        string text => string.IsNullOrWhiteSpace(text) ? "none" : $"\"{text}\"",
        bool flag => flag ? "true" : "false",
        int number => number == 0 ? "none" : number.ToString(CultureInfo.InvariantCulture),
        double number => number == 0 ? "none" : number.ToString("0.###", CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "none",
    };

    /// <summary>
    /// Whether the pressing should be left alone: this library has nothing to say, or the store
    /// already has an answer of its own.
    ///
    /// <para>
    /// These four fields are the only ones here that a disagreement cannot settle, because nothing
    /// pulls them. <see cref="CloudSyncPlan"/> neither compares nor applies them: they belong to the
    /// album row, and a pull only ever writes a track row. If they were compared like a track's own
    /// fields, two installs that had linked one album to two pressings could never agree. Each would
    /// read the other's answer, call it a disagreement and push its own, one row per track, on every
    /// run.
    /// </para>
    ///
    /// <para>
    /// That is also more than wasted requests. Every write moves the store's stamp, which makes
    /// <see cref="CloudSyncPlan.ChangedThere"/> true on the next run, and that would let the same rows
    /// be pulled back and pushed again over their paths. Filling a blank and nothing more keeps the
    /// engine out of that cycle: the machine that linked the album first keeps its answer, and a machine
    /// that disagrees says so by relinking the album, not by out-pushing the other.
    /// </para>
    /// </summary>
    private static bool SamePressing(string? mine, string? theirs)
        => string.IsNullOrWhiteSpace(mine) || !string.IsNullOrWhiteSpace(theirs);

    private static bool SameNumber(int mine, int theirs) => mine <= 0 || mine == theirs;

    private static bool SameText(string? mine, string? theirs)
        => string.IsNullOrWhiteSpace(mine) || string.Equals(mine, theirs, StringComparison.Ordinal);
}
