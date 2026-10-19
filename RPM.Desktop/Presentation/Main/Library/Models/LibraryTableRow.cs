using System.ComponentModel;
using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Desktop.Services;

namespace RPM.Desktop.Presentation.Main.Library.Models;

/// <summary>
/// One track in the library's data table: every field the mirror holds about it, as the grid reads
/// them. There is one row per FILE and no hierarchy. The table is where a collection is checked
/// (does every track have a year, a genre, an album artist?). A question like that is asked of a
/// column, so every track has to be on the same list. Browsing by artist and album is the album
/// list's job.
///
/// <para>The values are strings because they are only shown as text. Ordering and filtering do not
/// use them. Both run in SQL on the real column (see <see cref="TrackTableQuery"/>), so a year sorts
/// as a number and a duration sorts by seconds, whatever the text looks like here.</para>
/// </summary>
[RpmBindable]
public sealed partial class LibraryTableRow : INotifyPropertyChanged
{
    private readonly PlaybackService? _playback;
    private TrackRow? _row;

    public LibraryTableRow(LibraryTrack track, string libraryRoot, PlaybackService? playback = null)
    {
        Track = track;
        LibraryRoot = libraryRoot;
        _playback = playback;
    }

    public LibraryTrack Track { get; }
    public string LibraryRoot { get; }

    public long Id => Track.Id;
    public string RelativePath => Track.RelativePath;

    public string FilePath =>
        Path.Combine(LibraryRoot, Track.RelativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The editable row the track dialogs use. It is built the first time a dialog opens. The
    /// table itself is read-only: it reports what is in the mirror, and the edit windows change it. So
    /// nothing needs this row until the user asks for it.</summary>
    public TrackRow Row => _row ??= new TrackRow(Track.ToTrackInfo(LibraryRoot));

    /// <summary>True while this is the track in the player. The grid shows the equaliser in place of the
    /// leading mark, as every other list in the app does.</summary>
    public bool IsPlaying => _playback?.IsPlaying(FilePath) == true;

    public void RaisePlaying()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlaying)));

    // ── the tags ──

    public string Title => Track.Title ?? "";

    /// <summary>The file's own advisory flag says the lyrics are explicit, the E in front of the
    /// title. A column read: the mirror carries the flag, so scrolling the table opens no files.</summary>
    public bool IsExplicit => Track.Advisory == ContentAdvisory.Explicit;

    public string Artist => Track.Artist ?? "";
    public string AlbumArtist => Track.AlbumArtist ?? "";
    public string Album => Track.Album ?? "";
    public string Genre => Track.Genre ?? "";
    public string DiscSubtitle => Track.DiscSubtitle ?? "";

    /// <summary>The composition this track is part of, and who wrote it. These are the two columns a
    /// classical shelf is read and sorted by. Most tracks leave them blank, so both columns are hidden
    /// by default and turned on from the column picker.</summary>
    public string Work => Track.Work ?? "";

    public string Composer => Track.Composer ?? "";

    public string TrackNumber => TrackFieldText.Num(Track.TrackNumber);
    public string DiscNumber => TrackFieldText.Num(Track.DiscNumber);
    public string Year => TrackFieldText.Num(Track.Year);

    // ── what the file is (spelled by TrackFieldText, so the import grid says it identically) ──

    public string Kind => TrackFieldText.Kind(Track.Extension);

    /// <summary>The decoder's name for the stream. This is the only thing that tells Apple Lossless from
    /// AAC. Both use the .m4a extension, so the extension alone cannot tell them apart.</summary>
    public string Codec => Track.CodecDescription ?? "";

    public string Bitrate => TrackFieldText.Bitrate(Track.BitrateKbps);
    public string SampleRate => TrackFieldText.SampleRate(Track.SampleRate);
    public string BitDepth => TrackFieldText.BitDepth(Track.BitsPerSample);
    public string Channels => TrackFieldText.Channels(Track.Channels);
    public string Duration => TrackFieldText.Duration(Track.DurationSeconds);
    public string Size => TrackFieldText.Size(Track.SizeBytes);

    // ── what the library knows, which the file does not ──

    public string Plays => Track.Plays > 0 ? Track.Plays.ToString() : "";
    public string LastPlayed => Stamp(Track.LastPlayedUtcTicks);
    public string Added => Stamp(Track.AddedUtcTicks);
    public string Modified => Stamp(Track.ModifiedUtcTicks);

    public string Cloud => Track.CloudState switch
    {
        CloudState.Queued => Strings.Loc.Get("Cloud_StateQueued"),
        CloudState.Uploading => Strings.Loc.Get("Cloud_StateUploading"),
        // Synced, but the cloud has no preview for this track. The column shows this state instead of
        // plain "Synced", so the reader can see why the track is not fully synced.
        CloudState.Synced when Track.CloudPartialError is { Length: > 0 }
            => Strings.Loc.Get("Cloud_StateNoPreview"),
        CloudState.Synced => Strings.Loc.Get("Cloud_StateSynced"),
        CloudState.CloudOnly => Strings.Loc.Get("Cloud_StateCloudOnly"),
        CloudState.Error => Strings.Loc.Get("Cloud_StateError"),
        _ => "",
    };

    // ── where it lives ──

    public string Folder => Track.Folder;
    public string FileName => Track.FileName;
    public string RpmId => Track.RpmId ?? "";

    /// <summary>A stored UTC tick count as a local date. Zero means the event never happened (never
    /// played, never dated). It shows as blank, not as the first day of the calendar.</summary>
    private static string Stamp(long utcTicks) => utcTicks > 0
        ? new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime().ToString("g")
        : "";

    public event PropertyChangedEventHandler? PropertyChanged;
}
