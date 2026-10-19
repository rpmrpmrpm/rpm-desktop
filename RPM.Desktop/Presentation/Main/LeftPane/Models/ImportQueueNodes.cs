using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Models;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using RPM.DataGrid.Tree;

namespace RPM.Desktop.Presentation.Main.LeftPane.Models;

/// <summary>
/// One audio file in the review workbench. Holds the CURRENT situation (what the
/// analysis found on disk, immutable) and the WANTED situation (editable metadata that
/// drives the proposed layout and gets written back on Apply). Shared by the leaf nodes
/// of both tree-grids.
/// </summary>
[RpmBindable]
public sealed partial class TrackRow : INotifyPropertyChanged
{
    // Not readonly. One thing changes it: the file this row stands for was moved on disk by a write
    // that this app made. See FiledAt.
    private TrackInfo _current;

    private string? _artist;
    private string? _albumArtist;
    private string? _album;
    private string? _title;
    private string? _track;
    private string? _trackCount;
    private string? _disc;
    private string? _discCount;
    private string? _discSubtitle;
    private string? _year;
    private string? _genre;
    private string? _subgenre;
    private string? _composers;
    private string? _conductor;
    private string? _comment;
    private string? _lyrics;
    private string? _grouping;
    private string? _bpm;
    private string? _copyright;
    private string? _publisher;
    private string? _remixedBy;
    private string? _isrc;
    private string? _musicalKey;
    private string? _releaseCountry;

    // The sort names, the classical block, the pressing and the ids. Each one is a string that the user
    // may edit, like the fields above, so they need no special handling. See TagFields, the one place
    // where any window learns that a field exists.
    private string? _titleSort;
    private string? _artistSort;
    private string? _albumArtistSort;
    private string? _composerSort;
    private string? _work;
    private string? _movementName;
    private string? _movement;
    private string? _movementCount;
    private string? _lyricist;
    private string? _arranger;
    private string? _ensemble;
    private string? _soloists;
    private string? _barcode;
    private string? _catalogNumber;
    private string? _media;
    /// <summary>The release type by its stored name (ReleaseTypes.ToStored), or null where none is set.</summary>
    private string? _releaseType;
    private string? _originalYear;
    private string? _recordingId;
    private string? _releaseId;
    private string? _releaseGroupId;
    private string? _artistId;
    private string? _releaseArtistId;

    /// <summary>The advisory flag is not a text box. It is a badge, and the catalogue gives the answer.
    /// It is carried on the row, so a rip or a lookup can set it and Apply can write it.</summary>
    private ContentAdvisory _advisory;

    private byte[]? _pendingArt;      // null = keep file's art; [] = remove
    private string? _pendingArtMime;

    private MetadataSource _source;
    private double _score;
    private bool _isChecked;

    /// <summary>Raised after any wanted-side field changes. The model re-plans the wanted tree.</summary>
    public event Action? WantedChanged;

    /// <summary>Raised when the row is ticked or unticked, so the import set has changed.</summary>
    public event Action? CheckedChanged;

    public TrackRow(TrackInfo current)
    {
        _current = current;
        _artist = current.Artist;
        _albumArtist = current.AlbumArtist;
        _album = current.Album;
        _title = current.Title;
        _track = Num(current.TrackNumber);
        _trackCount = Num(current.TrackCount);
        _disc = Num(current.DiscNumber);
        _discCount = Num(current.DiscCount);
        _discSubtitle = current.DiscSubtitle;
        _year = Num(current.Year);
        _genre = current.Genre;
        _subgenre = current.Subgenre;
        _composers = current.Composers;
        _conductor = current.Conductor;
        _comment = current.Comment;
        _lyrics = current.Lyrics;
        _grouping = current.Grouping;
        _bpm = Num(current.Bpm);
        _copyright = current.Copyright;
        _publisher = current.Publisher;
        _remixedBy = current.RemixedBy;
        _isrc = current.Isrc;
        _musicalKey = current.MusicalKey;
        _releaseCountry = current.ReleaseCountry;
        _advisory = current.Advisory;
        _titleSort = current.TitleSort;
        _artistSort = current.ArtistSort;
        _albumArtistSort = current.AlbumArtistSort;
        _composerSort = current.ComposerSort;
        _work = current.Work;
        _movementName = current.MovementName;
        _movement = Num(current.MovementNumber);
        _movementCount = Num(current.MovementCount);
        _lyricist = current.Lyricist;
        _arranger = current.Arranger;
        _ensemble = current.Ensemble;
        _soloists = current.Soloists;
        _barcode = current.Barcode;
        _catalogNumber = current.CatalogNumber;
        _media = current.Media;
        _releaseType = current.ReleaseType is { } currentType ? RPM.Core.Library.ReleaseTypes.ToStored(currentType) : null;
        _originalYear = Num(current.OriginalYear);
        _recordingId = current.MusicBrainzRecordingId;
        _releaseId = current.MusicBrainzReleaseId;
        _releaseGroupId = current.MusicBrainzReleaseGroupId;
        _artistId = current.MusicBrainzArtistId;
        _releaseArtistId = current.MusicBrainzReleaseArtistId;
        _source = current.Source;
        _score = current.IdentificationScore;
    }

    private static string? Num(uint v) => v > 0 ? v.ToString() : "";

    /// <summary>Rebuilds a row from stored current and wanted metadata. The DB-backed queue reads a
    /// <see cref="RPM.Core.Library.QueueTrack"/> back into a row through this method. The wanted side
    /// and the tick are set on the private fields directly, so NO change events fire. Wire
    /// WantedChanged and CheckedChanged AFTER calling this. Otherwise the store would see a
    /// write-back for a row that it has just handed out.</summary>
    public static TrackRow FromStored(TrackInfo current, TrackInfo wanted, bool isChecked)
    {
        var row = new TrackRow(current);
        row.LoadWanted(wanted, isChecked);
        return row;
    }

    /// <summary>
    /// Re-reads the stored wanted side and tick into a row that is already on screen, and says so once.
    ///
    /// <para>A page fetch returns what the store holds, and that is what this row was last told. Through
    /// the ordinary setters, every field of every visible row would be sent straight back to the store,
    /// one write per row per fetch. A fetch that started before an edit could also land after it, and
    /// put the old value back over the new one. That is the same failure as a tag panel that builds its
    /// row twice: the edit is saved, and then quietly undone.</para>
    ///
    /// <para>So this method sets fields, not setters. It raises no <see cref="WantedChanged"/> and no
    /// <see cref="CheckedChanged"/>. It raises one unnamed PropertyChanged at the end, because every
    /// field may have changed, and the cells read themselves off the row. <see cref="SetCheckedQuietly"/>
    /// does the same for the tick alone.</para>
    /// </summary>
    public void LoadStoredQuietly(TrackInfo wanted, bool isChecked)
    {
        LoadWanted(wanted, isChecked);
        PropertyChanged?.Invoke(this, EverythingChanged);
    }

