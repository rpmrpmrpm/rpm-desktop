using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// Who a playlist is made of: credits cut into the acts they name, counted per act, most first.
/// </summary>
public class PlaylistFeaturesTests
{
    [Fact]
    public void The_artist_the_list_is_built_around_wins_however_they_were_typed()
    {
        var featured = PlaylistFeatures.Of(
        [
            "Kanye West",
            "Kanye West & Ty Dolla $ign",
            "Jay-Z & Kanye West",
            "Rihanna",
        ], most: 3);

        // Counted whole, those first three would be three artists with one song each and Rihanna would
        // tie with all of them. Cut into acts, Kanye West has three.
        Assert.Equal("Kanye West", featured.Named[0]);
    }

    [Fact]
    public void Everyone_named_leaves_nobody_over()
    {
        var featured = PlaylistFeatures.Of(["Blur", "Pulp"], most: 3);

        Assert.Equal(["Blur", "Pulp"], featured.Named);
        Assert.Equal(0, featured.More);
    }

    [Fact]
    public void The_rest_are_counted_rather_than_named()
    {
        var featured = PlaylistFeatures.Of(
            ["A Band", "B Band", "C Band", "D Band", "E Band"], most: 2);

        Assert.Equal(2, featured.Named.Count);
        Assert.Equal(3, featured.More);
    }

    [Fact]
    public void One_artist_typed_two_ways_is_one_artist()
    {
        var featured = PlaylistFeatures.Of(["Blur", "BLUR", "blur"], most: 3);

        var only = Assert.Single(featured.Named);
        Assert.Equal("Blur", only);   // the first spelling the list used
        Assert.Equal(0, featured.More);
    }

    [Fact]
    public void A_song_that_names_nobody_is_not_an_act()
    {
        var featured = PlaylistFeatures.Of(["Blur", null, "", "   "], most: 3);

        Assert.Equal(["Blur"], featured.Named);
        Assert.Equal(0, featured.More);
    }

    [Fact]
    public void Ties_fall_back_to_the_name_so_the_line_is_the_same_line_every_time()
    {
        var featured = PlaylistFeatures.Of(["Zebra", "Aardvark", "Mongoose"], most: 3);

        Assert.Equal(["Aardvark", "Mongoose", "Zebra"], featured.Named);
    }

    [Fact]
    public void A_list_that_names_nobody_features_nobody()
    {
        var featured = PlaylistFeatures.Of([null, ""], most: 3);

        Assert.Empty(featured.Named);
        Assert.Equal(0, featured.More);
    }

    [Fact]
    public void Naming_none_of_them_still_counts_them_all()
    {
        var featured = PlaylistFeatures.Of(["Blur", "Pulp"], most: 0);

        Assert.Empty(featured.Named);
        Assert.Equal(2, featured.More);
    }
}
