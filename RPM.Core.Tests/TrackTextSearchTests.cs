using RPM.Core.Analysis;

namespace RPM.Core.Tests;

public class TrackTextSearchTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blank_query_matches_everything(string? query)
        => Assert.True(TrackTextSearch.Matches(query, "Radiohead", "Creep"));

    [Fact]
    public void Matches_any_field_case_insensitively()
    {
        Assert.True(TrackTextSearch.Matches("radiohead", "Radiohead", "Creep", "Pablo Honey"));
        Assert.True(TrackTextSearch.Matches("PABLO", "Radiohead", "Creep", "Pablo Honey"));
    }

    [Fact]
    public void Matches_substring_not_only_whole_word()
        => Assert.True(TrackTextSearch.Matches("cre", "Radiohead", "Creep"));

    [Fact]
    public void Query_is_trimmed_before_matching()
        => Assert.True(TrackTextSearch.Matches("  creep  ", "Radiohead", "Creep"));

    [Fact]
    public void No_match_when_query_absent_from_all_fields()
        => Assert.False(TrackTextSearch.Matches("beatles", "Radiohead", "Creep", "Pablo Honey"));

    [Fact]
    public void Null_and_empty_fields_are_skipped()
        => Assert.False(TrackTextSearch.Matches("x", null, "", "abc"));
}
