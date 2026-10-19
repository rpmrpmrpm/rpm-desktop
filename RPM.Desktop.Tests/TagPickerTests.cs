using RPM.Desktop.Presentation.Dialogs;

namespace RPM.Desktop.Tests;

/// <summary>
/// Unsetting a tag.
///
/// <para>Every editing window could set a value, and none of them could take one away. An empty box means
/// "leave this alone", which it has to, or applying a window over forty tracks would wipe every field
/// nobody touched. So emptying a field is said with a Clear row at the head of the list. That row shifts
/// every index after it, and these tests check the arithmetic of that shift.</para>
/// </summary>
public class TagPickerTests
{
    private static readonly IReadOnlyList<TagFieldOption> Options =
    [
        new("yes", "Details_Yes"),
        new("no", "Details_No"),
    ];

    [Fact]
    public void The_first_entry_is_not_one_of_the_answers()
    {
        Assert.Equal("", TagPicker.ValueAt(Options, 0));
        Assert.Equal("yes", TagPicker.ValueAt(Options, 1));
        Assert.Equal("no", TagPicker.ValueAt(Options, 2));
    }

    [Fact]
    public void Nothing_chosen_is_no_value_rather_than_the_first_one()
    {
        Assert.Equal("", TagPicker.ValueAt(Options, -1));
    }

    [Fact]
    public void An_entry_that_is_not_there_answers_nothing()
    {
        Assert.Equal("", TagPicker.ValueAt(Options, 9));
    }

    [Fact]
    public void A_value_sits_one_on_from_where_it_does_in_the_options()
    {
        Assert.Equal(1, TagPicker.IndexOf(Options, "yes"));
        Assert.Equal(2, TagPicker.IndexOf(Options, "no"));
    }

    [Fact]
    public void Emptiness_is_the_clear_row()
    {
        Assert.Equal(0, TagPicker.IndexOf(Options, ""));
        Assert.Equal(0, TagPicker.IndexOf(Options, null));
    }

    [Fact]
    public void A_token_the_list_never_heard_of_selects_nothing()
    {
        Assert.Equal(-1, TagPicker.IndexOf(Options, "maybe"));
    }

    [Fact]
    public void A_value_survives_the_round_trip()
    {
        foreach (var option in Options)
            Assert.Equal(option.Value, TagPicker.ValueAt(Options, TagPicker.IndexOf(Options, option.Value)));
    }

    [Fact]
    public void The_clear_rows_own_label_is_never_a_value()
    {
        // The brackets are what say "this is not a value": no file holds a genre called "<Clear>".
        // The label is still passed in rather than assumed, because in another language it reads
        // "<Effacer>".
        Assert.Equal("", TagPicker.ValueOfText("<Clear>", "<Clear>"));
        Assert.Equal("", TagPicker.ValueOfText("<Effacer>", "<Effacer>"));
        Assert.Equal("Clear", TagPicker.ValueOfText("Clear", "<Clear>"));
        Assert.Equal("Clearwater Revival", TagPicker.ValueOfText("Clearwater Revival", "<Clear>"));
    }

    [Fact]
    public void A_typed_value_reads_back_as_itself()
    {
        Assert.Equal("Nederpop", TagPicker.ValueOfText("Nederpop", "<Clear>"));
        Assert.Equal("", TagPicker.ValueOfText(null, "<Clear>"));
    }

    [Fact]
    public void Choosing_clear_is_an_answer_that_stays_on_the_line()
    {
        // The box keeps reading "<Clear>", which shows it is chosen, while what it will WRITE is empty.
        // Emptying the box instead would show nothing. On a field the tracks disagree about, that would
        // show the "several values" placeholder. So the one gesture that means "empty this" would look
        // like not having answered.
        Assert.Equal("", TagPicker.ValueOfText("<Clear>", "<Clear>"));
        Assert.Equal(0, TagPicker.IndexOf(Options, TagPicker.ValueOfText("<Clear>", "<Clear>")));
    }
}
