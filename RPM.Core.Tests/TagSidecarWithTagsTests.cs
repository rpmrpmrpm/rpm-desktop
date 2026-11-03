using RPM.Core.Cloud;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>
/// An editor's whole tag set written into the sidecar of a track whose file is in the cloud.
/// </summary>
public class TagSidecarWithTagsTests
{
    private static readonly TagSidecar Stored = new()
    {
        RelativePath = "Wolfgang Amadeus Mozart/Requiem/01 - Introitus.flac",
        Extension = ".flac",
        RpmId = "rpm-1",
        Title = "Introitus",
        Album = "Requiem",
        Publisher = "Philips",
        Conductor = "Peter Schreier",
        Comment = "a comment",
        Art = [1, 2, 3],
        ArtMime = "image/jpeg",
        Favorite = true,
        DurationSeconds = 250,
    };

    [Fact]
    public void Every_tag_is_written_and_what_is_not_a_tag_stays()
    {
        var edited = Stored.WithTags(new TrackInfo
        {
            FilePath = "x.flac",
            Extension = ".flac",
            Title = "Introitus: Requiem aeternam",
            Album = "Requiem",
            Publisher = "Philips Classics",
            Conductor = "Peter Schreier",
            Ensemble = "Staatskapelle Dresden",
            Soloists = "Margaret Price (soprano)",
            Comment = null,   // cleared in the editor
        });

        Assert.Equal("Introitus: Requiem aeternam", edited.Title);
        Assert.Equal("Philips Classics", edited.Publisher);
        Assert.Equal("Staatskapelle Dresden", edited.Ensemble);
        Assert.Equal("Margaret Price (soprano)", edited.Soloists);
        Assert.Null(edited.Comment);

        Assert.Equal(Stored.RelativePath, edited.RelativePath);
        Assert.Equal("rpm-1", edited.RpmId);
        Assert.True(edited.Favorite);
        Assert.Equal(250, edited.DurationSeconds);
        Assert.Equal([1, 2, 3], edited.Art);
    }

    [Fact]
    public void A_picture_changes_only_where_the_edit_set_or_removed_one()
    {
        Assert.Equal([9], Stored.WithTags(new TrackInfo { FilePath = "x.flac", Extension = ".flac", PendingArt = [9], PendingArtMime = "image/png" }).Art);
        Assert.Null(Stored.WithTags(new TrackInfo { FilePath = "x.flac", Extension = ".flac", PendingArt = [] }).Art);
        Assert.Equal([1, 2, 3], Stored.WithTags(new TrackInfo { FilePath = "x.flac", Extension = ".flac" }).Art);
    }
}
