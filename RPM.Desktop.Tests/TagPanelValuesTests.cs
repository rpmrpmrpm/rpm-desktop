using RPM.Core.Models;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Presentation.Import;
using RPM.Desktop.Presentation.Main.LeftPane.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// What a set of ticked rows says about one field: the same thing, or several things.
///
/// <para>This is the panel's whole reading of a selection. It follows the mass editor's convention rather
/// than inventing a second one. A field the rows disagree on starts empty and offers what they each say.
/// There is one way in RPM to show "these rows say different things".</para>
/// </summary>
public class TagPanelValuesTests
{
    private static TrackRow Row(string genre) =>
        new(new TrackInfo { FilePath = "/m/x.flac", Extension = ".flac" }) { Genre = genre };

    // The registry, not a second list of fields: TagFields.All is where a field is declared once.
    private static TagField Genre => TagFields.All.First(f => f.Kind == TagFieldKind.Genre);

    [Fact]
    public void One_row_agrees_with_itself()
    {
        var got = TagPanelValues.Of([Row("New Age")], Genre);

        Assert.True(got.AllSame);
        Assert.Equal("New Age", got.CommonValue);
    }

    [Fact]
    public void Rows_that_agree_report_the_shared_value()
    {
        var got = TagPanelValues.Of([Row("New Age"), Row("New Age")], Genre);

        Assert.True(got.AllSame);
        Assert.Equal("New Age", got.CommonValue);
    }

    [Fact]
    public void Rows_that_disagree_offer_every_value_once()
    {
        var got = TagPanelValues.Of([Row("New Age"), Row("Pop"), Row("New Age")], Genre);

        Assert.False(got.AllSame);
        Assert.Equal("", got.CommonValue);
        Assert.Equal(["New Age", "Pop"], got.Distinct);
    }

    [Fact]
    public void An_empty_selection_agrees_on_nothing()
    {
        var got = TagPanelValues.Of([], Genre);

        Assert.True(got.AllSame);
        Assert.Equal("", got.CommonValue);
    }

    [Fact]
    public void An_empty_value_is_a_value_the_rows_can_disagree_with()
    {
        var got = TagPanelValues.Of([Row(""), Row("Pop")], Genre);

        Assert.False(got.AllSame);
        Assert.Equal(2, got.Distinct.Count);
    }

    // ── the picture the rows carry ──

    /// <summary>
    /// One cover chosen for a record reaches every track as its own array. The rows are made from the store
    /// one at a time, and the picture is loaded per row. If the comparison is by reference, a record
    /// never agrees with itself. That is what emptied the tag panel's cover well for every selection of
    /// more than one file.
    /// </summary>
    [Fact]
    public void The_same_cover_on_every_row_is_one_cover_however_many_arrays_it_arrived_in()
    {
        byte[] Cover() => [1, 2, 3, 4];

        Assert.Equal(Cover(), TagPanelValues.AgreedPicture([Cover(), Cover(), Cover()]));
    }

    [Fact]
    public void One_row_carrying_a_different_cover_is_a_disagreement()
        => Assert.Null(TagPanelValues.AgreedPicture([[1, 2, 3], [1, 2, 3], [9, 9, 9]]));

    [Fact]
    public void A_row_with_no_cover_at_all_is_a_disagreement()
        => Assert.Null(TagPanelValues.AgreedPicture([[1, 2, 3], null]));

    [Fact]
    public void Nothing_selected_and_nothing_carried_agree_on_nothing()
    {
        Assert.Null(TagPanelValues.AgreedPicture([]));
        Assert.Null(TagPanelValues.AgreedPicture([null, null]));
        Assert.Null(TagPanelValues.AgreedPicture([[]]));
    }
}
