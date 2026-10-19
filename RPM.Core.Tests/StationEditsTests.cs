using RPM.Core.Radio;

namespace RPM.Core.Tests;

/// <summary>
/// The admin's edits to how a station is shown: its title, its tags, its description.
///
/// <para>Most of these hold a <see cref="StationEdits.Set"/> in hand. The ones that go through the
/// app's own reads (<see cref="RadioStation.Display"/>, the sieve, <see cref="RadioNames.Distinguish"/>)
/// set the global one, under uuids that nothing else in the suite uses, and put it back afterwards.</para>
/// </summary>
public sealed class StationEditsTests : IDisposable
{
    private static readonly string Kept = Guid.NewGuid().ToString("D");
    private static readonly string Folded = Guid.NewGuid().ToString("D");
    private static readonly string Other = Guid.NewGuid().ToString("D");

    public void Dispose() => StationEdits.Replace(StationEdits.Set.None);

    private static RadioStation Station(
        string uuid, string name, string[]? tags = null, string[]? alsoFiledAs = null,
        string? countryCode = "NL", int clicks = 0)
        => new()
        {
            Uuid = uuid,
            Name = name,
            StreamUrl = "http://example.test/" + uuid,
            Tags = tags ?? [],
            AlsoFiledAs = alsoFiledAs ?? [],
            CountryCode = countryCode,
            ClickCount = clicks,
        };

    private static StationEdits.Set SetOf(params (string Uuid, StationEdit Edit)[] edits)
        => new(edits.Select(e => KeyValuePair.Create(e.Uuid, e.Edit)));

    [Fact]
    public void A_folded_station_finds_the_edit_of_any_of_its_rows()
    {
        // The edit was made on the row that the fold did not keep.
        var set = SetOf((Folded, new StationEdit("Radio 2", null, null)));
        var station = Station(Kept, "NPO Radio 2", alsoFiledAs: [Folded]);

        Assert.Equal("Radio 2", set.Of(station)?.Name);
    }

    [Fact]
    public void The_kept_rows_own_edit_comes_first()
    {
        var set = SetOf(
            (Folded, new StationEdit("From the folded row", null, null)),
            (Kept, new StationEdit("From the kept row", null, null)));

        Assert.Equal("From the kept row", set.Of(Station(Kept, "NPO Radio 2", alsoFiledAs: [Folded]))?.Name);
    }

    [Fact]
    public void Uuids_are_matched_without_regard_to_case()
    {
        var set = SetOf((Kept.ToUpperInvariant(), new StationEdit("Radio 2", null, null)));

        Assert.NotNull(set.Of(Station(Kept, "NPO Radio 2")));
    }

    [Fact]
    public void Blank_values_are_the_stations_own()
    {
        var set = SetOf((Kept, new StationEdit("  ", [" ", ""], "")));
        var edit = set.Of(Station(Kept, "NPO Radio 2"));

        Assert.NotNull(edit);
        Assert.Null(edit.Name);
        Assert.Null(edit.Tags);
        Assert.Null(edit.Description);
    }

    [Fact]
    public void Tags_are_read_as_the_directorys_are()
    {
        var set = SetOf((Kept, new StationEdit(null, [" News", "news", "Talk"], null)));

        Assert.Equal(["news", "talk"], set.Of(Station(Kept, "X"))?.Tags);
    }

    [Fact]
    public void Two_answers_that_say_the_same_are_the_same()
    {
        var a = SetOf((Kept, new StationEdit("Radio 2", ["pop"], "A line.")));
        var b = SetOf((Kept.ToUpperInvariant(), new StationEdit("Radio 2", ["Pop"], "A line.")));
        var c = SetOf((Kept, new StationEdit("Radio 2", ["pop", "news"], "A line.")));

        Assert.True(a.SameAs(b));
        Assert.False(a.SameAs(c));
        Assert.False(a.SameAs(StationEdits.Set.None));
    }

    [Fact]
    public void Replace_says_whether_anything_moved()
    {
        var set = SetOf((Kept, new StationEdit("Radio 2", null, null)));

        Assert.True(StationEdits.Replace(set));
        Assert.False(StationEdits.Replace(SetOf((Kept, new StationEdit("Radio 2", null, null)))));
    }

