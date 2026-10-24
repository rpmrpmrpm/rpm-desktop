using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>The four orders Pegasus's artists screen offers, the same as the phone's ArtistSort.</summary>
public class ArtistSortTests
{
    private static ArtistWallItem Act(string name, string[] albums, int tracks = 1, string[]? together = null)
        => new(name, albums, together ?? [], [], tracks);

    private static AlbumFacts Facts(long addedAt) => new("", "", addedAt, 0, 0);

    private static readonly IReadOnlyDictionary<string, AlbumFacts> NoFacts =
        new Dictionary<string, AlbumFacts>();

    [Fact]
    public void Name_order_puts_the_hash_bucket_last()
    {
        var wall = new[]
        {
            Act("2Pac", ["2Pac/A"]),
            Act("Björk", ["Björk/A"]),
            Act("abba", ["abba/A"]),
        };

        var sorted = ArtistSort.By(wall, ArtistOrdering.Name, NoFacts);

        Assert.Equal(["abba", "Björk", "2Pac"], sorted.Select(a => a.Name));
    }

    /// <summary>A name led by punctuation stands among the names of the letter it files under, so the
    /// ladder of letters beside the list reads A to Z with "#" last.</summary>
    [Fact]
    public void Name_order_files_a_name_led_by_punctuation_under_its_own_letter()
    {
        var wall = new[]
        {
            Act("(hed) p.e.", ["H/a"]),
            Act("Air", ["A/a"]),
            Act("Haim", ["H/b"]),
            Act("!!!", ["X/a"]),
        };

        var sorted = ArtistSort.By(wall, ArtistOrdering.Name, NoFacts);

        Assert.Equal(["Air", "(hed) p.e.", "Haim", "!!!"], sorted.Select(a => a.Name));
        Assert.Equal(['A', 'H', 'H', '#'], sorted.Select(a => a.Initial));
    }

    [Fact]
    public void Albums_order_counts_joint_records_and_falls_back_to_the_name()
    {
        var wall = new[]
        {
            Act("Cher", ["Cher/A"]),
            Act("Blur", ["Blur/A"], together: ["Blur & Cher/B"]),
            Act("Abba", ["Abba/A"]),
        };

        var sorted = ArtistSort.By(wall, ArtistOrdering.Albums, NoFacts);

        Assert.Equal(["Blur", "Abba", "Cher"], sorted.Select(a => a.Name));
    }

    [Fact]
    public void Tracks_order_is_most_songs_first()
    {
        var wall = new[]
        {
            Act("A", ["A/1"], tracks: 3),
            Act("B", ["B/1"], tracks: 30),
            Act("C", ["C/1"], tracks: 3),
        };

        var sorted = ArtistSort.By(wall, ArtistOrdering.Tracks, NoFacts);

        Assert.Equal(["B", "A", "C"], sorted.Select(a => a.Name));
    }

    /// <summary>Arrival is the newest of their own records, and an artist nothing of whose can be dated
    /// goes last rather than wherever a zero falls.</summary>
    [Fact]
    public void Added_order_reads_the_newest_own_record_and_puts_the_undated_last()
    {
        var wall = new[]
        {
            Act("Old", ["Old/1", "Old/2"]),
            Act("Undated", ["Undated/1"]),
            Act("New", ["New/1"]),
        };
        var facts = new Dictionary<string, AlbumFacts>
        {
            ["Old/1"] = Facts(10),
            ["Old/2"] = Facts(20),
            ["New/1"] = Facts(30),
        };

        var sorted = ArtistSort.By(wall, ArtistOrdering.Added, facts);

        Assert.Equal(["New", "Old", "Undated"], sorted.Select(a => a.Name));
        Assert.Equal(20, ArtistSort.ArrivalOf(wall[0], facts));
        Assert.Equal(0, ArtistSort.ArrivalOf(wall[1], facts));
    }

    /// <summary>A guest verse on a record added today is not the artist arriving.</summary>
    [Fact]
    public void Added_order_ignores_appearances()
    {
        var guest = new ArtistWallItem("Guest", ["Guest/1"], [], ["Other/Today"], 2);
        var facts = new Dictionary<string, AlbumFacts>
        {
            ["Guest/1"] = Facts(5),
            ["Other/Today"] = Facts(99),
        };

        Assert.Equal(5, ArtistSort.ArrivalOf(guest, facts));
    }
}
