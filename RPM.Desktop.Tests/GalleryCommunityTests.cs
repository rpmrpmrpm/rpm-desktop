using RPM.Core.Cloud;
using RPM.Desktop.Presentation.Gallery;

namespace RPM.Desktop.Tests;

/// <summary>
/// The stand-in wall behind the album page under <c>--gallery</c>.
///
/// <para>These tests check the part that a fixed list of rows would get wrong. The pools are created once
/// and then kept. So a sort tab re-orders the same reviews, and does not deal a new set. A vote, a rating
/// and a comment made on the page are still there after the page is left and opened again. A harness that
/// forgot them would look right in a screenshot, and it would be useless for design work.</para>
///
/// <para>Each test uses its own album name. The store is process-wide, as a server is.</para>
/// </summary>
public class GalleryCommunityTests
{
    private const int PageSize = 10;

    [Fact]
    public void The_same_record_always_opens_with_the_same_wall()
    {
        var first = GalleryCommunity.Album("Test Artist", "Stable Record", 2001, null);
        var second = GalleryCommunity.Album("Test Artist", "Stable Record", 2001, null);

        Assert.Equal(first.AlbumId, second.AlbumId);
        Assert.Equal(first.ReviewCount, second.ReviewCount);
        Assert.Equal(first.AverageRating, second.AverageRating);
    }

    [Fact]
    public void Two_records_get_different_walls()
    {
        var one = GalleryCommunity.Album("Test Artist", "Record One", 2001, null);
        var other = GalleryCommunity.Album("Test Artist", "Record Two", 2001, null);

        Assert.NotEqual(one.AlbumId, other.AlbumId);
    }

    [Fact]
    public void The_numbers_on_the_entry_are_counted_from_what_the_page_draws()
    {
        var album = GalleryCommunity.Album("Test Artist", "Counted Record", 1999, null);
        var page = GalleryCommunity.Page(album.AlbumId, RpmReviewSort.Votes, 1, PageSize);

        Assert.Equal(album.ReviewCount, page.Total);
        Assert.Equal(GalleryCommunity.Said(album.AlbumId).Count, album.CommentCount);
        Assert.InRange(album.AverageRating, 0.5, 5);
    }

    [Fact]
    public void A_sort_tab_reorders_the_same_reviews_rather_than_dealing_new_ones()
    {
        var album = GalleryCommunity.Album("Test Artist", "Sorted Record", 1995, null);

        var byVotes = All(album.AlbumId, RpmReviewSort.Votes);
        var byDate = All(album.AlbumId, RpmReviewSort.Date);

        Assert.Equal(
            byVotes.Select(r => r.UserId).OrderBy(id => id, StringComparer.Ordinal),
            byDate.Select(r => r.UserId).OrderBy(id => id, StringComparer.Ordinal));

        Assert.Equal(byVotes.Select(r => r.Score).OrderByDescending(s => s), byVotes.Select(r => r.Score));
        Assert.Equal(byDate.Select(r => r.CreatedAt).OrderByDescending(t => t), byDate.Select(r => r.CreatedAt));
    }

    [Fact]
    public void The_worst_first_tab_starts_at_the_lowest_rating()
    {
        var album = GalleryCommunity.Album("Test Artist", "Negative Record", 1990, null);

        var negative = All(album.AlbumId, RpmReviewSort.Negative);
        var positive = All(album.AlbumId, RpmReviewSort.Positive);

        Assert.Equal(negative.Min(r => r.Rating), negative[0].Rating);
        Assert.Equal(positive.Max(r => r.Rating), positive[0].Rating);
    }

    [Fact]
    public void Pages_run_over_one_pool_without_repeating_a_row()
    {
        var album = GalleryCommunity.Album("Test Artist", "Paged Record", 1988, null);
        var first = GalleryCommunity.Page(album.AlbumId, RpmReviewSort.Votes, 1, PageSize);

        Assert.True(first.Total > PageSize, "the pool is meant to be deep enough to page");
        Assert.Equal(PageSize, first.Items.Count);

        var second = GalleryCommunity.Page(album.AlbumId, RpmReviewSort.Votes, 2, PageSize);
        Assert.Empty(first.Items.Select(r => r.UserId).Intersect(second.Items.Select(r => r.UserId)));
    }

    [Fact]
    public void A_vote_sticks_and_moves_the_score_by_one()
    {
        var album = GalleryCommunity.Album("Test Artist", "Voted Record", 2004, null);
        var row = All(album.AlbumId, RpmReviewSort.Votes).First(r => r.MyVote == 0);

        var voted = GalleryCommunity.Vote(album.AlbumId, row.UserId, 1);

        Assert.NotNull(voted);
        Assert.Equal(1, voted!.MyVote);
        Assert.Equal(row.Score + 1, voted.Score);

        // And it is still there when the page is drawn again.
        var reread = All(album.AlbumId, RpmReviewSort.Votes).Single(r => r.UserId == row.UserId);
        Assert.Equal(voted.Score, reread.Score);
    }

    [Fact]
    public void Pressing_the_same_arrow_again_takes_the_vote_back()
    {
        var album = GalleryCommunity.Album("Test Artist", "Untouched Record", 2010, null);
        var row = All(album.AlbumId, RpmReviewSort.Votes).First(r => r.MyVote == 0);

        GalleryCommunity.Vote(album.AlbumId, row.UserId, 1);
        var back = GalleryCommunity.Vote(album.AlbumId, row.UserId, 0);

        Assert.Equal(0, back!.MyVote);
        Assert.Equal(row.Score, back.Score);
    }