    /// <summary>"Re-read all of it." One instance for the whole grid: a page of rows raises one
    /// each.</summary>
    private static readonly PropertyChangedEventArgs EverythingChanged = new(null);

    /// <summary>Copies a stored wanted side onto the fields themselves. Private on purpose: setting
    /// these through their properties is what asks the store for a write, and both callers here are
    /// reading FROM the store.</summary>
    private void LoadWanted(TrackInfo wanted, bool isChecked)
    {
        _artist = wanted.Artist;
        _albumArtist = wanted.AlbumArtist;
        _album = wanted.Album;
        _title = wanted.Title;
        _track = Num(wanted.TrackNumber);
        _trackCount = Num(wanted.TrackCount);
        _disc = Num(wanted.DiscNumber);
        _discCount = Num(wanted.DiscCount);
        _discSubtitle = wanted.DiscSubtitle;
        _year = Num(wanted.Year);
        _genre = wanted.Genre;
        _subgenre = wanted.Subgenre;
        _composers = wanted.Composers;
        _conductor = wanted.Conductor;
        _comment = wanted.Comment;
        _lyrics = wanted.Lyrics;
        _grouping = wanted.Grouping;
        _bpm = Num(wanted.Bpm);
        _copyright = wanted.Copyright;
        _publisher = wanted.Publisher;
        _remixedBy = wanted.RemixedBy;
        _isrc = wanted.Isrc;
        _musicalKey = wanted.MusicalKey;
        _releaseCountry = wanted.ReleaseCountry;
        _advisory = wanted.Advisory;
        _titleSort = wanted.TitleSort;
        _artistSort = wanted.ArtistSort;
        _albumArtistSort = wanted.AlbumArtistSort;
        _composerSort = wanted.ComposerSort;
        _work = wanted.Work;
        _movementName = wanted.MovementName;
        _movement = Num(wanted.MovementNumber);
        _movementCount = Num(wanted.MovementCount);
        _lyricist = wanted.Lyricist;
        _arranger = wanted.Arranger;
        _ensemble = wanted.Ensemble;
        _soloists = wanted.Soloists;
        _barcode = wanted.Barcode;
        _catalogNumber = wanted.CatalogNumber;
        _media = wanted.Media;
        _releaseType = wanted.ReleaseType is { } wantedType ? RPM.Core.Library.ReleaseTypes.ToStored(wantedType) : null;
        _originalYear = Num(wanted.OriginalYear);
        _recordingId = wanted.MusicBrainzRecordingId;
        _releaseId = wanted.MusicBrainzReleaseId;
        _releaseGroupId = wanted.MusicBrainzReleaseGroupId;
        _artistId = wanted.MusicBrainzArtistId;
        _releaseArtistId = wanted.MusicBrainzReleaseArtistId;
        _source = wanted.Source;
        _score = wanted.IdentificationScore;
        _isChecked = isChecked;
        // The cover the user chose is part of the wanted side, like any other field. Without the two
        // lines below, the store keeps it and the row it is handed back to does not.
        _pendingArt = wanted.PendingArt;
        _pendingArtMime = wanted.PendingArtMime;
    }

    /// <summary>The store id of this row in the DB-backed queue. It is 0 when the row is not a queue row.
    /// It is set when the row is built from the store. Every write-back is keyed by this id.</summary>
    public long QueueId { get; set; }

    public TrackInfo Current => _current;
    public string FilePath => _current.FilePath;

    /// <summary>The file's size on disk, carried from the store. It is the Size column in the grid, and
    /// the final word on which of two encodes of one track is the better one.</summary>
    public long SizeBytes { get; set; }

    /// <summary>Where the file came in from: the source folder's own name, then the path under it. The
    /// tree groups by the tags. That is the right view until the tags say nothing. After that, the
    /// folder a file was in is the only clue about it.</summary>
    public string Folder { get; set; } = "";

    /// <summary>The album a split kept. It sits in the Originals folder, beside the tracks it was cut
    /// into. It is listed so that a bad cut can be redone from it. It is never filed, so it is never
    /// ticked, and its box is not offered.</summary>
    public bool IsPreservedOriginal =>
        RPM.Core.Splitting.AlbumFileSplitter.IsOriginalsFolder(
            Folder[(Folder.LastIndexOf('/') + 1)..]);

    /// <summary>Whether the library, or the rest of this queue, already holds this track. It is set when
    /// the row is built from the store, which computes it.</summary>
    public RPM.Core.Library.QueueDuplicate Duplicate { get; set; }

    /// <summary>Whether the file could be read at all. A cloud placeholder that was not opened on purpose,
    /// and a file where no audio could be found, cannot be imported. Neither case is the same as a track
    /// that simply has no tags yet.</summary>
    public FileAvailability Availability => _current.Availability;

    // ── current situation (what the file carries right now, read-only) ──
    public string CurrentArtist => _current.Artist ?? "";
    public string CurrentAlbum => _current.Album ?? "";

    // ── wanted situation (editable) ──
    public string Artist { get => _artist ?? ""; set => SetWanted(ref _artist, value); }
    public string AlbumArtist { get => _albumArtist ?? ""; set => SetWanted(ref _albumArtist, value); }
    public string Album { get => _album ?? ""; set => SetWanted(ref _album, value); }
    public string Title { get => _title ?? ""; set => SetWanted(ref _title, value); }
    public string Track { get => _track ?? ""; set => SetWanted(ref _track, value); }
    public string TrackCount { get => _trackCount ?? ""; set => SetWanted(ref _trackCount, value); }
    public string Disc { get => _disc ?? ""; set => SetWanted(ref _disc, value); }
    public string DiscCount { get => _discCount ?? ""; set => SetWanted(ref _discCount, value); }
    public string DiscSubtitle { get => _discSubtitle ?? ""; set => SetWanted(ref _discSubtitle, value); }
    public string Year { get => _year ?? ""; set => SetWanted(ref _year, value); }
    public string Genre { get => _genre ?? ""; set => SetWanted(ref _genre, value); }
    public string Subgenre { get => _subgenre ?? ""; set => SetWanted(ref _subgenre, value); }
    public string Composers { get => _composers ?? ""; set => SetWanted(ref _composers, value); }
    public string Conductor { get => _conductor ?? ""; set => SetWanted(ref _conductor, value); }
    public string Comment { get => _comment ?? ""; set => SetWanted(ref _comment, value); }
    public string Lyrics { get => _lyrics ?? ""; set => SetWanted(ref _lyrics, value); }
    public string Grouping { get => _grouping ?? ""; set => SetWanted(ref _grouping, value); }
    public string Bpm { get => _bpm ?? ""; set => SetWanted(ref _bpm, value); }
    public string Copyright { get => _copyright ?? ""; set => SetWanted(ref _copyright, value); }
    public string Publisher { get => _publisher ?? ""; set => SetWanted(ref _publisher, value); }
    public string RemixedBy { get => _remixedBy ?? ""; set => SetWanted(ref _remixedBy, value); }
    public string Isrc { get => _isrc ?? ""; set => SetWanted(ref _isrc, value); }
    public string MusicalKey { get => _musicalKey ?? ""; set => SetWanted(ref _musicalKey, value); }