    [Fact]
    public void A_genre_takes_in_the_stations_retagged_into_it()
    {
        var set = SetOf(
            (Kept, new StationEdit(null, ["news"], null)),
            (Other, new StationEdit(null, ["pop"], null)),
            (Folded, new StationEdit("Only a title", null, null)));

        Assert.Equal([Kept], set.RetaggedInto("News"));
        Assert.Equal([Other], set.RetaggedInto("Pop"));
        Assert.Empty(set.RetaggedInto("Jazz"));
    }

    [Fact]
    public void Display_shows_the_admins_title_and_Name_stays_the_directorys()
    {
        var station = Station(Kept, "NPO Radio 2 - Hits");
        StationEdits.Replace(SetOf((Kept, new StationEdit("Radio 2", null, null))));

        Assert.Equal("Radio 2", station.Display);
        Assert.Equal("NPO Radio 2 - Hits", station.Name);
    }

    [Fact]
    public void A_genre_filter_reads_the_admins_tags_and_the_ads_rule_does_not()
    {
        var station = Station(Kept, "Pop Station", tags: ["pop"]);
        StationEdits.Replace(SetOf((Kept, new StationEdit(null, ["news"], null))));

        Assert.True(new StationSieve { Genre = "News" }.Keeps(station));
        Assert.False(new StationSieve { Genre = "Pop" }.Keeps(station));
        Assert.Equal(["pop"], station.Tags);
    }

    [Fact]
    public void Distinguish_counts_an_edited_station_by_its_title()
    {
        // Two Concertzenders would both print "Concertzender" and get their straplines back. One
        // is given its own title, so neither shares a head any more, and the edited one is left
        // without a strapline (Display reads the edit first).
        var a = Station(Kept, "Concertzender - Hard Bop");
        var b = Station(Other, "Concertzender - De Gehoorde Stilte");
        StationEdits.Replace(SetOf((Kept, new StationEdit("Hard Bop Radio", null, null))));

        var told = RadioNames.Distinguish([a, b]);

        Assert.Equal("Hard Bop Radio", told[0].Display);
        Assert.Equal("Concertzender", told[1].Display);
    }

    [Fact]
    public void A_title_the_same_as_another_stations_name_tells_that_one_apart()
    {
        var a = Station(Kept, "Something Else");
        var b = Station(Other, "Concertzender - De Gehoorde Stilte");
        StationEdits.Replace(SetOf((Kept, new StationEdit("Concertzender", null, null))));

        var told = RadioNames.Distinguish([a, b]);

        Assert.Equal("Concertzender", told[0].Display);
        Assert.Equal("Concertzender - De Gehoorde Stilte", told[1].Display);
    }

    [Fact]
    public void A_retagged_station_is_added_to_a_genre_answer_in_play_order()
    {
        var answer = new[] { Station("a", "A", clicks: 900), Station("b", "B", clicks: 100) };
        var retagged = new[] { Station(Kept, "K", clicks: 500) };

        var merged = RadioResults.WithRetagged(answer, retagged, new RadioQuery());

        Assert.Equal(["a", Kept, "b"], merged.Select(s => s.Uuid));
    }

    [Fact]
    public void A_retagged_station_is_added_only_where_the_question_lets_it_through()
    {
        var answer = new[] { Station("a", "A") };
        var abroad = Station(Kept, "Kiss FM", countryCode: "DE");
        var named = Station(Other, "Kiss FM", countryCode: "NL");

        var merged = RadioResults.WithRetagged(
            answer, [abroad, named], new RadioQuery { CountryCode = "nl", Name = "kiss" });

        Assert.Equal(["a", Other], merged.Select(s => s.Uuid));
    }

    [Fact]
    public void A_retagged_station_already_in_the_answer_is_not_added_twice()
    {
        var answer = new[] { Station(Kept, "K") };

        Assert.Same(answer, RadioResults.WithRetagged(answer, [Station(Kept, "K")], new RadioQuery()));
    }
}
