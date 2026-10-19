using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Dialogs.Controls;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary> How many files a field is a fact about. It is not a permission. Every field stays editable
/// wherever it has meaning, because correcting one file's album name is how a record that was split in
/// two gets repaired. The scope sets the <b>default reach</b> of a write. It decides which window shows
/// a field as its own subject, and which shows it as something to be told about. </summary>
public enum TagScope
{
    /// <summary>True of this file alone: its title, its number, who played on it.</summary>
    Track,

    /// <summary>True of one disc of a release: which disc it is, what the disc is called, and how many
    /// tracks are on it. The disc's printed label is also a picture at this level.</summary>
    Disc,

    /// <summary>True of the whole record: its name, its year, who put it out. The cover is a picture at
    /// this level: one image, embedded in every track and written to the folder.</summary>
    Album,
}

/// <summary>Every tag field the app edits.</summary>
public enum TagFieldKind
{
    // ── track ──
    Title, Artist, TrackNo, Composers, Conductor, RemixedBy, Bpm, Isrc, Grouping, Comment, Lyrics,
    TitleSort, ArtistSort, MusicalKey, RecordingId, ArtistId, Advisory,

    // ── the classical block: a movement of a work, and who wrote and played it ──
    Work, MovementName, Movement, MovementCount, ComposerSort, Lyricist, Arranger, Ensemble, Soloists,

    // ── disc ──
    Disc, TrackCount, DiscSubtitle,

    // ── album ──
    Album, AlbumArtist, Year, Genre, Subgenre, ReleaseType, Publisher, Copyright, DiscCount,
    AlbumArtistSort, OriginalYear, Barcode, CatalogNumber, Media, ReleaseCountry,
    ReleaseId, ReleaseGroupId, ReleaseArtistId,
}

/// <summary> One field, declared once: what it is called, how wide it wants to be, which group it
/// belongs under, how it is read off a row and written back, and how it lands in a file's tags.
/// </summary>
/// <param name="GroupKey">The group heading it sits under. The same in every window, so a field is
/// always found in the same place.</param>
/// <param name="PairedWith">A field that shares its row, such as "Track [3] of [12]" or "Disc [1] of
/// [2]". The paired field is drawn by its partner and gets no row of its own.</param>
/// <param name="Options">The only values this field may take, when it is a choice rather than free
/// text. A field with options is drawn as a list, not a box, in every window. An advisory flag has
/// exactly three states, and typing into it could only produce a fourth.</param>
/// <param name="Suggestions">Values worth offering, for a field that is neither a closed list nor free
/// text. It is drawn as a box that can be typed in, with the list beside it. A genre names a library
/// group, so the groups that already exist are offered, and any other value can still be typed.</param>
/// <param name="SuggestionsFor">The same as Suggestions, but narrowed by the genre the record already
/// has. A style belongs to one genre, so the styles offered are the ones under that genre. That is a
/// dozen names, not 394, and none of them from another genre. A field like this follows the genre box
/// beside it. When the genre changes, the styles change too, because a style that fits the old genre
/// may not fit the new one.</param>
public sealed record TagField(
    TagFieldKind Kind,
    string LabelKey,
    TagScope Scope,
    string GroupKey,
    Func<TrackRow, string> Get,
    Action<TrackRow, string> Set,
    Func<TrackInfo, string, TrackInfo> Apply,
    double? Width = null,
    bool Multiline = false,
    TagFieldKind? PairedWith = null,
    IReadOnlyList<TagFieldOption>? Options = null,
    bool Numeric = false,
    IReadOnlyList<string>? Suggestions = null,
    Func<string?, IReadOnlyList<string>>? SuggestionsFor = null);

/// <summary> One entry in a choice field's list: what gets stored, and what the user reads. </summary>
/// <param name="Value">Stored and compared exactly as written. It is never the label. A file tagged in
/// French and read in German must give the same flag, so the value is a fixed token and only the label
/// is translated.</param>
public sealed record TagFieldOption(string Value, string LabelKey);

/// <summary>The catalogue that every editing window is built from: the mass edit, the album editor and
/// the per-file editor all read this one list. None of them keeps its own list of tags.
/// </summary>
public static class TagFields
{
    private static uint Num(string value) => uint.TryParse(value?.Trim(), out var n) ? n : 0;