    public string TitleSort { get => _titleSort ?? ""; set => SetWanted(ref _titleSort, value); }
    public string ArtistSort { get => _artistSort ?? ""; set => SetWanted(ref _artistSort, value); }
    public string AlbumArtistSort { get => _albumArtistSort ?? ""; set => SetWanted(ref _albumArtistSort, value); }
    public string ComposerSort { get => _composerSort ?? ""; set => SetWanted(ref _composerSort, value); }

    public string Work { get => _work ?? ""; set => SetWanted(ref _work, value); }
    public string MovementName { get => _movementName ?? ""; set => SetWanted(ref _movementName, value); }
    public string Movement { get => _movement ?? ""; set => SetWanted(ref _movement, value); }
    public string MovementCount { get => _movementCount ?? ""; set => SetWanted(ref _movementCount, value); }
    public string Lyricist { get => _lyricist ?? ""; set => SetWanted(ref _lyricist, value); }
    public string Arranger { get => _arranger ?? ""; set => SetWanted(ref _arranger, value); }
    public string Ensemble { get => _ensemble ?? ""; set => SetWanted(ref _ensemble, value); }
    public string Soloists { get => _soloists ?? ""; set => SetWanted(ref _soloists, value); }

    public string Barcode { get => _barcode ?? ""; set => SetWanted(ref _barcode, value); }
    public string CatalogNumber { get => _catalogNumber ?? ""; set => SetWanted(ref _catalogNumber, value); }
    public string Media { get => _media ?? ""; set => SetWanted(ref _media, value); }
    /// <summary>The release type's stored name, "" for none: the value of one of TagFields.ReleaseTypeValues' options.</summary>
    public string ReleaseType { get => _releaseType ?? ""; set => SetWanted(ref _releaseType, value); }
    public string OriginalYear { get => _originalYear ?? ""; set => SetWanted(ref _originalYear, value); }

    public string RecordingId { get => _recordingId ?? ""; set => SetWanted(ref _recordingId, value); }
    public string ReleaseId { get => _releaseId ?? ""; set => SetWanted(ref _releaseId, value); }
    public string ReleaseGroupId { get => _releaseGroupId ?? ""; set => SetWanted(ref _releaseGroupId, value); }
    public string ArtistId { get => _artistId ?? ""; set => SetWanted(ref _artistId, value); }
    public string ReleaseArtistId { get => _releaseArtistId ?? ""; set => SetWanted(ref _releaseArtistId, value); }

    /// <summary>What the catalogue said about the lyrics. It is not a text box. It is a badge that the row
    /// shows, and only a lookup or the file's own tags set it.</summary>
    public ContentAdvisory Advisory
    {
        get => _advisory;
        set
        {
            if (_advisory == value)
                return;
            _advisory = value;
            OnChanged(nameof(Advisory));
            OnChanged(nameof(IsExplicit));
            WantedChanged?.Invoke();
        }
    }

    /// <summary>Whether this row shows the explicit mark. This is the same question the library table
    /// asks.</summary>
    public bool IsExplicit => _advisory == ContentAdvisory.Explicit;

    /// <summary>The advisory as the edit windows read and write it: a fixed token, so the field goes
    /// through the same string-shaped catalogue as everything else. See TagFields.AdvisoryValues.</summary>
    public string AdvisoryValue
    {
        get => Dialogs.TagFields.AdvisoryValues.ToValue(_advisory);
        set => Advisory = Dialogs.TagFields.AdvisoryValues.Parse(value);
    }

    /// <summary>Two-letter country of the pressing.</summary>
    public string ReleaseCountry
    {
        get => _releaseCountry ?? "";
        set => SetWanted(ref _releaseCountry, value);
    }

    /// <summary>Replacement cover art: null = keep, empty = remove, bytes = new front cover.</summary>
    public byte[]? PendingArt => _pendingArt;
    public string? PendingArtMime => _pendingArtMime;

    public void SetPendingArt(byte[]? bytes, string? mime)
    {
        _pendingArt = bytes;
        _pendingArtMime = mime;
        OnChanged(nameof(PendingArt));
        WantedChanged?.Invoke();
    }

    public MetadataSource Source => _source;
    public double Score => _score;

    /// <summary>Set when the file looks like a whole album or record in one file (a long duration, or a
    /// sibling cue sheet). It drives the split badge and the context action.</summary>
    public RPM.Core.Splitting.AlbumFileCandidate? SplitCandidate { get; set; }

    /// <summary>True when the wanted side still lacks a reliable artist or title. The queue asks the user
    /// to fix this. It does NOT hold the row out of an import. A ticked row is imported as long as it has
    /// an artist and a title to file it under, guessed or not.</summary>
    public bool NeedsRecognition =>
        string.IsNullOrWhiteSpace(_artist) || string.IsNullOrWhiteSpace(_title)
        || _source is MetadataSource.FilenameGuess or MetadataSource.Unknown;

    /// <summary>
    /// The minimum a track needs before the library can shelve it the way the library shelves things:
    /// artist, album artist, album, title, track number, year and genre. An import needs only an artist
    /// and a title, because a path is built from those. This higher bar is what the queue's tick mark
    /// checks. The queue is the place where a gap is cheap to fill. Later, finding the gap in a filed
    /// collection is a hunt.
    ///
    /// <para>Asked for the glyph AND the colour of every visible row, on every binding evaluation. So it
    /// allocates nothing and looks nothing up. Naming the missing fields is the job of the tooltip
    /// only.</para>
    /// </summary>
    public bool HasMinimumMetadata => RPM.Core.Library.QueueTags.Satisfied(
        _artist, _albumArtist, _album, _title, Uint(_track), Uint(_year), _genre, _subgenre);

    /// <summary>What the edit windows call each tag. The tooltip names the field the same way as the box
    /// that fills it is labelled.</summary>
    internal static string LabelKey(RPM.Core.Library.QueueTag tag) => tag switch
    {
        RPM.Core.Library.QueueTag.Artist => "Review_ColArtist",
        RPM.Core.Library.QueueTag.AlbumArtist => "Review_ColAlbumArtist",
        RPM.Core.Library.QueueTag.Album => "Review_ColAlbum",
        RPM.Core.Library.QueueTag.Title => "Review_ColTitle",
        RPM.Core.Library.QueueTag.Track => "Review_ColTrack",
        RPM.Core.Library.QueueTag.Year => "Review_ColYear",
        RPM.Core.Library.QueueTag.Genre => "Review_ColGenre",
        RPM.Core.Library.QueueTag.Subgenre => "Review_ColSubgenre",
        // No default arm. A new tag must get its own label here. A default would give it a wrong label on
        // screen without any error. QueueTags.MissingSql has no default arm for the same reason.
        _ => throw new ArgumentOutOfRangeException(nameof(tag), tag, "no label for this tag"),
    };

