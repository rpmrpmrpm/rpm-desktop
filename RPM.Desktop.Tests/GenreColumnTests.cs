using RPM.Desktop.Presentation.Main.Genres;

namespace RPM.Desktop.Tests;

/// <summary>
/// Whether a genre's page stands its left-hand column, read as the sentence it is.
///
/// <para>The column holds two things: what the encyclopedia says about the genre, and the genres next
/// to it in the collection. Whether the column stands depends on what has actually arrived, not on what
/// was asked for. If an article never arrives and the collection holds no related genres, a standing
/// column would be empty room that looks like a page still loading forever.</para>
/// </summary>
public class GenreColumnTests
{
    /// <summary>A genre with no description has nothing to show, so no column is drawn for it, whatever
    /// else is true.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_genre_nothing_is_written_about_gets_no_column(bool articleGone, bool hasRelations) =>
        Assert.False(GenreDetailView.ColumnWorthStanding(
            described: false, articleGone, hasRelations));

    /// <summary>A pending article is not the same as a missing one. While it is on its way the column
    /// stands, with the spinning record shown, which tells the reader to wait.</summary>
    [Fact]
    public void The_column_stands_while_the_article_is_still_on_its_way() =>
        Assert.True(GenreDetailView.ColumnWorthStanding(
            described: true, articleGone: false, hasRelations: false));

    [Fact]
    public void An_article_that_arrived_is_worth_a_column() =>
        Assert.True(GenreDetailView.ColumnWorthStanding(
            described: true, articleGone: false, hasRelations: true));

    /// <summary>The case this rule is for: the article is not coming and the collection holds nothing
    /// next to this genre, so the column would be empty room with nothing in it.</summary>
    [Fact]
    public void No_article_and_no_neighbours_takes_the_column_away() =>
        Assert.False(GenreDetailView.ColumnWorthStanding(
            described: true, articleGone: true, hasRelations: false));

    /// <summary>Related genres are the column's other half, so they keep it standing alone. The message
    /// that the article could not be read is shown beside them, where it explains the gap. It is not the
    /// whole column.</summary>
    [Fact]
    public void Neighbours_alone_keep_the_column() =>
        Assert.True(GenreDetailView.ColumnWorthStanding(
            described: true, articleGone: true, hasRelations: true));
}
