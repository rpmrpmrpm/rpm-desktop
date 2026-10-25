using System.Text.Json;
using System.Text.Json.Serialization;
using RPM.Core.Cloud;
using RPM.Core.Analysis;
using RPM.Core.Import;
using RPM.Core.Sharing;

namespace RPM.Desktop.Services.Shell;

/// <summary>Which of the library's three views the panel is showing.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LibraryViewMode>))]
public enum LibraryViewMode
{
    /// <summary>The wall of covers, with an inline flip-down track list.</summary>
    Wall = 0,

    /// <summary>The album list: a column of records under their artist, each with its cover beside its
    /// tracks. This is the browsing view.</summary>
    List = 1,

    /// <summary>The data table: one row per track, every field the mirror holds, sortable and filterable.
    /// This is the view for checking a collection's metadata, not for listening.</summary>
    Table = 2,
}

/// <summary>Which of the playlist pane's two views its tree is read in.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PlaylistViewMode>))]
public enum PlaylistViewMode
{
    /// <summary>The tree: one row per folder and playlist. Folders open in place under their own line, and
    /// everything is shown at once.</summary>
    List = 0,

    /// <summary>Covers in a grid, one folder at a time. A folder is opened and then stepped back out of,
    /// rather than expanded. This is how a file browser shows the same tree.</summary>
    Icons = 1,
}

/// <summary>What the album list gathers its records under.</summary>
/// <remarks>Persisted BY NAME, the same rule <see cref="AlbumOrdering"/> keeps. If it were stored as a
/// number, a grouping inserted here would silently point everybody's saved choice at its
/// neighbour.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter<AlbumGrouping>))]
public enum AlbumGrouping
{
    /// <summary>Nothing. The records form one run in artist order, with no headings over them. This is the
    /// default. The collection is shown as one run until the listener asks for it to be divided.</summary>
    None,

    /// <summary>By whose record it is: a shelf per artist.</summary>
    Artist,

    /// <summary>By what kind of record it is. A record whose tracks do not agree on one, or that has none
    /// tagged at all, is gathered under "Ungrouped". It is not given a genre that it may not
    /// have.</summary>
    Genre,

    /// <summary>By when it came out, newest year first. Records with no year are gathered at the
    /// end.</summary>
    Year,
}

/// <summary>What the All tracks listing on an artist's page groups its songs under, once it holds more than
/// 20.</summary>
/// <remarks>Persisted by name, like <see cref="AlbumGrouping"/>.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter<ArtistTrackGrouping>))]
public enum ArtistTrackGrouping
{
    /// <summary>By the first letter of the title, A to Z, then # for the rest. This is the
    /// default.</summary>
    Letter,

    /// <summary>By the track's year, newest first. Songs with no year are gathered at the end.</summary>
    Year,
}

/// <summary>How a multi-disc release (both CD rips and vinyl) is treated once it is identified.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MultiDiscMode>))]
public enum MultiDiscMode
{
    /// <summary>The default. All discs become one album with continuing track numbers, as the lookup window
    /// returns them from MusicBrainz or Discogs. Each track still carries its disc number.</summary>
    MergedAlbum = 0,

    /// <summary>Each disc becomes its own album. It is tagged "disc N of M" and named from the lookup for
    /// that disc alone.</summary>
    SeparateAlbums = 1,
}

/// <summary>What an import does with the files that it files into the collection.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ImportAction>))]
public enum ImportAction
{
    /// <summary>The file leaves the source folder and lands in the collection. So there is one copy of the
    /// record, and it is the one that the library knows about.</summary>
    Move = 0,

    /// <summary>The default. The source keeps its files, and the collection gets a copy. This is also what
    /// a read-only or borrowed source (a burned disc, somebody else's stick) needs. A settings file that
    /// names its own value keeps that value.</summary>
    Copy = 1,
}

/// <summary>
/// Local app settings (settings.json next to auth.json). Loaded once at startup, and saved on change.
/// </summary>
public sealed partial class SettingsService
{
    private sealed record Model(
        string? LibraryPath,
        CloudSyncMode CloudMode = CloudSyncMode.None,
        bool AlbumGridView = false,
        LibraryViewMode? LibraryView = null,
        PlaylistViewMode PlaylistView = PlaylistViewMode.List,
        double AlbumTileSize = 150,
        double PlaylistTileSize = 150,
        bool LockedListsHidden = false,
        AlbumOrdering AlbumOrdering = AlbumOrdering.Artist,
        AlbumOrdering PegasusAlbumOrdering = AlbumOrdering.Artist,
        bool PegasusMixtapeAfter = true,
        bool PegasusAlbumCarousel = false,
        AlbumGrouping AlbumGrouping = AlbumGrouping.None,
        ArtistTrackGrouping ArtistTrackGrouping = ArtistTrackGrouping.Letter,
        string? CustomBackgroundPath = null,
        string? BuiltInBackground = null,
        IReadOnlyList<NamingTemplate>? CustomTemplates = null,
        string? DefaultTemplateName = null,
        MultiDiscMode MultiDiscMode = MultiDiscMode.MergedAlbum,
        ImportAction ImportAction = ImportAction.Copy,
        string? FanartApiKey = null,
        bool FingerprintLibrary = true,
        bool ShareListening = true,
        bool ArtDrawerParked = false,
        bool HeaderRecord = true,
        bool HeaderArtistPhoto = true,
        double ImportPaneWidth = 0,
        bool? AlbumSimilarOpen = null,
        bool? AlbumGrooveOpen = null,
        bool ImportTagPanelOpen = false,
        string? ImportHiddenColumns = null,
        bool? ImportFilterStripOpen = null,
        string? ImportRequiredTags = null,
        bool ImportFillFromCatalogue = true,
        string? AudioDevice = null,
        IReadOnlyList<EqProfile>? DeviceEq = null,
        IReadOnlyList<OutputVolume>? DeviceVolumes = null,
        IReadOnlyList<CastDeviceProfile>? CastDevices = null,
        bool CastScreenOn = true,
        RPM.Core.Audio.CastAudioFormat CastFormat = RPM.Core.Audio.CastAudioFormat.Mp3,
        NormalizeMode NormalizeMode = NormalizeMode.Album,
        bool NormalizeClassical = false,
        bool MaskProfanity = false,
        bool AlignWords = false,
        // Pegasus's player: the layout it shows, and the lyric key's choice.
        string? PegasusLayout = null,
        bool PegasusLyrics = false,
        // ...and the animation its room draws.
        string? PegasusRoomStyle = null,
        // ...and the minutes without a key before its screensaver.
        int PegasusScreensaverMinutes = RPM.Core.Viz.Screensaver.DefaultMinutes,
        // ...and where its mini player stands over the collection.
        string? PegasusMiniPlayer = null,
        // ...and whether a screen it opens says what the remote's keys do there.
        bool PegasusKeyLegend = true,
        // ...and how bright and saturated it is drawn.
        string? PegasusPicture = null,
        // ...and what its collection's screens are drawn on.
        string? PegasusPageBackground = null,
        bool WriteAnalysisTags = true,
        bool SyncAllPausedByFull = false,
        DateTime? CloudFullNoticeUtc = null,
        int CloudFullNoticeCount = 0,
        bool CloudFullNoticeSuppressed = false,
        bool ShareLibrary = true,
        string? ShareName = null,
        string? ProfileName = null,
        long SupporterUntilUnix = 0,
        int SupporterPretend = 0,
        string? UpdateDeferred = null,
        IReadOnlyList<string>? CoachmarksSeen = null,
        RPM.Core.Crashes.CrashConsent CrashConsent = RPM.Core.Crashes.CrashConsent.NotAsked,
        string? CrashInstallId = null);