    /// <summary>The styles offered for a record filed under this genre, whatever the genre box says.
    /// The genre text goes through <see cref="Genres.Match"/> first. So a box that reads "Deep House"
    /// or "edm" offers the styles of the genre it means. For a genre the app does not know, the list is
    /// empty and the style stays a plain box that can still be typed into.</summary>
    public static IReadOnlyList<string> StylesUnder(string? genre)
        => [.. Subgenres.Under(Genres.Match(genre)).Select(s => s.Name)];

    public static readonly IReadOnlyList<TagField> All =
    [
        // ── track: true of this file and nothing else ──
        new(TagFieldKind.Title, "Review_ColTitle", TagScope.Track, Groups.Track,
            r => r.Title, (r, v) => r.Title = v, (i, v) => i with { Title = v }),
        new(TagFieldKind.Artist, "Review_ColArtist", TagScope.Track, Groups.Track,
            r => r.Artist, (r, v) => r.Artist = v, (i, v) => i with { Artist = v }),
        // The number is the track's. The count belongs to the disc the track is on, so the two have
        // different scopes, even though they share a row.
        new(TagFieldKind.TrackNo, "Details_TrackNo", TagScope.Track, Groups.Track,
            r => r.Track, (r, v) => r.Track = v, (i, v) => i with { TrackNumber = Num(v) },
            Width: Widths.Tiny, PairedWith: TagFieldKind.TrackCount, Numeric: true),
        // The two values the app measures itself. They are grouped apart from the other track fields,
        // because they are not on a sleeve or in a catalogue. They are what the audio was found to
        // contain when RPM analysed it. See Analysis/AnalysisTags.
        new(TagFieldKind.Bpm, "Details_Bpm", TagScope.Track, Groups.Measured,
            r => r.Bpm, (r, v) => r.Bpm = v, (i, v) => i with { Bpm = Num(v) }, Width: Widths.Narrow, Numeric: true),
        new(TagFieldKind.Isrc, "Details_Isrc", TagScope.Track, Groups.Track,
            r => r.Isrc, (r, v) => r.Isrc = v, (i, v) => i with { Isrc = v }),
        new(TagFieldKind.Grouping, "Details_Grouping", TagScope.Track, Groups.Track,
            r => r.Grouping, (r, v) => r.Grouping = v, (i, v) => i with { Grouping = v }),
        new(TagFieldKind.Comment, "Details_Comment", TagScope.Track, Groups.Track,
            r => r.Comment, (r, v) => r.Comment = v, (i, v) => i with { Comment = v }, Multiline: true),
        new(TagFieldKind.Lyrics, "Details_Lyrics", TagScope.Track, Groups.Track,
            r => r.Lyrics, (r, v) => r.Lyrics = v, (i, v) => i with { Lyrics = v }, Multiline: true),

        new(TagFieldKind.MusicalKey, "Details_MusicalKey", TagScope.Track, Groups.Measured,
            r => r.MusicalKey, (r, v) => r.MusicalKey = v, (i, v) => i with { MusicalKey = v },
            Width: Widths.Narrow),

        // Three states, so this is a list rather than a checkbox. "Not stated" is the honest answer for
        // most records. It is not the same as "clean". Clean means someone marked an explicit edited
        // version. A tick box would force every unmarked track to claim one or the other. See
        // ContentAdvisory, and AdvisoryTag for how the three values are written to a file.
        new(TagFieldKind.Advisory, "Details_Advisory", TagScope.Track, Groups.Track,
            r => r.AdvisoryValue, (r, v) => r.AdvisoryValue = v,
            (i, v) => i with { Advisory = AdvisoryValues.Parse(v) },
            Options: AdvisoryValues.Options),

        new(TagFieldKind.Composers, "Details_Composers", TagScope.Track, Groups.Credits,
            r => r.Composers, (r, v) => r.Composers = v, (i, v) => i with { Composers = v }),
        new(TagFieldKind.Conductor, "Details_Conductor", TagScope.Track, Groups.Credits,
            r => r.Conductor, (r, v) => r.Conductor = v, (i, v) => i with { Conductor = v }),
        // The orchestra or choir. On a classical record this is almost never the album artist.
        // "Berliner Philharmoniker" belongs here, not in a field for who the record is filed under.
        new(TagFieldKind.Ensemble, "Details_Ensemble", TagScope.Track, Groups.Credits,
            r => r.Ensemble, (r, v) => r.Ensemble = v, (i, v) => i with { Ensemble = v }),
        new(TagFieldKind.Soloists, "Details_Soloists", TagScope.Track, Groups.Credits,
            r => r.Soloists, (r, v) => r.Soloists = v, (i, v) => i with { Soloists = v }),
        new(TagFieldKind.Lyricist, "Details_Lyricist", TagScope.Track, Groups.Credits,
            r => r.Lyricist, (r, v) => r.Lyricist = v, (i, v) => i with { Lyricist = v }),
        new(TagFieldKind.Arranger, "Details_Arranger", TagScope.Track, Groups.Credits,
            r => r.Arranger, (r, v) => r.Arranger = v, (i, v) => i with { Arranger = v }),
        new(TagFieldKind.RemixedBy, "Details_RemixedBy", TagScope.Track, Groups.Credits,
            r => r.RemixedBy, (r, v) => r.RemixedBy = v, (i, v) => i with { RemixedBy = v }),

        // ── the classical block ──
        //
        // A movement is true of one file. The work it belongs to is true of the files that make it up,
        // but not of the record, which may hold three symphonies. So both are track scope. There is no
        // "work" level between the track and the disc. Adding one to hold two fields would add a scope
        // that nothing else uses.
        new(TagFieldKind.Work, "Details_Work", TagScope.Track, Groups.Work,
            r => r.Work, (r, v) => r.Work = v, (i, v) => i with { Work = v }),
        new(TagFieldKind.MovementName, "Details_MovementName", TagScope.Track, Groups.Work,
            r => r.MovementName, (r, v) => r.MovementName = v, (i, v) => i with { MovementName = v }),
        new(TagFieldKind.Movement, "Details_Movement", TagScope.Track, Groups.Work,
            r => r.Movement, (r, v) => r.Movement = v, (i, v) => i with { MovementNumber = Num(v) },
            Width: Widths.Tiny, PairedWith: TagFieldKind.MovementCount, Numeric: true),
        new(TagFieldKind.MovementCount, "Details_MovementCount", TagScope.Track, Groups.Work,
            r => r.MovementCount, (r, v) => r.MovementCount = v,
            (i, v) => i with { MovementCount = Num(v) }, Width: Widths.Tiny, Numeric: true),

        // ── sort names ──
        //
        // What the library sorts by, as opposed to what is printed on the sleeve or the file. Title and
        // performer are track scope. Album artist and composer are album scope, because a record has
        // one of each. A classical library with "Beethoven, Ludwig van" on some tracks and "Ludwig van
        // Beethoven" on others would list two composers for one man.
        new(TagFieldKind.TitleSort, "Details_TitleSort", TagScope.Track, Groups.Sorting,
            r => r.TitleSort, (r, v) => r.TitleSort = v, (i, v) => i with { TitleSort = v }),
        new(TagFieldKind.ArtistSort, "Details_ArtistSort", TagScope.Track, Groups.Sorting,
            r => r.ArtistSort, (r, v) => r.ArtistSort = v, (i, v) => i with { ArtistSort = v }),
        new(TagFieldKind.ComposerSort, "Details_ComposerSort", TagScope.Album, Groups.Sorting,
            r => r.ComposerSort, (r, v) => r.ComposerSort = v, (i, v) => i with { ComposerSort = v }),
        new(TagFieldKind.AlbumArtistSort, "Details_AlbumArtistSort", TagScope.Album, Groups.Sorting,
            r => r.AlbumArtistSort, (r, v) => r.AlbumArtistSort = v,
            (i, v) => i with { AlbumArtistSort = v }),

        // ── disc: true of one disc of the release ──
        new(TagFieldKind.Disc, "Details_Disc", TagScope.Disc, Groups.Release,
            r => r.Disc, (r, v) => r.Disc = v, (i, v) => i with { DiscNumber = Num(v) },
            Width: Widths.Tiny, PairedWith: TagFieldKind.DiscCount, Numeric: true),
        new(TagFieldKind.TrackCount, "Details_TrackCount", TagScope.Disc, Groups.Track,
            r => r.TrackCount, (r, v) => r.TrackCount = v, (i, v) => i with { TrackCount = Num(v) },
            Width: Widths.Tiny, Numeric: true),
        new(TagFieldKind.DiscSubtitle, "Details_DiscSubtitle", TagScope.Disc, Groups.Release,
            r => r.DiscSubtitle, (r, v) => r.DiscSubtitle = v, (i, v) => i with { DiscSubtitle = v }),

        // ── album: true of the whole record ──
        new(TagFieldKind.Album, "Review_ColAlbum", TagScope.Album, Groups.Release,
            r => r.Album, (r, v) => r.Album = v, (i, v) => i with { Album = v }),
        new(TagFieldKind.AlbumArtist, "Details_AlbumArtist", TagScope.Album, Groups.Release,
            r => r.AlbumArtist, (r, v) => r.AlbumArtist = v, (i, v) => i with { AlbumArtist = v }),
        new(TagFieldKind.Year, "Review_ColYear", TagScope.Album, Groups.Release,
            r => r.Year, (r, v) => r.Year = v, (i, v) => i with { Year = Num(v) }, Width: Widths.Narrow, Numeric: true),
        // Suggested, not enforced. The list keeps a library's genres from splitting three ways over one
        // kind of music. A record already tagged "Northern Soul" keeps that value until someone changes
        // it.
        new(TagFieldKind.Genre, "EditSel_Genre", TagScope.Album, Groups.Release,
            r => r.Genre, (r, v) => r.Genre = v, (i, v) => i with { Genre = v },
            Suggestions: Genres.All),
        // The style: which part of the genre the record sits in. It is suggested from the styles under
        // the genre already set, so the list is a dozen names, not 394. Like the genre above it, this
        // is only a suggestion. A record already tagged "Northern Soul" keeps its value, whatever this
        // offers.
        new(TagFieldKind.Subgenre, "EditSel_Subgenre", TagScope.Album, Groups.Release,
            r => r.Subgenre, (r, v) => r.Subgenre = v, (i, v) => i with { Subgenre = v },
            SuggestionsFor: StylesUnder),
        // What kind of release it is. A closed list, so it is always one of the shelves the artist page has (see
        // ArtistShelves); Clear leaves the page guessing from the title.
        new(TagFieldKind.ReleaseType, "Details_ReleaseType", TagScope.Album, Groups.Release,
            r => r.ReleaseType, (r, v) => r.ReleaseType = v,
            (i, v) => i with { ReleaseType = ReleaseTypes.FromStored(v) },
            Options: ReleaseTypeValues.Options),
        new(TagFieldKind.DiscCount, "Details_DiscCount", TagScope.Album, Groups.Release,
            r => r.DiscCount, (r, v) => r.DiscCount = v, (i, v) => i with { DiscCount = Num(v) },
            Width: Widths.Tiny, Numeric: true),
        new(TagFieldKind.Publisher, "Details_Publisher", TagScope.Album, Groups.Publishing,
            r => r.Publisher, (r, v) => r.Publisher = v, (i, v) => i with { Publisher = v }),
        new(TagFieldKind.Copyright, "Details_Copyright", TagScope.Album, Groups.Publishing,
            r => r.Copyright, (r, v) => r.Copyright = v, (i, v) => i with { Copyright = v }),

        // ── which pressing this is ──
        //
        // The facts printed on the sleeve, not in the music. RPM is a record app. The whole difference
        // between the 1977 UK cut and the 2013 remaster is in these four fields. The year alone cannot
        // tell them apart, because the remaster's year is 2013 either way.
        new(TagFieldKind.OriginalYear, "Details_OriginalYear", TagScope.Album, Groups.Pressing,
            r => r.OriginalYear, (r, v) => r.OriginalYear = v, (i, v) => i with { OriginalYear = Num(v) },
            Width: Widths.Narrow, Numeric: true),
        new(TagFieldKind.Media, "Details_Media", TagScope.Album, Groups.Pressing,
            r => r.Media, (r, v) => r.Media = v, (i, v) => i with { Media = v }),
        new(TagFieldKind.CatalogNumber, "Details_CatalogNumber", TagScope.Album, Groups.Pressing,
            r => r.CatalogNumber, (r, v) => r.CatalogNumber = v, (i, v) => i with { CatalogNumber = v }),
        new(TagFieldKind.Barcode, "Details_Barcode", TagScope.Album, Groups.Pressing,
            r => r.Barcode, (r, v) => r.Barcode = v, (i, v) => i with { Barcode = v }),
        // Written by every rip. It can be wrong when a pressing is matched to the wrong catalogue
        // entry, so it can be edited here.
        new(TagFieldKind.ReleaseCountry, "Details_ReleaseCountry", TagScope.Album, Groups.Pressing,
            r => r.ReleaseCountry, (r, v) => r.ReleaseCountry = v,
            (i, v) => i with { ReleaseCountry = v }, Width: Widths.Narrow),

        // ── MusicBrainz identifiers ──
        //
        // Editable, because the values are sometimes wrong, and pasting the right one is the only
        // repair. A file that arrives from Picard already carries them, and a window that could not
        // show them would look as though it had lost them. Each one is scoped to what it identifies, so
        // a mass edit over one album offers the release ids and never the per-track ones.
        new(TagFieldKind.RecordingId, "Details_RecordingId", TagScope.Track, Groups.Identifiers,
            r => r.RecordingId, (r, v) => r.RecordingId = v,
            (i, v) => i with { MusicBrainzRecordingId = v }),
        new(TagFieldKind.ArtistId, "Details_ArtistId", TagScope.Track, Groups.Identifiers,
            r => r.ArtistId, (r, v) => r.ArtistId = v, (i, v) => i with { MusicBrainzArtistId = v }),
        new(TagFieldKind.ReleaseId, "Details_ReleaseId", TagScope.Album, Groups.Identifiers,
            r => r.ReleaseId, (r, v) => r.ReleaseId = v, (i, v) => i with { MusicBrainzReleaseId = v }),
        new(TagFieldKind.ReleaseGroupId, "Details_ReleaseGroupId", TagScope.Album, Groups.Identifiers,
            r => r.ReleaseGroupId, (r, v) => r.ReleaseGroupId = v,
            (i, v) => i with { MusicBrainzReleaseGroupId = v }),
        new(TagFieldKind.ReleaseArtistId, "Details_ReleaseArtistId", TagScope.Album, Groups.Identifiers,
            r => r.ReleaseArtistId, (r, v) => r.ReleaseArtistId = v,
            (i, v) => i with { MusicBrainzReleaseArtistId = v }),
    ];

