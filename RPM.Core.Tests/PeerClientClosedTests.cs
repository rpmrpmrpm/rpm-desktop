using System.Net;
using System.Net.Http;
using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// What a peer's client does after it has been closed.
///
/// <para>A client is shared by everything that reads one machine, and five separate events can close
/// it: the peer going quiet, discovery saying it is gone, the user stopping the share, sharing being
/// switched off, and the app closing. The pairing's own client is closed when the dialog closes. So a
/// call on a closed client is not a mistake that can be designed away: it is a race that will keep
/// happening, and the only question is what the call returns.</para>
///
/// <para>It must not throw <see cref="ObjectDisposedException"/> from the middle of a catalogue pull,
/// several frames below any networking code, which would kill the pull with a warning in the log. It
/// returns nothing, quietly.</para>
/// </summary>
public class PeerClientClosedTests
{
    /// <summary>Answers nothing, slowly enough to still be answering when the test closes the client.
    /// Never reached in the tests below that close first: the point is that no call gets that far.</summary>
    private sealed class Silent : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellation)
        {
            await Task.Delay(Timeout.Infinite, cancellation);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static PeerClient Closed()
    {
        var client = new PeerClient(PeerIdentity.Create(), "192.0.2.1", 51733, [1, 2, 3], new Silent());
        client.Dispose();
        return client;
    }

    // ── the premise ──

    [Fact]
    public async Task The_library_itself_throws_on_a_closed_client()
    {
        // Confirms what the tests below are about, so none of them can pass because nothing throws.
        var http = new HttpClient(new Silent());
        http.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => http.GetAsync("https://192.0.2.1:51733/"));
    }

    // ── every way in answers the same ──

    [Fact]
    public async Task A_closed_client_says_the_machine_did_not_answer()
    {
        Assert.Null(await Closed().HelloAsync());
    }

    [Fact]
    public async Task A_closed_client_has_no_listing()
    {
        // The full path: CatalogAsync, through TokenAsync and DeviceIdAsync, into HelloAsync.
        Assert.Null(await Closed().CatalogAsync(have: null));
    }

    [Fact]
    public async Task A_closed_client_has_no_token()
    {
        Assert.Null(await Closed().TokenAsync());
    }

    [Fact]
    public async Task A_closed_client_has_no_url_for_a_record()
    {
        Assert.Null(await Closed().TrackUrlAsync("rpm-id"));
    }

    [Fact]
    public async Task A_closed_client_will_not_say_whose_account_it_is()
    {
        Assert.Null(await Closed().AccountAsync());
    }

    [Fact]
    public async Task A_closed_client_has_no_picture()
    {
        Assert.Null(await Closed().ImageAsync(SharingProtocol.CoverRoute, "rpm-id"));
    }

    [Fact]
    public async Task A_cancelled_pairing_gets_no_answer()
    {
        // A pairing run is disposed while its status call is held open on the other machine, so
        // this is the most likely case of all.
        Assert.Null(await Closed().PairStatusAsync("id", 1, CancellationToken.None));
        var asker = new PeerClient.PairingAsker(Closed());
        var round = new PairStatus { State = PairStatusKind.ShowingCode, Round = 1, Commit = SharingProtocol.ToBase64Url([1]) };
        Assert.Null(await asker.JoinAsync("id", round, CancellationToken.None));
        Assert.Null(await asker.TypeAsync("id", "123456", CancellationToken.None));
        Assert.False(await Closed().PairWithdrawAsync("id", CancellationToken.None));
        Assert.Equal(PairDelivery.Undelivered,
            (await Closed().PairAsync("laptop", "", "", PairingCode.Nonce(), CancellationToken.None)).Delivery);
    }

    // ── closing one twice does nothing ──

    [Fact]
    public async Task Closing_a_client_that_is_already_closed_is_nothing()
    {
        var client = Closed();
        client.Dispose();

        Assert.Null(await client.HelloAsync());
    }
}