    private Model _model = new(null);

    /// <summary>False when the settings file is there but could not be read at startup. In that case the
    /// session runs on defaults and writes nothing, so the user's file is still there on the next launch.
    /// See RPM.Core.Files.StoreFile.</summary>
    private bool _mayWrite = true;

    private static string StorePath => Path.Combine(LocalData.Dir, "settings.json");

    /// <summary>Raised when a setting changes, so that the sync engine can react to being switched
    /// on.</summary>
    public event Action? Changed;

    /// <summary>The central music folder (the rpm library root). Null until first-run setup.</summary>
    public string? LibraryPath
    {
        get => _model.LibraryPath;
        set => Update(_model with { LibraryPath = value });
    }

    /// <summary>
    /// Whether this computer offers its library to the computers it is paired with, and looks for theirs.
    ///
    /// <para>On by default. It costs nothing until somebody is paired: a computer with no pairings says who
    /// it is on the network, and answers nothing else.</para>
    /// </summary>
    public bool ShareLibrary
    {
        get => _model.ShareLibrary;
        set => Update(_model with { ShareLibrary = value });
    }

    /// <summary>Whether crash reports are sent. The app asks once, at the first crash. The switch on the
    /// Profile page of Settings changes it later. It is stored by name. It is saved quietly: nothing reacts
    /// to it through
    /// <see cref="Changed"/>, and every subscriber would otherwise run on each answer.</summary>
    public RPM.Core.Crashes.CrashConsent CrashConsent
    {
        get => _model.CrashConsent;
        set => Update(_model with { CrashConsent = value }, notify: false);
    }

    /// <summary>A random id for this installation. It is made the first time a crash report is sent, and it
    /// is used only to count how many machines hit the same crash.</summary>
    public string EnsureCrashInstallId()
    {
        if (_model.CrashInstallId is { Length: > 0 } id)
            return id;
        var made = Guid.NewGuid().ToString("N");
        Update(_model with { CrashInstallId = made }, notify: false);
        return made;
    }

    /// <summary>What this computer is called on somebody else's screen. Empty means the machine's own name,
    /// which is what it is called everywhere else on the network.</summary>
    public string ShareName
    {
        get => PeerLabel.Announce(_model.ShareName, _model.ProfileName, Platform.ComputerName.Mine).Name;
        set => Update(_model with { ShareName = string.IsNullOrWhiteSpace(value) ? null : value.Trim() });
    }

    /// <summary>What the user actually typed into "This computer is called", or null when they have left it
    /// alone. This is separate from <see cref="ShareName"/>, because the box on screen has to be able to
    /// LOOK empty. Pre-filling it with the resolved name would make "leave this empty" an instruction that
    /// nobody can follow. It would also make a typed name and a fallback impossible to tell
    /// apart.</summary>
    public string? ShareNameTyped => _model.ShareName;

    /// <summary>
    /// The display name that goes out beside <see cref="ShareName"/>, or empty.
    ///
    /// <para>Nobody edits this setting here. It is <see cref="ProfileName"/> put through the one rule that
    /// decides whether it travels at all, which is <see cref="PeerLabel.Announce"/>. Read this rather than
    /// the profile name directly. A user who has named this computer has said what the neighbours see, and
    /// that answer is made there.</para>
    /// </summary>
    public string ShareFace =>
        PeerLabel.Announce(_model.ShareName, _model.ProfileName, Platform.ComputerName.Mine).Face;

    /// <summary>
    /// The display name on the signed-in account, as it was last read, or null.
    ///
    /// <para>
    /// This is a copy of somebody else's fact, kept here for one reason. The beacon needs a name the
    /// instant the app starts, on a machine with no route to the internet. The profile is fetched through
    /// <c>GetProfileAsync</c>. Two laptops on a home network with the line down is exactly the case that
    /// sharing was built for. So a name that can only be fetched is a name that is missing whenever it is
    /// most wanted. It is written whenever the profile is read or changed, and it is cleared on sign-out.
    /// The next person to sign in on this machine must not inherit the last person's name.
    /// </para>
    /// </summary>
    public string? ProfileName
    {
        get => _model.ProfileName;
        set
        {
            var next = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (string.Equals(next, _model.ProfileName, StringComparison.Ordinal))
                return;

            Update(_model with { ProfileName = next });
        }
    }

    /// <summary>Whether the cloud holds everything, a chosen subset, or nothing.</summary>
    public CloudSyncMode CloudMode
    {
        get => _model.CloudMode;
        set => Update(_model with { CloudMode = value });
    }

    /// <summary>
    /// Whether "all my music" was the standing instruction when the cloud filled up.
    ///
    /// <para>When the store runs out, the mode drops to the user's own selection (see
    /// <see cref="RPM.Core.Cloud.CloudFullPolicy"/>). This value is how the app remembers that the drop was
    /// its own doing and not the user's. So when more room is bought later, the app puts back what the user
    /// actually asked for. It is cleared as soon as the user picks a mode themselves. From then on, the
    /// choice on screen is theirs, and nothing may overrule it.</para>
    ///
    /// <para>This is saved quietly. Nothing reacts to it on its own. The mode change beside it is the
    /// event.</para>
    /// </summary>
    public bool SyncAllPausedByFull
    {
        get => _model.SyncAllPausedByFull;
        set => Update(_model with { SyncAllPausedByFull = value }, notify: false);
    }

