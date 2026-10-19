using System.Text.Json;
using RPM.Core.Library;
using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>The seam every other test in this area skips: the catalogue actually going over the wire.</summary>
public class PeerPlaylistWireTests
{
    [Fact]
    public void A_catalog_carrying_playlists_survives_the_wire()
    {
        var tree = new PlaylistTreeRow[]
        {
            new(1, null, PlaylistNodeKind.Folder, "Driving", 0, [], new PlaylistCover.Automatic()),
            new(2, 1, PlaylistNodeKind.Playlist, "Road trips", 0,
                [new PlaylistTreeEntry(PlaylistItemKind.Song, "gid-1", "Band/Record/01 One.flac", null)],
                new PlaylistCover.Automatic()),
            new(3, null, PlaylistNodeKind.Playlist, "Everything", 1,
                [new PlaylistTreeEntry(PlaylistItemKind.Playlist, null, null, 2)],
                new PlaylistCover.Automatic()),
        };

        var catalog = PeerCatalogBuilder.Catalog(
            [], "Their laptop", [], PeerCatalogBuilder.Playlists(tree, new Dictionary<long, string>()), []);

        var json = JsonSerializer.Serialize(catalog, SharingProtocol.Json);
        var back = JsonSerializer.Deserialize<PeerCatalog>(json, SharingProtocol.Json);

        Assert.NotNull(back);
        Assert.Equal(3, back!.Playlists.Count);

        var road = back.Playlists.Single(p => p.Name == "Road trips");
        Assert.Equal(1, road.ParentId);
        Assert.False(road.IsFolder);
        Assert.Equal("gid-1", road.Items.Single().RpmId);

        Assert.Equal(2, back.Playlists.Single(p => p.Name == "Everything").Items.Single().Ref);
        Assert.True(back.Playlists.Single(p => p.Name == "Driving").IsFolder);
    }

    /// <summary>
    /// A picture the owner chose crosses as a stamp on its row, and arrives as the cover the borrower
    /// asks for it by: the OWNER's node id, which the mirror does not keep anywhere else, and the stamp
    /// the copy is filed under. A row with no stamp arrives automatic, which is what an older build's
    /// listing reads as.
    /// </summary>
    [Fact]
    public void A_chosen_picture_crosses_as_the_owners_node_and_its_stamp()
    {
        var tree = new PlaylistTreeRow[]
        {
            new(7, null, PlaylistNodeKind.Playlist, "Picked", 0, [], new PlaylistCover.Picture()),
            new(8, null, PlaylistNodeKind.Playlist, "Plain", 1, [], new PlaylistCover.Automatic()),
        };

        var sent = PeerCatalogBuilder.Playlists(tree, new Dictionary<long, string> { [7] = "ABC123" });
        var json = JsonSerializer.Serialize(
            PeerCatalogBuilder.Catalog([], "Their laptop", [], sent, []), SharingProtocol.Json);
        var arrived = PeerCatalogBuilder.To(JsonSerializer.Deserialize<PeerCatalog>(json, SharingProtocol.Json)!.Playlists);

        Assert.Equal(new PlaylistCover.ChosenByOwner(7, "ABC123"), arrived.Single(r => r.Name == "Picked").Cover);
        Assert.IsType<PlaylistCover.Automatic>(arrived.Single(r => r.Name == "Plain").Cover);
    }

    /// <summary>A changed picture changes the listing's version, so a borrower holding the old one is
    /// sent the new stamp rather than told nothing moved.</summary>
    [Fact]
    public void A_changed_picture_changes_the_version()
    {
        PlaylistTreeRow[] tree = [new(7, null, PlaylistNodeKind.Playlist, "Picked", 0, [], new PlaylistCover.Picture())];

        Assert.NotEqual(
            PeerCatalog.VersionOf([], [], PeerCatalogBuilder.Playlists(tree, new Dictionary<long, string> { [7] = "A" }), []),
            PeerCatalog.VersionOf([], [], PeerCatalogBuilder.Playlists(tree, new Dictionary<long, string> { [7] = "B" }), []));
    }

    /// <summary>The explicit flag survives the wire, and a listing from a build that never sent it
    /// reads as no flag rather than failing.</summary>
    [Fact]
    public void The_explicit_flag_survives_the_wire_and_an_older_listing_reads_as_none()
    {
        var track = new PeerTrack
        {
            RpmId = "gid-1", RelativePath = "Band/Record/01 One.flac",
            Advisory = (int)Models.ContentAdvisory.Explicit,
        };

        var json = JsonSerializer.Serialize(track, SharingProtocol.Json);
        Assert.Equal(track.Advisory, JsonSerializer.Deserialize<PeerTrack>(json, SharingProtocol.Json)!.Advisory);

        var older = JsonSerializer.Deserialize<PeerTrack>(
            """{"rpmId":"gid-1","path":"Band/Record/01 One.flac"}""", SharingProtocol.Json);
        Assert.Equal(0, older!.Advisory);
    }
}
