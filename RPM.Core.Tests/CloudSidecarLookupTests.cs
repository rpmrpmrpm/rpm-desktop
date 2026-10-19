using System.Net;
using System.Text;
using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// Asking the store for a track's sidecar when the store has never heard of that track.
///
/// <para>
/// This is the ordinary case, not a rare one. A borrowed record on a friend's shelf has their track
/// id, and this account has no sidecar under it; a wall of forty such records asks forty times. Like
/// every other lookup on the client (the album cover, the disc label, the stream URL), this one
/// reads 404 as "there is none". Throwing instead has two costs. The debugger prints a first-chance
/// exception per tile. And the caller records "blank" on the line after the fetch, so an exception
/// skips that line and every scroll asks the network again.
/// </para>
/// </summary>
public class CloudSidecarLookupTests
{
    /// <summary>Answers the sidecar endpoint with one status, and the presigned URL it hands out with
    /// the sidecar body.</summary>
    private sealed class FakeStore(HttpStatusCode status, string? sidecarJson = null) : HttpMessageHandler
    {
        public int Asks { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (path.Contains("/blob/"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(sidecarJson ?? "{}", Encoding.UTF8, "application/json"),
                });

            Asks++;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    status == HttpStatusCode.OK ? """{"url":"https://blobs.test/blob/one"}""" : """{"error":"no"}""",
                    Encoding.UTF8, "application/json"),
            });
        }
    }

    private static RpmCloudClient Client(FakeStore store) =>
        new(new HttpClient(store) { BaseAddress = new Uri("https://api.test/") },
            _ => Task.FromResult<string?>("token"));

    private static string Sidecar() => new TagSidecar
    {
        RelativePath = "Band/Record/01 One.flac",
        Extension = ".flac",
        Title = "One",
    }.ToJson();

    [Fact]
    public async Task A_sidecar_that_is_there_comes_back()
    {
        var found = await Client(new FakeStore(HttpStatusCode.OK, Sidecar())).GetSidecarAsync("rpm_one");

        Assert.NotNull(found);
        Assert.Equal("One", found.Title);
    }

    [Fact]
    public async Task A_track_the_store_never_heard_of_is_no_sidecar_rather_than_a_failure()
    {
        // The answer for a borrowed record, once per album on a shelf. The callers expect null:
        // RestoreAsync turns it into its own message, and the art service reads it as "this album has
        // no picture" and stops asking.
        Assert.Null(await Client(new FakeStore(HttpStatusCode.NotFound)).GetSidecarAsync("rpm_theirs"));
    }

    [Fact]
    public async Task The_store_being_down_is_still_a_failure()
    {
        // The important difference: "there is none" is a fact about the record, and it is stored. An
        // outage is not. Reading an outage as "no picture" would blank a wall of albums that do have
        // covers for the rest of the session.
        var store = new FakeStore(HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<HttpRequestException>(() => Client(store).GetSidecarAsync("rpm_mine"));
    }
}
