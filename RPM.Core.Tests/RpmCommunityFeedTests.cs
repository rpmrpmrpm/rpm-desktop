using System.Net;
using System.Text;
using RPM.Core.Cloud;
using Xunit;

namespace RPM.Core.Tests;

/// <summary>Reading everyone's feed: the shapes the server sends, and the address the client asks.</summary>
public sealed class RpmCommunityFeedTests
{
    private const string Page =
        """
        {
          "items": [
            { "kind": "post", "createdAt": 1700000002000,
              "post": { "ownerId": "u1", "postId": "p1", "authorName": "Ann", "hasAvatar": true,
                        "body": "Hello", "song": { "artist": "Can", "title": "Vitamin C", "album": "Ege Bamyasi" },
                        "createdAt": 1700000002000, "ups": 3, "downs": 1, "score": 2, "myVote": 1,
                        "replyCount": 4, "authorDeleted": false, "authorSupporter": true } },
            { "kind": "review", "createdAt": 1700000001000,
              "review": { "albumId": "a1", "userId": "u2", "authorName": "Bo", "hasAvatar": false,
                          "rating": 9, "body": "Great", "createdAt": 1700000001000, "updatedAt": 1700000001000,
                          "ups": 0, "downs": 0, "score": 0, "myVote": 0,
                          "albumArtist": "Can", "albumTitle": "Tago Mago", "albumYear": 1971 } },
            { "kind": "poll", "createdAt": 1700000000500 },
            { "kind": "post", "createdAt": 1700000000000 }
          ],
          "next": 1700000000000
        }
        """;

    [Fact]
    public async Task A_page_keeps_posts_and_reviews_and_leaves_out_what_it_cannot_draw()
    {
        var handler = new Answering(Page);
        var client = Client(handler);

        var page = await client.GetFeedAsync(before: null, limit: 20);

        Assert.NotNull(page);
        Assert.Equal(1700000000000, page.Next);
        Assert.Equal(2, page.Known.Count);

        var post = page.Known[0].Post!;
        Assert.Equal("p1", post.PostId);
        Assert.True(post.AuthorSupporter);
        Assert.Equal("Vitamin C", post.Song?.Title);
        Assert.Equal(4, post.ReplyCount);

        var review = page.Known[1].Review!;
        Assert.Equal("Tago Mago", review.AlbumTitle);
        Assert.Equal(1971, review.AlbumYear);
        Assert.Equal(4.5, review.Stars);
        Assert.False(review.AuthorSupporter);

        Assert.Equal("/feed?limit=20", handler.Asked);
    }

    [Fact]
    public async Task The_next_page_is_asked_from_before_and_the_limit_is_capped()
    {
        var handler = new Answering("""{ "items": [] }""");
        var page = await Client(handler).GetFeedAsync(before: 1234, limit: 500);

        Assert.NotNull(page);
        Assert.Empty(page.Known);
        Assert.Null(page.Next);
        Assert.Equal($"/feed?limit={RpmCommunityClient.LongestFeedPage}&before=1234", handler.Asked);
    }

    [Fact]
    public async Task A_server_without_a_feed_answers_null()
    {
        var page = await Client(new Answering(null)).GetFeedAsync(before: null, limit: 20);
        Assert.Null(page);
    }

    [Fact]
    public void A_vote_answer_moves_the_counts_onto_the_feed_review()
    {
        var shown = new RpmFeedReview("a1", "u2", "Bo", false, 9, "Great", 1, 1, 0, 0, 0, 0,
            AlbumTitle: "Tago Mago");
        var answer = new RpmReview("a1", "u2", "Bo", false, 9, "Great", 1, 1, 1, 0, 1, 1);

        var changed = shown.With(answer);

        Assert.Equal(1, changed.Ups);
        Assert.Equal(1, changed.MyVote);
        Assert.Equal("Tago Mago", changed.AlbumTitle);
    }

    private static RpmCommunityClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") },
            _ => Task.FromResult<string?>(null));

    /// <summary>Answers every request with <paramref name="json"/>, or 404 when it is null, and notes the
    /// address asked.</summary>
    private sealed class Answering(string? json) : HttpMessageHandler
    {
        public string? Asked { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked = request.RequestUri?.PathAndQuery;
            return Task.FromResult(json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                });
        }
    }
}
