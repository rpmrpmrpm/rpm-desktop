using RPM.Core.Library;
using RPM.Core.Models;

namespace RPM.Core.Metadata;

/// <summary>Which part of the sheet a fact belongs under.</summary>
public enum TrackFactGroup
{
    /// <summary>What the song is: its name, its artist, and where it sits on the record.</summary>
    Song,

    /// <summary>Who else made it, and who owns it. These are the credits that an ordinary player does not
    /// show.</summary>
    Credits,

    /// <summary>What the file is, as opposed to what its tags claim.</summary>
    File,

    /// <summary>What this library knows that no file does: when the track arrived and how often it has been
    /// played.</summary>
    Library,
}

/// <summary>One line of the sheet: a label to look up, and the value as it should read.</summary>
public sealed record TrackFact(TrackFactGroup Group, string LabelKey, string Value);

/// <summary>
/// The few values that depend on the user's language and time zone. The host supplies them. This keeps the
/// sheet itself a rule about which facts get a line, and in what order. That rule is the part worth
/// testing, and a locale would otherwise make it hard to test.
/// </summary>
/// <param name="Duration">Seconds as a running time, for example "4:32".</param>
/// <param name="Size">Bytes as a person would say them, for example "38.2 MB".</param>
/// <param name="Timestamp">UTC ticks as a local date. Any value that means nothing, including zero (which
/// means never), must come back blank. A blank value is dropped from the sheet.</param>
/// <param name="OfCount">A position in a set. For example, 3 and 12 give "3 of 12". It is called with a
/// zero count when the size of the set is unknown. Then it must return only the position.</param>
public sealed record TrackFactFormats(
    Func<double, string> Duration,
    Func<long, string> Size,
    Func<long, string> Timestamp,
    Func<uint, uint, string> OfCount);

