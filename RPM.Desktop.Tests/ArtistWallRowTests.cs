using RPM.Core.Cloud;
using RPM.Desktop.Presentation.Main.Artists.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// How one line of an artist's wall becomes a card: which sentence it uses, whether it carries a
/// rating, and whether the user may delete it.
///
/// <para>The words themselves are not checked here (they need a resource table and a running app).
/// These tests check the row's decisions, which is where a wrong line would come from.</para>
/// </summary>
public class ArtistWallRowTests
{
    private const string Me = "user-me";
    private const string Somebody = "user-else";

    private static RpmWallEntry Post(string userId, string body = "Saw them in 1980.") => new(
        "art_1", "1700000000000-abcd", RpmWallEntry.PostKind, userId, "Ines V.",
        HasAvatar: false, body, CreatedAt: 1_700_000_000_000);

    private static RpmWallEntry Review(int rating, bool hasBody) => new(
        "art_1", "1700000000000-r1234", RpmWallEntry.ReviewKind, Somebody, "Ovid",
        HasAvatar: false, "", CreatedAt: 1_700_000_000_000,
        AlbumId: "alb_1", AlbumTitle: "Remain in Light", Rating: rating, HasBody: hasBody);

    [Fact]
    public void A_post_is_its_authors_own_words()
    {
        var row = new WallRow(Post(Somebody), Me, apiBase: null);

        Assert.True(row.IsPost);
        Assert.False(row.IsReview);
        Assert.Equal("", row.LineKey);
        Assert.Equal("Saw them in 1980.", row.Line);
        Assert.False(row.HasStars);
    }

    /// <summary>Whether the review has text decides which of two sentences is used, and it is the only
    /// thing the line says about the review behind it.</summary>
    [Fact]
    public void A_review_and_a_bare_rating_are_two_different_sentences()
    {
        var reviewed = new WallRow(Review(9, hasBody: true), Me, apiBase: null);
        var rated = new WallRow(Review(9, hasBody: false), Me, apiBase: null);

        Assert.Equal("Artists_WallWroteReview", reviewed.LineKey);
        Assert.Equal("Artists_WallRated", rated.LineKey);
        Assert.Equal("Remain in Light", reviewed.AlbumTitle);
    }

    /// <summary>Halves of a star, doubled on the wire: eight is four stars.</summary>
    [Fact]
    public void A_verdict_is_drawn_in_stars()
    {
        var row = new WallRow(Review(8, hasBody: true), Me, apiBase: null);

        Assert.True(row.HasStars);
        Assert.Equal(4d, row.Stars);
    }

    /// <summary>A rating of zero is not a rating, and a row of dim stars under a line would look like a
    /// rating of zero instead of no rating.</summary>
    [Fact]
    public void A_review_line_with_no_rating_draws_no_stars()
        => Assert.False(new WallRow(Review(0, hasBody: true), Me, apiBase: null).HasStars);

    [Fact]
    public void Your_own_post_is_yours_and_somebody_elses_is_not()
    {
        Assert.True(new WallRow(Post(Me), Me, apiBase: null).IsMine);
        Assert.False(new WallRow(Post(Somebody), Me, apiBase: null).IsMine);
    }

    /// <summary>Signed out, nothing is yours: every row offers a report and none a delete.</summary>
    [Fact]
    public void Signed_out_no_row_is_yours()
        => Assert.False(new WallRow(Post(Me), signedInUserId: null, apiBase: null).IsMine);

    /// <summary>A review line can never be deleted from the wall, even your own: it is removed when the
    /// review is deleted, and that is done on the album page.</summary>
    [Fact]
    public void Your_own_review_line_is_not_yours_to_delete()
    {
        var mine = Review(9, hasBody: true) with { UserId = Me };

        Assert.False(new WallRow(mine, Me, apiBase: null).IsMine);
    }

    [Fact]
    public void A_writer_with_no_picture_is_drawn_as_their_initial()
    {
        var row = new WallRow(Post(Somebody), Me, apiBase: null);

        Assert.Equal("I", row.Initial);
        Assert.False(row.HasAvatar);
        Assert.Null(row.AvatarUrl);
    }

    [Fact]
    public void A_writer_with_a_picture_is_drawn_from_the_api()
    {
        var entry = Post(Somebody) with { HasAvatar = true };
        var row = new WallRow(entry, Me, new Uri("https://api.rpmrpmrpm.com/"));

        Assert.True(row.HasAvatar);
        Assert.Equal("https://api.rpmrpmrpm.com/profiles/user-else/avatar", row.AvatarUrl!.ToString());
    }
}
