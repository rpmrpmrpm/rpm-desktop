using RPM.Core.Library;
using Xunit;

namespace RPM.Core.Tests;

/// <summary>The library search's recent searches: the rules the phones keep too.</summary>
public class SearchHistoryTests
{
    [Fact]
    public void The_newest_search_goes_on_top()
    {
        var kept = SearchHistory.Remember(["queen"], "bowie");
        Assert.Equal(["bowie", "queen"], kept);
    }

    [Fact]
    public void The_steps_of_one_search_are_one_entry()
    {
        // Typing on past a pause, and taking a word back, both replace the newest entry.
        var kept = SearchHistory.Remember(["radi", "queen"], "radiohead");
        Assert.Equal(["radiohead", "queen"], kept);

        kept = SearchHistory.Remember(kept, "radio");
        Assert.Equal(["radio", "queen"], kept);
    }

    [Fact]
    public void A_search_asked_again_moves_up_and_is_not_kept_twice()
    {
        var kept = SearchHistory.Remember(["bowie", "queen", "abba"], "Queen");
        Assert.Equal(["Queen", "bowie", "abba"], kept);
    }

    [Fact]
    public void One_letter_is_not_remembered_and_spaces_are_trimmed()
    {
        Assert.Equal(["queen"], SearchHistory.Remember(["queen"], " a "));
        Assert.Equal(["abba", "queen"], SearchHistory.Remember(["queen"], "  abba  "));
    }

    [Fact]
    public void At_most_ten_are_kept()
    {
        IReadOnlyList<string> kept = [];
        for (var i = 0; i < 15; i++)
            kept = SearchHistory.Remember(kept, $"search {i:00} x{i}");

        Assert.Equal(SearchHistory.Most, kept.Count);
        Assert.Equal("search 14 x14", kept[0]);
    }

    [Fact]
    public void Forgetting_takes_out_that_one_only()
    {
        Assert.Equal(["bowie", "abba"], SearchHistory.Forget(["bowie", "queen", "abba"], "queen"));
    }
}
