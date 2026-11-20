using System.Security.Cryptography;
using System.Text;

namespace RPM.Core.Library;

/// <summary>An album folder and the catalogue releases it is linked to, as it travels to the store.</summary>
/// <param name="Folder">The album's folder, as the catalogue rows in the cloud name it.</param>
/// <param name="Provider">The catalogue the pressing is linked through, or null where only the release group is
/// known.</param>
/// <param name="ReleaseId">That catalogue's id for the pressing.</param>
/// <param name="ReleaseGroupId">The release group the album belongs to, or null.</param>
public sealed record CatalogueAlbum(string Folder, string? Provider, string? ReleaseId, string? ReleaseGroupId);

/// <summary>What one catalogue said about one release, in its own words, as it travels. See
/// <see cref="ReleaseMeta"/>, which keeps the same words and RPM's reading of them.</summary>
/// <param name="Votes">How many people voted for each of <paramref name="Genres"/>, or null.</param>
public sealed record CatalogueRelease(
    string Provider, string ReleaseId, IReadOnlyList<string> Genres, IReadOnlyList<string> Styles, IReadOnlyList<int>? Votes);

/// <summary>
/// What the catalogues said about the albums of one library, as it travels to the store
/// (<c>cloud/album-catalogue</c>): which release each album is linked to, and what each of those releases was
/// filed under.
///
/// <para><b>The catalogues' own words, not RPM's reading of them.</b> The library that takes a copy in maps the words
/// itself (<see cref="LibraryDatabase.SetReleaseMeta"/>), with the same code, so an album reads the same genre and
/// styles there (<see cref="LibraryDatabase.CatalogueFactsByFolder"/>).</para>
/// </summary>
public sealed record AlbumCatalogue(IReadOnlyList<CatalogueAlbum> Albums, IReadOnlyList<CatalogueRelease> Releases)
{
    public static readonly AlbumCatalogue None = new([], []);

    /// <summary>The most albums one copy carries. Far more than most collections hold, and few enough that the copy
    /// stays a few megabytes. The store refuses a copy with more.</summary>
    public const int MostAlbums = 10_000;

    /// <summary>The most releases one copy carries: one for each album's pressing and one for its group.</summary>
    public const int MostReleases = 2 * MostAlbums;

    /// <summary>A short hash of the contents, in a fixed order, so a sender can tell whether anything changed since
    /// its last send.</summary>
    public string Fingerprint
    {
        get
        {
            var text = new StringBuilder();
            foreach (var a in Albums.OrderBy(a => a.Folder, StringComparer.Ordinal))
                text.Append('a').Append(a.Folder).Append('|').Append(a.Provider).Append('|').Append(a.ReleaseId)
                    .Append('|').Append(a.ReleaseGroupId).Append('\n');
            foreach (var r in Releases.OrderBy(r => r.Provider, StringComparer.Ordinal).ThenBy(r => r.ReleaseId, StringComparer.Ordinal))
                text.Append('r').Append(r.Provider).Append('|').Append(r.ReleaseId)
                    .Append('|').AppendJoin(';', r.Genres).Append('|').AppendJoin(';', r.Styles)
                    .Append('|').AppendJoin(';', r.Votes ?? []).Append('\n');
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..16];
        }
    }
}

/// <summary>
/// The catalogues' answers about the albums, taken out of a library for the store and put into the store's own
/// library.
///
/// <para>A computer is where albums are linked to catalogue releases and where the catalogues are asked about them,
/// partly on the listener's own Discogs account. The store cuts lists for the phones from a library of its own (see
/// the server's list builder). Without these answers it would place each record by its file tags alone, and its
/// lists would group and narrow records differently from the computer's.</para>
/// </summary>
public sealed partial class LibraryDatabase
{
    /// <summary>The fingerprint of the copy the store last took (<see cref="AlbumCatalogue.Fingerprint"/>).</summary>
    public const string AlbumCatalogueSentKey = "album_catalogue_sent";