    /// <summary>
    /// When the "your cloud is full" window was last shown, in UTC. Null means never.
    ///
    /// <para>The window is shown once a week (see <see cref="RPM.Core.Cloud.CloudFullNotice"/>). The state
    /// it reports lasts until the user does something about it. A sync pass happens on every library
    /// change. So without a stamp, the app would show the same window all day.</para>
    /// </summary>
    public DateTime? CloudFullNoticeUtc
    {
        get => _model.CloudFullNoticeUtc;
        set => Update(_model with { CloudFullNoticeUtc = value }, notify: false);
    }

    /// <summary>How many times that window has been shown. The opt-out box depends on this (see
    /// <see cref="RPM.Core.Cloud.CloudFullNotice.MayOptOut"/>).</summary>
    public int CloudFullNoticeCount
    {
        get => _model.CloudFullNoticeCount;
        set => Update(_model with { CloudFullNoticeCount = value }, notify: false);
    }

    /// <summary>The user ticked "never show this again". This is final. It is set only when the user asks
    /// for it. Nothing else in the app sets it.</summary>
    public bool CloudFullNoticeSuppressed
    {
        get => _model.CloudFullNoticeSuppressed;
        set => Update(_model with { CloudFullNoticeSuppressed = value }, notify: false);
    }

    /// <summary>The version of the app that the user was offered and chose Later for, or null.
    ///
    /// <para>This is a version, not a flag. Later is an answer about one release, and the next release asks
    /// again. See <see cref="RPM.Desktop.Services.Updates.UpdateAsk"/>. The update that the user put off
    /// still installs when they quit, which is what the window says it will do.</para></summary>
    public string? UpdateDeferred
    {
        get => _model.UpdateDeferred;
        set => Update(_model with { UpdateDeferred = value }, notify: false);
    }

    /// <summary>
    /// Which view the library panel is in: the wall, the album list, or the data table. It is one view of
    /// one collection, so it is remembered across runs like any other panel choice.
    ///
    /// <para>A null in the stored model means the settings file was written before the third view existed.
    /// That file holds the older two-way <c>AlbumGridView</c> flag instead, so the wall's users keep the
    /// wall. Everyone else lands on the album list. The album list replaced the folder tree-grid for
    /// browsing. The data table is now the metadata view, not the browsing view.</para>
    /// </summary>
    public LibraryViewMode LibraryView
    {
        get => _model.LibraryView ?? (_model.AlbumGridView ? LibraryViewMode.Wall : LibraryViewMode.List);
        set => Update(_model with { LibraryView = value, AlbumGridView = value == LibraryViewMode.Wall });
    }

    /// <summary>
    /// Which view the playlist pane is in: the tree, or the grid of covers. It is one view of one set of
    /// playlists, so it is remembered across runs, like the library's own view choice.
    ///
    /// <para>This is saved quietly. The pane has already changed its own contents by the time this is
    /// written, and nothing else in the app reads it. The shell is the one thing that reacts to a global
    /// Changed. It re-applies its backdrop on every Changed, which flashes the window for a switch that is
    /// none of its business.</para>
    /// </summary>
    public PlaylistViewMode PlaylistView
    {
        get => _model.PlaylistView;
        set => Update(_model with { PlaylistView = value }, notify: false);
    }

    /// <summary>The album wall's cover size, in pixels for the edge of the art square. The header slider
    /// sets this target, and the wall snaps to the nearest value that fills its rows. It is remembered
    /// across runs, like the view choice. The default is the size that the wall starts at.</summary>
    public double AlbumTileSize
    {
        get => _model.AlbumTileSize;
        // Saved quietly. The slider already pushes the size straight to the wall. A global Changed would
        // run every subscriber again. The shell re-applies its backdrop on Changed, which reloads the
        // full-screen background image and flashes the whole window on each save.
        set => Update(_model with { AlbumTileSize = value }, notify: false);
    }

    /// <summary>The playlists grid's cover size. It is the same kind of number as the album wall's, for the
    /// other grid of squares. The header slider sets the target edge, and the grid snaps to the nearest
    /// value that fills its rows. It has its own setting, not the album wall's. The two grids are the same
    /// kind of thing, but they are not read at the same time. A reader who wants large sleeves and small
    /// playlist tiles is asking for two things, not contradicting themselves. It is saved quietly, for the
    /// reason given at <see cref="AlbumTileSize"/>.</summary>
    public double PlaylistTileSize
    {
        get => _model.PlaylistTileSize;
        set => Update(_model with { PlaylistTileSize = value }, notify: false);
    }

    /// <summary>The user closed the "Playlists you can unlock" card under the playlists tree. This is set
    /// only by the card's own close button. It is saved quietly, for the reason given at <see
    /// cref="AlbumTileSize"/>.</summary>
    public bool LockedListsHidden
    {
        get => _model.LockedListsHidden;
        set => Update(_model with { LockedListsHidden = value }, notify: false);
    }

    /// <summary>
    /// How wide the left pane is while it shows Import, in pixels. Import is the one view that is worked
    /// IN, rather than read from. It is a queue of tracks in columns, which are ticked and edited. So it is
    /// the one view with no width ceiling, and it has a width of its own. The user drags it to whatever the
    /// job needs, and the width is remembered across runs.
    ///
    /// <para>Zero means it was never dragged. The pane then opens Import at the width that every other view
    /// is capped at. So the setting starts where the user left off, not somewhere new.</para>
    /// </summary>
    public double ImportPaneWidth
    {
        get => _model.ImportPaneWidth;
        // Saved quietly, like the cover size. The width is already on screen, because the drag wrote it. A
        // global Changed would make the shell reload its backdrop on every pointer move of the splitter.
        set => Update(_model with { ImportPaneWidth = value }, notify: false);
    }

    /// <summary>
    /// Whether the album page's row of suggestions is open. Null means nobody has given an answer yet. The
    /// page then decides for itself. The row is open on a card with the height to hold it, and shut on one
    /// without. Once a reader has opened or shut it, that answer is used on every record and at every
    /// window size. That includes sizes where keeping it open costs the sleeve some of its size.</summary>
    public bool? AlbumSimilarOpen
    {
        get => _model.AlbumSimilarOpen;
        set => Update(_model with { AlbumSimilarOpen = value }, notify: false);
    }

    /// <summary>
    /// Whether the album page's groove is open. Null means nobody has given an answer yet. The page then
    /// decides for itself, based on how much of the screen the window is taking. See
    /// <see cref="Presentation.Main.Library.AlbumPageDensity.GrooveOpensItself"/>. Once a reader has opened
    /// or shut it, that answer is used on every record. This is the same rule that <see
    /// cref="AlbumSimilarOpen"/> keeps, for the same reason: a fold that the reader made is a preference,
    /// not a reaction to one card.
    ///
    /// <para>It is not read at every window size, and that is the one difference from the suggestions row.
    /// A window that takes two thirds of the screen or more has no fold at all, so the value is kept but
    /// not asked. It is decided again as soon as the window gives the screen back.</para></summary>
    public bool? AlbumGrooveOpen
    {
        get => _model.AlbumGrooveOpen;
        set => Update(_model with { AlbumGrooveOpen = value }, notify: false);
    }

