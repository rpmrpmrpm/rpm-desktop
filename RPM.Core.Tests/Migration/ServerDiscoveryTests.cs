using System.Net;
using System.Net.Sockets;
using System.Text;
using RPM.Core.Migration;

namespace RPM.Core.Tests;

/// <summary>Finding media servers on the network, so the person picks one instead of typing its address.</summary>
public class ServerDiscoveryTests
{
    [Fact]
    public async Task Jellyfin_answers_the_broadcast_with_its_address_and_name()
    {
        using var server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)server.Client.LocalEndPoint!).Port;
        var answered = Task.Run(async () =>
        {
            var asked = await server.ReceiveAsync();
            Assert.Equal("who is JellyfinServer?", Encoding.UTF8.GetString(asked.Buffer));
            var reply = """{"Address":"http://192.168.1.20:8096","Id":"f1e2d3c4","Name":"Living room","EndpointAddress":null}"""u8.ToArray();
            await server.SendAsync(reply, asked.RemoteEndPoint);
        });

        var found = await ServerDiscovery.AskJellyfinAsync(new IPEndPoint(IPAddress.Loopback, port), TimeSpan.FromSeconds(2), CancellationToken.None);
        await answered;

        Assert.Equal([new FoundServer(SourceKind.Jellyfin, "Living room", new Uri("http://192.168.1.20:8096"))], found);
    }

    [Fact]
    public async Task A_server_on_this_computer_is_found_by_its_usual_port()
    {
        var http = new FixtureHttp(r => (r.RequestUri!.Host, r.RequestUri.Port, r.RequestUri.AbsolutePath) switch
        {
            ("localhost", 8096, "/System/Info/Public") => Json("""{"ServerName":"Mac Jellyfin","Id":"abc"}"""),
            ("localhost", 4533, "/rest/ping") => Json("""{"subsonic-response":{"status":"failed","type":"navidrome","error":{"code":10}}}"""),
            _ => FixtureHttp.Status(HttpStatusCode.NotFound),
        });

        var jellyfin = await ServerDiscovery.OnThisComputerAsync(SourceKind.Jellyfin, http.Client(), CancellationToken.None);
        var navidrome = await ServerDiscovery.OnThisComputerAsync(SourceKind.Navidrome, http.Client(), CancellationToken.None);

        Assert.Equal(new FoundServer(SourceKind.Jellyfin, "Mac Jellyfin", new Uri("http://localhost:8096")), jellyfin);
        Assert.Equal(new FoundServer(SourceKind.Navidrome, "Navidrome", new Uri("http://localhost:4533")), navidrome);
    }

    [Fact]
    public async Task Nothing_on_the_usual_port_is_nothing_found()
    {
        var http = new FixtureHttp(_ => FixtureHttp.Status(HttpStatusCode.NotFound));
        Assert.Null(await ServerDiscovery.OnThisComputerAsync(SourceKind.Navidrome, http.Client(), CancellationToken.None));
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