    /// <summary>
    /// Every album whose release a catalogue has answered about, with the answers, at most
    /// <see cref="AlbumCatalogue.MostAlbums"/> of them.
    /// </summary>
    /// <remarks>Only albums with at least one track in the cloud: the store cuts its lists from the cloud's
    /// music, so an album only on this computer has no place there.</remarks>
    public AlbumCatalogue AlbumCatalogueToSend()
    {
        using (_readGate.Take())
        {
            using var cmd = _read.CreateCommand();
            cmd.CommandText =
                """
                SELECT a.folder,
                       d.provider, d.release_id, d.raw_genres, d.raw_styles, d.votes,
                       m.release_id, m.raw_genres, m.raw_styles, m.votes
                FROM album_meta a
                LEFT JOIN release_meta d
                       ON d.provider = a.release_provider AND d.release_id = a.release_id
                LEFT JOIN release_meta m
                       ON m.provider = $group AND m.release_id = a.release_group_id
                WHERE (d.provider IS NOT NULL OR m.provider IS NOT NULL)
                  AND a.folder <> ''
                  AND EXISTS (SELECT 1 FROM tracks t
                              WHERE t.folder = a.folder AND t.rpm_id IS NOT NULL AND t.rpm_id <> '')
                ORDER BY a.folder
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$group", ReleaseGroupProvider);
            cmd.Parameters.AddWithValue("$limit", AlbumCatalogue.MostAlbums);

            using var reader = cmd.ExecuteReader();
            var albums = new List<CatalogueAlbum>();
            var releases = new Dictionary<(string, string), CatalogueRelease>();
            while (reader.Read())
            {
                string? provider = null, releaseId = null, groupId = null;
                if (!reader.IsDBNull(1))
                {
                    provider = reader.GetString(1);
                    releaseId = reader.GetString(2);
                    releases.TryAdd((provider, releaseId), Release(provider, releaseId, reader, 3));
                }
                if (!reader.IsDBNull(6))
                {
                    groupId = reader.GetString(6);
                    releases.TryAdd((ReleaseGroupProvider, groupId), Release(ReleaseGroupProvider, groupId, reader, 7));
                }
                albums.Add(new CatalogueAlbum(reader.GetString(0), provider, releaseId, groupId));
            }

            return new AlbumCatalogue(albums, [.. releases.Values]);
        }

        static CatalogueRelease Release(string provider, string id, Microsoft.Data.Sqlite.SqliteDataReader r, int at) => new(
            provider, id,
            SplitStyles(r.IsDBNull(at) ? null : r.GetString(at)),
            SplitStyles(r.IsDBNull(at + 1) ? null : r.GetString(at + 1)),
            ParseVotes(r.IsDBNull(at + 2) ? null : r.GetString(at + 2)));
    }

    /// <summary>
    /// Takes in another library's copy (<see cref="AlbumCatalogueToSend"/>), filling only what this library is
    /// missing: a link where an album has none, and an answer where a release has none.
    /// </summary>
    /// <remarks>
    /// Nothing held here is replaced, so the first copy taken in decides an album that two copies link
    /// differently, and a library's own links and answers always stand. A folder named twice in one copy is taken
    /// the first time.
    /// </remarks>
    /// <returns>How many albums were linked.</returns>
    public int AcceptAlbumCatalogue(AlbumCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        foreach (var release in catalogue.Releases)
        {
            if (string.IsNullOrWhiteSpace(release.Provider) || string.IsNullOrWhiteSpace(release.ReleaseId))
                continue;
            if (ReadReleaseMeta(release.Provider, release.ReleaseId) is not null)
                continue;
            SetReleaseMeta(release.Provider, release.ReleaseId, release.Genres, release.Styles, release.Votes);
        }

        var held = GetAlbumMetaMany(catalogue.Albums.Select(a => a.Folder));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var linked = 0;
        foreach (var album in catalogue.Albums)
        {
            if (string.IsNullOrWhiteSpace(album.Folder) || !seen.Add(album.Folder))
                continue;

            var meta = held.GetValueOrDefault(album.Folder);
            var pressing = meta is not { IsLinkedToPressing: true }
                && !string.IsNullOrWhiteSpace(album.Provider) && !string.IsNullOrWhiteSpace(album.ReleaseId);
            var group = string.IsNullOrWhiteSpace(meta?.ReleaseGroupId) && !string.IsNullOrWhiteSpace(album.ReleaseGroupId);

            // Filed and not said: nobody linked the album here, so the row does not travel to this library's peers
            // as somebody's edit.
            if (pressing)
                UpsertAlbumMeta(
                    AlbumWrite.Filed, album.Folder,
                    "release_provider = $p, release_id = $v",
                    ("$p", album.Provider!), ("$v", album.ReleaseId!),
                    insertColumns: "release_provider, release_id", insertValues: "$p, $v");
            if (group)
                UpsertAlbumMeta(
                    AlbumWrite.Filed, album.Folder,
                    "release_group_id = $v", ("$v", album.ReleaseGroupId!),
                    insertColumns: "release_group_id", insertValues: "$v");
            if (pressing || group)
                linked++;
        }

        return linked;
    }
}
