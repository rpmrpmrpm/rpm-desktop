using RPM.Core.Analysis;
using RPM.Core.Models;
using RPM.Core.Import;

namespace RPM.Core.Tests;

public class MultiDiscNamingTests
{
    private static TrackInfo Track(uint disc, uint track, string title, uint trackCount = 3)
        => new()
        {
            FilePath = $"/src/CD{disc}/{track:00}.flac",
            Extension = ".flac",
            Artist = "X",
            AlbumArtist = "X",
            Album = "Set",
            Title = title,
            TrackNumber = track,
            TrackCount = trackCount,
            DiscNumber = disc,
            DiscCount = 2,
        };

    [Fact]
    public void Merged_discs_are_named_by_the_albums_continuing_count()
    {
        List<TrackInfo> album = [Track(1, 1, "A"), Track(1, 2, "B"), Track(1, 3, "C"),
                                 Track(2, 1, "D"), Track(2, 2, "E")];

        var plan = RenamePlanner.Plan(album, NamingTemplate.ArtistAlbumNumberedTitle, "/lib",
            albumFolderOverride: null,
            namingView: t => MultiDiscImport.NamedInMergedAlbum(t, album, NamingTemplate.ArtistAlbumNumberedTitle));

        Assert.Equal(
            ["X/Set/01 - A.flac", "X/Set/02 - B.flac", "X/Set/03 - C.flac",
             "X/Set/04 - D.flac", "X/Set/05 - E.flac"],
            plan.Select(p => p.ProposedRelativePath));

        // The NAME counts across the set; the file's tags are not changed, and the library reads its
        // disc sections from them.
        Assert.Equal([1u, 2u, 3u, 1u, 2u], plan.Select(p => p.Track.TrackNumber));
        Assert.Equal([1u, 1u, 1u, 2u, 2u], plan.Select(p => p.Track.DiscNumber));
    }

    [Fact]
    public void Two_discs_sharing_a_track_title_no_longer_collide()
    {
        // Without this, both discs start with "Intro", both are named "01 - Intro", and the second
        // is filed as "01 - Intro (2).flac" in the same folder.
        List<TrackInfo> album = [Track(1, 1, "Intro", trackCount: 1), Track(2, 1, "Intro", trackCount: 1)];

        var plan = RenamePlanner.Plan(album, NamingTemplate.ArtistAlbumNumberedTitle, "/lib",
            albumFolderOverride: null,
            namingView: t => MultiDiscImport.NamedInMergedAlbum(t, album, NamingTemplate.ArtistAlbumNumberedTitle));

        Assert.Equal(["X/Set/01 - Intro.flac", "X/Set/02 - Intro.flac"],
                     plan.Select(p => p.ProposedRelativePath));
    }

    [Fact]
    public void A_template_that_names_the_disc_is_left_to_do_it()
    {
        var template = new NamingTemplate("disc", "{albumartist}/{album}/{disc}-{track:00} - {title}");
        List<TrackInfo> album = [Track(1, 1, "A"), Track(2, 1, "D")];

        var plan = RenamePlanner.Plan(album, template, "/lib",
            albumFolderOverride: null,
            namingView: t => MultiDiscImport.NamedInMergedAlbum(t, album, template));

        Assert.Equal(["X/Set/1-01 - A.flac", "X/Set/2-01 - D.flac"],
                     plan.Select(p => p.ProposedRelativePath));
    }
}
