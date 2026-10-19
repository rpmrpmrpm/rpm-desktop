using RPM.Core.Library;

namespace RPM.Core.Tests;

public class AlbumTitleTests
{
    [Fact]
    public void The_edition_comes_off_the_end_of_the_name()
    {
        var title = AlbumTitle.Split("Short N' Sweet (Deluxe)");

        Assert.Equal("Short N' Sweet", title.Name);
        Assert.Equal(["Deluxe"], title.Editions);
    }

    /// <summary>The page draws the halves, but everything it passes on (a review's heading) names the
    /// record as it was tagged. Joining the halves again would get the spacing wrong.</summary>
    [Fact]
    public void The_title_as_tagged_is_kept_whole()
    {
        Assert.Equal("Short N' Sweet (Deluxe)", AlbumTitle.Split("Short N' Sweet (Deluxe)").Full);
    }

    [Fact]
    public void A_record_with_no_brackets_states_no_edition()
    {
        var title = AlbumTitle.Split("Arrival");

        Assert.Equal("Arrival", title.Name);
        Assert.Empty(title.Editions);
    }

    [Fact]
    public void Square_brackets_are_brackets_too()
    {
        var title = AlbumTitle.Split("Kid A [Remastered]");

        Assert.Equal("Kid A", title.Name);
        Assert.Equal(["Remastered"], title.Editions);
    }

    /// <summary>Two of them, in reading order: taggers often add the edition and the pressing.</summary>
    [Fact]
    public void Every_trailing_group_comes_off()
    {
        var title = AlbumTitle.Split("Rumours (Deluxe Edition) [Remastered]");

        Assert.Equal("Rumours", title.Name);
        Assert.Equal(["Deluxe Edition", "Remastered"], title.Editions);
    }

    /// <summary>A bracket at the FRONT is part of the title. Oasis did not release a record called
    /// Morning Glory? in the What's the Story edition.</summary>
    [Fact]
    public void A_leading_group_is_the_name()
    {
        var title = AlbumTitle.Split("(What's the Story) Morning Glory?");

        Assert.Equal("(What's the Story) Morning Glory?", title.Name);
        Assert.Empty(title.Editions);
    }

    /// <summary>Youssou N'Dour's record is called The Guide (Wommat): the bracket is the Wolof half of
    /// its name, not a pressing. Moved onto a second line, it would rename the record on the one
    /// heading that has to get its name right. See TitleAsides, which is where the test lives.</summary>
    [Fact]
    public void A_bracket_that_is_not_an_edition_stays_in_the_name()
    {
        var title = AlbumTitle.Split("The Guide (Wommat)");

        Assert.Equal("The Guide (Wommat)", title.Name);
        Assert.Empty(title.Editions);
    }

    /// <summary>Sigur Rós's "( )". Removing it would leave the heading empty and write the record's
    /// whole name underneath it in grey.</summary>
    [Fact]
    public void A_title_that_is_nothing_but_brackets_keeps_them()
    {
        var title = AlbumTitle.Split("( )");

        Assert.Equal("( )", title.Name);
        Assert.Empty(title.Editions);
    }

    /// <summary>Whole, not split at the inner bracket and left with a stray one on the end.</summary>
    [Fact]
    public void A_group_with_brackets_inside_it_comes_off_whole()
    {
        var title = AlbumTitle.Split("Rumours (Deluxe (2013))");

        Assert.Equal("Rumours", title.Name);
        Assert.Equal(["Deluxe (2013)"], title.Editions);
    }

    /// <summary>A bracket that never closes is a bracket in the title, not an edition.</summary>
    [Fact]
    public void An_unclosed_bracket_is_left_alone()
    {
        var title = AlbumTitle.Split("Rumours (Deluxe");

        Assert.Equal("Rumours (Deluxe", title.Name);
        Assert.Empty(title.Editions);
    }

    /// <summary>An empty group is a tagging accident. Dropping it would delete characters the file
    /// carries, and showing it would draw a blank line.</summary>
    [Fact]
    public void An_empty_group_is_not_an_edition()
    {
        var title = AlbumTitle.Split("Rumours ()");

        Assert.Equal("Rumours ()", title.Name);
        Assert.Empty(title.Editions);
    }

    [Fact]
    public void An_untitled_record_splits_into_nothing()
    {
        var title = AlbumTitle.Split(null);

        Assert.Equal("", title.Full);
        Assert.Equal("", title.Name);
        Assert.Empty(title.Editions);
    }
}