    [Fact]
    public void The_reader_has_a_review_of_their_own_to_rewrite_and_delete()
    {
        var album = GalleryCommunity.Album("Test Artist", "Mine Record", 1975, null);

        Assert.Contains(All(album.AlbumId, RpmReviewSort.Date), r => r.UserId == GalleryCommunity.MeUserId);

        GalleryCommunity.WriteReview(album.AlbumId, 10, "Rewritten.");
        var mine = All(album.AlbumId, RpmReviewSort.Date).Single(r => r.UserId == GalleryCommunity.MeUserId);
        Assert.Equal(10, mine.Rating);
        Assert.Equal("Rewritten.", mine.Body);

        GalleryCommunity.DeleteMyReview(album.AlbumId);
        Assert.DoesNotContain(All(album.AlbumId, RpmReviewSort.Date), r => r.UserId == GalleryCommunity.MeUserId);
    }

    [Fact]
    public void Deleting_the_readers_review_takes_it_off_the_count()
    {
        var album = GalleryCommunity.Album("Test Artist", "Counted Again", 1982, null);
        var before = album.ReviewCount;

        GalleryCommunity.DeleteMyReview(album.AlbumId);

        Assert.Equal(before - 1, GalleryCommunity.Album(album.AlbumId).ReviewCount);
    }

    [Fact]
    public void A_comment_stays_where_it_was_posted_and_can_be_taken_back_down()
    {
        var album = GalleryCommunity.Album("Test Artist", "Said Record", 2016, null);
        var before = GalleryCommunity.Said(album.AlbumId).Count;

        GalleryCommunity.AddComment(album.AlbumId, "  Posted from the harness.  ");

        var said = GalleryCommunity.Said(album.AlbumId);
        Assert.Equal(before + 1, said.Count);

        var posted = said.Last();
        Assert.Equal("Posted from the harness.", posted.Body);
        Assert.Equal(GalleryCommunity.MeUserId, posted.UserId);
        Assert.Equal(said.Count, GalleryCommunity.Album(album.AlbumId).CommentCount);

        GalleryCommunity.DeleteComment(album.AlbumId, posted.CommentId);
        Assert.Equal(before, GalleryCommunity.Said(album.AlbumId).Count);
    }

    /// <summary>The row must be drawn without a network. Both kinds of card must be in it, because the page
    /// decides which is which by looking up each suggestion in the library.</summary>
    [Fact]
    public void The_suggestions_row_is_full_and_never_names_the_record_itself()
    {
        var found = GalleryCommunity.Similar("Daft Punk", "Discovery");

        Assert.Equal(8, found.Count);
        Assert.DoesNotContain(found, r => r.Title == "Discovery");
        Assert.All(found, r => Assert.False(string.IsNullOrWhiteSpace(r.Artist)));
        Assert.All(found, r => Assert.False(string.IsNullOrWhiteSpace(r.Ref.Id)));
    }

    /// <summary>The demo shelf's pressings are the real ones. Any other record still gets a plausible
    /// catalogue number, not an empty line on the page.</summary>
    [Fact]
    public void Every_record_has_a_pressing_and_the_demo_shelf_has_its_own()
    {
        var known = GalleryCommunity.Pressing("Daft Punk", "Discovery");
        Assert.Equal("7243 8 49606 1 8", known.Catno);
        Assert.Equal("2001-03-12", known.Released);

        var made = GalleryCommunity.Pressing("Nobody", "Nothing In Particular");
        Assert.False(string.IsNullOrWhiteSpace(made.Catno));
        Assert.False(string.IsNullOrWhiteSpace(made.Released));
    }

    /// <summary>The review lines on an artist's wall name that artist's own records, so the wall and the
    /// album list beside it agree. An artist with no record of their own falls back to the pool's titles,
    /// so no review line is drawn about nothing.</summary>
    [Fact]
    public void The_wall_names_the_artists_own_records()
    {
        var artist = new RPM.Core.Library.ArtistWallItem(
            "Wall Test Artist", ["Wall Test Artist/First Record", "Wall Test Artist/Second Record"], [], [], 16);

        var entry = GalleryCommunity.Artist(artist);
        var named = GalleryCommunity.Wall(entry.ArtistId)
            .Where(e => e.Kind == RpmWallEntry.ReviewKind)
            .Select(e => e.AlbumTitle)
            .ToList();

        Assert.NotEmpty(named);
        Assert.All(named, title => Assert.Contains(title, new[] { "First Record", "Second Record" }));

        var stranger = new RPM.Core.Library.ArtistWallItem("Wall Test Guest", [], [], ["Someone/Their Record"], 1);
        var guest = GalleryCommunity.Wall(GalleryCommunity.Artist(stranger).ArtistId)
            .Where(e => e.Kind == RpmWallEntry.ReviewKind)
            .ToList();

        Assert.NotEmpty(guest);
        Assert.All(guest, e => Assert.False(string.IsNullOrWhiteSpace(e.AlbumTitle)));
    }

    private static List<RpmReview> All(string albumId, RpmReviewSort sort)
    {
        var rows = new List<RpmReview>();
        var page = 1;
        while (true)
        {
            var got = GalleryCommunity.Page(albumId, sort, page, PageSize);
            rows.AddRange(got.Items);
            if (page >= got.PageCount) break;
            page++;
        }
        return rows;
    }
}