/// <summary>
/// Everything the app knows about one track, as an ordered list of labelled lines.
///
/// <para>The rule this is built on: a fact that is not known gets no line. A sheet that lists "Conductor:
/// none" and "ISRC: none" for the ninety-nine records in a hundred that have neither is a sheet that nobody
/// reads to the bottom. The two records that do have a conductor are exactly the ones where the line
/// matters. So blanks are left out, and the sheet is as long as the track has facts to show.</para>
///
/// <para>The tags come from the file itself, not from the library mirror. The mirror holds what a grid
/// needs. It has no column for a conductor or a publisher, and those are half of the reason to open this
/// pane. The mirror still holds the facts that no file can carry: when the track joined the library, and
/// how often it has been played.</para>
/// </summary>
public static class TrackFacts
{
    /// <param name="tags">The file's own tags, freshly read. Null when the file could not be read. In that
    /// case the mirror's row gives what it can.</param>
    /// <param name="row">The library's row for the track. Null for a track played from outside the
    /// library.</param>
    public static IReadOnlyList<TrackFact> Build(TrackInfo? tags, LibraryTrack? row, TrackFactFormats formats)
    {
        var facts = new List<TrackFact>(24);
        if (tags is null && row is null)
            return facts;

        void Add(TrackFactGroup group, string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                facts.Add(new TrackFact(group, key, value.Trim()));
        }

        // The song
        Add(TrackFactGroup.Song, "Review_ColTitle", tags?.Title ?? row?.Title);
        Add(TrackFactGroup.Song, "Review_ColArtist", tags?.Artist ?? row?.Artist);

        // Shown only when it differs from the track artist. On a single-artist record the two are the same
        // word twice. The line is worth its width only on compilations, where the two differ.
        var artist = tags?.Artist ?? row?.Artist;
        var albumArtist = tags?.AlbumArtist ?? row?.AlbumArtist;
        if (!string.Equals(artist, albumArtist, StringComparison.OrdinalIgnoreCase))
            Add(TrackFactGroup.Song, "Details_AlbumArtist", albumArtist);

        Add(TrackFactGroup.Song, "Review_ColAlbum", tags?.Album ?? row?.Album);
        Add(TrackFactGroup.Song, "EditSel_Genre", tags?.Genre ?? row?.Genre);

        var year = tags?.Year ?? row?.Year ?? 0;
        if (year > 0)
            Add(TrackFactGroup.Song, "Review_ColYear", year.ToString());

        var trackNumber = tags?.TrackNumber ?? row?.TrackNumber ?? 0;
        if (trackNumber > 0)
            Add(TrackFactGroup.Song, "Details_TrackNo", formats.OfCount(trackNumber, tags?.TrackCount ?? 0));

        // A disc number tells nothing on a single-disc record. "1 of 1" and a bare "1" both say nothing. So
        // the line appears only when there is more than one disc.
        var discNumber = tags?.DiscNumber ?? row?.DiscNumber ?? 0;
        var discCount = tags?.DiscCount ?? row?.DiscCount ?? 0;
        if (discNumber > 0 && discCount > 1)
            Add(TrackFactGroup.Song, "Details_Disc", formats.OfCount(discNumber, discCount));

        Add(TrackFactGroup.Song, "Details_DiscSubtitle", tags?.DiscSubtitle ?? row?.DiscSubtitle);

        var bpm = tags?.Bpm ?? 0;
        if (bpm > 0)
            Add(TrackFactGroup.Song, "Details_Bpm", bpm.ToString());

        // The credits
        Add(TrackFactGroup.Credits, "Details_Composers", tags?.Composers);
        Add(TrackFactGroup.Credits, "Details_Conductor", tags?.Conductor);
        Add(TrackFactGroup.Credits, "Details_RemixedBy", tags?.RemixedBy);
        Add(TrackFactGroup.Credits, "Details_Publisher", tags?.Publisher);
        Add(TrackFactGroup.Credits, "Details_Copyright", tags?.Copyright);
        Add(TrackFactGroup.Credits, "Details_Isrc", tags?.Isrc);
        Add(TrackFactGroup.Credits, "Details_Grouping", tags?.Grouping);
        Add(TrackFactGroup.Credits, "Details_ReleaseCountry", tags?.ReleaseCountry);
        Add(TrackFactGroup.Credits, "Details_Comment", tags?.Comment);

        // The file
        var seconds = tags?.DurationSeconds ?? row?.DurationSeconds ?? 0;
        if (seconds > 0)
            Add(TrackFactGroup.File, "TrackInfo_Length", formats.Duration(seconds));

        var extension = tags?.Extension ?? row?.Extension;
        var described = AudioFormat.Describe(
            extension,
            tags?.Channels ?? row?.Channels,
            tags?.CodecDescription ?? row?.CodecDescription,
            tags?.BitsPerSample ?? row?.BitsPerSample,
            tags?.SampleRate ?? row?.SampleRate,
            tags?.BitrateKbps ?? row?.BitrateKbps);
        if (described.Count > 0)
            Add(TrackFactGroup.File, "TrackInfo_Encoding", string.Join("  ·  ", described));

        // The rates are written out under the sleeve line. The sleeve line says "FLAC 24/96". A reader of
        // this pane often wants the numbers as well.
        var sampleRate = tags?.SampleRate ?? row?.SampleRate ?? 0;
        if (sampleRate > 0)
            Add(TrackFactGroup.File, "TrackInfo_SampleRate", $"{sampleRate / 1000.0:0.###} kHz");

        var bitrate = tags?.BitrateKbps ?? row?.BitrateKbps ?? 0;
        if (bitrate > 0)
            Add(TrackFactGroup.File, "TrackInfo_Bitrate", $"{bitrate} kbps");

        var channels = tags?.Channels ?? row?.Channels ?? 0;
        if (channels > 0)
            Add(TrackFactGroup.File, "TrackInfo_ChannelCount", channels.ToString());

        if (row is { SizeBytes: > 0 })
            Add(TrackFactGroup.File, "TrackInfo_FileSize", formats.Size(row.SizeBytes));

        Add(TrackFactGroup.File, "TrackInfo_FileName", row?.FileName ?? FileNameOf(tags?.FilePath));
        Add(TrackFactGroup.File, "TrackInfo_Folder", row?.Folder);

        if (row is { ModifiedUtcTicks: > 0 })
            Add(TrackFactGroup.File, "TrackInfo_Modified", formats.Timestamp(row.ModifiedUtcTicks));

        // What only the library knows
        if (row is not null)
        {
            if (row.AddedUtcTicks > 0)
                Add(TrackFactGroup.Library, "TrackInfo_Added", formats.Timestamp(row.AddedUtcTicks));
            if (row.LastPlayedUtcTicks > 0)
                Add(TrackFactGroup.Library, "TrackInfo_LastPlayed", formats.Timestamp(row.LastPlayedUtcTicks));
            if (row.Plays > 0)
                Add(TrackFactGroup.Library, "TrackInfo_Plays", row.Plays.ToString());

            // A file that cannot hold an id is marked with a blank id (RpmTag.NoCarrier). The emptiness
            // check above drops that value. The marker is a note about the container, not a fact about the
            // record.
            Add(TrackFactGroup.Library, "TrackInfo_RpmId", row.RpmId);
        }

        return facts;
    }

    private static string? FileNameOf(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFileName(path);
}
