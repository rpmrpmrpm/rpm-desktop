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
    public async Task A_person_page_reads_the_profile_with_its_banner_counts_listening_and_badge()
    {
        var handler = new Answering(
            """
            { "userId": "u1", "displayName": "Ann", "hasAvatar": true, "avatarUpdatedAt": 5, "createdAt": 1700000000000,
              "supporter": true, "hasBanner": true, "bannerUpdatedAt": 9,
              "listening": { "artist": "Can", "title": "Vitamin C", "album": "Ege Bamyasi", "at": 1700000001000 },
              "counts": { "albums": 1610, "tracks": 23329, "updatedAt": 1 },
              "friendship": "self", "blocked": false, "bannerSource": "photo",
              "badges": ["first-spin", "rock"], "badge": "rock", "badgeColour": "#2563EB" }
            """);

        var person = await Client(handler).GetPersonAsync("u1");

        Assert.NotNull(person);
        Assert.Equal("/profiles/u1", handler.Asked);
        Assert.True(person.Supporter);
        Assert.Equal(1610, person.Counts?.Albums);
        Assert.Equal("Vitamin C", person.Listening?.Title);
        Assert.Equal("rock", person.Badge);
        Assert.Equal(["first-spin", "rock"], person.Badges);
        Assert.Equal("profiles/u1/banner?v=9", RpmCommunityClient.BannerPath("u1", person.BannerUpdatedAt));
    }

    [Fact]
    public async Task A_profile_without_the_page_fields_still_reads()
    {
        var person = await Client(new Answering(
            """{ "userId": "u1", "displayName": "Ann", "hasAvatar": false, "avatarUpdatedAt": 0, "createdAt": 0 }"""))
            .GetPersonAsync("u1");

        Assert.NotNull(person);
        Assert.Null(person.Counts);
        Assert.Null(person.Listening);
        Assert.False(person.HasBanner);
    }

    [Fact]
    public async Task A_person_feed_is_asked_by_id_with_its_own_cap()
    {
        var handler = new Answering("""{ "items": [], "next": null }""");
        await Client(handler).GetPersonFeedAsync("a b", before: 77, limit: 100);

        Assert.Equal($"/profiles/a%20b/feed?limit={RpmCommunityClient.LongestPersonFeedPage}&before=77", handler.Asked);
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

    private static RpmCommunityClient Client(HttpMessageHandler handler, string? token = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.test/") },
            _ => Task.FromResult(token));

    private static RpmReply Reply(string id, string parent) =>
        new("p", id, parent, "u", "Ann", false, id, 0, 0, 0, 0, 0);

    [Fact]
    public void A_reply_tree_puts_each_reply_under_its_parent_and_keeps_orphans()
    {
        // b answers a; c answers the post; d answers a reply that is not in the list.
        var tree = new RpmReplyTree("p", [Reply("a", "p"), Reply("b", "a"), Reply("c", "p"), Reply("d", "gone")]);

        Assert.Equal(["a:0", "b:1", "c:0", "d:0"], tree.Lines.Select(l => $"{l.Reply.ReplyId}:{l.Depth}"));
    }

    [Fact]
    public async Task A_post_is_sent_with_its_song()
    {
        var handler = new Answering(
            """
            { "ownerId": "u", "postId": "p", "authorName": "Ann", "hasAvatar": false, "body": "Hi", "createdAt": 1,
            "ups": 0, "downs": 0, "score": 0, "myVote": 0, "replyCount": 0 }
            """);
        var post = await Client(handler, "t").CreatePostAsync("Hi", new RpmSong("Can", "Vitamin C", "Ege Bamyasi"));

        Assert.Equal("p", post.PostId);
        Assert.Equal("POST", handler.Method);
        Assert.Equal("/profile/posts", handler.Asked);
        Assert.Contains("\"body\":\"Hi\"", handler.Sent, StringComparison.Ordinal);
        Assert.Contains("\"song\":{\"artist\":\"Can\"", handler.Sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_names_its_parent_and_a_report_names_the_post()
    {
        var handler = new Answering(
            """
            { "postId": "p", "replyId": "r", "parentId": "q", "userId": "u", "authorName": "Ann", "hasAvatar": false,
            "body": "Yes", "createdAt": 1, "ups": 0, "downs": 0, "score": 0, "myVote": 0 }
            """);
        var client = Client(handler, "t");

        var reply = await client.ReplyAsync("o w", "p", "q", "Yes");
        Assert.Equal("r", reply.ReplyId);
        Assert.Equal("/profiles/o%20w/posts/p/replies", handler.Asked);
        Assert.Contains("\"parentId\":\"q\"", handler.Sent, StringComparison.Ordinal);

        await client.ReportPostAsync("o", "p", "r", "spam");
        Assert.Equal("/reports", handler.Asked);
        Assert.Contains("\"targetType\":\"reply\"", handler.Sent, StringComparison.Ordinal);
        Assert.Contains("\"ownerId\":\"o\"", handler.Sent, StringComparison.Ordinal);
        Assert.Contains("\"targetId\":\"r\"", handler.Sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_notice_page_keeps_the_kinds_it_knows()
    {
        var handler = new Answering(
            """
            { "items": [
            { "id": "1", "kind": "reply", "fromUserId": "u", "fromName": "Ann", "fromHasAvatar": true, "postId": "p" },
            { "id": "2", "kind": "poke", "fromUserId": "u", "fromName": "Ann", "fromHasAvatar": true },
            { "id": "3", "kind": "badge", "fromUserId": "me", "fromName": "Me", "fromHasAvatar": false, "badgeId": "rock" } ],
            "unread": 2, "next": "3" }
            """);
        var page = await Client(handler, "t").GetNoticesAsync(null);

        Assert.Equal(2, page.Unread);
        Assert.Equal("3", page.Next);
        Assert.Equal(["1", "3"], page.Items!.Where(n => n.Known).Select(n => n.Id));
        Assert.Equal("rock", page.Items![2].BadgeId);
    }

    [Fact]
    public async Task Choosing_a_badge_sends_no_unlocked_ids()
    {
        var handler = new Answering("""{ "userId": "u", "displayName": "Ann", "hasAvatar": false, "avatarUpdatedAt": 0, "createdAt": 0 }""");
        await Client(handler, "t").SetBadgeAsync("rock", "#2563EB");

        Assert.Equal("PUT", handler.Method);
        Assert.Contains("\"unlocked\":[]", handler.Sent, StringComparison.Ordinal);
        Assert.Contains("\"badge\":\"rock\"", handler.Sent, StringComparison.Ordinal);
    }

    /// <summary>Answers every request with <paramref name="json"/>, or 404 when it is null, and notes the
    /// address asked.</summary>
    private sealed class Answering(string? json) : HttpMessageHandler
    {
        public string? Asked { get; private set; }

        public string? Method { get; private set; }

        public string? Sent { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Asked = request.RequestUri?.PathAndQuery;
            Method = request.Method.Method;
            Sent = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
        }
    }
}
