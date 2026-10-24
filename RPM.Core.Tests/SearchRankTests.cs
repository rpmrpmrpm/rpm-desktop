using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The order of a search's answers on Pegasus's search screen, the Android TV app's rule: names that start
/// with the query, then names with a word that starts with it, then the rest, each in the order found.
/// </summary>
public class SearchRankTests
{
    private static List<string> Best(string query, int most, params string[] found)
        => SearchRank.Best(found, s => s, query, most);

    [Fact]
    public void A_name_that_starts_with_the_query_comes_first()
    {
        Assert.Equal(
            ["Love Me Do", "Endless Love", "Glove"],
            Best("love", 10, "Glove", "Endless Love", "Love Me Do"));
    }

    [Fact]
    public void Each_group_keeps_the_order_found()
    {
        Assert.Equal(
            ["Love A", "Love B", "My Love", "Your Love", "Clover", "Glove"],
            Best("love", 10, "Clover", "My Love", "Love A", "Glove", "Your Love", "Love B"));
    }

    [Fact]
    public void Case_is_ignored()
    {
        Assert.Equal(["ABBA", "The Abbey"], Best("abb", 10, "The Abbey", "ABBA"));
    }

    [Fact]
    public void The_word_rule_reads_the_first_word_of_the_query()
    {
        // "Abbey" starts the second word of the title, and the query's first word is "abbey".
        Assert.Equal(
            ["Abbey Road", "Back to Abbey Lane", "Crabbey"],
            Best("abbey road", 10, "Crabbey", "Back to Abbey Lane", "Abbey Road"));
    }

    [Fact]
    public void The_list_is_cut_at_its_most()
    {
        Assert.Equal(["Love 1", "Love 2"], Best("love", 2, "Glove", "Love 1", "Love 2", "Love 3"));
    }

    [Fact]
    public void Nothing_for_an_empty_query()
    {
        Assert.Empty(Best("   ", 10, "Anything"));
        Assert.Empty(SearchRank.Best(["Anything"], s => s, null, 10));
    }

    [Fact]
    public void A_missing_name_falls_in_the_rest()
    {
        Assert.Equal(["Love", null], SearchRank.Best(new string?[] { null, "Love" }, s => s, "love", 10));
    }
}
