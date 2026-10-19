using RPM.Core.Analysis;
using RPM.Core.Models;

namespace RPM.Core.Metadata;

/// <summary>Reads and writes embedded audio tags (ID3v2, Vorbis comments, MP4 atoms and so on) using
/// TagLib#.</summary>
public static class TagReader
{
    /// <summary>
    /// Whether there is nothing here to read. That is the case when there is no file at this path, or when
    /// the path is a cloud placeholder whose contents are not on this disk.
    ///
    /// <para>The placeholder case is <see cref="Files.CloudFile.IsDataless"/>. Opening one starts the
    /// download, so it is never opened. The first case is the common one. A track that the account holds
    /// but this machine has never had (<c>CloudState.CloudOnly</c>) still has a full path built from the
    /// library root. No part of that path exists here: not the file, and not its album folder. Passing that
    /// path to TagLib would throw <see cref="DirectoryNotFoundException"/> once per track, for something
    /// that is expected.</para>
    ///
    /// <para>It also catches a path that is not a file at all, such as a radio station's stream URL. A tag
    /// reader would either throw on that path or start downloading the broadcast.</para>
    /// </summary>
    private static bool NotOnThisDisk(string path)
        => !File.Exists(path) || Files.CloudFile.IsDataless(path);

    /// <summary>
    /// Reads the file's tags into a TrackInfo. This never throws. An unreadable or untagged file comes back
    /// with Source=Unknown, and only FilePath and Extension are filled in. Cover-art bytes are not loaded
    /// here, only a flag is set. Use <see cref="LoadArt"/> to load them when they are needed.
    ///
    /// <para>A cloud placeholder is checked before anything opens the file. Opening it would start the
    /// download, so the answer is that there is nothing to read. Reading a folder of placeholders in
    /// parallel would download a whole collection just because the user pointed at a folder. Such a file
    /// comes back as <see cref="FileAvailability.Dataless"/>, with the name it has on disk and nothing
    /// else. That is all that is known about it.</para>
    ///
    /// <para>A file that is here but still gives nothing is tried twice: first TagLib, then ffmpeg. Only
    /// when both fail does it come back <see cref="FileAvailability.Unreadable"/>. An untagged file and a
    /// corrupt file look the same from outside, but they are different. An untagged file needs its tags
    /// filled in. A corrupt file should be thrown away.</para>
    /// </summary>
    public static TrackInfo Read(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();

        // Not on this disk. Say so and touch nothing. The size and date can be read without a download, but
        // nothing inside the file can.
        if (NotOnThisDisk(path))
            return new TrackInfo { FilePath = path, Extension = ext, Availability = FileAvailability.Dataless };

        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;

            string? artist = Clean(Join(tag.Performers)) ?? Clean(Join(tag.AlbumArtists));
            var (movement, movementCount) = MovementTag.Read(file);

            var info = new TrackInfo
            {
                FilePath = path,
                Extension = ext,
                DurationSeconds = file.Properties?.Duration.TotalSeconds ?? 0,
                Title = Clean(tag.Title),
                Artist = artist,
                AlbumArtist = Clean(Join(tag.AlbumArtists)),
                Album = Clean(tag.Album),
                TrackNumber = tag.Track,
                TrackCount = tag.TrackCount,
                DiscNumber = tag.Disc,
                DiscCount = tag.DiscCount,
                DiscSubtitle = ExtendedTags.DiscSubtitle.Read(file),
                Year = tag.Year,
                Genre = Clean(tag.Genres is { Length: > 0 } ? tag.Genres[0] : null),
                Subgenre = Clean(ExtendedTags.ReadSubgenre(file)),
                Composers = Clean(Join(tag.Composers)),
                Conductor = Clean(tag.Conductor),
                Comment = Clean(tag.Comment),
                Lyrics = Clean(tag.Lyrics),
                Grouping = Clean(tag.Grouping),
                Bpm = tag.BeatsPerMinute,
                Copyright = Clean(tag.Copyright),
                Publisher = Clean(tag.Publisher),
                RemixedBy = Clean(tag.RemixedBy),
                Isrc = Clean(tag.ISRC),
                MusicalKey = Clean(tag.InitialKey),
                ReleaseCountry = Clean(tag.MusicBrainzReleaseCountry),
                RpmId = RpmTag.Read(file),
                HasArt = tag.Pictures is { Length: > 0 },
                Advisory = AdvisoryTag.Read(file),

                TitleSort = Clean(tag.TitleSort),
                ArtistSort = Clean(Join(tag.PerformersSort)),
                AlbumArtistSort = Clean(Join(tag.AlbumArtistsSort)),
                ComposerSort = Clean(Join(tag.ComposersSort)),

                Work = ExtendedTags.Work.Read(file),
                MovementName = ExtendedTags.MovementName.Read(file),
                MovementNumber = movement,
                MovementCount = movementCount,
                Lyricist = ExtendedTags.Lyricist.Read(file),
                Arranger = ExtendedTags.Arranger.Read(file),
                Ensemble = ExtendedTags.Ensemble.Read(file),
                Soloists = ExtendedTags.Soloists.Read(file),

                Barcode = ExtendedTags.Barcode.Read(file),
                CatalogNumber = ExtendedTags.CatalogNumber.Read(file),
                MusicBrainzDiscId = ExtendedTags.MusicBrainzDiscId.Read(file),
                Media = ExtendedTags.Media.Read(file),
                ReleaseType = ReleaseTypeTag.Read(file),
                OriginalYear = ExtendedTags.OriginalYear.ReadNumber(file),

                MusicBrainzRecordingId = Clean(tag.MusicBrainzTrackId),
                MusicBrainzReleaseId = Clean(tag.MusicBrainzReleaseId),
                MusicBrainzReleaseGroupId = Clean(tag.MusicBrainzReleaseGroupId),
                MusicBrainzArtistId = Clean(tag.MusicBrainzArtistId),
                MusicBrainzReleaseArtistId = Clean(tag.MusicBrainzReleaseArtistId),

                // The stream values. These are stored even when the decoder reports nothing (zero, not
                // null). In the mirror, null means "never read". Re-reading a file that has already refused
                // to answer costs as much as reading one that would answer.
                Channels = file.Properties?.AudioChannels ?? 0,
                SampleRate = file.Properties?.AudioSampleRate ?? 0,
                BitsPerSample = file.Properties?.BitsPerSample ?? 0,
                BitrateKbps = file.Properties?.AudioBitrate ?? 0,
                CodecDescription = Clean(file.Properties?.Description),

                // A reading that another tagger already made, if the file has one. It is worth taking. It
                // saves decoding the whole track. A library that came through foobar2000, beets or Picard
                // already has these values.
                LoudnessLufs = TaggedLoudness(tag),
                TruePeak = TaggedPeak(tag),
            };

            info = info with { Source = info.HasUsableMetadata ? MetadataSource.Tags : MetadataSource.Unknown };
            return WithProbedStream(path, info);
        }
        catch
        {
            // TagLib gave up on the whole file. That alone means little, because TagLib refuses many files
            // that play correctly. So ffmpeg makes the final decision below.
            return WithProbedStream(path, new TrackInfo { FilePath = path, Extension = ext });
        }
    }

    /// <summary>
    /// The loudness implied by the file's own ReplayGain tag, or null when there is none.
    ///
    /// <para>The result is stored as a loudness, not as the gain the tag was written with. That way one
    /// column answers "how loud is this record", whether the number was read from a file or measured here.
    /// The conversion follows the tag's own definition. A gain is the distance from the writer's reference
    /// level, and ReplayGain 2.0 fixes that reference at -18 LUFS.</para>
    ///
    /// <para>Tags written by ReplayGain 1.0 measured loudness in a cruder way. They are still used as they
    /// are. The two references were set to match when the second version was specified. So an old tag is
    /// off by a fraction of a dB, not by anything audible. Re-decoding a library to improve on that is a
    /// poor trade.</para>
    /// </summary>
    private static double? TaggedLoudness(TagLib.Tag tag)
    {
        // TagLib returns NaN, not zero, for a tag that is not there. Zero is a real reading. It means
        // "already at the reference level". So the two must not be confused.
        double gain = tag.ReplayGainTrackGain;
        return double.IsFinite(gain) ? Analysis.ReplayGain.LufsFromGain(gain) : null;
    }

    /// <summary>The peak from the file's own ReplayGain tag, as a linear amplitude. Null when there is
    /// none, and also when the value cannot be a peak. Many writers got this tag wrong, and a wrong peak
    /// clamps a gain to the wrong place.</summary>
    private static double? TaggedPeak(TagLib.Tag tag)
    {
        double peak = tag.ReplayGainTrackPeak;
        return double.IsFinite(peak) && peak > 0 ? peak : null;
    }

    /// <summary>
    /// Fills in the stream facts that TagLib did not provide, by asking ffmpeg.
    ///
    /// <para>Some containers are laid out in a way that TagLib does not expect. TagLib then throws instead
    /// of answering. The track lands in the library with no length, although it plays correctly, because
    /// playback uses ffmpeg, which reads the same file without any problem. A track with no length cannot
    /// be laid out, queued or scrubbed. So asking ffmpeg is worth the cost of starting a process.</para>
    ///
    /// <para>This is only done when the duration is missing. A missing duration is the sign that TagLib got
    /// nowhere. It also stops a scan of a whole library from starting a process for every file.</para>
    /// </summary>
    private static TrackInfo WithProbedStream(string path, TrackInfo info)
    {
        if (info.DurationSeconds > 0)
            return info;

        var outcome = MediaProbe.Probe(path, out var facts);

        if (outcome is not MediaProbe.Outcome.Found)
        {
            // ffmpeg looked and found no audio. That is the whole test for a corrupt file, and it is the
            // right test. ffmpeg is the decoder that playback uses. A file with no audio that ffmpeg can
            // see is a file this app could never play. Importing it would add a track that cannot be heard.
            //
            // Unavailable is a different answer. It says nothing about the file. If ffmpeg is not installed
            // on this machine, that is not the file's fault. A track must never be marked corrupt because a
            // tool is missing.
            return outcome is MediaProbe.Outcome.NoAudio
                ? info with { Availability = FileAvailability.Unreadable }
                : info;
        }

        return info with
        {
            DurationSeconds = facts!.DurationSeconds,
            Channels = info.Channels > 0 ? info.Channels : facts.Channels,
            SampleRate = info.SampleRate > 0 ? info.SampleRate : facts.SampleRate,
            BitrateKbps = info.BitrateKbps > 0 ? info.BitrateKbps : facts.BitrateKbps,
            CodecDescription = info.CodecDescription ?? facts.Codec,
        };
    }

    /// <summary>
    /// Writes identified metadata into the file's tags. Only fields that have values are written. Existing
    /// tag data is kept where the TrackInfo has none. Throws on I/O errors.
    /// </summary>
    public static void Write(string path, TrackInfo info)
        => WriteCore(path, info, overwriteAll: false);

    /// <summary>
    /// Writes the full wanted state into the file. Every supported field is set exactly as it is in
    /// <paramref name="info"/>, and an empty field clears the tag. This is used for rows that a user has
    /// edited. The wanted state starts from the file's own tags, so untouched fields are written back
    /// unchanged. Also applies <see cref="TrackInfo.PendingArt"/> when it is set. Throws on I/O errors.
    /// </summary>
    public static void WriteAll(string path, TrackInfo info)
        => WriteCore(path, info, overwriteAll: true);

    /// <summary>
    /// Writes what the analyser measured (the tempo and the key) into the file's own tags.
    ///
    /// <para>The file is opened once. What the file already holds decides what is written (see <see
    /// cref="AnalysisTags.Wanted"/>). So the read and the write always agree. A file with nothing to gain
    /// is closed again without being saved. This is why it is safe to run over a whole library. A track
    /// that is already tagged costs one open and nothing more. There is no rewrite, no new modified time,
    /// no re-sync and no re-upload.</para>
    ///
    /// <para>Nothing else in the file is touched. This does not go through <see cref="Write"/>, which would
    /// set every field the info carries and could also change the RPM id's carrier. These two frames are
    /// written by hand because this pass runs unattended over files that the user did not ask to have
    /// edited.</para>
    /// </summary>
    /// <returns>What was written. <see cref="AnalysisTagWrite.Nothing"/> when the file was left
    /// alone.</returns>
    public static AnalysisTagWrite WriteAnalysisTags(string path, TrackAnalysis analysis, bool overwrite = false)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        // A placeholder has no bytes here to write into, and opening one starts a download.
        if (NotOnThisDisk(path))
            return AnalysisTagWrite.Nothing;

        using var file = TagLib.File.Create(path);
        var tag = file.Tag;

        var wanted = AnalysisTags.Wanted(analysis, tag.BeatsPerMinute, tag.InitialKey, overwrite);
        if (!wanted.Any)
            return AnalysisTagWrite.Nothing;

        if (wanted.Bpm > 0)
            tag.BeatsPerMinute = wanted.Bpm;
        if (wanted.Key is { Length: > 0 } key)
            tag.InitialKey = key;

        file.Save();
        return wanted;
    }

    /// <summary>
    /// Makes sure the file has an RPM id, and returns it. The id the file already has is kept, unless
    /// <paramref name="force"/> is true. Then a new id is made. Use this for a file that was copied outside
    /// the app, because that file shares its id with the original. Returns null when the file cannot be
    /// read, or when its format has no tag for the id. Saves the file only when it stamped something.
    /// </summary>
    public static string? StampRpmId(string path, bool force = false)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            if (!force && RpmTag.Read(file) is { } existing)
                return existing;

            string id = RpmTag.New();
            if (!RpmTag.Write(file, id))
                return null;
            file.Save();
            return id;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a <em>particular</em> id into a file, instead of making a new one.
    ///
    /// <para>
    /// This has one caller and one reason. The account already holds these exact bytes under an id that
    /// another of the user's installs made. Using that id makes the same CD, ripped on two machines, one
    /// track in the cloud and not two. That means one blob, one catalogue row and one quota charge. It is
    /// only done when the canonical hashes match. A match proves that the two files are the same recording.
    /// </para>
    /// </summary>
    /// <returns>False when the file cannot carry an id at all. The caller then uploads the file as it
    /// is.</returns>
    public static bool WriteRpmId(string path, string id)
    {
        if (!RpmTag.IsId(id)) return false;

        try
        {
            using var file = TagLib.File.Create(path);
            if (!RpmTag.Write(file, id)) return false;
            file.Save();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void WriteCore(string path, TrackInfo info, bool overwriteAll)
    {
        using var file = TagLib.File.Create(path);
        var tag = file.Tag;

        // Every file that RPM writes leaves with an id. That is the id it should have, the id it already
        // has, or a new one. This costs nothing extra, because the save happens anyway. A NoCarrier marker
        // is not an id, so it must never be written to the file.
        RpmTag.Write(file, RpmTag.IsId(info.RpmId)
            ? info.RpmId!
            : RpmTag.Read(file) ?? RpmTag.New());

        void Set(string? value, Action<string?> assign)
        {
            if (overwriteAll || !string.IsNullOrWhiteSpace(value))
                assign(string.IsNullOrWhiteSpace(value) ? null : value.Trim());
        }
        void SetList(string? value, Action<string[]> assign)
        {
            if (overwriteAll || !string.IsNullOrWhiteSpace(value))
                assign(SplitList(value));
        }
        void SetNum(uint value, Action<uint> assign)
        {
            if (overwriteAll || value > 0)
                assign(value);
        }
        // TagLib's MusicBrainz properties throw on null.
        //
        // Every other property on Tag treats null as "clear it". These do not. The Xiph and Apple code
        // passes the value to a SetField or SetDashBox overload that dereferences it. So clearing one
        // raises a NullReferenceException. The exception comes out of TagLib, through WriteCore, and into
        // the ripper's best-effort catch. There it silently costs the file every tag, not only the one
        // being cleared. An empty string clears these properly in all three containers, so that is what is
        // used.
        void SetId(string? value, Action<string> assign)
        {
            if (overwriteAll || !string.IsNullOrWhiteSpace(value))
                assign(value?.Trim() ?? "");
        }
        // The extended fields have no TagLib property, so they are written through their carrier. They
        // follow the same rule as everything above. On a partial write, a field that this info knows
        // nothing about is left exactly as the file had it.
        void SetTag(TagCarrier carrier, string? value)
        {
            if (overwriteAll || !string.IsNullOrWhiteSpace(value))
                carrier.Write(file, value);
        }
        void SetTagNum(TagCarrier carrier, uint value)
        {
            if (overwriteAll || value > 0)
                carrier.WriteNumber(file, value);
        }

        Set(info.Title, v => tag.Title = v);
        SetList(info.Artist, v => tag.Performers = v);
        SetList(info.AlbumArtist, v => tag.AlbumArtists = v);
        Set(info.Album, v => tag.Album = v);
        SetList(info.Genre, v => tag.Genres = v);
        SetList(info.Composers, v => tag.Composers = v);
        Set(info.Conductor, v => tag.Conductor = v);
        Set(info.Comment, v => tag.Comment = v);
        Set(info.Lyrics, v => tag.Lyrics = v);
        Set(info.Grouping, v => tag.Grouping = v);
        Set(info.Copyright, v => tag.Copyright = v);
        Set(info.Publisher, v => tag.Publisher = v);
        Set(info.RemixedBy, v => tag.RemixedBy = v);
        Set(info.Isrc, v => tag.ISRC = v);
        Set(info.MusicalKey, v => tag.InitialKey = v);
        SetId(info.ReleaseCountry, v => tag.MusicBrainzReleaseCountry = v);
        SetNum(info.TrackNumber, v => tag.Track = v);
        SetNum(info.TrackCount, v => tag.TrackCount = v);
        SetNum(info.DiscNumber, v => tag.Disc = v);
        SetNum(info.DiscCount, v => tag.DiscCount = v);
        SetTag(ExtendedTags.DiscSubtitle, info.DiscSubtitle);
        SetTag(ExtendedTags.Subgenre, info.Subgenre);
        // Unknown means nobody has said. It does not mean clean. On a partial write, the file's own flag is
        // left alone. This avoids clearing a good flag that the editor never had a value for.
        if (overwriteAll || info.Advisory != ContentAdvisory.Unknown)
            AdvisoryTag.Write(file, info.Advisory);
        SetNum(info.Year, v => tag.Year = v);
        SetNum(info.Bpm, v => tag.BeatsPerMinute = v);

        Set(info.TitleSort, v => tag.TitleSort = v);
        SetList(info.ArtistSort, v => tag.PerformersSort = v);
        SetList(info.AlbumArtistSort, v => tag.AlbumArtistsSort = v);
        SetList(info.ComposerSort, v => tag.ComposersSort = v);

        SetTag(ExtendedTags.Work, info.Work);
        SetTag(ExtendedTags.MovementName, info.MovementName);
        if (overwriteAll || info.MovementNumber > 0 || info.MovementCount > 0)
            MovementTag.Write(file, info.MovementNumber, info.MovementCount);
        // Derived, never edited. A file that names a work and a movement is part of a larger work. A file
        // that names neither is not. See ExtendedTags.ShowMovementFor.
        if (overwriteAll || !string.IsNullOrWhiteSpace(info.Work))
            ExtendedTags.ShowMovement.Write(
                file, ExtendedTags.ShowMovementFor(info.Work, info.MovementName) ? "1" : null);
        SetTag(ExtendedTags.Lyricist, info.Lyricist);
        SetTag(ExtendedTags.Arranger, info.Arranger);
        SetTag(ExtendedTags.Ensemble, info.Ensemble);
        SetTag(ExtendedTags.Soloists, info.Soloists);

        SetTag(ExtendedTags.Barcode, info.Barcode);
        SetTag(ExtendedTags.CatalogNumber, info.CatalogNumber);
        SetTag(ExtendedTags.MusicBrainzDiscId, info.MusicBrainzDiscId);
        SetTag(ExtendedTags.Media, info.Media);
        // Null is "nobody has said": on a partial write the file's own type is left alone, as for every field here.
        if (overwriteAll || info.ReleaseType is not null)
            ReleaseTypeTag.Write(file, info.ReleaseType);
        SetTagNum(ExtendedTags.OriginalYear, info.OriginalYear);

        SetId(info.MusicBrainzRecordingId, v => tag.MusicBrainzTrackId = v);
        SetId(info.MusicBrainzReleaseId, v => tag.MusicBrainzReleaseId = v);
        SetId(info.MusicBrainzReleaseGroupId, v => tag.MusicBrainzReleaseGroupId = v);
        SetId(info.MusicBrainzArtistId, v => tag.MusicBrainzArtistId = v);
        SetId(info.MusicBrainzReleaseArtistId, v => tag.MusicBrainzReleaseArtistId = v);

        if (info.PendingArt is { } art)
        {
            tag.Pictures = art.Length == 0
                ? Array.Empty<TagLib.IPicture>()
                : new TagLib.IPicture[]
                {
                    new TagLib.Picture(new TagLib.ByteVector(art))
                    {
                        Type = TagLib.PictureType.FrontCover,
                        MimeType = info.PendingArtMime ?? "image/jpeg",
                    },
                };
        }

        file.Save();
    }

    /// <summary>
    /// Loads the front-cover art bytes and the MIME type. Returns null when there is none, or on error.
    ///
    /// <para>Returns null without opening anything when the bytes are not on this disk. This is the same
    /// rule that <see cref="Read"/> follows, for the same two reasons. A track that exists only in the
    /// cloud has no file here to hold a picture. A placeholder would have to be downloaded in full before
    /// it could be checked. Cover art is decoration, so it is never worth downloading a record for.</para>
    /// </summary>
    public static (byte[] Bytes, string Mime)? LoadArt(string path)
    {
        if (NotOnThisDisk(path))
            return null;

        try
        {
            using var file = TagLib.File.Create(path);
            var pics = file.Tag.Pictures;
            if (pics is not { Length: > 0 })
                return null;
            var pic = pics.FirstOrDefault(p => p.Type == TagLib.PictureType.FrontCover) ?? pics[0];
            var data = pic.Data?.Data;
            if (data is not { Length: > 0 })
                return null;
            return (data, string.IsNullOrWhiteSpace(pic.MimeType) ? "image/jpeg" : pic.MimeType);
        }
        catch
        {
            return null;
        }
    }

    private static string? Join(string[]? values)
        => values is { Length: > 0 } ? string.Join("; ", values.Where(v => !string.IsNullOrWhiteSpace(v))) : null;

    private static string[] SplitList(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        s = s.Trim();
        // Placeholder values that mean the field is untagged
        return s is "Unknown" or "Unknown Artist" or "Unknown Album" ? null : s;
    }
}
