using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>A genre's popular songs: the Android TV app's rule (GenrePopular.kt), over the world's
/// plays.</summary>
public class GenrePopularTests
{
    private sealed record Song(string Title, string Act, long Plays);

    private static IReadOnlyList<string> Ranked(IEnumerable<Song> songs, int most = 32, int whole = 1000)
        => [.. GenrePopular.Ranked(songs, s => s.Title, s => s.Plays, s => s.Act, whole, most).Select(s => s.Title)];

    [Fact]
    public void No_act_has_more_than_three_songs()
    {
        var songs = new[]
        {
            new Song("a1", "A", 100), new Song("a2", "A", 90), new Song("a3", "A", 80), new Song("a4", "A", 70),
            new Song("b1", "B", 60), new Song("c1", "C", 50),
        };

        Assert.Equal(["a1", "a2", "a3", "b1", "c1"], Ranked(songs));
    }

    [Fact]
    public void An_act_is_one_act_however_it_is_spelled()
    {
        var songs = new[]
        {
            new Song("a1", "AC/DC", 100), new Song("a2", "ACDC", 90), new Song("a3", "ac/dc", 80),
            new Song("a4", "AC-DC", 70), new Song("b1", "B", 60),
        };

        Assert.DoesNotContain("a4", Ranked(songs));
    }

    [Fact]
    public void One_per_title_and_none_below_the_fewest()
    {
        var songs = new[]
        {
            new Song("Hello", "A", 100), new Song("Hello (Live)", "B", 90), new Song("Other", "C", 80),
            new Song("Third", "D", 70),
        };

        // Three titles are not a list of popular songs.
        Assert.Empty(Ranked(songs));
    }

    [Fact]
    public void At_most_what_the_screen_asks_for()
    {
        var songs = Enumerable.Range(1, 50).Select(i => new Song($"s{i}", $"Act {i}", 1000 - i));

        Assert.Equal(32, Ranked(songs).Count);
    }

    [Fact]
    public void The_records_played_most_here_are_taken_first_and_the_rest_keep_their_order()
    {
        var records = Enumerable.Range(1, 60).Select(i => (Folder: $"r{i}", Plays: i == 50 ? 7 : 0)).ToList();

        var taken = GenrePopular.RecordsToRank(records, r => r.Plays);

        Assert.Equal(GenrePopular.Records, taken.Count);
        Assert.Equal("r50", taken[0].Folder);
        Assert.Equal("r1", taken[1].Folder);
        Assert.Equal("r39", taken[^1].Folder);
    }
}