    /// <summary> The advisory flag as the edit windows deal with it: a fixed token in, a translated
    /// label out.
    ///
    /// <para>The token is stored and compared. The label is only read. A file tagged on a French
    /// install and opened on a German one therefore comes back as the same flag. It does not come back
    /// as a value that neither list recognises.</para> </summary>
    public static class AdvisoryValues
    {
        public const string Unknown = "";
        public const string Explicit = "explicit";
        public const string Clean = "clean";

        public static readonly IReadOnlyList<TagFieldOption> Options =
        [
            new(Unknown, "Details_AdvisoryUnknown"),
            new(Explicit, "Details_AdvisoryExplicit"),
            new(Clean, "Details_AdvisoryClean"),
        ];

        public static ContentAdvisory Parse(string? value) => value?.Trim().ToLowerInvariant() switch
        {
            Explicit => ContentAdvisory.Explicit,
            Clean => ContentAdvisory.Clean,
            _ => ContentAdvisory.Unknown,
        };

        public static string ToValue(ContentAdvisory advisory) => advisory switch
        {
            ContentAdvisory.Explicit => Explicit,
            ContentAdvisory.Clean => Clean,
            _ => Unknown,
        };
    }

    /// <summary>The release type's choices: the seven types by their stored names (<see cref="ReleaseTypes.ToStored"/>).
    /// No "none" option: every choice box puts its own Clear row first (MassEditDialog.ChoiceBox), and an empty value
    /// selects it, so a second way of saying "no type" would sit right under it. The keys are written out rather than
    /// built from the names, so the string-table check can find them.</summary>
    public static class ReleaseTypeValues
    {
        public static readonly IReadOnlyList<TagFieldOption> Options =
        [
            new(nameof(ReleaseType.Album), "ReleaseType_Album"),
            new(nameof(ReleaseType.Ep), "ReleaseType_Ep"),
            new(nameof(ReleaseType.Single), "ReleaseType_Single"),
            new(nameof(ReleaseType.Live), "ReleaseType_Live"),
            new(nameof(ReleaseType.Mixtape), "ReleaseType_Mixtape"),
            new(nameof(ReleaseType.Compilation), "ReleaseType_Compilation"),
            new(nameof(ReleaseType.Soundtrack), "ReleaseType_Soundtrack"),
        ];
    }

