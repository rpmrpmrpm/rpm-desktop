using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>The artists wall's heart: who the listener has hearted something of.</summary>
public class ArtistWallFavoritesTests
{
    private static ArtistCreditRow Row(string folder, string? albumArtist, string? artist, int tracks = 1)
        => new(folder, albumArtist, artist, tracks);

    private static readonly IReadOnlyList<ArtistWallItem> Shelf = ArtistWall.Build(
    [
        Row("ABBA/Arrival", "ABBA", "ABBA", 10),
        Row("Queen/Innuendo", "Queen", "Queen", 12),
        Row("Calvin Harris/18 Months", "Calvin Harris", "Calvin Harris feat. Ellie Goulding", 1),
        Row("Calvin Harris/18 Months", "Calvin Harris", "Calvin Harris feat. Rihanna", 1),
        Row("AC-DC/Back in Black", "AC/DC", "AC/DC", 10),
        // Guests get a box only with a record of their own (ArtistWall.Build).
        Row("Ellie Goulding/Lights", "Ellie Goulding", "Ellie Goulding", 10),
        Row("Rihanna/Loud", "Rihanna", "Rihanna", 10),
    ]);

    [Fact]
    public void A_hearted_record_brings_its_artist()
    {
        var kept = ArtistWall.Favorites(Shelf, new HashSet<string> { "ABBA/Arrival" }, []);

        Assert.Equal(["ABBA"], kept);
    }

    [Fact]
    public void A_hearted_song_brings_each_act_it_credits_and_not_the_other_guests()
    {
        var kept = ArtistWall.Favorites(Shelf, new HashSet<string>(), ["Calvin Harris feat. Ellie Goulding"]);

        Assert.Contains("Calvin Harris", kept);
        Assert.Contains("Ellie Goulding", kept);
        Assert.DoesNotContain("Rihanna", kept);
    }

    [Fact]
    public void A_band_with_a_separator_in_its_name_is_matched_whole()
    {
        var kept = ArtistWall.Favorites(Shelf, new HashSet<string>(), ["ac/dc"]);

        Assert.Equal(["AC/DC"], kept);
    }

    [Fact]
    public void Nothing_hearted_keeps_nobody()
    {
        Assert.Empty(ArtistWall.Favorites(Shelf, new HashSet<string>(), ["", "  "]));
    }
}
