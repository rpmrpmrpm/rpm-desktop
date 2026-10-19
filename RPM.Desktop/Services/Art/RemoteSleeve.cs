using RPM.Core.Library;
using RPM.Desktop.Services.Cloud;
using RPM.Desktop.Services.Sharing;

namespace RPM.Desktop.Services.Art;

/// <summary>
/// The sleeve of a record whose files are not on this disk: a borrowed one, or one this machine has
/// offloaded.
///
/// <para>
/// Two places can answer. Both are asked here, in order of cost. The computer that holds the record comes
/// first. It has the picture its owner is looking at, and it is on this network. What it sends is kept per
/// album, so a wall scrolling past forty borrowed records asks once for each. Then comes this account's own
/// cloud. It keeps one cover per album, under a key made from the artist and the album. That key is the
/// same on any machine, so the cloud can answer for a friend's record that the user happens to own too.
/// </para>
///
/// <para>The order is set in one place, here. Four kinds of surface draw a record: the wall, the album
/// page, the track pane, and the art display behind the window. Each of them must use this method. A
/// surface that skips the computer that holds the record shows an empty square for a borrowed album,
/// even when its sleeve is cached on this disk.</para>
/// </summary>
public static class RemoteSleeve
{
    /// <param name="row">The library row for the record. Which computer holds it, and the artist and album
    /// its cover is keyed under, both come from this row.</param>
    /// <param name="artist">Used when the row has no artist. This is the tags on the deck, for a track
    /// being played rather than one being drawn.</param>
    /// <param name="album">The same, for the album.</param>
    public static async Task<byte[]?> ForAsync(
        LibraryTrack row, string? artist = null, string? album = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        var byArtist = string.IsNullOrWhiteSpace(row.Artist) ? artist : row.Artist;
        var byAlbum = string.IsNullOrWhiteSpace(row.Album) ? album : row.Album;

        var sharing = App.Services.GetService<SharingService>();

        if (sharing is not null
            && row is { PeerDeviceId: { Length: > 0 } peer, RpmId: { Length: > 0 } rpmId }
            && await sharing.CoverAsync(peer, rpmId, row.AlbumArtist, byArtist, byAlbum, ct)
               is { Length: > 0 } borrowed)
            return borrowed;

        // The owner's storage holds the sleeve. A friend's offloaded record has its cover there, and
        // nowhere this machine can reach. The cloud grant is the same as the music grant, so a shelf that
        // plays away from the network also draws away from it.
        var owner = sharing?.CloudOwner(row.PeerDeviceId);

        // A track id names a row in the storage of the account that uploaded it. A guest's track id is not
        // valid here, so use PeerTrust.OwnCloudId instead. Passing a guest's id makes the sidecar fallback
        // ask for one per borrowed album, and each of those requests returns 404.
        var cloudId = sharing is null ? row.RpmId : sharing.OwnCloudId(row.RpmId, row.PeerDeviceId);

        return await App.Services.GetRequiredService<CloudArtService>()
            .CoverAsync(row.AlbumArtist, byArtist, byAlbum, cloudId, ct, owner);
    }

    /// <summary>
    /// The album's own picture of its RECORD, disc.png, for a record whose files are not on this disk. It is
    /// null when there is none, which is the case for most records.
    ///
    /// <para>One place can answer, not two. Disc art is a folder file on the machine that holds the record.
    /// The cloud keeps sleeves, not records. So a borrowed album shows its disc while that computer is on the
    /// network. When it is not, the album shows the app's own pressing. This is the same disc a record of
    /// this collection gets when its folder holds nothing.</para>
    ///
    /// <para>This is its own method, not a flag on <see cref="ForAsync"/>, because the two are wanted at
    /// different times. The sleeve is what a record is on screen. The disc is requested a moment later, by
    /// the one or two surfaces that draw a turning record.</para>
    /// </summary>
    /// <param name="row">The library row for the record. It gives the computer that holds the record, and
    /// the names its picture is cached under.</param>
    public static async Task<byte[]?> DiscForAsync(LibraryTrack row, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (App.Services.GetService<SharingService>() is not { } sharing
            || row is not { PeerDeviceId: { Length: > 0 } peer, RpmId: { Length: > 0 } rpmId })
            return null;

        return await sharing.DiscAsync(peer, rpmId, row.AlbumArtist, row.Artist, row.Album, ct);
    }
}