    /// <summary>The group headings, in the order a window lays them out.</summary>
    public static class Groups
    {
        public const string Track = "MassEdit_GroupTrack";
        public const string Release = "MassEdit_GroupRelease";
        public const string Credits = "MassEdit_GroupCredits";
        public const string Publishing = "MassEdit_GroupPublishing";

        /// <summary>The composition a track is a movement of.</summary>
        public const string Work = "MassEdit_GroupWork";

        /// <summary>How the shelf is ordered, as against what is printed on it.</summary>
        public const string Sorting = "MassEdit_GroupSorting";

        /// <summary>Which pressing this is: barcode, catalogue number, media, first release.</summary>
        public const string Pressing = "MassEdit_GroupPressing";

        /// <summary>The two things the app measures and a file can carry: the tempo and the
        /// key.</summary>
        public const string Measured = "MassEdit_GroupMeasured";

        /// <summary>What those two came out of: the grid, the confidences, and which reader answered.
        /// Shown, never edited.</summary>
        public const string Reading = "MassEdit_GroupReading";

        /// <summary>How loud the record is. The library's own measurement; no tag carries it.</summary>
        public const string Level = "MassEdit_GroupLevel";

        /// <summary>The MusicBrainz ids that make the next lookup exact.</summary>
        public const string Identifiers = "MassEdit_GroupIdentifiers";
    }

