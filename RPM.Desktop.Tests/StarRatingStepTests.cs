using RPM.Desktop.Presentation.Controls;

namespace RPM.Desktop.Tests;

/// <summary>
/// Left and Right on a record page's stars in Pegasus move the rating by whole stars (StarRating.Stepped).
/// The collection keeps whole stars, and a key cannot clear a rating, as a click cannot.
/// </summary>
public class StarRatingStepTests
{
    [Fact]
    public void Right_from_no_rating_gives_one_star()
        => Assert.Equal(1, StarRating.Stepped(0, 1));

    [Fact]
    public void A_whole_rating_moves_a_whole_star_either_way()
    {
        Assert.Equal(4, StarRating.Stepped(3, 1));
        Assert.Equal(2, StarRating.Stepped(3, -1));
    }

    [Fact]
    public void A_half_star_goes_to_the_whole_star_in_that_direction()
    {
        Assert.Equal(4, StarRating.Stepped(3.5, 1));
        Assert.Equal(3, StarRating.Stepped(3.5, -1));
    }

    [Fact]
    public void Left_from_one_star_or_none_does_not_move()
    {
        Assert.Null(StarRating.Stepped(1, -1));
        Assert.Null(StarRating.Stepped(0, -1));
    }

    [Fact]
    public void Right_from_five_stars_does_not_move()
        => Assert.Null(StarRating.Stepped(5, 1));
}
