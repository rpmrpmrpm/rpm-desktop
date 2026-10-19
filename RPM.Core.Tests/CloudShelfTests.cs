using System.Net;
using System.Text;
using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// Whose collection a cloud read is served from.
///
/// <para>
/// The type exists so that "whose" cannot be forgotten. Forgetting it fails silently in the worst
/// direction: a listing read without it is the caller's own library shown under somebody else's
/// name. So the tests check the requests sent, not the record: the shelf reaches every route that
/// can belong to somebody else, and the ordinary case still sends nothing at all.
/// </para>
/// </summary>
public class CloudShelfTests
{
    /// <summary>Records every path requested, and answers each route with the smallest valid
    /// response.</summary>
    private sealed class FakeStore : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        /// <summary>Whose rows this store says it is returning, or null for the caller's own.
        /// Set by a test that wants the answer to disagree with the request.</summary>
        public string? Owner { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Asked.Add(uri.PathAndQuery);

            // The listing states whose it is, which is what the client checks before keeping a row.
            var owner = Owner ?? System.Web.HttpUtility.ParseQueryString(uri.Query)["owner"];

            var body = uri.AbsolutePath switch
            {
                "/cloud/list" => $$"""{"files":[],"cursor":null,"owner":{{Named(owner)}}}""",
                "/cloud/art" => """{"albums":[{"albumKey":"d90d382db82e7ba38c335b59ae3eaf33","updatedAt":7}]}""",
                _ => """{"url":"https://blobs.test/one","kilobits":320,"updatedAt":7}""",
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>An owner as JSON: the string, or null where the read is the caller's own.</summary>
    private static string Named(string? owner) =>
        owner is null ? "null" : System.Text.Json.JsonSerializer.Serialize(owner);

    private static RpmCloudClient Client(HttpMessageHandler store) =>
        new(new HttpClient(store) { BaseAddress = new Uri("https://api.test/") },
            _ => Task.FromResult<string?>("token"));

    private const string Friend = "user-friend";
    private const string AlbumKey = "d90d382db82e7ba38c335b59ae3eaf33";
    private const string RpmId = "11111111-2222-3333-4444-555555555555";

    /// <summary>
    /// A store that does not know about shared collections is refused rather than believed.
    ///
    /// <para>An unbound query parameter is not an error in ASP.NET. A server that does not support
    /// <c>?owner=</c> ignores it and answers with the CALLER's own rows and a 200. A client that
    /// trusts that answer shows the user's own records under a friend's name and treats all their
    /// track ids as borrowed, which turns off favourites, playlists and the LAN path on the user's
    /// own music. The listing must say whose it is; a listing that does not is refused.</para>
    /// </summary>
    [Fact]
    public async Task A_listing_that_does_not_say_whose_it_is_is_refused()
    {
        var store = new OldStore();

        var refused = await Assert.ThrowsAsync<CloudShelfNotServedException>(
            () => Client(store).ListAsync(CloudShelf.Of(Friend)));

        Assert.Equal(Friend, refused.OwnerId);
    }

    [Fact]
    public async Task A_listing_of_the_callers_own_needs_no_such_answer()
    {
        // The same old server. Nothing is requested about somebody else, so there is nothing to check
        // and every released client keeps working.
        var rows = await Client(new OldStore()).ListAsync(CloudShelf.Own);

        Assert.Empty(rows);
    }

    [Fact]
    public async Task A_listing_that_names_somebody_else_is_refused()
    {
        var store = new FakeStore { Owner = "user-somebody-else" };

        await Assert.ThrowsAsync<CloudShelfNotServedException>(
            () => Client(store).ListAsync(CloudShelf.Of(Friend)));
    }

    /// <summary>A store without shared collections: it answers the listing without saying whose it
    /// is.</summary>
    private sealed class OldStore : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"files":[],"cursor":null}""", Encoding.UTF8, "application/json"),
            });
    }

    [Fact]
    public void The_default_shelf_is_the_callers_own()
    {
        Assert.True(default(CloudShelf).IsOwn);
        Assert.True(CloudShelf.Own.IsOwn);
        Assert.Null(CloudShelf.Own.Owner);
    }

    [Fact]
    public void A_shelf_has_to_belong_to_somebody()
    {
        Assert.Throws<ArgumentException>(() => CloudShelf.Of(""));
        Assert.Throws<ArgumentException>(() => CloudShelf.Of("  "));
    }

    [Fact]
    public void Somebody_elses_shelf_is_not_own_and_says_whose_it_is()
    {
        var shelf = CloudShelf.Of(Friend);

        Assert.False(shelf.IsOwn);
        Assert.Equal(Friend, shelf.Owner);
    }

    [Fact]
    public async Task The_ordinary_read_names_nobody()
    {
        var store = new FakeStore();
        var client = Client(store);

        await client.ListAsync(CloudShelf.Own);
        await client.GetStreamAsync(CloudShelf.Own, RpmId);
        await client.GetAlbumArtAsync(CloudShelf.Own, AlbumKey);
        await client.GetAlbumArtCatalogAsync(CloudShelf.Own);

        Assert.All(store.Asked, path => Assert.DoesNotContain("owner=", path));
    }

    [Fact]
    public async Task A_borrowed_read_names_the_owner_on_every_route()
    {
        var store = new FakeStore();
        var client = Client(store);
        var shelf = CloudShelf.Of(Friend);

        await client.ListAsync(shelf);
        await client.GetStreamAsync(shelf, RpmId);
        await client.GetAlbumArtAsync(shelf, AlbumKey);
        await client.GetAlbumArtCatalogAsync(shelf);

        Assert.Equal(4, store.Asked.Count);
        Assert.All(store.Asked, path => Assert.Contains($"owner={Friend}", path));
    }

    /// <summary>
    /// The listing already carries a limit and a cursor, so the shelf has to join a query rather
    /// than start one. A second '?' in a URL is a query nobody parses.
    /// </summary>
    [Fact]
    public async Task The_listing_joins_the_query_it_already_has()
    {
        var store = new FakeStore();

        await Client(store).ListAsync(CloudShelf.Of(Friend));

        var asked = Assert.Single(store.Asked);
        Assert.Equal(1, asked.Count(c => c == '?'));
        Assert.Contains("limit=2000", asked);
        Assert.Contains($"&owner={Friend}", asked);
    }
}
