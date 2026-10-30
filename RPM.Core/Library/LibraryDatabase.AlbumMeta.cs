using RPM.Core.Cloud;
using RPM.Core.Metadata.Providers;
using Microsoft.Data.Sqlite;

namespace RPM.Core.Library;

/// <summary>The user's own take on an album: whether it is a favourite, their star rating and markdown
/// review, and the pressing the album was identified as. It is keyed by the album folder (an album
/// is a folder in this library). A row exists only once the user has said something about the album.
/// An album nobody has touched has no row and reads back as <see cref="Default"/>.</summary>
/// <param name="ReleaseId">The catalogue's id for this pressing. It means nothing without
/// <paramref name="ReleaseProvider"/> beside it, the same pairing <c>ReleaseRef</c> carries
/// everywhere else, because a Discogs release number and an MBID share no namespace.</param>
/// <param name="ReleaseProvider">Which catalogue gave the id, as its own
/// <c>IMetadataProvider.Id</c>: <c>discogs</c>, or <c>rpm</c> for the RPM API, whose release ids are
/// MusicBrainz's.</param>
/// <param name="RpmAlbumId">RPM's own id for this pressing, once the album page has resolved one.
/// It is cached so that a linked album opens on the shared entry and is not matched again from its
/// text.</param>
/// <param name="Edition">What the pressing is, in the catalogue's words: "Vinyl, LP, Limited Edition,
/// Red Translucent". It tells two copies of one album apart when the catalogue number does not, and
/// it cannot be worked out from the files.</param>
/// <param name="PopularTrack">The title of the song on this record that people play most, or empty
/// for "asked, and no one song stands out". This is the one value in the row that is not about the
/// user or their copy. It is an answer fetched from outside and kept, so the star can show as soon as
/// the page opens. See <c>TrackPopularity</c>.</param>
/// <param name="PopularAskedUtcTicks">When that answer was fetched, so the page can ask again after a
/// week. Zero means "never asked", and it makes the next page open ask the services.</param>
public sealed record AlbumMeta(
    string Folder,
    bool IsFavorite,
    int Rating,
    string? ReviewMarkdown,
    long ReviewUpdatedUtcTicks,
    string? ReleaseId,
    string? Catno = null,
    string? Released = null,
    bool? Classical = null,
    bool? Normalize = null,
    string? ReleaseProvider = null,
    string? Barcode = null,
    string? RpmAlbumId = null,
    string? DiscId = null,
    string? ReleaseGroupId = null,
    string? Edition = null,
    string? PopularTrack = null,
    long PopularAskedUtcTicks = 0,
    string? Styles = null,
    string? Series = null,
    string? SeriesVolume = null)
{
    /// <summary>What an untouched album reads back as: no row in <c>album_meta</c>.</summary>
    public static AlbumMeta Default(string folder) => new(folder, false, 0, null, 0, null);

    /// <summary>The series this record is one volume of, or null where it stands alone. See
    /// <see cref="AlbumSeries"/>.</summary>
    public AlbumSeries? InSeries => AlbumSeries.Of(Series, SeriesVolume);

    /// <summary>True once the user has rated or written something. An empty review with no stars
    /// is nothing to show.</summary>
    public bool HasReview => Rating > 0 || !string.IsNullOrWhiteSpace(ReviewMarkdown);

    /// <summary>The Discogs release, when the album is linked to Discogs. It is what "Show in Discogs"
    /// opens. It is null for an album linked to any other catalogue.</summary>
    public string? DiscogsReleaseId => Is(DiscogsJson.ProviderId) ? ReleaseId : null;

    /// <summary>
    /// The MusicBrainz release this album is linked to, when it is.
    ///
    /// <para>The RPM API's release ids are MusicBrainz ids. The app sees one provider and the server
    /// does not say which catalogue answered, but what comes back is an MBID. So an album linked
    /// through the release picker already has the id that the shared entry needs.</para>
    /// </summary>
    public string? MusicBrainzReleaseId => Is(RpmMetadataProvider.ProviderId) ? ReleaseId : null;

    /// <summary>Whether RPM knows which pressing this is, as against which album. The album page's
    /// link control is for albums where this is false, for example vinyl rips and albums imported from
    /// files that nobody has linked.</summary>
    public bool IsLinkedToPressing => !string.IsNullOrEmpty(ReleaseId);

    private bool Is(string provider) =>
        !string.IsNullOrEmpty(ReleaseId) &&
        string.Equals(ReleaseProvider, provider, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Where one record's favourite stands between this machine and the store.
///
/// <para>There are three stamps, and each is compared only with one on its own side.
/// <see cref="EditedUtcTicks"/> against <see cref="SyncedUtcTicks"/> says this machine has a mark it
/// has not sent. The store's stamp on the row against <see cref="CloudUpdatedAt"/> says another
/// machine has written one since. Nothing here compares the clock of one computer with the clock of
/// another. The track catalogue follows the same rule, for the same reason.</para>
/// </summary>
public sealed record AlbumMarkSync(
    string Folder, bool IsFavorite, long EditedUtcTicks, long SyncedUtcTicks, long CloudUpdatedAt)
{
    /// <summary>This machine has changed the mark since it last agreed with the store.</summary>
    public bool ChangedHere => EditedUtcTicks > SyncedUtcTicks;
}

/// <summary>
/// Where one record's classical mark stands between this machine and the store. It has the same three
/// stamps as <see cref="AlbumMarkSync"/>, compared in the same way: each only with one on its own side.
/// </summary>
/// <param name="Classical">The mark as this library holds it. Null and false both draw an ordinary
/// record. Null means nobody has answered for the record here.</param>
public sealed record AlbumClassicalSync(
    string Folder, bool? Classical, long EditedUtcTicks, long SyncedUtcTicks, long CloudUpdatedAt)
{
    /// <summary>This machine has changed the mark since it last agreed with the store.</summary>
    public bool ChangedHere => EditedUtcTicks > SyncedUtcTicks;

    /// <summary>Whether the record is drawn as works and movements.</summary>
    public bool IsClassical => Classical == true;
}

/// <summary>How much of an album's audio the cloud is holding, rolled up from its tracks' own
/// <see cref="CloudState"/> and cloud-selected flags. It drives the tile's cloud badge and decides
/// whether the album's menu offers Add or Remove.</summary>
/// <param name="InFlight">Tracks that are queued or uploading. This is what makes the badge pulse
/// instead of staying still. It is counted apart from <paramref name="InSet"/> because otherwise
/// "chosen for the cloud" and "on its way there now" look the same in the rollup.</param>
/// <param name="NotHere">Tracks whose bytes are not on this disk at all. It is a subset of
/// <paramref name="InCloud"/>: an album can be fully in the cloud and fully on this computer at the
/// same time, and the offloaded badge exists to tell those two cases apart.</param>
public sealed record AlbumCloudRollup(int Total, int InCloud, int InSet, int InFlight = 0, int NotHere = 0)
{
    public static readonly AlbumCloudRollup Empty = new(0, 0, 0);

    /// <summary>Every track's bytes are in the cloud (Synced or offloaded): the filled badge.</summary>
    public bool FullyInCloud => Total > 0 && InCloud == Total;

    /// <summary>A track is not yet chosen for the cloud, so "Add to cloud" has something to do.</summary>
    public bool CanAdd => Total > 0 && InSet < Total;

    /// <summary>At least one track is in the cloud or chosen for it, so "Remove from cloud" has
    /// something to do.</summary>
    public bool CanRemove => InSet > 0;

    /// <summary>
    /// Work in progress wins over the resting states. An album that is half uploaded is better
    /// described as "going up" than as "partly up", and it is the only state the user is waiting on.
    /// </summary>
    public AlbumCloudBadge Badge => BadgeWhen(uploadsStopped: false);

    /// <summary>
    /// The same badge, told whether anything is actually going up.
    ///
    /// <para>
    /// A queued track is "on its way" only while a queue is moving. When the store is full the engine
    /// stops, and those rows stay queued until the user buys space or frees some, which may be days.
    /// If they were read as work in progress, the record would show a cloud with an arrow, breathing,
    /// for ever, over nothing. The mark would claim something that is not happening, and the breathing
    /// repaints the whole window every frame.
    /// </para>
    ///
    /// <para>So the in-flight branch is skipped and the record shows what it really is: chosen for
    /// the cloud, partly up, at rest.</para>
    /// </summary>
    public AlbumCloudBadge BadgeWhen(bool uploadsStopped) =>
        Total == 0 ? AlbumCloudBadge.None
        : InFlight > 0 && !uploadsStopped ? AlbumCloudBadge.Working
        // In the cloud and nowhere else. This is tested before "Full" because it is the stronger
        // statement: a wall of filled clouds cannot show which records still use this computer's disk,
        // and that is the one question offloading leaves.
        : NotHere == Total ? AlbumCloudBadge.Offloaded
        : InCloud == Total ? AlbumCloudBadge.Full
        : InSet > 0 ? AlbumCloudBadge.Partial
        : AlbumCloudBadge.None;

    /// <summary>
    /// The same record seen on somebody else's shelf, where the question is a different one.
    ///
    /// <para>
    /// <see cref="BadgeWhen"/> answers "how much of this disk does this record use". On a borrowed
    /// shelf the answer is "none" for every record, so a wall of identical cyan clouds would say only
    /// what the viewer already knows. What they cannot see is how the music reaches them. So the mark
    /// shows that: the store and their computer both (<see cref="AlbumCloudBadge.Full"/>), the store
    /// alone because that computer is off (<see cref="AlbumCloudBadge.Offloaded"/>), or their
    /// computer alone, which stops playing when they close the lid
    /// (<see cref="AlbumCloudBadge.PeerLink"/>).
    /// </para>
    ///
    /// <para>Uploads are not read. A borrowed record cannot be put in the cloud or taken out of it
    /// from here, so there is never work in flight to report.</para>
    /// </summary>
    /// <param name="ownerReachable">Whether the computer lending this shelf answered.</param>
    public AlbumCloudBadge BadgeOnBorrowedShelf(bool ownerReachable) =>
        Total == 0 ? AlbumCloudBadge.None
        : InCloud == 0 ? AlbumCloudBadge.PeerLink
        : InCloud == Total ? (ownerReachable ? AlbumCloudBadge.Full : AlbumCloudBadge.Offloaded)
        : AlbumCloudBadge.Partial;
}

/// <summary>The cloud badge an album tile shows in its top-left corner.</summary>
public enum AlbumCloudBadge
{
    /// <summary>Nothing of the album is in the cloud: no badge.</summary>
    None,

    /// <summary>Some tracks are in the cloud or on their way: an outline cloud.</summary>
    Partial,

    /// <summary>Every track is in the cloud: a filled, glass-white cloud.</summary>
    Full,

    /// <summary>
    /// Every track is in the cloud and none is on this disk: a filled cloud in rpm cyan.
    ///
    /// <para>It has its own badge because an offloaded library would otherwise be a wall of identical
    /// white clouds. All records are there and all play, but nothing on screen says which ones this
    /// computer still stores. The user cannot find that out by looking.</para>
    /// </summary>
    Offloaded,

    /// <summary>Tracks are queued or uploading now: a cloud with an arrow up, breathing.</summary>
    Working,

    /// <summary>The album is being taken back out of the cloud: a struck-through cloud, breathing.
    /// It cannot be derived from the rollup, because removal leaves no state in the mirror while it
    /// runs. The sync service reports it for as long as it lasts.</summary>
    Leaving,

    /// <summary>
    /// A borrowed record that reaches this computer over the network and in no other way, because its
    /// owner has not put it in the cloud. A link.
    ///
    /// <para>It is drawn only on somebody else's shelf, where a cloud says how the music arrives and
    /// not whose disk it uses: see <see cref="AlbumCloudRollup.BadgeOnBorrowedShelf"/>. It is the one
    /// mark on that shelf that comes with a condition: the record plays while the owner's computer is
    /// on, and disappears from the shelf when it is off.</para>
    /// </summary>
    PeerLink,

    /// <summary>
    /// Meant to be in the cloud, is not, and has stopped trying: a red cloud.
    ///
    /// <para>It is separate from <see cref="Partial"/>, which is the same case without the last part.
    /// An outline cloud means "on its way, or waiting its turn", and the user can leave it alone. This
    /// one must be read differently, because nothing more will happen to the track until somebody acts.
    /// The track carries the reason.</para>
    /// </summary>
    Failed,

    /// <summary>
    /// In the cloud, but something about it could not be finished: a yellow cloud.
    ///
    /// <para>At present this is a head clip that will not encode. The music is in the store and plays
    /// from it, which is why this is not <see cref="Failed"/>. Telling someone that their backed-up
    /// track is not backed up is worse than saying nothing.</para>
    /// </summary>
    Incomplete,
}

public sealed partial class LibraryDatabase
{
    // ── per-album metadata (favourite / rating / review / Discogs id) ──

    /// <summary>The user's take on one album, or <see cref="AlbumMeta.Default"/> when they have not
    /// said anything about it yet.</summary>
    public AlbumMeta GetAlbumMeta(string folder)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                "SELECT is_favorite, rating, review_md, review_updated_utc_ticks, release_id, " +
                "catno, released, classical, normalize, release_provider, barcode, rpm_album_id, " +
                "disc_id, release_group_id, edition, popular_track, popular_asked_utc_ticks, styles, " +
                "series, series_volume FROM album_meta WHERE folder = $folder";
            cmd.Parameters.AddWithValue("$folder", folder);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? ReadAlbumMeta(folder, reader) : AlbumMeta.Default(folder);
        }
    }

    /// <summary>Every touched album among <paramref name="folders"/>, by folder. It is the wall's one
    /// query for a whole page of tiles, instead of one round trip per card. Folders with no row are
    /// absent from the map (the caller reads them as <see cref="AlbumMeta.Default"/>).</summary>
    public IReadOnlyDictionary<string, AlbumMeta> GetAlbumMetaMany(IEnumerable<string> folders)
    {
        var wanted = new HashSet<string>(folders, StringComparer.OrdinalIgnoreCase);
        var map = new Dictionary<string, AlbumMeta>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
            return map;

        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                "SELECT folder, is_favorite, rating, review_md, review_updated_utc_ticks, " +
                "release_id, catno, released, classical, normalize, release_provider, barcode, " +
                "rpm_album_id, disc_id, release_group_id, edition, popular_track, " +
                "popular_asked_utc_ticks, styles, series, series_volume FROM album_meta";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var folder = reader.GetString(0);
                if (wanted.Contains(folder))
                    map[folder] = ReadAlbumMeta(folder, reader, folderOrdinalOffset: 1);
            }
        }
        return map;
    }

    /// <summary>
    /// Sets (or clears) an album's favourite flag, creating the row on first touch.
    ///
    /// <para>The row is stamped as changed here, which puts the record in front of the next sync. The
    /// mark belongs to the listener and goes to every machine they own. See
    /// <see cref="AcceptAlbumFavorite"/> for the other direction.</para>
    /// </summary>
    public void SetAlbumFavorite(string folder, bool favorite) => UpsertAlbumMeta(
        AlbumWrite.Said,
        folder,
        "is_favorite = $v, favorite_edited_utc_ticks = $now",
        ("$v", favorite ? 1 : 0),
        ("$now", DateTime.UtcNow.Ticks),
        insertColumns: "is_favorite, favorite_edited_utc_ticks",
        insertValues: "$v, $now");

    // ── the mark's own half of the sync ──

    /// <summary>Where one record's favourite stands with the cloud: when this machine last changed it,
    /// when it last agreed with the store, and the store's stamp as it last accepted it. All values are
    /// zero for a record nobody has marked.</summary>
    public AlbumMarkSync GetAlbumMarkSync(string folder)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                "SELECT is_favorite, favorite_edited_utc_ticks, favorite_synced_utc_ticks, " +
                "favorite_cloud_updated_at FROM album_meta WHERE folder = $folder";
            cmd.Parameters.AddWithValue("$folder", folder);
            using var reader = cmd.ExecuteReader();
            return reader.Read()
                ? new AlbumMarkSync(
                    folder, reader.GetInt64(0) != 0, reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3))
                : new AlbumMarkSync(folder, false, 0, 0, 0);
        }
    }

    /// <summary>Every record this library has an opinion about, with where each one's mark stands.
    /// One sync pass walks this list.</summary>
    public IReadOnlyList<AlbumMarkSync> AlbumMarkSyncStates()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                "SELECT folder, is_favorite, favorite_edited_utc_ticks, favorite_synced_utc_ticks, " +
                "favorite_cloud_updated_at FROM album_meta";
            using var reader = cmd.ExecuteReader();
            var states = new List<AlbumMarkSync>();
            while (reader.Read())
                states.Add(new AlbumMarkSync(
                    reader.GetString(0), reader.GetInt64(1) != 0,
                    reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)));
            return states;
        }
    }

    /// <summary>
    /// Takes the store's word for a record's mark: the flag, and the stamp it came with. It sets no
    /// edit stamp. A mark that came from the store counts as already sent, and stamping it would make
    /// this machine push the store's own value back on the next pass.
    /// </summary>
    public void AcceptAlbumFavorite(string folder, bool favorite, long cloudUpdatedAt) => UpsertAlbumMeta(
        AlbumWrite.Said,
        folder,
        "is_favorite = $v, favorite_cloud_updated_at = $at, favorite_synced_utc_ticks = $now",
        ("$v", favorite ? 1 : 0),
        ("$at", cloudUpdatedAt),
        ("$now", DateTime.UtcNow.Ticks),
        insertColumns: "is_favorite, favorite_cloud_updated_at, favorite_synced_utc_ticks",
        insertValues: "$v, $at, $now");

    /// <summary>The store has taken this machine's mark: record the stamp it was written with, and
    /// that the two now agree.</summary>
    public void NoteAlbumMarkSent(string folder, long cloudUpdatedAt) => UpsertAlbumMeta(
        AlbumWrite.Filed,
        folder,
        "favorite_cloud_updated_at = $at, favorite_synced_utc_ticks = $now",
        ("$at", cloudUpdatedAt),
        ("$now", DateTime.UtcNow.Ticks),
        insertColumns: "favorite_cloud_updated_at, favorite_synced_utc_ticks",
        insertValues: "$at, $now");

    /// <summary>Stores the user's star rating (0 = unrated) and markdown review for an album.</summary>
    public void SetAlbumReview(string folder, int rating, string? reviewMarkdown, long updatedUtcTicks) =>
        UpsertAlbumMeta(
            AlbumWrite.Said,
            folder,
            "rating = $rating, review_md = $md, review_updated_utc_ticks = $ts",
            ("$rating", Math.Clamp(rating, 0, 5)),
            ("$md", (object?)reviewMarkdown ?? DBNull.Value),
            ("$ts", updatedUtcTicks),
            insertColumns: "rating, review_md, review_updated_utc_ticks",
            insertValues: "$rating, $md, $ts");

    /// <summary>
    /// Stamps the pressing an album was identified as: the catalogue that named it and that
    /// catalogue's id for it.
    /// </summary>
    /// <remarks>Set both together or neither. An id with no catalogue beside it names nothing, since
    /// Discogs release 430456 and MusicBrainz release 430456 are different records. To clear the link,
    /// pass null for the id.</remarks>
    public void SetAlbumPressing(string folder, string? provider, string? releaseId) => UpsertAlbumMeta(
        AlbumWrite.Said,
        folder,
        "release_provider = $p, release_id = $v",
        ("$p", string.IsNullOrWhiteSpace(releaseId) ? DBNull.Value : (object?)provider ?? DBNull.Value),
        ("$v", (object?)releaseId ?? DBNull.Value),
        insertColumns: "release_provider, release_id",
        insertValues: "$p, $v");

    /// <summary>
    /// Caches what the linked release says this pressing is: its catalogue number, the day it came
    /// out, and the barcode on the packaging.
    ///
    /// <para>These are stored and not asked for again because they are the best handles a record has
    /// (RPM's shared album entry matches a pressing on them), and because each release lookup uses
    /// the user's own rate limit at the catalogue that answered. Any of them may be null: many
    /// records have no catalogue number, and many releases are dated by year alone.</para>
    /// </summary>
    public void SetAlbumRelease(string folder, string? catno, string? released, string? barcode = null) =>
        UpsertAlbumMeta(
            AlbumWrite.Said,
            folder,
            "catno = $catno, released = $released, barcode = $barcode",
            ("$catno", (object?)catno ?? DBNull.Value),
            ("$released", (object?)released ?? DBNull.Value),
            ("$barcode", (object?)barcode ?? DBNull.Value),
            insertColumns: "catno, released, barcode",
            insertValues: "$catno, $released, $barcode");

    /// <summary>
    /// Remembers what the catalogue says this pressing is: "Vinyl, LP, Limited Edition, Red
    /// Translucent", or whatever it answered with.
    /// </summary>
    /// <remarks>This is a separate write and not another argument of <see cref="SetAlbumRelease"/>.
    /// That method stores the three handles that the shared entry matches a pressing on, and this is
    /// text for a reader. A lookup that learns only one of them should not have to pass null for
    /// the rest.</remarks>
    public void SetAlbumEdition(string folder, string? edition) => UpsertAlbumMeta(
        // Filed and not said, for the same reason as SetAlbumPopularTrack: a catalogue describing a
        // pressing is not the listener saying something about their copy.
        AlbumWrite.Filed,
        folder,
        "edition = $v",
        ("$v", (object?)edition ?? DBNull.Value),
        insertColumns: "edition",
        insertValues: "$v");

    /// <summary>
    /// Keeps the narrow labels Discogs files this release under: "Deep House", "Boom Bap", "Northern
    /// Soul".
    ///
    /// <para><b>They answer a question the canonical genre cannot.</b> A shelf label is one broad word
    /// by design: see <see cref="Metadata.Genres"/>, which keeps a library to about thirty genres
    /// instead of four hundred. <see cref="MoodFlavour"/> needs the finer detail for one purpose: to
    /// know which records inside a shelf are the kind of thing a list is named after. Discogs draws
    /// this line itself, between a release's genres and its STYLES.</para>
    ///
    /// <para><b>Empty is an answer and null is not.</b> A release that Discogs files under a genre
    /// with no style is stored as an empty string, which stops the sweep asking about it again. Null
    /// means nobody has asked. <see cref="AlbumMeta.Edition"/> makes the same distinction, for the
    /// same reason.</para>
    ///
    /// <para>The styles are joined with semicolons and not commas, because a style may contain a comma
    /// ("Rock, Pop") and a separator that can appear inside a value is not a separator.</para>
    /// </summary>
    public void SetAlbumStyles(string folder, IReadOnlyList<string>? styles) => UpsertAlbumMeta(
        // Filed, not said: a catalogue naming what a record sounds like is not the listener speaking.
        AlbumWrite.Filed,
        folder,
        "styles = $v",
        ("$v", styles is null
            ? DBNull.Value
            : string.Join("; ", styles.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()))),
        insertColumns: "styles",
        insertValues: "$v");

    /// <summary>
    /// Remembers which song on this record people play most, by its title.
    /// </summary>
    /// <remarks>It stores the title and not a track number or a recording id. The number belongs to
    /// the pressing and the id means nothing to the rows in the library, but the title is what both
    /// sides of the question share. Empty is a real answer: it means "asked, and no song stood out
    /// enough to mark", and it stops the page asking again every time it opens.</remarks>
    /// <param name="askedUtcTicks">When the answer was fetched. It is passed in and not taken from the
    /// clock here, because it is the moment the SERVICES were asked, not the moment the row was
    /// written. The two differ by the time the lookup took, and the first one decides when to ask
    /// again.</param>
    public void SetAlbumPopularTrack(string folder, string? title, long askedUtcTicks) => UpsertAlbumMeta(
        // Filed, not said. The listener has no opinion here about which song people play most. Stamping
        // the row as edited would send it round the user's other computers as if they had rated the
        // record.
        AlbumWrite.Filed,
        folder,
        "popular_track = $v, popular_asked_utc_ticks = $asked",
        ("$v", (object?)title ?? DBNull.Value),
        ("$asked", askedUtcTicks),
        insertColumns: "popular_track, popular_asked_utc_ticks",
        insertValues: "$v, $asked");

    /// <summary>
    /// Remembers the disc an album was ripped from.
    /// </summary>
    /// <remarks>It is kept apart from <see cref="SetAlbumRelease"/> because no catalogue said it. It
    /// is computed from the disc itself, and it is true whether or not any catalogue knows the
    /// record.</remarks>
    public void SetAlbumDiscId(string folder, string? discId) => UpsertAlbumMeta(
        AlbumWrite.Said,
        folder,
        "disc_id = $v",
        ("$v", (object?)discId ?? DBNull.Value),
        insertColumns: "disc_id",
        insertValues: "$v");

    /// <summary>
    /// Remembers the album this record is a pressing of.
    /// </summary>
    /// <remarks>It is separate from the pressing because the two answer different questions: the
    /// pressing is which record you own, and the group is which album it is. A user who owns the 1977
    /// cut and a user who owns the remaster own different records but talk about one album.
    /// </remarks>
    public void SetAlbumReleaseGroup(string folder, string? groupId) => UpsertAlbumMeta(
        AlbumWrite.Said,
        folder,
        "release_group_id = $v",
        ("$v", (object?)groupId ?? DBNull.Value),
        insertColumns: "release_group_id",
        insertValues: "$v");

    /// <summary>Remembers RPM's own id for this album, once the shared entry has been resolved. This
    /// saves matching the album from its text on every visit, and a linked album is opened by it.</summary>
    public void SetAlbumRpmId(string folder, string? albumId) => UpsertAlbumMeta(
        AlbumWrite.Said,
        folder,
        "rpm_album_id = $v",
        ("$v", (object?)albumId ?? DBNull.Value),
        insertColumns: "rpm_album_id",
        insertValues: "$v");

    /// <summary>
    /// Says whether this album is a classical record.
    ///
    /// <para>This is the user's own answer and nothing else. Nothing reads it from the tags, because
    /// that wrongly marked some records as classical. Null means nobody has answered, and it is read
    /// as an ordinary record, the same as false. Once given, the answer stays through every later
    /// rescan and retag.</para>
    /// </summary>
    public void SetAlbumClassical(string folder, bool? classical) => UpsertAlbumMeta(
        AlbumWrite.Said,
        folder,
        // Stamped as changed here, which puts the record in front of the next sync, as
        // SetAlbumFavorite does for the favourite mark.
        "classical = $v, classical_edited_utc_ticks = $now",
        ("$v", classical is { } c ? c ? 1 : 0 : (object)DBNull.Value),
        ("$now", DateTime.UtcNow.Ticks),
        insertColumns: "classical, classical_edited_utc_ticks",
        insertValues: "$v, $now");

    /// <summary>
    /// Says which series this record is one volume of, or that it stands alone (null). See
    /// <see cref="AlbumSeries"/>: the listener's own answer, kept here and never in the files.
    /// </summary>
    public void SetAlbumSeries(string folder, AlbumSeries? series) => UpsertAlbumMeta(
        AlbumWrite.Said,
        folder,
        "series = $name, series_volume = $volume",
        ("$name", (object?)series?.Name ?? DBNull.Value),
        ("$volume", (object?)series?.Volume ?? DBNull.Value),
        insertColumns: "series, series_volume",
        insertValues: "$name, $volume");

    /// <summary>Every series name the library holds, in alphabetical order: what the album editor
    /// offers, so the forty-third volume is filed under the same name as the first.</summary>
    public IReadOnlyList<string> AlbumSeriesNames()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT series FROM album_meta
                WHERE series IS NOT NULL AND TRIM(series) <> ''
                GROUP BY series COLLATE NOCASE
                ORDER BY series COLLATE NOCASE
                """;
            using var reader = cmd.ExecuteReader();
            var names = new List<string>();
            while (reader.Read())
                names.Add(reader.GetString(0));
            return names;
        }
    }

    /// <summary>Every record filed under one series, by folder, in volume order. A folder emptied by
    /// a move holds no series, so only records still on the shelf answer.</summary>
    public IReadOnlyList<(string Folder, AlbumSeries Series)> AlbumsInSeries(string name)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                "SELECT folder, series, series_volume FROM album_meta WHERE series = $name COLLATE NOCASE";
            cmd.Parameters.AddWithValue("$name", name.Trim());
            using var reader = cmd.ExecuteReader();
            var found = new List<(string, AlbumSeries)>();
            while (reader.Read())
                if (AlbumSeries.Of(reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)) is { } series)
                    found.Add((reader.GetString(0), series));
            return [.. found.OrderBy(f => f.Item2.Order.Number).ThenBy(f => f.Item2.Order.Text, StringComparer.OrdinalIgnoreCase)];
        }
    }

    // ── the classical mark's own half of the sync ──

    /// <summary>Every record this library has a row for, with where its classical mark stands. One
    /// sync pass walks this list.</summary>
    public IReadOnlyList<AlbumClassicalSync> AlbumClassicalSyncStates()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                "SELECT folder, classical, classical_edited_utc_ticks, classical_synced_utc_ticks, " +
                "classical_cloud_updated_at FROM album_meta";
            using var reader = cmd.ExecuteReader();
            var states = new List<AlbumClassicalSync>();
            while (reader.Read())
                states.Add(new AlbumClassicalSync(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1) != 0,
                    reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4)));
            return states;
        }
    }

    /// <summary>
    /// Takes the store's word for a record's classical mark, with the stamp it came with. Returns
    /// false and writes nothing when the mark was changed here after <paramref name="read"/> was taken.
    /// </summary>
    /// <param name="read">The state the decision to accept was made from. The statement compares the
    /// row's edit stamp with it. So a mark changed in the editor between that read and this write is
    /// kept and sent on the next pass, and the store's value does not overwrite it.</param>
    public bool AcceptAlbumClassical(AlbumClassicalSync read, bool classical, long cloudUpdatedAt)
    {
        ArgumentNullException.ThrowIfNull(read);

        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            // An insert for a record this library has never written anything about. Such a record
            // reads as all-zero stamps.
            //
            // edited_utc_ticks is NOT moved, and an inserted row gets zero. That stamp decides which
            // whole row wins when two of the listener's computers merge over the LAN. If the store's
            // answer moved it, this row would win against the other machine's row and replace its
            // review, rating and pressing. The other machine reads the same answer from the store
            // itself.
            cmd.CommandText =
                """
                INSERT INTO album_meta (folder, classical, classical_cloud_updated_at,
                                        classical_synced_utc_ticks, edited_utc_ticks)
                VALUES ($folder, $v, $at, $now, 0)
                ON CONFLICT(folder) DO UPDATE SET
                    classical = $v,
                    classical_cloud_updated_at = $at,
                    classical_synced_utc_ticks = MAX(album_meta.classical_edited_utc_ticks, $now)
                WHERE album_meta.classical_edited_utc_ticks = $readEdited
                """;
            cmd.Parameters.AddWithValue("$folder", read.Folder);
            cmd.Parameters.AddWithValue("$v", classical ? 1 : 0);
            cmd.Parameters.AddWithValue("$at", cloudUpdatedAt);
            cmd.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
            cmd.Parameters.AddWithValue("$readEdited", read.EditedUtcTicks);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>
    /// The store has taken this machine's classical mark, or already held the same answer. Record the
    /// store's stamp, and that the two agree up to the edit that was sent.
    /// </summary>
    /// <param name="sent">The state the value sent was read from. Synced is set to ITS edit stamp, not
    /// the clock. A mark changed again while the write was in flight has a later stamp, stays
    /// outstanding, and is sent on the next pass.</param>
    public void NoteAlbumClassicalSent(AlbumClassicalSync sent, long cloudUpdatedAt)
    {
        ArgumentNullException.ThrowIfNull(sent);

        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                """
                UPDATE album_meta
                SET classical_cloud_updated_at = $at,
                    classical_synced_utc_ticks = MAX(classical_synced_utc_ticks, $sentEdited)
                WHERE folder = $folder
                """;
            cmd.Parameters.AddWithValue("$folder", sent.Folder);
            cmd.Parameters.AddWithValue("$at", cloudUpdatedAt);
            cmd.Parameters.AddWithValue("$sentEdited", sent.EditedUtcTicks);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Says whether this record is levelled with the rest of the collection, or leaves the question
    /// to the settings.
    ///
    /// <para>Null is the default and the common case: the global setting decides, and the classical
    /// rule under it excuses the records that levelling serves worst. True and false are the listener
    /// overruling all of that for this record alone (for example a live album whose quiet opening is
    /// the point, or a compilation so uneven that it needs the help). They stay through every
    /// rescan.</para>
    ///
    /// <para>An answer here beats the classical rule as well as the global setting, on purpose.
    /// Someone who turns levelling ON for one boxed set has said something more specific than "leave
    /// classical alone".</para>
    /// </summary>
    public void SetAlbumNormalize(string folder, bool? normalize) => UpsertAlbumMeta(
        AlbumWrite.Said,
        folder,
        "normalize = $v",
        ("$v", normalize is { } n ? n ? 1 : 0 : (object)DBNull.Value),
        insertColumns: "normalize",
        insertValues: "$v");

    /// <summary>One upsert shape: INSERT the row with just the touched column(s), or UPDATE them if the
    /// album already has a row. The other columns keep their table defaults on insert.</summary>
    /// <summary>
    /// Which kind of write to <c>album_meta</c> this is. There is no default: the answer decides
    /// whether the row is stamped as edited, and the stamp is what carries it to the listener's other
    /// computers. If it were forgotten on a bookkeeping write, a machine's own filing would go back
    /// across the network as though somebody had said it.
    /// </summary>
    private enum AlbumWrite
    {
        /// <summary>A person said something about this record: set it classical, rated it, linked
        /// its pressing, or wrote a review. The row is stamped, and it travels.</summary>
        Said,

        /// <summary>The machine keeping its own books: what the store has taken, what has been sent.
        /// Nobody said anything, so the row keeps the time of the last thing somebody did say.</summary>
        Filed,
    }

    private void UpsertAlbumMeta(
        AlbumWrite kind, string folder, string setClause,
        (string Name, object Value) p1,
        string insertColumns, string insertValues)
        => UpsertAlbumMetaCore(kind, folder, setClause, insertColumns, insertValues, [p1]);

    private void UpsertAlbumMeta(
        AlbumWrite kind, string folder, string setClause,
        (string Name, object Value) p1, (string Name, object Value) p2,
        string insertColumns, string insertValues)
        => UpsertAlbumMetaCore(kind, folder, setClause, insertColumns, insertValues, [p1, p2]);

    private void UpsertAlbumMeta(
        AlbumWrite kind, string folder, string setClause,
        (string Name, object Value) p1, (string Name, object Value) p2, (string Name, object Value) p3,
        string insertColumns, string insertValues)
        => UpsertAlbumMetaCore(kind, folder, setClause, insertColumns, insertValues, [p1, p2, p3]);

    /// <summary>
    /// Every setter above lands here. Every one of them is a PERSON saying something about a record,
    /// so the row is stamped with the moment they said it, in one place and not in twelve.
    ///
    /// <para>The stamp lets another of this listener's computers merge its own row with this one:
    /// the row edited later survives. See <see cref="ApplyAlbumMetaEdits"/>, the one door that writes
    /// a stamp it was GIVEN and does not take the clock. A row arriving from another machine must keep
    /// the moment it was edited there, not the moment it arrived here.</para>
    /// </summary>
    private void UpsertAlbumMetaCore(
        AlbumWrite kind, string folder, string setClause, string insertColumns, string insertValues,
        (string Name, object Value)[] parameters)
    {
        var said = kind == AlbumWrite.Said;

        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText =
                $"""
                INSERT INTO album_meta (folder, edited_utc_ticks, {insertColumns})
                VALUES ($folder, $edited, {insertValues})
                ON CONFLICT(folder) DO UPDATE SET {setClause}{(said ? ", edited_utc_ticks = $edited" : "")}
                """;
            cmd.Parameters.AddWithValue("$folder", folder);

            // On an INSERT the stamp is written either way. A row that exists at all was created by
            // something, and zero would say "older than everything" about a brand new row. On an UPDATE
            // only what somebody SAID moves it.
            cmd.Parameters.AddWithValue("$edited", said ? DateTime.UtcNow.Ticks : 0L);
            foreach (var (name, value) in parameters)
                cmd.Parameters.AddWithValue(name, value);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Drops an album's metadata row. Used when the album is deleted from the library, so its
    /// favourite, review and rating do not stay behind as an orphan.</summary>
    public void DeleteAlbumMeta(string folder)
    {
        using (_gate.Take())
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM album_meta WHERE folder = $folder";
            cmd.Parameters.AddWithValue("$folder", folder);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Re-keys an album's metadata row onto the folder its tracks moved to, so that re-filing does not
    /// cost the user the rating and review they wrote. It uses <c>UPDATE OR REPLACE</c> because the
    /// destination may already have a row of its own. The record that moved in wins it, in the same way
    /// its cover overwrites the folder's.
    ///
    /// <para><b>It also leaves an emptied row behind, stamped now.</b> The row is keyed by folder, and
    /// the merge between the listener's own computers is "later stamp wins, whole row, and nothing is
    /// ever deleted" (see LibraryDatabase.PeerAlbumMeta). If a move only re-keyed the row HERE, the
    /// other machine would see an arrival and never a departure. It would take the row at the new
    /// folder and keep its own at the old one. Both machines would then hold the record's stars and
    /// pressing twice, once under a folder with no music in it. Worse, when something else is later
    /// filed into that folder, it would inherit a stranger's rating and release.</para>
    ///
    /// <para>A removal must travel or it does not hold, and the only thing that travels here is a row.
    /// So the departure is sent as a row: the same fields, emptied, stamped at this moment so it beats
    /// whatever the peer still holds. Both ends then settle on one row with the marks and one row that
    /// says nothing, and the next pass in either direction has nothing to do.</para>
    ///
    /// <para>The favourite flag is the one thing that is not emptied: see the note on the statement
    /// below.</para>
    ///
    /// <para>The moved row's OWN stamp is left alone. That column means "when a person last said any of
    /// this", and re-filing a file is not a person saying something about the record. It also keeps a
    /// peer's later opinion of the destination winning over an older one that arrives with the
    /// move.</para>
    /// </summary>
    public void MoveAlbumMeta(string fromFolder, string toFolder)
    {
        using (_gate.Take())
        {
            using var tx = _connection.BeginTransaction();

            // The favourite flag is read before the row moves, because the emptied row keeps it: see
            // the note on the statement below. If there is no row at all, nothing was ever written
            // about this record. There is no departure to announce, and no reason to leave an empty row
            // on two machines.
            object? favorite;
            using (var standing = _connection.CreateCommand())
            {
                standing.Transaction = tx;
                standing.CommandText = "SELECT is_favorite FROM album_meta WHERE folder = $from";
                standing.Parameters.AddWithValue("$from", fromFolder);
                favorite = standing.ExecuteScalar();
            }
            if (favorite is null or DBNull)
            {
                tx.Commit();
                return;
            }

            using (var move = _connection.CreateCommand())
            {
                move.Transaction = tx;
                move.CommandText = "UPDATE OR REPLACE album_meta SET folder = $to WHERE folder = $from";
                move.Parameters.AddWithValue("$from", fromFolder);
                move.Parameters.AddWithValue("$to", toFolder);
                move.ExecuteNonQuery();
            }

            using (var emptied = _connection.CreateCommand())
            {
                emptied.Transaction = tx;
                // Everything the merge carries EXCEPT the favourite flag. The exception is important.
                //
                // ApplyAlbumMetaEdits bumps favorite_edited_utc_ticks for any row it takes, because a
                // mark that arrived over the LAN is a mark the cloud is owed as well. On the machine
                // that has NOT re-filed, this folder still has music in it. If the emptied row turned
                // the flag off, that machine would tell the store the record is no longer a favourite,
                // while the machine that re-filed has no tracks under the new folder yet and cannot say
                // otherwise. The favourite would be lost in the cloud because a file was renamed.
                //
                // The flag is carried across unchanged, so the arriving value equals the one already
                // there. The mark pass finds nothing to send, and a move cannot change anybody's
                // favourite. A flag left on a folder with no tracks does nothing: nothing draws it, and
                // the store's pass finds a record's key through its tracks, so it is never reached. It
                // also errs in the safe direction: a record somebody marked is never silently
                // un-marked.
                //
                // The row is INSERTed and not updated: the move above took the old row with it, so there
                // is nothing at this folder to update. The conflict branch handles the case where
                // something else already sits here.
                emptied.CommandText =
                    """
                    INSERT INTO album_meta (folder, is_favorite, edited_utc_ticks)
                    VALUES ($from, $favorite, $now)
                    ON CONFLICT(folder) DO UPDATE SET
                        is_favorite = $favorite,
                        rating = 0,
                        review_md = NULL,
                        review_updated_utc_ticks = 0,
                        release_id = NULL,
                        catno = NULL,
                        released = NULL,
                        classical = NULL,
                        normalize = NULL,
                        release_provider = NULL,
                        barcode = NULL,
                        rpm_album_id = NULL,
                        disc_id = NULL,
                        release_group_id = NULL,
                        edition = NULL,
                        styles = NULL,
                        series = NULL,
                        series_volume = NULL,
                        popular_track = NULL,
                        popular_asked_utc_ticks = 0,
                        edited_utc_ticks = $now
                    """;
                emptied.Parameters.AddWithValue("$from", fromFolder);
                emptied.Parameters.AddWithValue("$favorite", favorite);
                emptied.Parameters.AddWithValue("$now", DateTime.UtcNow.Ticks);
                emptied.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    private static AlbumMeta ReadAlbumMeta(string folder, SqliteDataReader r, int folderOrdinalOffset = 0)
        => new(
            folder,
            IsFavorite: r.GetInt64(folderOrdinalOffset + 0) != 0,
            Rating: (int)r.GetInt64(folderOrdinalOffset + 1),
            ReviewMarkdown: r.IsDBNull(folderOrdinalOffset + 2) ? null : r.GetString(folderOrdinalOffset + 2),
            ReviewUpdatedUtcTicks: r.GetInt64(folderOrdinalOffset + 3),
            ReleaseId: r.IsDBNull(folderOrdinalOffset + 4) ? null : r.GetString(folderOrdinalOffset + 4),
            Catno: r.IsDBNull(folderOrdinalOffset + 5) ? null : r.GetString(folderOrdinalOffset + 5),
            Released: r.IsDBNull(folderOrdinalOffset + 6) ? null : r.GetString(folderOrdinalOffset + 6),
            Classical: r.IsDBNull(folderOrdinalOffset + 7) ? null : r.GetInt64(folderOrdinalOffset + 7) != 0,
            Normalize: r.IsDBNull(folderOrdinalOffset + 8) ? null : r.GetInt64(folderOrdinalOffset + 8) != 0,
            ReleaseProvider: r.IsDBNull(folderOrdinalOffset + 9) ? null : r.GetString(folderOrdinalOffset + 9),
            Barcode: r.IsDBNull(folderOrdinalOffset + 10) ? null : r.GetString(folderOrdinalOffset + 10),
            RpmAlbumId: r.IsDBNull(folderOrdinalOffset + 11) ? null : r.GetString(folderOrdinalOffset + 11),
            DiscId: r.IsDBNull(folderOrdinalOffset + 12) ? null : r.GetString(folderOrdinalOffset + 12),
            ReleaseGroupId: r.IsDBNull(folderOrdinalOffset + 13) ? null : r.GetString(folderOrdinalOffset + 13),
            Edition: r.IsDBNull(folderOrdinalOffset + 14) ? null : r.GetString(folderOrdinalOffset + 14),
            PopularTrack: r.IsDBNull(folderOrdinalOffset + 15) ? null : r.GetString(folderOrdinalOffset + 15),
            PopularAskedUtcTicks: r.GetInt64(folderOrdinalOffset + 16),
            Styles: r.IsDBNull(folderOrdinalOffset + 17) ? null : r.GetString(folderOrdinalOffset + 17),
            Series: r.IsDBNull(folderOrdinalOffset + 18) ? null : r.GetString(folderOrdinalOffset + 18),
            SeriesVolume: r.IsDBNull(folderOrdinalOffset + 19) ? null : r.GetString(folderOrdinalOffset + 19));

    /// <summary>One album that the styles sweep still has to ask about: where it is, and the Discogs
    /// release it is already linked to.</summary>
    public sealed record AlbumToStyle(string Folder, string ReleaseId);

    /// <summary>
    /// The next albums that are linked to a Discogs release and have no styles read from it yet.
    ///
    /// <para><b>Only linked albums are returned, which is the whole point of this pass.</b> A linked
    /// album needs one request by id: exact, cheap, and no search to get wrong. An album with no link
    /// would need a search per record against the user's own Discogs rate limit, and a wrong match
    /// would write the wrong vocabulary onto a record that had none.</para>
    ///
    /// <para>An album whose styles are the empty string has been asked and answered (Discogs filed it
    /// under a genre with no style), so it does not come back. That keeps the pass finite.</para>
    /// </summary>
    public IReadOnlyList<AlbumToStyle> NextAlbumsWithoutStyles(int limit)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT folder, release_id
                FROM album_meta
                WHERE styles IS NULL
                  AND release_provider = $discogs
                  AND release_id IS NOT NULL AND TRIM(release_id) <> ''
                ORDER BY folder
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$discogs", DiscogsJson.ProviderId);
            cmd.Parameters.AddWithValue("$limit", Math.Max(0, limit));

            using var reader = cmd.ExecuteReader();
            var albums = new List<AlbumToStyle>();
            while (reader.Read())
                albums.Add(new AlbumToStyle(reader.GetString(0), reader.GetString(1)));
            return albums;
        }
    }

    // ── an album's tracks, and its cloud rollup ──

    /// <summary>Every track id in an album folder. "Add to cloud" selects them in one call.</summary>
    public IReadOnlyList<long> FolderTrackIds(string folder)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText = "SELECT id FROM tracks WHERE folder = $folder";
            cmd.Parameters.AddWithValue("$folder", folder);
            using var reader = cmd.ExecuteReader();
            var ids = new List<long>();
            while (reader.Read())
                ids.Add(reader.GetInt64(0));
            return ids;
        }
    }

    /// <summary>Every track row in an album folder. "Remove from cloud" walks them, so it can delete
    /// the server copy of the tracks that are really up there and un-pick the rest.</summary>
    public IReadOnlyList<LibraryTrack> FolderTracks(string folder)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                "SELECT * FROM tracks WHERE folder = $folder" +
                (ReachTerm("cloud_state") is { Length: > 0 } reach ? $" AND {reach}" : "") +
                " ORDER BY file_name";
            cmd.Parameters.AddWithValue("$folder", folder);
            using var reader = cmd.ExecuteReader();
            var tracks = new List<LibraryTrack>();
            while (reader.Read())
                tracks.Add(ReadTrack(reader));
            return tracks;
        }
    }

    /// <summary>How much of an album is in the cloud, for the tile badge and the Add/Remove menu split.
    /// "In cloud" is Synced or offloaded. "In set" also counts a track that is chosen for the cloud but
    /// not yet uploaded.
    ///
    /// <para>It counts the rows the shelf is showing, which on a narrowed borrowed shelf is not all of
    /// them (see <see cref="CloudRowsOnly"/>). The mark on a tile has to describe the record the viewer
    /// can see. Otherwise rows hidden because the lender's computer is off would make a record that is
    /// fully playable from the store look like a half-present one.</para></summary>
    public AlbumCloudRollup GetAlbumCloudRollup(string folder)
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                $"""
                SELECT
                    COUNT(*),
                    SUM(CASE WHEN cloud_state IN ({(int)CloudState.Synced}, {(int)CloudState.CloudOnly}) THEN 1 ELSE 0 END),
                    SUM(CASE WHEN cloud_selected = 1 OR cloud_state IN ({(int)CloudState.Synced}, {(int)CloudState.CloudOnly}) THEN 1 ELSE 0 END),
                    SUM(CASE WHEN cloud_state IN ({(int)CloudState.Queued}, {(int)CloudState.Uploading}) THEN 1 ELSE 0 END),
                    SUM(CASE WHEN cloud_state = {(int)CloudState.CloudOnly} THEN 1 ELSE 0 END)
                FROM tracks WHERE folder = $folder
                """ + (ReachTerm("cloud_state") is { Length: > 0 } reach ? $" AND {reach}" : "");
            cmd.Parameters.AddWithValue("$folder", folder);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return AlbumCloudRollup.Empty;
            int total = (int)reader.GetInt64(0);
            if (total == 0)
                return AlbumCloudRollup.Empty;
            int inCloud = reader.IsDBNull(1) ? 0 : (int)reader.GetInt64(1);
            int inSet = reader.IsDBNull(2) ? 0 : (int)reader.GetInt64(2);
            int inFlight = reader.IsDBNull(3) ? 0 : (int)reader.GetInt64(3);
            int notHere = reader.IsDBNull(4) ? 0 : (int)reader.GetInt64(4);
            return new AlbumCloudRollup(total, inCloud, inSet, inFlight, notHere);
        }
    }

    /// <summary>
    /// The same rollup for a whole shelf of records, in one pass.
    ///
    /// <para><b>Why this exists.</b> <see cref="GetAlbumCloudRollup"/> is one query and one take of the
    /// reading gate. That is right for one record and wrong for a thousand. Every mark on screen is
    /// re-read whenever the cloud speaks or a lender's computer starts or stops answering. That would
    /// be a thousand round trips through the one lock that every read in the app uses, and a thousand
    /// chances for the wall's own query to queue behind one of them. On a borrowed shelf, a single such
    /// query held the reading gate for about 19 s while using only a few milliseconds of processor
    /// time, and every other read waited for all of it.</para>
    ///
    /// <para>The batches hold 900 folders because SQLite's parameter limit is 999. All the batches run
    /// inside ONE take of the gate. The aim is not fewer statements but fewer moments at which someone
    /// else can be made to wait. Folders that are not in the table are absent from the result and not
    /// present and empty, so a caller that asks about a record that has gone gets nothing back and
    /// must decide what to do.</para>
    /// </summary>
    public IReadOnlyDictionary<string, AlbumCloudRollup> GetAlbumCloudRollups(IReadOnlyList<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);

        // NOCASE, because the column is: a folder asked for in one case and stored in another is the
        // same record, and an ordinal dictionary would quietly lose its mark.
        var rollups = new Dictionary<string, AlbumCloudRollup>(StringComparer.OrdinalIgnoreCase);
        if (folders.Count == 0)
            return rollups;

        using (_readGate.Take())
        {
            for (var start = 0; start < folders.Count; start += 900)
            {
                var batch = Math.Min(900, folders.Count - start);
                using var cmd = _read.CreateCommand();
                var names = string.Join(",", Enumerable.Range(0, batch).Select(i => $"$f{i}"));
                cmd.CommandText =
                    $"""
                    SELECT
                        folder,
                        COUNT(*),
                        SUM(CASE WHEN cloud_state IN ({(int)CloudState.Synced}, {(int)CloudState.CloudOnly}) THEN 1 ELSE 0 END),
                        SUM(CASE WHEN cloud_selected = 1 OR cloud_state IN ({(int)CloudState.Synced}, {(int)CloudState.CloudOnly}) THEN 1 ELSE 0 END),
                        SUM(CASE WHEN cloud_state IN ({(int)CloudState.Queued}, {(int)CloudState.Uploading}) THEN 1 ELSE 0 END),
                        SUM(CASE WHEN cloud_state = {(int)CloudState.CloudOnly} THEN 1 ELSE 0 END)
                    FROM tracks WHERE folder IN ({names})
                    """
                    + (ReachTerm("cloud_state") is { Length: > 0 } reach ? $" AND {reach}" : "")
                    + " GROUP BY folder";
                for (var i = 0; i < batch; i++)
                    cmd.Parameters.AddWithValue($"$f{i}", folders[start + i]);

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    var folder = reader.GetString(0);
                    var total = (int)reader.GetInt64(1);
                    rollups[folder] = total == 0
                        ? AlbumCloudRollup.Empty
                        : new AlbumCloudRollup(
                            total,
                            reader.IsDBNull(2) ? 0 : (int)reader.GetInt64(2),
                            reader.IsDBNull(3) ? 0 : (int)reader.GetInt64(3),
                            reader.IsDBNull(4) ? 0 : (int)reader.GetInt64(4),
                            reader.IsDBNull(5) ? 0 : (int)reader.GetInt64(5));
                }
            }
        }

        return rollups;
    }

    /// <summary>
    /// Where a named set of rows keeps its copies: whether any of them still has bytes on this disk,
    /// and whether the cloud is holding anything for any of them.
    ///
    /// <para>The Delete submenu uses this to grey out its choices: see <see cref="DeleteOffer"/>. It
    /// is one query and not a row read per path, because the whole collection can be selected when the
    /// library table is right-clicked, and a menu that takes a second to open looks broken.</para>
    /// </summary>
    public (bool AnyLocalFile, bool AnyInCloud) CloudSpread(IReadOnlyList<string> relativePaths)
    {
        ArgumentNullException.ThrowIfNull(relativePaths);
        if (relativePaths.Count == 0)
            return (false, false);

        var anyLocal = false;
        var anyCloud = false;
        using (_readGate.Take())
        {
            // SQLite's parameter limit is 999 by default, so the paths go in in batches. Both answers
            // are OR-ed across the batches, and a batch is skipped once both answers are settled.
            for (var start = 0; start < relativePaths.Count && !(anyLocal && anyCloud); start += 900)
            {
                var batch = Math.Min(900, relativePaths.Count - start);
                using var cmd = _read.CreateCommand();
                var names = string.Join(",", Enumerable.Range(0, batch).Select(i => $"$p{i}"));
                cmd.CommandText =
                    $"""
                    SELECT
                        MAX(CASE WHEN cloud_state <> {(int)CloudState.CloudOnly} THEN 1 ELSE 0 END),
                        MAX(CASE WHEN cloud_state IN (
                                {(int)CloudState.Queued}, {(int)CloudState.Uploading},
                                {(int)CloudState.Synced}, {(int)CloudState.CloudOnly}) THEN 1 ELSE 0 END)
                    FROM tracks WHERE relative_path IN ({names})
                    """;
                for (var i = 0; i < batch; i++)
                    cmd.Parameters.AddWithValue($"$p{i}", relativePaths[start + i]);

                using var reader = cmd.ExecuteReader();
                if (!reader.Read())
                    continue;
                anyLocal |= !reader.IsDBNull(0) && reader.GetInt64(0) == 1;
                anyCloud |= !reader.IsDBNull(1) && reader.GetInt64(1) == 1;
            }
        }
        return (anyLocal, anyCloud);
    }
}
