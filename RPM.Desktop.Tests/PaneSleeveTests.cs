using RPM.Desktop.Presentation.Main.LeftPane;

namespace RPM.Desktop.Tests;

/// <summary>
/// <see cref="PaneSleeve"/>: whether the track pane's cover square already holds the next track's sleeve.
///
/// <para>The fault it prevents: a track of album B starts while the square still shows album A. Its
/// sleeve read goes to the cloud, which is slow. A second track of B starts before that read answers, so
/// the first read is dropped. If "the square is B's" was noted when the first read started, the second
/// track keeps the square as it is, and album A's cover stays up for the whole of album B.</para>
/// </summary>
public class PaneSleeveTests
{
    private const string AlbumA = "James Blunt/Back to Bedlam";
    private const string AlbumB = "Counting Crows/Across A Wire (Live In New York)";

    [Fact]
    public void An_empty_square_holds_no_record()
    {
        var sleeve = new PaneSleeve();

        Assert.False(sleeve.Holds(AlbumA));
    }

    [Fact]
    public void A_painted_settled_sleeve_is_kept_for_the_same_record_only()
    {
        var sleeve = new PaneSleeve();

        sleeve.Painted(AlbumA, settled: true);

        Assert.True(sleeve.Holds(AlbumA));
        Assert.False(sleeve.Holds(AlbumB));
    }

    [Fact]
    public void A_read_that_never_reached_the_square_does_not_make_it_the_next_records()
    {
        var sleeve = new PaneSleeve();
        sleeve.Painted(AlbumA, settled: true);

        // The first track of B asks for its sleeve, and is replaced by a second track of B before the
        // read answers. Nothing is painted in between, so nothing is written.

        Assert.False(sleeve.Holds(AlbumB));
        Assert.True(sleeve.Holds(AlbumA));
    }

    [Fact]
    public void A_placeholder_from_a_failed_read_is_not_kept()
    {
        var sleeve = new PaneSleeve();

        sleeve.Painted(AlbumB, settled: false);

        Assert.False(sleeve.Holds(AlbumB));
    }

    [Fact]
    public void A_failed_read_replaces_the_previous_records_sleeve()
    {
        var sleeve = new PaneSleeve();
        sleeve.Painted(AlbumA, settled: true);

        sleeve.Painted(AlbumB, settled: false);

        Assert.False(sleeve.Holds(AlbumA));
        Assert.False(sleeve.Holds(AlbumB));
    }

    [Fact]
    public void A_file_outside_any_collection_never_keeps_the_sleeve()
    {
        var sleeve = new PaneSleeve();

        sleeve.Painted("", settled: true);

        Assert.False(sleeve.Holds(""));
    }

    [Fact]
    public void An_emptied_pane_asks_again_for_the_same_record()
    {
        var sleeve = new PaneSleeve();
        sleeve.Painted(AlbumA, settled: true);

        sleeve.Forget();

        Assert.False(sleeve.Holds(AlbumA));
    }
}