    /// <summary>Named so a field can ask for a box the size of what goes in it without every window
    /// repeating the number. Resolved late: the metrics live in Themes/Generic.xaml.</summary>
    public static class Widths
    {
        /// <summary>A sentinel that the windows swap for <see cref="DenseForm.NarrowWidth"/>, for
        /// example a year or a BPM. It is a literal, not the resource itself, because the catalogue is
        /// a static field and the application's resources are not loaded when it is built.</summary>
        public const double Narrow = -1;

        /// <summary>Two digits: a disc or track number.</summary>
        public const double Tiny = -2;
    }

    /// <summary>Turns a catalogue width sentinel into the real width. Null passes through unchanged. A
    /// field that asks for no width takes the column.</summary>
    public static double? Resolve(double? width) => width switch
    {
        Widths.Narrow => DenseForm.NarrowWidth,
        Widths.Tiny => DenseForm.TinyWidth,
        _ => width,
    };

    /// <summary>
    /// The tokens a pattern may name, and the field each one reads.
    ///
/// <para>It shares one vocabulary with <see cref="RPM.Core.Import.NamingTemplate"/> and <see
/// cref="RPM.Core.Import.FilenameTags"/>. <c>{artist}</c> means the same thing whether it names a file
/// being filed, is read back out of a file name, or builds one tag out of the others. Where the two
/// vocabularies differ, the names are the app's, not the enum's. Nobody types <c>{trackno}</c>.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, TagFieldKind> Tokens =
        new Dictionary<string, TagFieldKind>(StringComparer.OrdinalIgnoreCase)
        {
            ["artist"] = TagFieldKind.Artist,
            ["albumartist"] = TagFieldKind.AlbumArtist,
            ["album"] = TagFieldKind.Album,
            ["title"] = TagFieldKind.Title,
            ["track"] = TagFieldKind.TrackNo,
            ["trackcount"] = TagFieldKind.TrackCount,
            ["disc"] = TagFieldKind.Disc,
            ["disccount"] = TagFieldKind.DiscCount,
            ["year"] = TagFieldKind.Year,
            ["genre"] = TagFieldKind.Genre,
            ["subgenre"] = TagFieldKind.Subgenre,
            ["comment"] = TagFieldKind.Comment,
            ["composer"] = TagFieldKind.Composers,
            ["conductor"] = TagFieldKind.Conductor,
            ["grouping"] = TagFieldKind.Grouping,
            ["publisher"] = TagFieldKind.Publisher,
            ["work"] = TagFieldKind.Work,
            ["movement"] = TagFieldKind.MovementName,
        };

