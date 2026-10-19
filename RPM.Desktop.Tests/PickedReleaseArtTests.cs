using RPM.Core.Metadata.Providers;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Presentation.Dialogs.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// What the release lookup hands back about a pressing's picture.
///
/// <para>The fault this guards against: half the rows in a result list arrive with no picture address,
/// because the search does not give one. The list then resolves one per row so it has a thumbnail to
/// draw. That address must be kept. Later, the sleeve fetch asks the archives again at full size, and
/// falls back to the search result's own address when they do not answer twice. If that address was
/// dropped, the fallback finds nothing. Picking a row before its thumbnail has loaded would then file
/// the record with no cover at all.</para>
/// </summary>
public class PickedReleaseArtTests
{
    private static ReleaseSearchResult Result(string? coverUrl) =>
        new(new ReleaseRef("discogs", "1"), "The Record", "The Band", 1994,
            Date: null, Country: "US", Format: "CD", TrackCount: 11, CoverUrl: coverUrl);

    [Fact]
    public void A_release_the_search_gave_no_picture_for_carries_the_one_the_list_found()
    {
        var row = new ReleaseLookupRow { Release = Result(null), CoverUrl = "https://art/full.jpg" };

        Assert.Equal("https://art/full.jpg", ReleaseLookupDialog.Picked(row).CoverUrl);
    }

    /// <summary>The search's own address wins where there is one: it is what the release actually came
    /// with, and the list's is the same thing at thumbnail size.</summary>
    [Fact]
    public void A_release_that_came_with_a_picture_keeps_the_one_it_came_with()
    {
        var row = new ReleaseLookupRow
        {
            Release = Result("https://search/thumb.jpg"),
            CoverUrl = "https://art/other.jpg",
        };

        Assert.Equal("https://search/thumb.jpg", ReleaseLookupDialog.Picked(row).CoverUrl);
    }

    /// <summary>A row picked before its own lane got anywhere has nothing to add, and must not replace
    /// what it has with nothing.</summary>
    [Fact]
    public void A_release_with_no_picture_anywhere_is_handed_back_as_it_is()
    {
        var row = new ReleaseLookupRow { Release = Result(null) };

        Assert.Null(ReleaseLookupDialog.Picked(row).CoverUrl);
        Assert.Same(row.Release, ReleaseLookupDialog.Picked(row));
    }
}
