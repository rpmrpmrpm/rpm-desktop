using RPM.Desktop.Presentation.Common;
using Windows.UI;

namespace RPM.Desktop.Tests;

/// <summary>
/// The join between the left pane and the album page: the pane's colour is carried into the page's
/// facing corner. Three things must all be true before this means anything.
///
/// <para>Every fact below states all three, so nothing here depends on the order the tests run in. The
/// thing under test is one static. A static suits a fact about the window, but it must be reset between
/// tests, so one test does not leave it half-written for the next.</para>
/// </summary>
public class PaneCornerTests
{
    private static readonly Color Lit = Color.FromArgb(0xFF, 0x2E, 0x4A, 0x6B);

    [Fact]
    public void The_page_showing_the_pane_s_own_record_is_joined_to_it()
    {
        PaneCorner.Wearing(Lit, "Cher/Half Breed");
        PaneCorner.Showing(true);

        Assert.Equal(Lit, PaneCorner.For("Cher/Half Breed"));
    }

    /// <summary>The join says "this album is the one playing". On any other album that is a sentence
    /// about the wrong record.</summary>
    [Fact]
    public void A_page_showing_another_album_is_not()
    {
        PaneCorner.Wearing(Lit, "Cher/Half Breed");
        PaneCorner.Showing(true);

        Assert.Null(PaneCorner.For("James Blunt/Back to Bedlam"));
    }

    [Fact]
    public void A_pane_that_is_not_on_screen_lends_nothing()
    {
        PaneCorner.Wearing(Lit, "Cher/Half Breed");
        PaneCorner.Showing(false);

        Assert.Null(PaneCorner.For("Cher/Half Breed"));
    }

    /// <summary>A pane standing on plain glass has no colour to lend, and a record the collection has
    /// never heard of has no folder to be matched against.</summary>
    [Fact]
    public void A_pane_with_no_colour_and_a_record_with_no_folder_both_leave_it_alone()
    {
        PaneCorner.Wearing(null, "Cher/Half Breed");
        PaneCorner.Showing(true);
        Assert.Null(PaneCorner.For("Cher/Half Breed"));

        PaneCorner.Wearing(Lit, "");
        Assert.Null(PaneCorner.For(""));
    }

    [Fact]
    public void A_write_that_changes_nothing_tells_nobody()
    {
        PaneCorner.Wearing(Lit, "Cher/Half Breed");
        PaneCorner.Showing(true);

        var told = 0;
        void Count() => told++;

        PaneCorner.Changed += Count;
        try
        {
            PaneCorner.Wearing(Lit, "Cher/Half Breed");
            PaneCorner.Showing(true);
            Assert.Equal(0, told);

            PaneCorner.Showing(false);
            Assert.Equal(1, told);
        }
        finally
        {
            PaneCorner.Changed -= Count;
        }
    }
}