    /// <summary>What a row currently holds for a token, or null when no such token exists. The value is
    /// the wanted side, which is what the grid shows, because a pattern is written while the user looks
    /// at the grid.</summary>
    public static string? TokenValue(TrackRow row, string token) =>
        Tokens.TryGetValue(token, out var kind) ? Get(kind).Get(row) : null;

    private static readonly Dictionary<TagFieldKind, TagField> ByKind = All.ToDictionary(f => f.Kind);

    public static TagField Get(TagFieldKind kind) => ByKind[kind];

    /// <summary>The fields of these scopes, in catalogue order.</summary>
    public static IEnumerable<TagField> InScope(params TagScope[] scopes) =>
        All.Where(f => scopes.Contains(f.Scope));

    /// <summary>Everything that a release's own fields cannot be: the per-file facts. This is what a
    /// window that edits a set of tracks from all over the library may safely offer.</summary>
    public static IEnumerable<TagField> TrackOnly() => InScope(TagScope.Track);

    /// <summary>What a record is: its own fields and the fields of its discs. This is what the album
    /// editor edits. A selection inside one folder may also be offered the track fields on top of
    /// these.</summary>
    public static IEnumerable<TagField> AlbumAndDisc() => InScope(TagScope.Album, TagScope.Disc);
}
