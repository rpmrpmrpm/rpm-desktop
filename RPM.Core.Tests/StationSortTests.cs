using RPM.Core.Radio;

namespace RPM.Core.Tests;

/// <summary>
/// The order the dial lists its stations in. What is tested hardest is that every order is TOTAL: a
/// list that shuffles its ties is a list that moves under somebody who is only scrolling.
/// </summary>
public class StationSortTests
{
    private static RadioStation Station(string name, int votes = 0, int bitrate = 0, string? uuid = null)
        => new()
        {
            Uuid = uuid ?? name,
            Name = name,
            StreamUrl = "http://example.test/stream",
            Votes = votes,
            BitrateKbps = bitrate,
        };

    private static IReadOnlyList<string> Names(IReadOnlyList<RadioStation> stations)
        => [.. stations.Select(s => s.Name)];

    [Fact]
    public void The_directorys_own_order_is_left_exactly_as_it_arrived()
    {
        var listed = new[] { Station("Zed FM"), Station("Alpha FM"), Station("Mid FM") };

        Assert.Same(listed, StationSort.By(StationOrdering.Listed, listed));
    }

    [Fact]
    public void By_name_reads_A_to_Z_and_back()
    {
        var listed = new[] { Station("Zed FM"), Station("alpha FM"), Station("Mid FM") };

        Assert.Equal(["alpha FM", "Mid FM", "Zed FM"],
                     Names(StationSort.By(StationOrdering.NameUp, listed)));
        Assert.Equal(["Zed FM", "Mid FM", "alpha FM"],
                     Names(StationSort.By(StationOrdering.NameDown, listed)));
    }

    [Fact]
    public void By_votes_and_by_quality_put_the_biggest_first()
    {
        var listed = new[]
        {
            Station("Quiet", votes: 3, bitrate: 128),
            Station("Loud", votes: 900, bitrate: 64),
            Station("Rich", votes: 40, bitrate: 320),
        };

        Assert.Equal(["Loud", "Rich", "Quiet"], Names(StationSort.By(StationOrdering.Votes, listed)));
        Assert.Equal(["Rich", "Quiet", "Loud"], Names(StationSort.By(StationOrdering.Quality, listed)));
    }

    /// <summary>Ties are what an order is really made of: a dial is full of stations with no votes and
    /// the same bitrate, and they must come out the same way every time the list is built.</summary>
    [Fact]
    public void Stations_that_tie_fall_back_to_their_names()
    {
        var listed = new[]
        {
            Station("Charlie", votes: 5, bitrate: 128),
            Station("Alpha", votes: 5, bitrate: 128),
            Station("Bravo", votes: 5, bitrate: 128),
        };

        Assert.Equal(["Alpha", "Bravo", "Charlie"], Names(StationSort.By(StationOrdering.Votes, listed)));
        Assert.Equal(["Alpha", "Bravo", "Charlie"], Names(StationSort.By(StationOrdering.Quality, listed)));
    }

    /// <summary>And two stations that tie on the name as well (a network that runs the same name in
    /// twenty towns) are told apart by the one thing no two of them share.</summary>
    [Fact]
    public void Stations_that_tie_on_the_name_too_are_still_one_order()
    {
        var listed = new[]
        {
            Station("Radio One", uuid: "ccc"),
            Station("Radio One", uuid: "aaa"),
            Station("Radio One", uuid: "bbb"),
        };

        var once = StationSort.By(StationOrdering.NameUp, listed);
        var again = StationSort.By(StationOrdering.NameUp, [.. listed.Reverse()]);

        Assert.Equal(["aaa", "bbb", "ccc"], once.Select(s => s.Uuid));
        Assert.Equal(once.Select(s => s.Uuid), again.Select(s => s.Uuid));
    }

    /// <summary>A name is sorted as the row PRINTS it, not as the directory holds it: a station that
    /// leads with a dash or trails a bitrate is filed under the letter a reader is looking down the
    /// list for. See RadioNames.Clean.</summary>
    [Fact]
    public void A_name_is_sorted_by_what_the_row_shows()
    {
        var dashed = Station("- Zulu FM");      // shown as "Zulu FM"
        var beta = Station("Beta FM");

        var sorted = StationSort.By(StationOrdering.NameUp, [dashed, beta]);

        // On the raw name the dash would sort first; on the name the row prints, Z comes after B.
        Assert.Equal("Zulu FM", dashed.Display);
        Assert.Equal(["Beta FM", "- Zulu FM"], Names(sorted));
    }
}
