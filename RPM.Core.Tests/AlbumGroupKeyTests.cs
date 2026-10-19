using RPM.Core.Library;

namespace RPM.Core.Tests;

public class AlbumGroupKeyTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("None")]
    [InlineData("none")]
    [InlineData("(none)")]
    [InlineData("null")]
    [InlineData("Unknown")]
    [InlineData("N/A")]
    [InlineData("-")]
    [InlineData("?")]
    [InlineData("​ ")]
    public void Nothing_tagged_is_no_group(string? raw)
        => Assert.Null(AlbumGroupKey.Display(raw));

    [Theory]
    [InlineData("Rock", "Rock")]
    [InlineData("  Rock  ", "Rock")]
    [InlineData("Hip   Hop", "Hip Hop")]
    [InlineData("Rock;", "Rock")]
    [InlineData("﻿Rock", "Rock")]
    public void Display_tidies_without_renaming(string raw, string expected)
        => Assert.Equal(expected, AlbumGroupKey.Display(raw));

    [Theory]
    [InlineData("Acoustic", "acoustic")]
    [InlineData("Acoustic", "ACOUSTIC ")]
    [InlineData("Hip Hop", "Hip-Hop")]
    [InlineData("Hip Hop", "HipHop")]
    [InlineData("R.E.M.", "REM")]
    [InlineData("Café Tacvba", "Cafe Tacvba")]
    public void One_thing_spelled_two_ways_is_one_key(string a, string b)
        => Assert.Equal(AlbumGroupKey.Key(a), AlbumGroupKey.Key(b));

    [Theory]
    [InlineData("Rock", "Pop")]
    [InlineData("Acoustic", "Acoustic Rock")]
    [InlineData("1998", "1999")]
    public void Different_things_keep_different_keys(string a, string b)
        => Assert.NotEqual(AlbumGroupKey.Key(a), AlbumGroupKey.Key(b));

    [Fact]
    public void Symbol_only_names_do_not_all_collapse_into_one_group()
    {
        Assert.NotEqual(AlbumGroupKey.Key("!!!"), AlbumGroupKey.Key("+/-"));
        Assert.NotEmpty(AlbumGroupKey.Key("!!!"));
    }

    [Fact]
    public void Heading_is_the_spelling_most_records_carry()
    {
        var headings = AlbumGroupKey.Headings(["hip-hop", "Hip Hop", "Hip Hop", "HipHop"]);

        var heading = Assert.Single(headings);
        Assert.Equal(AlbumGroupKey.Key("Hip Hop"), heading.Key);
        Assert.Equal("Hip Hop", heading.Value);
    }

    [Fact]
    public void Tied_spellings_pick_one_and_keep_picking_it()
    {
        var once = AlbumGroupKey.Headings(["Hip-Hop", "Hip Hop"]);
        var again = AlbumGroupKey.Headings(["Hip Hop", "Hip-Hop"]);

        Assert.Equal(once, again);
        Assert.Equal("Hip Hop", once.Values.Single());
    }

    [Fact]
    public void Values_that_say_nothing_name_no_group()
    {
        var headings = AlbumGroupKey.Headings(["Rock", "None", null, "", "unknown"]);

        Assert.Equal("Rock", Assert.Single(headings).Value);
    }

    /// <summary>The one property the shelf depends on: sorting by the key and breaking on the key put
    /// every record of a group in one run, so a heading is drawn exactly once per group.</summary>
    [Fact]
    public void Sorting_by_key_leaves_one_run_per_group()
    {
        string[] raw =
        [
            "Acoustic", "rock", "acoustic", "Hip-Hop", "Rock", "ACOUSTIC", "hip hop", "Rock ",
        ];

        var ordered = raw
            .Select(r => (Raw: r, Key: AlbumGroupKey.Key(r)))
            .OrderBy(a => a.Key, StringComparer.Ordinal)
            .ToList();

        var opened = new List<string>();
        string? key = null;
        foreach (var (_, k) in ordered)
        {
            if (!string.Equals(key, k, StringComparison.Ordinal))
                opened.Add(k);
            key = k;
        }

        Assert.Equal(opened.Count, opened.Distinct().Count());
        Assert.Equal(3, opened.Count);
    }
}