    /// <summary>Which of the minimum fields are empty, named as the edit windows name them. This is the
    /// tooltip on a row's question mark, so it says what is missing, not just that something is. It is
    /// built on demand only. See <see cref="HasMinimumMetadata"/> for the cheap check.</summary>
    public IReadOnlyList<string> MissingFields
    {
        get
        {
            return [.. RPM.Core.Library.QueueTags
                .MissingFrom(_artist, _albumArtist, _album, _title, Uint(_track), Uint(_year), _genre, _subgenre)
                .Select(tag => Strings.Loc.Get(LabelKey(tag)))];
        }
    }

    /// <summary>Ticked in the queue's checkbox column. The ticked rows are the ones that Recognize and
    /// Import act on. Rows start unticked, so nothing is acted on until the user ticks it.</summary>
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnChanged();
            CheckedChanged?.Invoke();
        }
    }

    /// <summary>Sets the tick without asking for a write-back. Use this for a bulk tick that the store has
    /// already applied. Writing each row back again would be one UPDATE per visible row, for a change that
    /// is already made.</summary>
    public void SetCheckedQuietly(bool value)
    {
        if (_isChecked == value)
            return;
        _isChecked = value;
        OnChanged(nameof(IsChecked));
    }

    /// <summary>"Edit &amp; move right": the user confirmed the wanted fields by hand. It takes effect only
    /// once artist and title are filled. Then the row counts as recognised.</summary>
    public void MarkManual()
    {
        if (string.IsNullOrWhiteSpace(_artist) || string.IsNullOrWhiteSpace(_title))
            return;
        _source = MetadataSource.Manual;
        RaiseAll();
        WantedChanged?.Invoke();
    }

    /// <summary>
    /// The file this row stands for is now at this path. A write that the app has just made moved it.
    ///
    /// <para>The current side of a row is otherwise fixed for the life of the row. It is what was found on
    /// disk, and the wanted side is proposed against it. A move is the one thing that can make it untrue
    /// without the row being rebuilt. A row that still gives the old path would be skipped as unreadable
    /// by any later write. A second window that shows the same rows has the same problem after the first
    /// window has moved them.</para>
    ///
    /// <para>Only the path changes. The rest of the current side was read from the file, and it is still
    /// true. The tags that a write has just put in the file belong to the wanted side. Keeping the two
    /// apart is what keeps <see cref="TagsDirty"/> correct.</para>
    /// </summary>
    public void FiledAt(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || string.Equals(path, _current.FilePath, StringComparison.Ordinal))
            return;

        _current = _current with { FilePath = path };
        OnChanged(nameof(FilePath));
        OnChanged(nameof(Current));
    }

    /// <summary>True when any wanted field differs from what the file currently carries.</summary>
    public bool TagsDirty =>
        Diff(_artist, _current.Artist) || Diff(_albumArtist, _current.AlbumArtist)
        || Diff(_album, _current.Album) || Diff(_title, _current.Title)
        || Diff(_track, Num(_current.TrackNumber)) || Diff(_trackCount, Num(_current.TrackCount))
        || Diff(_disc, Num(_current.DiscNumber)) || Diff(_discCount, Num(_current.DiscCount))
        || Diff(_discSubtitle, _current.DiscSubtitle)
        || Diff(_year, Num(_current.Year)) || Diff(_genre, _current.Genre)
        || Diff(_subgenre, _current.Subgenre)
        || Diff(_composers, _current.Composers) || Diff(_conductor, _current.Conductor)
        || Diff(_comment, _current.Comment) || Diff(_lyrics, _current.Lyrics)
        || Diff(_grouping, _current.Grouping) || Diff(_bpm, Num(_current.Bpm))
        || Diff(_copyright, _current.Copyright) || Diff(_publisher, _current.Publisher)
        || Diff(_remixedBy, _current.RemixedBy) || Diff(_isrc, _current.Isrc)
        || Diff(_musicalKey, _current.MusicalKey) || _advisory != _current.Advisory
        || Diff(_releaseCountry, _current.ReleaseCountry)
        || Diff(_titleSort, _current.TitleSort) || Diff(_artistSort, _current.ArtistSort)
        || Diff(_albumArtistSort, _current.AlbumArtistSort) || Diff(_composerSort, _current.ComposerSort)
        || Diff(_work, _current.Work) || Diff(_movementName, _current.MovementName)
        || Diff(_movement, Num(_current.MovementNumber))
        || Diff(_movementCount, Num(_current.MovementCount))
        || Diff(_lyricist, _current.Lyricist) || Diff(_arranger, _current.Arranger)
        || Diff(_ensemble, _current.Ensemble) || Diff(_soloists, _current.Soloists)
        || Diff(_barcode, _current.Barcode) || Diff(_catalogNumber, _current.CatalogNumber)
        || Diff(_media, _current.Media) || Diff(_originalYear, Num(_current.OriginalYear))
        || Diff(_releaseType, _current.ReleaseType is { } diffType ? RPM.Core.Library.ReleaseTypes.ToStored(diffType) : null)
        || Diff(_recordingId, _current.MusicBrainzRecordingId)
        || Diff(_releaseId, _current.MusicBrainzReleaseId)
        || Diff(_releaseGroupId, _current.MusicBrainzReleaseGroupId)
        || Diff(_artistId, _current.MusicBrainzArtistId)
        || Diff(_releaseArtistId, _current.MusicBrainzReleaseArtistId);

    private static bool Diff(string? a, string? b)
        => (a ?? "").Trim() != (b ?? "").Trim();

    /// <summary>Applies a recognition result to the wanted side.</summary>
    public void ApplyRecognition(string title, string artist, string? album, uint year, double score)
    {
        _title = title;
        _artist = artist;
        if (!string.IsNullOrWhiteSpace(album)) _album = album;
        if (year > 0) _year = year.ToString();
        _source = MetadataSource.Fingerprint;
        _score = score;
        RaiseAll();
        WantedChanged?.Invoke();
    }

    /// <summary>The wanted situation as a TrackInfo (drives planning and tag write-back).</summary>
    public TrackInfo ToWantedTrackInfo() => _current with
    {
        Artist = Null(_artist),
        AlbumArtist = Null(_albumArtist),
        Album = Null(_album),
        Title = Null(_title),
        Genre = Null(_genre),
        Subgenre = Null(_subgenre),
        Composers = Null(_composers),
        Conductor = Null(_conductor),
        Comment = Null(_comment),
        Lyrics = Null(_lyrics),
        Grouping = Null(_grouping),
        Copyright = Null(_copyright),
        Publisher = Null(_publisher),
        RemixedBy = Null(_remixedBy),
        Isrc = Null(_isrc),
        MusicalKey = Null(_musicalKey),
        ReleaseCountry = Null(_releaseCountry),
        Advisory = _advisory,
        TrackNumber = Uint(_track),
        TrackCount = Uint(_trackCount),
        DiscNumber = Uint(_disc),
        DiscCount = Uint(_discCount),
        DiscSubtitle = Null(_discSubtitle),
        Year = Uint(_year),
        Bpm = Uint(_bpm),

        TitleSort = Null(_titleSort),
        ArtistSort = Null(_artistSort),
        AlbumArtistSort = Null(_albumArtistSort),
        ComposerSort = Null(_composerSort),

        Work = Null(_work),
        MovementName = Null(_movementName),
        MovementNumber = Uint(_movement),
        MovementCount = Uint(_movementCount),
        Lyricist = Null(_lyricist),
        Arranger = Null(_arranger),
        Ensemble = Null(_ensemble),
        Soloists = Null(_soloists),

        Barcode = Null(_barcode),
        CatalogNumber = Null(_catalogNumber),
        Media = Null(_media),
        ReleaseType = RPM.Core.Library.ReleaseTypes.FromStored(_releaseType),
        OriginalYear = Uint(_originalYear),

        MusicBrainzRecordingId = Null(_recordingId),
        MusicBrainzReleaseId = Null(_releaseId),
        MusicBrainzReleaseGroupId = Null(_releaseGroupId),
        MusicBrainzArtistId = Null(_artistId),
        MusicBrainzReleaseArtistId = Null(_releaseArtistId),

        PendingArt = _pendingArt,
        PendingArtMime = _pendingArtMime,
        TagsEdited = TagsDirty,
        Source = _source,
        IdentificationScore = _score,
    };

    private static string? Null(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static uint Uint(string? s) => uint.TryParse(s, out var v) ? v : 0;

    private void SetWanted(ref string? field, string value, [CallerMemberName] string? name = null)
    {
        if (field == value) return;
        field = value;
        OnChanged(name);
        OnChanged(nameof(NeedsRecognition));
        WantedChanged?.Invoke();
    }

    private void RaiseAll()
    {
        foreach (var p in new[] { nameof(Artist), nameof(Album), nameof(Title), nameof(Track), nameof(Year),
                                  nameof(NeedsRecognition), nameof(HasMinimumMetadata) })
            OnChanged(p);
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// A row in the import queue's tree-grid: an artist, one of that artist's albums, or a file leaf
/// wrapping a <see cref="TrackRow"/>. It implements <see cref="ITreeGridRow"/>, so the grid shows it
/// as a tree.
///
/// <para>The two group levels come from the METADATA, not from the folders the files came from. The
/// queue is a preview of the library. Where a file sits right now is the one thing that importing
/// throws away. A track with no artist or no album still gets a group, the "Unknown" ones. A file that
/// nobody named is exactly the file that someone has to look at.</para>
/// </summary>
[RpmBindable]
public sealed partial class ImportNode : INotifyPropertyChanged, ITreeGridRow
{
    private bool _isExpanded = true;
    private string _name = "";

    // Not `required`: Uno's bindable-metadata generator needs a parameterless Activator.

    /// <summary>What the tree column calls this row. It can be set, and it raises change events. A leaf
    /// that is read again after an edit is the same node under a new title (see
    /// <see cref="Reseat"/>).</summary>
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
                return;
            _name = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name)));
        }
    }

    /// <summary>How deep the row sits: the tree column's indent. It can be set, for the same reason as
    /// <see cref="Name"/>. A row that changed its record keeps its node.</summary>
    public int Depth { get; set; }

    public List<ImportNode> Children { get; } = new();

    private readonly TrackRow? _row;

    /// <summary>The file this leaf is about, for the life of the node. The store never changes what a
    /// queued file is. It only changes what the file is wanted to say. So a re-read loads the new wanted
    /// side into this row, and does not replace the row. That keeps the tag panel, a drag in progress and
    /// the grid's selection holding the row they were given.
    ///
    /// <para>The hook is attached when the row is given to the node, not by whoever builds the node. Every
    /// cell on this line reads from the row. So a row that has changed, while its line has not repainted,
    /// is a state that this class must not allow. Writing values onto a fetched row, other than through
    /// the row's own setters, can leave the line out of date. For example, numbering a record writes the
    /// numbers, but the column stays blank until the page is read again.</para></summary>
    public TrackRow? Row
    {
        get => _row;
        init
        {
            _row = value;
            if (value is not null)
                value.PropertyChanged += OnRowChanged;
        }
    }

    /// <summary>
    /// The row changed, so the line says so too. The row and the node live and die together, so there is
    /// nothing to unhook.
    ///
    /// <para>There are three cases. A tick changes only the box. Ticking a queue changes hundreds of rows
    /// at once. If each row re-read all twenty of its cells to colour a checkbox, a "select all" would take
    /// a second. The mark works the same way, one level up. Any other change is a VALUE. A value is a cell,
    /// a title, and a mark that may have just been answered, so the line is read again as a whole.</para>
    /// </summary>
    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(TrackRow.IsChecked):
                RaiseChecked();
                break;
            case nameof(TrackRow.NeedsRecognition):
            case nameof(TrackRow.HasMinimumMetadata):
                RaiseCurrentState();
                break;
            default:
                SyncFromRow();
                break;
        }
    }

    /// <summary>
    /// Re-reads the line from the row: its title, its mark, and every cell.
    ///
    /// <para>This raises one unnamed PropertyChanged, not a dozen named ones. A cell that is not realised
    /// has nothing bound to it, so it costs nothing either way. A row that IS on screen may have had every
    /// field rewritten, and an edit does not say which field changed.</para>
    /// </summary>
    public void SyncFromRow()
    {
        if (_row is null)
            return;
        _name = LeafTitle(_row, ColFile, Parent?.GroupDiscCount ?? 0);
        _stateTip = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    /// <summary>
    /// What a track is called in the tree: its place on the record, as the library will show it. A file
    /// that nobody has named yet falls back to the name it has on disk. At that point, that is the only
    /// thing known about it.
    /// </summary>
    /// <param name="albumDiscCount">On a multi-disc set, the track number alone is not enough. Every disc
    /// has a track 1. Two rows both called "01  Intro" would look like a duplicate of the first
    /// disc.</param>
    public static string LeafTitle(TrackRow row, string fileName, int albumDiscCount)
    {
        if (string.IsNullOrWhiteSpace(row.Title))
            return fileName;
        if (row.Track.Length == 0 || !uint.TryParse(row.Track, out var n) || n == 0)
            return row.Title;

        return albumDiscCount > 1 && uint.TryParse(row.Disc, out var disc) && disc > 0
            ? $"{disc}-{n:00}  {row.Title}"
            : $"{n:00}  {row.Title}";
    }

    public ImportNode? Parent { get; set; }

    /// <summary>The store id of a file leaf in the DB-backed queue. It is 0 for folders and library nodes.
    /// It ties the row back to its row in the <c>queue</c> table, for edit write-back.</summary>
    public long QueueId { get; init; }

    /// <summary>Playback service for the play/stop glyph on file leaves (null on folder nodes).</summary>
    public PlaybackService? Playback { get; init; }

    /// <summary>Group nodes only: the artist this group files under, exactly as the store keys it. An empty
    /// string is the Unknown Artist bucket. It is a real key, not a missing one.</summary>
    public string? GroupArtist { get; init; }

    /// <summary>Album-group nodes only: the album within <see cref="GroupArtist"/>. An empty string means
    /// Unknown Album. It is null on an artist node, which stands for every album under that
    /// artist.</summary>
    public string? GroupAlbum { get; init; }

    /// <summary>Album-group nodes only: the highest disc number under this record. It is 0 when nothing
    /// here is numbered, and more than 1 when the record is a set. The group uses it to say "2 discs", and
    /// its leaves use it to show "2-01" instead of a second "01".</summary>
    public int GroupDiscCount { get; init; }

    /// <summary>Album-group nodes only: this record has answers of its own, such as classical, levelled, or
    /// a disc label. They wait for the folder that the import will give it. The mark is on the row, because
    /// an answer given before the import would otherwise stay hidden until it is too late to
    /// check.</summary>
    public bool HasAlbumIntent { get; init; }

    /// <summary>Leaf nodes only: how far into its group's block this row sat when it was fetched. With the
    /// group's key, this lets a rebuilt tree put the viewport back where it was. The skeleton is built
    /// again from scratch, so a row can only be found again by where it was. It moves when the row moves,
    /// which is why it is not fixed when the node is built.</summary>
    public int LeafOffset { get; set; }

    /// <summary>Group nodes only: how many tracks the group holds directly. They are not in
    /// <see cref="Children"/>, because the virtualised source pages them in from the store on demand. The
    /// expander chevron must still show.</summary>
    public int VirtualTrackCount { get; init; }

    public bool HasChildren => Children.Count > 0 || VirtualTrackCount > 0;
    public bool IsFile => Row is not null;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    /// <summary>
    /// Puts an existing leaf back into the tree after its store row was read again. It is the same node and
    /// the same <see cref="Row"/>, under a new title and in a new place.
    ///
    /// <para>Keeping the node is the point. In this pane the ticks ARE the grid's selection, and the grid
    /// holds that selection as a set of these objects. If a re-fetch built new objects, every row would
    /// stay ticked but none would show as selected, and the selection would look broken. A field edit and
    /// a rearrange inside one record are both the same rows read again. Keeping the node makes that
    /// true.</para>
    ///
    /// <para>One unnamed PropertyChanged at the end: every cell on the line is derived from what was just
    /// re-read.</para>
    /// </summary>
    public void Reseat(string name, ImportNode parent, int leafOffset)
    {
        _name = name;
        Parent = parent;
        Depth = parent.Depth + 1;
        LeafOffset = leafOffset;
        _stateTip = null;   // it names the fields that are missing, and they may have just been filled
        // No name, which means "re-read all of it". It is created here, not kept in a static field. A
        // static field on this class would also load the brushes below, and those need a UI thread to
        // exist.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    /// <summary>For a GROUP node: ticks every row under it in the store. Its leaves are paged, not in
    /// <see cref="Children"/>, so they cannot be walked in memory. The queue source sets this after
    /// construction, because the callback needs the node it belongs to, and an initializer cannot name
    /// it.</summary>
    public Action<bool>? FolderCheckAll { get; set; }

    // A group's tick state (checked and total) is kept on the node, not asked of the store. The paged
    // leaves cannot be counted in memory. They do not need to be: the GROUP BY that builds the skeleton
    // has already counted them, and each tick changes these two numbers by hand.
    //
    // Asking the store for this count on each read is too slow. The count would be read on realise,
    // on scroll, and up the whole ancestor chain for every leaf tick. With twenty thousand rows, each
    // read is a two-millisecond scan of the queue, to colour one checkbox.
    private int _checked;
    private int _total;

    /// <summary>Sets a group's counters from the skeleton read.</summary>
    public void SetCheckState(int @checked, int total)
    {
        _checked = @checked;
        _total = total;
    }

    /// <summary>Moves a group's counters by hand after its own rows were ticked, and passes the move
    /// up: an album's change is its artist's change too.</summary>
    private void ShiftCheckState(int delta)
    {
        _checked = Math.Clamp(_checked + delta, 0, _total);
        RaiseChecked();
        Parent?.ShiftCheckState(delta);
    }

    /// <summary>Queue tick. A file keeps its own state on the TrackRow, so the state survives when the tree
    /// is rebuilt. A folder is ticked when every row under it is, and it is shown as indeterminate when
    /// only some are. Ticking a folder ticks all the rows under it. Leaves pass the change up, so the
    /// folders above them are read again.</summary>
    public bool? IsChecked
    {
        get
        {
            if (IsFile)
                return Row!.IsChecked;
            return _total == 0 || _checked == 0 ? false : _checked == _total ? true : null;
        }
        set
        {
            var on = value == true;
            if (IsFile)
            {
                if (Row!.IsChecked == on)
                    return;
                Row.IsChecked = on;
                // One leaf moved, so every group above it moved by one. No query, no re-count.
                Parent?.ShiftCheckState(on ? 1 : -1);
            }
            else if (FolderCheckAll is { } checkAll)
            {
                var delta = (on ? _total : 0) - _checked;
                checkAll(on);
                _checked = on ? _total : 0;
                // And every group under it moved with it. An artist's tick is its records' tick too. A box
                // that was set without a re-read is a record that looks untouched, while every track under
                // it is ticked.
                foreach (var child in Children)
                    child.SetCheckedThroughout(on);
                if (delta != 0)
                    Parent?.ShiftCheckState(delta);
            }
            else
            {
                foreach (var child in Children)
                    child.IsChecked = on;
            }

            RaiseChecked();
        }
    }

    /// <summary>
    /// Everything under this group is ticked, or none of it is. The store already says so.
    ///
    /// <para>This goes all the way down, and every level is told. A queue ticked from the header reaches
    /// each record through its artist, and each record has its own box on screen. A counter that is set
    /// without a notice would leave a record showing half-ticked while every track under it is
    /// clear.</para>
    /// </summary>
    public void SetCheckedThroughout(bool on)
    {
        _checked = on ? _total : 0;
        foreach (var child in Children)
            child.SetCheckedThroughout(on);
        RaiseChecked();
    }

    /// <summary>Re-reads this row's tick. It is public because a bulk tick applied straight to the store
    /// has to update the rows already on screen. A reset would avoid that, but it would move the reader's
    /// place.</summary>
    public void RaiseChecked()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));

    /// <summary>True on the file that is playing right now. The grid shows the playing bars in place of the
    /// leading glyph on this row. Double-click a row to play it. There is no play button.</summary>
    public bool IsPlaying => Row is not null && Playback?.IsPlaying(Row.FilePath) == true;

    public void RaisePlaying()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPlaying)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowsGrip)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowsGlyphOverride)));
    }

    private bool _hovered;

    /// <summary>
    /// True while the pointer is over this row. The grid sets it. The grid works out which row the pointer
    /// is over from the containers it holds. A tree column builds its own cells, and there is no template
    /// of ours on a row to attach a PointerEntered handler to.
    /// </summary>
    public bool IsHovered
    {
        get => _hovered;
        set
        {
            if (_hovered == value)
                return;
            _hovered = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsHovered)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowsGrip)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowsGlyphOverride)));
        }
    }

    /// <summary>
    /// Whether this row shows the drag handle instead of its usual state mark.
    ///
    /// <para>The handle has no column of its own. Under the pointer, the mark that says whether the file is
    /// identified turns into the grip that moves it. A row either says what it is or offers to be picked
    /// up. It needs to offer only while the pointer is on it. Files only. A record cannot be carried by the
    /// tree.</para>
    /// </summary>
    public bool ShowsGrip => _hovered && IsFile && !IsPlaying;

    /// <summary>Whether the leading glyph is standing aside for something else: the playing bars or the
    /// grip. One binding is used for both, because the column offers only one override.</summary>
    public bool ShowsGlyphOverride => IsPlaying || ShowsGrip;

    // ── grid columns ───────────────────────────────────────────────────────────────────────────────
    //
    // The same fields that the library's table shows, read from the WANTED side. The queue is a preview of
    // the library, so it is read the same way. The question this grid answers is "does everything have a
    // year, a genre and an album artist?". That question can only be asked of a column. Anything the
    // library knows and a queued file does not (plays, date added, cloud state) is absent, not blank.

    /// <summary>The folder the file came in from, under the source that was added. It sits beside the name,
    /// not at the far end with the file name. When a track lands in Unknown Artist, this is the only thing
    /// on the row that says anything about it, so it must be readable without scrolling.</summary>
    public string ColPath => Row?.Folder ?? "";

    public string ColArtist => Row?.Artist ?? "";
    public string ColAlbumArtist => Row?.AlbumArtist ?? "";
    public string ColAlbum => Row?.Album ?? "";
    public string ColTitle => Row?.Title ?? "";
    public string ColTrack => Row?.Track ?? "";
    public string ColDisc => Row?.Disc ?? "";
    public string ColDiscSubtitle => Row?.DiscSubtitle ?? "";
    public string ColYear => Row?.Year ?? "";
    public string ColGenre => Row?.Genre ?? "";
    public string ColSubgenre => Row?.Subgenre ?? "";

    public string ColLength => Row is null ? "" : TrackFieldText.Duration(Row.Current.DurationSeconds);
    public string ColKind => Row is null ? "" : TrackFieldText.Kind(Row.Current.Extension);
    public string ColCodec => Row?.Current.CodecDescription ?? "";
    public string ColBitrate => Row is null ? "" : TrackFieldText.Bitrate(Row.Current.BitrateKbps);
    public string ColSampleRate => Row is null ? "" : TrackFieldText.SampleRate(Row.Current.SampleRate);
    public string ColBitDepth => Row is null ? "" : TrackFieldText.BitDepth(Row.Current.BitsPerSample);
    public string ColChannels => Row is null ? "" : TrackFieldText.Channels(Row.Current.Channels);
    public string ColSize => Row is null ? "" : TrackFieldText.Size(Row.SizeBytes);

    // Fixed for the life of the row, so it is split once rather than on every read of the cell.
    private string? _colFile;

    /// <summary>Where the file is NOW. It is the last column, because importing rewrites it.</summary>
    public string ColFile => _colFile ??= Row is null ? "" : System.IO.Path.GetFileName(Row.FilePath);

    // ── the state marks ────────────────────────────────────────────────────────────────────────────

    // One brush per colour, shared by the whole grid. The glyph brush is read for every visible row on
    // every binding evaluation. If a brush were created on each read, scrolling would create thousands of
    // SolidColorBrush objects to paint eight fixed colours.
    //
    // One axis, not eight hues: the palette's azure, then its cyan, then white. Gold, vermilion, lime and
    // magenta all at once said only that everything was equally urgent, which says nothing. None of them
    // are this app's colours. On one ramp the marks read as a SCALE. The bluer the mark, the more there is
    // to do about it. White is where there is nothing to do. The glyph, not the hue, still says which
    // member of the family this is.
    private static readonly Brush GroupBrush = Frozen(0xFF, 0x03, 0x71, 0xD0);       // azure: structure
    private static readonly Brush ReadyBrush = Frozen(0x8C, 0xFF, 0xFF, 0xFF);       // nothing to do
    private static readonly Brush DatalessBrush = Frozen(0xFF, 0xB4, 0xD2, 0xE6);    // pale ice: a fact
    private static readonly Brush AlbumFileBrush = Frozen(0xFF, 0x63, 0xC9, 0xF0);   // sky: split it
    // The album a split kept. It is quiet, because there is nothing to do about it. It uses the steel blue
    // of the duplicate marks, which marks a fact about a file rather than a job. It has the weight of the
    // ready mark, so it sits at the "nothing to do" end of the ramp.
    private static readonly Brush KeptOriginalBrush = Frozen(0x8C, 0x8F, 0xB0, 0xC8);
    private static readonly Brush MissingBrush = Frozen(0xFF, 0x04, 0xC8, 0xFF);     // full cyan: fill it in
    // The far end of the ramp is white again, on purpose. A file that nothing can open needs the same thing
    // as a finished file, which is nothing. One is done and the other is a dead end. The weight (faint
    // against full) is what tells them apart.
    private static readonly Brush UnreadableBrush = Frozen(0xFF, 0xFF, 0xFF, 0xFF);  // full white: no way in
    // The two duplicate marks are one family. They use one steel blue at two weights. A duplicate is a
    // duplicate, and the glyph shows which kind it is. The cyan is for marks that are a reason to import,
    // not a warning: the upgrade, and the record that the shelf lists with no file here.
    private static readonly Brush InLibraryBrush = Frozen(0xFF, 0x8F, 0xB0, 0xC8);   // steel: the shelf has it
    private static readonly Brush InQueueBrush = Frozen(0xD8, 0x8F, 0xB0, 0xC8);     // the same, one step back
    private static readonly Brush UpgradeBrush = Frozen(0xFF, 0x04, 0xC8, 0xFF);     // cyan: a better copy
    private static readonly Brush AlbumIntentBrush = Frozen(0xFF, 0x04, 0xC8, 0xFF);  // rpm cyan
    private static readonly Brush NoBrush = Frozen(0x00, 0x00, 0x00, 0x00);

    private static Brush Frozen(byte a, byte r, byte g, byte b)
        => new SolidColorBrush(Color.FromArgb(a, r, g, b));

    /// <summary>
    /// The tree column's mark. A group shows which level it is. A file shows the one thing most worth
    /// knowing before importing. The checks run in this order, because the trouble matters in this order. A
    /// whole record in one file comes first, since nothing else about it is true until it is split. Then
    /// the metadata that is still missing. Then a track that has everything the shelf needs.
    /// </summary>
    public string CurrentStateGlyph =>
        // A record is shown as a record, not as a folder. These two levels come from the METADATA. Where
        // the files sit is the one thing that importing throws away. The vinyl icon is used, not the
        // compact disc, because the compact disc is used two lines down for the album-in-one-file mark.
        !IsFile ? (GroupAlbum is null ? "\uf007" : "\uf8d9")   // artist: person, album: record
        : Row!.Availability is FileAvailability.Unreadable ? "\uf071"  // warning triangle: nothing can read it
        : Row.Availability is FileAvailability.Dataless ? "\uf0c2"     // cloud: not on this disk
        // This check comes before the album-file mark, which the file would otherwise show. The file is a
        // whole record in one file, but it has already been cut and is being kept. Its box is not offered
        // (see CanTick), and that does not say why. This mark says why.
        : Row.IsPreservedOriginal ? "\uf187"                   // archive box: kept, never filed
        : Row.SplitCandidate is not null ? "\uf51f"            // compact disc: a record in one file
        : !Row.HasMinimumMetadata ? "\uf059"                   // question mark: fields still empty
        : "\uf00c";                                           // check: everything the shelf needs

    // The mark a row shows, quietest first. A file that needs nothing gets a faint white check. The blue
    // gets deeper towards the thing most worth doing: fetch it, split it, or fill it in.
    public Brush CurrentStateBrush =>
        !IsFile ? (HasAlbumIntent ? AlbumIntentBrush : GroupBrush)
        : Row!.Availability is FileAvailability.Unreadable ? UnreadableBrush
        : Row.Availability is FileAvailability.Dataless ? DatalessBrush
        : Row.IsPreservedOriginal ? KeptOriginalBrush
        : Row.SplitCandidate is not null ? AlbumFileBrush
        : !Row.HasMinimumMetadata ? MissingBrush
        : ReadyBrush;

    /// <summary>What the mark means, in words. A question mark says WHICH fields are missing. That is the
    /// whole difference between a warning and an instruction.</summary>
    // Worked out once per row and kept. Naming the missing fields costs a list and a resource lookup. This
    // is a tooltip binding. Without this cache it would run for every row that scrolled past, even when
    // nobody hovered over it.
    private string? _stateTip;

    public string CurrentStateTip => _stateTip ??= BuildStateTip();

    private string BuildStateTip() =>
        !IsFile ? (HasAlbumIntent ? Strings.Loc.Get("Queue_AlbumIntentTip") : "")
        : Row!.Availability is FileAvailability.Unreadable ? Strings.Loc.Get("QueueTip_Unreadable")
        : Row.Availability is FileAvailability.Dataless ? Strings.Loc.Get("QueueTip_Dataless")
        : Row.IsPreservedOriginal ? Strings.Loc.Get("QueueTip_Original")
        : Row.SplitCandidate is not null ? Strings.Loc.Get("QueueLegend_AlbumFile")
        : Row.MissingFields is { Count: > 0 } missing
            ? Strings.Loc.Format("Queue_MissingFields", string.Join(", ", missing))
        : Strings.Loc.Get("QueueLegend_Complete");

    /// <summary>Whether this row's box is offered at all. A group's box is always offered. A file's box is
    /// not offered when the file is the album that a split kept, because that file can never be filed. A
    /// box that cannot be ticked looks broken, so it is not drawn. The store side of the same rule is
    /// <c>LibraryDatabase</c>'s <c>TickableSql</c>.</summary>
    public bool CanTick => !IsFile || Row?.IsPreservedOriginal != true;

    /// <summary>The duplicate mark. The library already holds this track, or another file higher up this
    /// same queue does. Or this file is the better copy of a track that the library holds.</summary>
    public string DuplicateGlyph => Row?.Duplicate switch
    {
        RPM.Core.Library.QueueDuplicate.InLibrary => "\uf0c5",   // copies: the shelf has it already
        RPM.Core.Library.QueueDuplicate.Upgrade => "\uf102",     // chevrons up: better than the shelf has
        RPM.Core.Library.QueueDuplicate.InQueue => "\uf24d",     // clone: another queued file is better
        RPM.Core.Library.QueueDuplicate.InCloud => "\uf0c2",     // cloud: listed, but no file here
        _ => "",
    };

    // The duplicate column uses its own colours, not the tree column's ramp. A desaturated steel means that
    // something already has this. That is a fact about the shelf, not a job. The cyan is used for the two
    // cases that are a reason to import rather than a warning: a better copy than the shelf holds, and a
    // record that the shelf lists with no file on this computer.
    public Brush DuplicateBrush => Row?.Duplicate switch
    {
        RPM.Core.Library.QueueDuplicate.InLibrary => InLibraryBrush,
        RPM.Core.Library.QueueDuplicate.Upgrade => UpgradeBrush,
        RPM.Core.Library.QueueDuplicate.InQueue => InQueueBrush,
        RPM.Core.Library.QueueDuplicate.InCloud => UpgradeBrush,
        _ => NoBrush,
    };

    public string DuplicateTip => Row?.Duplicate switch
    {
        RPM.Core.Library.QueueDuplicate.InLibrary => Strings.Loc.Get("QueueDup_InLibrary"),
        RPM.Core.Library.QueueDuplicate.Upgrade => Strings.Loc.Get("QueueDup_Upgrade"),
        RPM.Core.Library.QueueDuplicate.InQueue => Strings.Loc.Get("QueueDup_InQueue"),
        RPM.Core.Library.QueueDuplicate.InCloud => Strings.Loc.Get("QueueDup_InCloud"),
        _ => "",
    };

    public void RaiseCurrentState()
    {
        _stateTip = null;   // the fields it names may have just changed
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentStateGlyph)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentStateBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CurrentStateTip)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