    /// <summary>Whether the import tag panel is open. It is closed until somebody opens it, and the choice
    /// is remembered between sessions. It is saved quietly, for the same reason as the pane's own width.
    /// Nothing else in the app reacts to it, and the panel is already on screen by the time this is
    /// written.</summary>
    public bool ImportTagPanelOpen
    {
        get => _model.ImportTagPanelOpen;
        set => Update(_model with { ImportTagPanelOpen = value }, notify: false);
    }

    /// <summary>Which of the import grid's optional columns are hidden, by their stable ids, comma
    /// separated. Hidden columns are stored rather than shown ones. That way, a column added in a later
    /// version arrives switched on, instead of missing from everybody's saved list.</summary>
    public string? ImportHiddenColumns
    {
        get => _model.ImportHiddenColumns;
        set => Update(_model with { ImportHiddenColumns = value }, notify: false);
    }

    /// <summary>Whether the import filter strip is on screen. Null means nobody has given an answer. The
    /// strip then decides for itself. It is shown exactly while the queue holds something worth
    /// narrowing.</summary>
    public bool? ImportFilterStripOpen
    {
        get => _model.ImportFilterStripOpen;
        set => Update(_model with { ImportFilterStripOpen = value }, notify: false);
    }

    /// <summary>
    /// Which tags a queued file must have before the bench stops marking it. These are the names of
    /// <see cref="RPM.Core.Library.QueueTag"/> values, comma separated. Null means nobody has chosen, which
    /// means all of them.
    ///
    /// <para>The names are stored rather than numbers. That keeps the file readable. A tag added to the
    /// enum later cannot silently change what an old setting meant.</para>
    /// </summary>
    public IReadOnlyList<RPM.Core.Library.QueueTag> ImportRequiredTags
    {
        get
        {
            if (_model.ImportRequiredTags is not { Length: > 0 } stored)
                return RPM.Core.Library.QueueTags.Default;

            var tags = stored
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => Enum.TryParse<RPM.Core.Library.QueueTag>(name, out var tag) ? tag : (RPM.Core.Library.QueueTag?)null)
                .Where(tag => tag is not null)
                .Select(tag => tag!.Value)
                .ToList();

            return tags.Count > 0 ? tags : RPM.Core.Library.QueueTags.Default;
        }
        set
        {
            var names = string.Join(',', (value ?? RPM.Core.Library.QueueTags.Default).Distinct());
            Update(_model with { ImportRequiredTags = names }, notify: false);
            // The rule is read for every row and every count, both in the store and in the grid. So it is
            // set where both of them read it, not passed down two separate paths.
            RPM.Core.Library.QueueTags.Required = value ?? RPM.Core.Library.QueueTags.Default;
        }
    }

    /// <summary>
    /// Whether music added to the import queue has its missing genre, style and year filled in from the
    /// catalogue, one lookup per record, without anybody pressing Look up. See
    /// <see cref="RPM.Core.Analysis.CatalogueFill"/>.
    ///
    /// <para>On unless somebody turns it off. The genre pages, the mood lists and the radio cannot see a record
    /// whose genre is blank or not one of the app's own, and the people whose files are like that are exactly
    /// the people who never open a tag editor. A switch they would have to find first would help nobody. It only
    /// ever fills blanks, on the queue's proposal, so the user sees every value before it reaches a file.</para>
    ///
    /// <para>Saved quietly. The pass reads it before each record, so a change takes effect at the next one, and
    /// nothing else in the app reacts to it.</para>
    /// </summary>
    public bool ImportFillFromCatalogue
    {
        get => _model.ImportFillFromCatalogue;
        set => Update(_model with { ImportFillFromCatalogue = value }, notify: false);
    }

    /// <summary>The order the album wall is in. See <see cref="AlbumOrdering"/>. It is saved quietly, like
    /// the cover size. The wall is re-sorted directly by the picker. A global Changed would make the shell
    /// reload its backdrop for a menu click.</summary>
    public AlbumOrdering AlbumOrdering
    {
        get => _model.AlbumOrdering;
        set => Update(_model with { AlbumOrdering = value }, notify: false);
    }

    /// <summary>The order Pegasus's album wall is in, kept apart from the window's wall: the two offer
    /// different orders (Pegasus has <see cref="AlbumOrdering.Date"/>, the window has
    /// <see cref="AlbumOrdering.Year"/>), as the Android TV app keeps its own. Saved quietly, for the same
    /// reason as the window's.</summary>
    public AlbumOrdering PegasusAlbumOrdering
    {
        get => _model.PegasusAlbumOrdering;
        set => Update(_model with { PegasusAlbumOrdering = value }, notify: false);
    }

    /// <summary>Whether an album, a playlist or a single that ends in Pegasus goes on as a mixtape of songs like it,
    /// as the Android TV app's Player setting chooses (on until changed). Only Pegasus reads it, while it is shown
    /// (see RPaMpModel.MixtapeAfter), so the main window's queue still stops at its end. Saved quietly.</summary>
    public bool PegasusMixtapeAfter
    {
        get => _model.PegasusMixtapeAfter;
        set => Update(_model with { PegasusMixtapeAfter = value }, notify: false);
    }

    /// <summary>Whether Pegasus's album wall is shown as a carousel rather than a grid, as the Android TV
    /// app's Album view setting chooses (Grid until changed). Saved quietly: only Pegasus reads it, when its
    /// wall comes up.</summary>
    public bool PegasusAlbumCarousel
    {
        get => _model.PegasusAlbumCarousel;
        set => Update(_model with { PegasusAlbumCarousel = value }, notify: false);
    }

    /// <summary>What the album list gathers its records under. See <see cref="AlbumGrouping"/>. It is saved
    /// quietly, for the same reason as the wall's ordering. The list regroups itself from the picker. A
    /// global Changed would make the shell reload its backdrop for a menu click.</summary>
    public AlbumGrouping AlbumGrouping
    {
        get => _model.AlbumGrouping;
        set => Update(_model with { AlbumGrouping = value }, notify: false);
    }

    /// <summary>What the All tracks listing on an artist's page groups its songs under. It is saved
    /// quietly, like
    /// <see cref="AlbumGrouping"/>. The page regroups itself from the switch.</summary>
    public ArtistTrackGrouping ArtistTrackGrouping
    {
        get => _model.ArtistTrackGrouping;
        set => Update(_model with { ArtistTrackGrouping = value }, notify: false);
    }

    /// <summary>
    /// Which first-run hints the listener has already seen. The keys come from <see
    /// cref="RPM.Core.Library.CoachTour"/>.
    ///
    /// <para><b>Seen, not dismissed.</b> A hint is added here whether it was closed with its own button or
    /// answered by using the control it points at. Both mean the same thing: this person knows about that
    /// control. Like the other quiet settings, it is kept out of the app's own Changed notification. A hint
    /// being retired is not a reason to repaint the backdrop.</para>
    /// </summary>
    public IReadOnlyList<string> CoachmarksSeen => _model.CoachmarksSeen ?? [];

    /// <summary>Retires a hint. This does nothing when the hint is already retired. So a caller can say
    /// this every time the thing it is about appears, without tracking whether it has already been
    /// said.</summary>
    public void MarkCoachmarkSeen(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || CoachmarksSeen.Contains(key, StringComparer.Ordinal))
            return;

        Update(_model with { CoachmarksSeen = [.. CoachmarksSeen, key] }, notify: false);
    }

    /// <summary>Puts every hint back. This is used by the Settings screen's own button. It is also how
    /// anybody working on the hints sees them again, without a fresh install.</summary>
    public void ResetCoachmarks() => Update(_model with { CoachmarksSeen = null }, notify: false);

    /// <summary>The listener parked the art drawer themselves (a double-click on the grip), and has not
    /// asked for it back. The mini player is their standing choice, so it survives a restart. The drawer
    /// opens parked, and a track starting does not pull it open. It is saved quietly, like the tile size.
    /// The drawer is already where the user put it, and the global Changed would repaint the
    /// backdrop.</summary>
    public bool ArtDrawerParked
    {
        get => _model.ArtDrawerParked;
        set => Update(_model with { ArtDrawerParked = value }, notify: false);
    }

    /// <summary>
    /// Whether the turning record is shown on the header at all.
    ///
    /// <para>Off does not mean "stop it turning". The disc is removed from the band entirely, and the band
    /// is laid out as if it had never been there. The photograph runs to the right edge, and the end of the
    /// seek bar goes with it. A header that kept room for a disc nobody can see would leave a hole in the
    /// middle of the app. See
    /// <c>ArtDisplay.ShowRecord</c>, which is the one entry point for this.</para>
    /// </summary>
    public bool HeaderRecord
    {
        get => _model.HeaderRecord;
        set => Update(_model with { HeaderRecord = value });
    }

    /// <summary>
    /// Whether the header shows a photograph of the artist who is playing.
    ///
    /// <para>When off, nothing is fetched, ranked or downloaded for it. The header uses the record's own
    /// colours. That is what it does for an artist with no picture in any catalogue. The cover, the words
    /// and the record are not affected.</para>
    /// </summary>
    public bool HeaderArtistPhoto
    {
        get => _model.HeaderArtistPhoto;
        set => Update(_model with { HeaderArtistPhoto = value });
    }

    /// <summary>
    /// Whether the visualizer places lyric words by listening to the audio, rather than sharing the line
    /// out evenly across them.
    ///
    /// <para>Off by default, and that is about the machine, not about taste. Placing words by listening
    /// means a model reads the record while it plays (see <see cref="LyricAlignment"/>). On a machine that
    /// cannot keep up, it stops on its own after one line. But it should not be something every listener
    /// pays for without asking. What it replaces is not nothing. The words are spread across the line by
    /// how long each one is. That is right most of the time, and it drifts on a held note.</para>
    ///
    /// <para>There is one switch for the whole app, not one per screen. A listener who wants this wants it
    /// everywhere, and anything else that follows a lyric reads the same answer. Only the visualizer offers
    /// it today.</para>
    /// </summary>
    public bool AlignWords
    {
        get => _model.AlignWords;
        set => Update(_model with { AlignWords = value }, notify: false);
    }

    /// <summary>
    /// Whether the lyric sheet masks ordinary swearing and named drugs as well as slurs. The key in the
    /// corner of the lyric view toggles it. The choice is remembered, because how somebody wants words
    /// shown is a standing preference, not a decision for each song.
    ///
    /// <para>Swearing and drugs share one switch. Both answer one question: does the listener want the
    /// lyric softened? Asking it twice would draw a distinction that almost nobody draws.</para>
    ///
    /// <para><b>Slurs are not governed by this, and they are always masked.</b> See <see
    /// cref="RPM.Core.Lyrics.LyricCensor"/>. Slurs are masked from the first run, without asking. This
    /// switch covers the wider list, and it applies to every record that is played. The track's own
    /// advisory flag has no say in it. A listener who would rather not read swearing most wants that on the
    /// explicit pressing. If the flag decided, the setting would do nothing on the very records it is
    /// for.</para>
    ///
    /// <para>It is OFF by default. This is a considered choice, not caution. When the flag does not decide,
    /// "on" would mark every explicit record's words, including "hell", "damn" and every drug named in
    /// them, for a listener who has never asked for anything. Turning a filter on for somebody who did not
    /// ask has no upside. The words are the ones the artist wrote and the speakers are playing. A lyric
    /// sheet full of asterisks that nobody wanted looks like the app is broken. A listener who does want it
    /// can say so once, and the app remembers.</para>
    ///
    /// <para>This is saved quietly, like the parked drawer. Nothing in the app reacts to it except the
    /// lyric sheet itself. The sheet reads the setting again on the press that changed it.</para>
    /// </summary>
    public bool MaskProfanity
    {
        get => _model.MaskProfanity;
        set => Update(_model with { MaskProfanity = value }, notify: false);
    }

    // ── Pegasus's player ──

    /// <summary>
    /// The layout Pegasus's player shows: "Spread" (Full) or "Centred" (Compact), by the name of the layout.
    /// Null until the layout key or the settings panel has been used. Any other name, "Room" among them,
    /// opens on the full layout (see PegasusPlayer.ReadStoredLayout).
    ///
    /// <para>It is kept so that the player opens as it was left, after a restart as well as after a visit
    /// to the library. A layout is a way of watching that the listener chose once. It is stored by name, so
    /// the settings file does not depend on the order of a type in the player.</para>
    ///
    /// <para>This is saved quietly. Nothing else in the app reacts to it.</para>
    /// </summary>
    public string? PegasusLayout
    {
        get => _model.PegasusLayout;
        set => Update(_model with { PegasusLayout = value }, notify: false);
    }

    /// <summary>
    /// Whether the listener has asked Pegasus's player for the lyrics with the lyric key, and not asked to
    /// stop. Where a record has a synced sheet, the player then shows it. It is kept for the same reason as
    /// <see cref="PegasusLayout"/>, and saved quietly too.
    /// </summary>
    public bool PegasusLyrics
    {
        get => _model.PegasusLyrics;
        set => Update(_model with { PegasusLyrics = value }, notify: false);
    }

    /// <summary>
    /// The animation Pegasus's room draws behind the player and the collection: "Original", "Yang", "Lava" or
    /// "Alchemy", by the name of the <see cref="RPM.Core.Viz.RoomStyle"/>. Null until the settings panel's Room
    /// animation row has been used. A name this build does not know draws the original (see
    /// <see cref="RPM.Core.Viz.RoomStyles.Named"/>). Stored by name for the same reason as
    /// <see cref="PegasusLayout"/>, and saved quietly too.
    /// </summary>
    public string? PegasusRoomStyle
    {
        get => _model.PegasusRoomStyle;
        set => Update(_model with { PegasusRoomStyle = value }, notify: false);
    }

    /// <summary>
    /// After how many minutes without a key or a pointer move Pegasus puts its screens away and only the
    /// room shows: one of <see cref="RPM.Core.Viz.Screensaver.Choices"/>, 0 for off, 2 until the settings
    /// panel's Screensaver row has been used. A figure that is not a choice is read as the default (see
    /// <see cref="RPM.Core.Viz.Screensaver.Minutes"/>). Saved quietly, as <see cref="PegasusLayout"/> is.
    /// </summary>
    public int PegasusScreensaverMinutes
    {
        get => _model.PegasusScreensaverMinutes;
        set => Update(_model with { PegasusScreensaverMinutes = value }, notify: false);
    }

    /// <summary>
    /// Where Pegasus's mini player stands over the collection: "Top", "Bottom", "BottomLeft", "BottomRight" or
    /// "Off", by the name of the Pegasus MiniPlayerPlace, as the Android TV app's Mini player setting chooses.
    /// Null until the settings panel's Mini player row has been used, which is the top. A name this build does
    /// not know is the top too. Stored by name for the same reason as <see cref="PegasusLayout"/>, and saved
    /// quietly: only Pegasus reads it.
    /// </summary>
    public string? PegasusMiniPlayer
    {
        get => _model.PegasusMiniPlayer;
        set => Update(_model with { PegasusMiniPlayer = value }, notify: false);
    }

    /// <summary>Whether a screen Pegasus opens shows for five seconds what the remote's keys do on it, as the
    /// Android TV app's Remote key hints setting chooses (on until changed). Saved quietly: only Pegasus reads
    /// it, as a screen opens.</summary>
    public bool PegasusKeyLegend
    {
        get => _model.PegasusKeyLegend;
        set => Update(_model with { PegasusKeyLegend = value }, notify: false);
    }

    /// <summary>
    /// How bright and saturated Pegasus is drawn: "Standard", "Softer" or "Muted", by the name of the
    /// <see cref="RPM.Desktop.Presentation.Pegasus.PictureLook"/>. Null until the settings panel's Picture row has
    /// been used. A name this build does not know draws Standard. Stored by name for the same reason as
    /// <see cref="PegasusLayout"/>, and saved quietly too.
    /// </summary>
    public string? PegasusPicture
    {
        get => _model.PegasusPicture;
        set => Update(_model with { PegasusPicture = value }, notify: false);
    }

    /// <summary>
    /// What Pegasus's collection is drawn on: "Default", "Pegasus", "Dark" or "Room" (the moving room), by the name
    /// of the <see cref="RPM.Desktop.Presentation.Pegasus.PageBackground"/>. Null until the settings panel's
    /// Background row has been used, which draws Default, as on the TV; so does a name this build does not know.
    /// Stored by name for the same reason as <see cref="PegasusLayout"/>, and saved quietly too.
    /// </summary>
    public string? PegasusPageBackground
    {
        get => _model.PegasusPageBackground;
        set => Update(_model with { PegasusPageBackground = value }, notify: false);
    }

    /// <summary>
    /// When the server last said this account's support runs out, as a Unix time in seconds. 0 means "not a
    /// supporter". A machine that has never been told also reads as 0.
    ///
    /// <para>This is kept so that the features that rpm enforces itself (the DJ and the word alignment)
    /// keep working while offline. Without it, a supporter on a train would be refused the very thing they
    /// pay for, because
    /// <see cref="Account.SupporterService"/> can read the standing only when it can reach the accounts
    /// service. This value is written only from an answer that the server actually gave. A failed read
    /// leaves the last value in place. The date is what ends it.</para>
    ///
    /// <para>This is saved quietly. Nothing on screen is laid out from it. A repaint on every sign-in would
    /// rebuild the backdrop for a number that nobody is looking at.</para>
    /// </summary>
    public long SupporterUntilUnix
    {
        get => _model.SupporterUntilUnix;
        set => Update(_model with { SupporterUntilUnix = value }, notify: false);
    }

    /// <summary>
    /// A standing to act on INSTEAD of the account's own, for testing both sides of the supporter gate
    /// without buying or cancelling. 0 uses the real account. 1 pretends to be a supporter. 2 pretends not
    /// to be.
    ///
    /// <para>This is read only in a debug build (see <see cref="Account.SupporterService.Pretend"/>). It is
    /// stored, not held for the session. The thing that is usually being tested is what the app does on
    /// startup: which sheet the lyric view opens with, or whether a set starts. A switch that resets on
    /// every launch cannot test any of that.</para>
    /// </summary>
    public int SupporterPretend
    {
        get => _model.SupporterPretend;
        set => Update(_model with { SupporterPretend = value }, notify: false);
    }

    /// <summary>A fanart.tv personal key saved by an older build, from before the key moved to the rpm
    /// account.
    /// <c>FanartService</c> reads it, uploads it on sign-in, and then sets this to null. Nothing else sets
    /// it. The key now lives on the server (<c>profile/fanart-key</c>).</summary>
    public string? FanartApiKey
    {
        get => _model.FanartApiKey;
        set => Update(_model with { FanartApiKey = string.IsNullOrWhiteSpace(value) ? null : value.Trim() });
    }

    /// <summary>
    /// Fingerprints the library quietly in the background (Chromaprint through fpcalc), and offers the
    /// fingerprints to the recognition service, so that identification improves for everyone. On by
    /// default. It reads files and uses a little CPU, but it never changes them. The settings toggle is the
    /// off switch. This notifies, so the worker reacts to being switched on.
    /// </summary>
    public bool FingerprintLibrary
    {
        get => _model.FingerprintLibrary;
        set => Update(_model with { FingerprintLibrary = value });
    }

    /// <summary>
    /// Whether this computer tells RPM which songs get played. This lets the album page mark the song that
    /// listeners go to, on records that the catalogues cannot answer for.
    ///
    /// <para>On by default. Be exact about what is sent. It is a count per song of a record, for example
    /// "this one was finished four times". This applies only to records whose page has been opened on this
    /// computer. No times, no order and no history are sent. The server keeps only a sum, with no sender
    /// attached. What comes back is the same kind of thing, about everybody: which song of a record is the
    /// one people play. See
    /// <see cref="Catalog.PlayReportService"/>.</para>
    ///
    /// <para>Off stops the sending at once, including counts that have been added up but not yet sent.
    /// Counts that are already on the server cannot be taken back out, because nothing there says which
    /// counts came from this listener. That is the same property that makes it safe to send them.</para>
    /// </summary>
    public bool ShareListening
    {
        get => _model.ShareListening;
        set => Update(_model with { ShareListening = value }, notify: false);
    }

    /// <summary>
    /// Whether a track that has been analysed gets its measured values, the tempo and the key, written into
    /// its own tags.
    ///
    /// <para>On by default. The app works out the tempo and key for every track that the DJ uses, and it
    /// kept them to itself. So a collection that RPM had fully analysed still showed as untagged in the
    /// app's own metadata table, in a file browser, and in anything else that the records are played from.
    /// Writing them is the point of measuring them.</para>
    ///
    /// <para>It only fills blanks. A tempo or key that is already in a file was put there by somebody, and
    /// a measurement is not a reason to overrule a person. Replacing what is there is the backfill's own
    /// explicit choice, and nothing else's. See <see cref="Audio.AnalysisTagService"/>.
    /// </para>
    ///
    /// <para>This is saved quietly. The pass reads this setting when a track finishes analysing, and the
    /// settings page starts the backfill itself. Nothing has to be run again when it changes. A global
    /// Changed would make the shell reload its backdrop for a toggle.</para>
    /// </summary>
    public bool WriteAnalysisTags
    {
        get => _model.WriteAnalysisTags;
        set => Update(_model with { WriteAnalysisTags = value }, notify: false);
    }

    /// <summary>
    /// Whether one record is played at the level of the last one, and what that level is measured over. On
    /// by default, as <see cref="NormalizeMode.Album"/>. A collection is made of records, not songs. If
    /// each track were levelled on its own, every side that was made to rise and fall would be flattened.
    ///
    /// <para>This notifies, because the measuring pass reacts to being switched on. See
    /// <see cref="Audio.LoudnessService"/>.</para>
    /// </summary>
    public NormalizeMode NormalizeMode
    {
        get => _model.NormalizeMode;
        set
        {
            if (value == _model.NormalizeMode)
                return;

            // This is said out loud, on the same channel as the decision for each track. Every way that
            // this can go wrong is silent. A mode that nobody chose sounds exactly like a record mastered
            // that way. The question about any one of them is always who set it, and nothing else answers
            // that.
            RPM.Core.Diagnostics.Said.Line("vollevelling",
                $"levelling set to {value} (was {_model.NormalizeMode})");

            Update(_model with { NormalizeMode = value });
        }
    }

    /// <summary>
    /// Whether to level classical recordings too. Off by default. That is the whole point of having this
    /// setting.
    ///
    /// <para>A symphony is the case that levelling handles worst. Its quiet movement is meant to be quiet.
    /// The distance between it and the finale is part of the performance. A meter reads that distance as
    /// two records at different levels, and it tries to close it. Album mode protects a symphony whose
    /// movements share a folder. It does nothing for a recital disc with four works that were never meant
    /// to match. It does nothing for a box set split into one disc per movement.</para>
    ///
    /// <para>The records this covers are the albums that the listener has marked as classical in the album
    /// editor. Nothing works it out from the tags. So a record is excused from levelling only where
    /// somebody has said what it is.</para>
    /// </summary>
    public bool NormalizeClassical
    {
        get => _model.NormalizeClassical;
        set => Update(_model with { NormalizeClassical = value });
    }

    /// <summary>
    /// The speakers the music comes out of, by the sound driver's own name for them. Null (the default)
    /// means whichever output the machine itself is set to, and the app follows that when it changes. A
    /// name fixes the app to that one output, whatever the rest of the system does.
    ///
    /// <para>This is saved quietly, like the other view-type settings. The page that changes it tells the
    /// sink directly. A global Changed would make the shell reload its backdrop.</para>
    /// </summary>
    public string? AudioDevice
    {
        get => _model.AudioDevice;
        set => Update(_model with { AudioDevice = value }, notify: false);
    }

    /// <summary>
    /// The equaliser curve for each set of speakers that the app has been played through. See <see
    /// cref="EqService"/> for why it is per device. It is saved quietly. It is written while a slider is
    /// being dragged, so the writes are merged upstream, not here.
    /// </summary>
    public IReadOnlyList<EqProfile> DeviceEq
    {
        get => _model.DeviceEq ?? [];
        set => Update(_model with { DeviceEq = value }, notify: false);
    }

    /// <summary>
    /// How loud each set of speakers was left, by the driver's name for them.
    ///
    /// <para>It is per device, for the same reason as the curve. Headphones at thirty and desk speakers at
    /// eighty are not a preference that the listener wants to set again every time the music moves. Cast
    /// speakers are deliberately left out. A speaker in a room remembers its own volume. RPM uses that when
    /// a cast starts, instead of keeping a second opinion about it.</para>
    ///
    /// <para>It is saved quietly, and it is written after a delay while a slider is moving.</para>
    /// </summary>
    public IReadOnlyList<OutputVolume> DeviceVolumes
    {
        get => _model.DeviceVolumes ?? [];
        set => Update(_model with { DeviceVolumes = value }, notify: false);
    }

    /// <summary>
    /// What each Cast speaker reported that it can decode, the last time the app spoke to it. See
    /// <see cref="CastDeviceProfile"/> for why it is worth keeping rather than asking again.
    /// </summary>
    public IReadOnlyList<CastDeviceProfile> CastDevices
    {
        get => _model.CastDevices ?? [];
        set => Update(_model with { CastDevices = value }, notify: false);
    }

    /// <summary>
    /// Whether a television that is being cast to shows anything. On is the default, which is what a screen
    /// is for. Off is for a panel in a room where nobody sits. There, a still picture held for the length
    /// of an album can burn in permanently.
    ///
    /// <para>This is not per speaker. It is a choice about how the listener uses their room, not about what
    /// a particular device can do. A speaker with no screen has no opinion about it.</para>
    /// </summary>
    public bool CastScreenOn
    {
        get => _model.CastScreenOn;
        set => Update(_model with { CastScreenOn = value }, notify: false);
    }

    /// <summary>
    /// The format the listener chose for casting: MP3, lossless or surround. This is not per speaker. A
    /// speaker that cannot play the choice gets MP3 for that cast, and the choice is kept for the next
    /// cast. See RPaMpModel.CastFormatFor.
    /// </summary>
    public RPM.Core.Audio.CastAudioFormat CastFormat
    {
        get => _model.CastFormat;
        set => Update(_model with { CastFormat = value }, notify: false);
    }

    /// <summary>
    /// A picture to stand behind the whole app, instead of the built-in backdrop. The user chose it. It is
    /// a copy of the picked file, kept in the local-data folder, so that it survives the original being
    /// moved or deleted. Null means the default backdrop.
    /// </summary>
    public string? CustomBackgroundPath
    {
        get => _model.CustomBackgroundPath;
        set => Update(_model with { CustomBackgroundPath = value });
    }

    /// <summary>
    /// Which of the app's OWN backdrops to stand behind, by id (see Backdrop.Presets). Null or empty means
    /// the default (Backdrop.BuiltIn).
    ///
    /// <para>This is kept apart from <see cref="CustomBackgroundPath"/>, not folded into it as a path. The
    /// two answer different questions. A picture chosen from disk should not be forgotten just because a
    /// built-in was tried for a minute. A custom picture still wins while it is set.</para>
    /// </summary>
    public string? BuiltInBackground
    {
        get => _model.BuiltInBackground;
        set => Update(_model with { BuiltInBackground = value });
    }

    /// <summary>
    /// How a multi-disc release is handled once it is identified. Merged (the default) folds every disc
    /// into one album with continuing track numbers. Separate makes each disc its own "disc N of M" album.
    /// </summary>
    public MultiDiscMode MultiDiscMode
    {
        get => _model.MultiDiscMode;
        set => Update(_model with { MultiDiscMode = value });
    }

    /// <summary>
    /// Whether filing the import queue MOVES the queued files into the collection (the default: one copy of
    /// the record, in the place the library knows about), or COPIES them and leaves the source as it was.
    /// Copying is the only thing that a read-only source can do.
    ///
    /// <para>This is saved quietly. Nothing in the running app reacts to it except the next import. A
    /// global
    /// <see cref="Changed"/> would make the shell reload its backdrop for a menu click.</para>
    /// </summary>
    public ImportAction ImportAction
    {
        get => _model.ImportAction;
        set => Update(_model with { ImportAction = value }, notify: false);
    }

    // ── file naming ──

    /// <summary>The user's own naming templates, on top of <see cref="NamingTemplate.Presets"/>. These are
    /// the ones that the settings editor can rename, retype and delete.</summary>
    public IReadOnlyList<NamingTemplate> CustomTemplates => _model.CustomTemplates ?? [];

    /// <summary>The name of the template that new rips, vinyl and the import queue start on. Null, or a
    /// name that no longer resolves, falls back to the first built-in preset.</summary>
    public string? DefaultTemplateName => _model.DefaultTemplateName;

    /// <summary>Every template on offer: the built-in presets first, then the user's own.</summary>
    public IReadOnlyList<NamingTemplate> AllTemplates =>
        [.. NamingTemplate.Presets, .. CustomTemplates];

    /// <summary>The template that rips, vinyl and the queue use by default. This is the stored default,
    /// found by name. If nothing is stored, or the stored one has been deleted, it is the first
    /// built-in.</summary>
    public NamingTemplate DefaultTemplate =>
        AllTemplates.FirstOrDefault(t => t.Name == _model.DefaultTemplateName) ?? NamingTemplate.Presets[0];

    /// <summary>Saves the custom templates and the default template together. The editor's Save is then one
    /// write and one <see cref="Changed"/>. The queue rereads once, not once per field.</summary>
    public void SetNaming(IReadOnlyList<NamingTemplate> customTemplates, string? defaultTemplateName)
        => Update(_model with { CustomTemplates = customTemplates, DefaultTemplateName = defaultTemplateName });

    private void Update(Model model, bool notify = true)
    {
        _model = model;
        if (_mayWrite)
            try { File.WriteAllText(StorePath, JsonSerializer.Serialize(model, SettingsServiceJson.Default.Model)); } catch { }
        RPM.Desktop.Diagnostics.SwitchDiag.Mark("write");
        if (notify)
            Notify();
        RPM.Desktop.Diagnostics.SwitchDiag.Mark("fanout");
    }

    /// <summary>
    /// Tells everybody who subscribed. Each one runs ON THIS THREAD, in order. So a subscriber that blocks
    /// holds up the setting's writer. For the library's view switch, that writer is a click.
    ///
    /// <para>This is why the diagnostic (see <see cref="RPM.Desktop.Diagnostics.SwitchDiag"/>) walks the
    /// invocation list by hand. It stamps each handler with the type that owns it. An event is one name in
    /// a profile, whatever is attached to it. "The fan-out took a second" is not a finding until it says
    /// whose second it was. When the diagnostic is off, this is the plain invoke.</para>
    /// </summary>
    private void Notify()
    {
        if (!RPM.Desktop.Diagnostics.SwitchDiag.On || Changed is not { } changed)
        {
            Changed?.Invoke();
            return;
        }

        foreach (var handler in changed.GetInvocationList())
        {
            handler.DynamicInvoke();
            RPM.Desktop.Diagnostics.SwitchDiag.Mark(NameOf(handler));
        }
    }

    /// <summary>The type that owns a handler. A lambda is compiled onto a closure class nested inside the
    /// type that wrote it. Its name, such as <c>&lt;&gt;c__DisplayClass42_0</c>, names nobody. So the
    /// nesting is walked outwards until a type that somebody actually wrote is reached.</summary>
    private static string NameOf(Delegate handler)
    {
        var type = handler.Method.DeclaringType;
        while (type is not null && type.Name.StartsWith('<'))
            type = type.DeclaringType;
        return $"{type?.Name ?? "?"}.{handler.Method.Name}";
    }

    /// <summary>Restores persisted settings at startup. A failure has no side effects.</summary>
    public void Load()
    {
        var read = RPM.Core.Files.StoreFile.Load(
            StorePath, text => JsonSerializer.Deserialize(text, SettingsServiceJson.Default.Model));
        _model = read.Value ?? new Model(null);
        _mayWrite = read.MayWrite;

        // The one setting that the store itself reads. It is put where both the grid and the SQL layer can
        // see it, before either is asked anything. So the first count of the session uses the user's own
        // choice.
        RPM.Core.Library.QueueTags.Required = ImportRequiredTags;
    }
    /// <summary>The settings file. A published build is Native AOT, so it serializes through this generated
    /// code and never by reflection.</summary>
    [JsonSerializable(typeof(Model))]
    private sealed partial class SettingsServiceJson : JsonSerializerContext;

}
