using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using static RPM.Core.Migration.MediaServerHttp;

namespace RPM.Core.Migration;

/// <summary>A media server found on the network: what it calls itself, and the address to sign in at.</summary>
public sealed record FoundServer(SourceKind Kind, string Name, Uri Address);

/// <summary>
/// Finds media servers, so the person picks one from a list instead of typing its address.
///
/// <para>Jellyfin answers a UDP broadcast on port 7359 with its address and name. A broadcast does not
/// reach a server in a container on this computer, so this computer's usual port is asked as well.
/// Navidrome and other Subsonic servers have no way to be found on a network; only this computer's
/// usual port is asked. Plex is not here: its sign-in lists the servers of the person's account.</para>
///
/// <para>Finding nothing is a normal answer. The person types the address, as without this.</para>
/// </summary>
public static class ServerDiscovery
{
    /// <summary>Jellyfin's discovery port and question.</summary>
    private const int JellyfinPort = 7359;
    private static readonly byte[] JellyfinQuestion = "who is JellyfinServer?"u8.ToArray();

    /// <summary>The servers of this kind that answer within <paramref name="wait"/>, each once.</summary>
    public static async Task<IReadOnlyList<FoundServer>> FindAsync(SourceKind kind, HttpClient http, TimeSpan wait, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        var asks = new List<Task<IReadOnlyList<FoundServer>>>
        {
            OnThisComputerAsync(kind, http, ct).ContinueWith(t => (IReadOnlyList<FoundServer>)(t.IsCompletedSuccessfully && t.Result is { } s ? [s] : []), TaskScheduler.Default),
        };
        if (kind == SourceKind.Jellyfin)
            asks.Add(AskJellyfinAsync(new IPEndPoint(IPAddress.Broadcast, JellyfinPort), wait, ct));

        var found = (await Task.WhenAll(asks)).SelectMany(f => f).ToList();
        // A server on this computer also answers the broadcast, under its network address. The name and
        // the address both say it is one server; the first answer is kept.
        return [.. found.GroupBy(f => (f.Name, f.Address.Port)).Select(g => g.First())];
    }

    /// <summary>Sends Jellyfin's question to <paramref name="target"/> (the broadcast address, or one
    /// computer in tests) and collects the answers that arrive within <paramref name="wait"/>.</summary>
    internal static async Task<IReadOnlyList<FoundServer>> AskJellyfinAsync(IPEndPoint target, TimeSpan wait, CancellationToken ct)
    {
        var found = new List<FoundServer>();
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
            await udp.SendAsync(JellyfinQuestion, target, ct);
            using var until = CancellationTokenSource.CreateLinkedTokenSource(ct);
            until.CancelAfter(wait);
            while (true)
            {
                UdpReceiveResult answer;
                try { answer = await udp.ReceiveAsync(until.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { break; }

                if (JellyfinAnswer(answer.Buffer) is { } server && !found.Contains(server))
                    found.Add(server);
            }
        }
        catch (SocketException)
        {
            // No network, or broadcasts not allowed here: nothing found.
        }
        return found;
    }

    private static FoundServer? JellyfinAnswer(byte[] buffer)
    {
        try
        {
            using var json = JsonDocument.Parse(buffer);
            var address = Str(json.RootElement, "Address");
            var name = Str(json.RootElement, "Name");
            return Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
                ? new FoundServer(SourceKind.Jellyfin, name.Length > 0 ? name : uri.Host, uri)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The server of this kind on this computer's usual port (Jellyfin 8096, Navidrome 4533), or
    /// null when nothing of that kind answers there.</summary>
    public static async Task<FoundServer?> OnThisComputerAsync(SourceKind kind, HttpClient http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        var (address, path) = kind switch
        {
            SourceKind.Jellyfin => (new Uri("http://localhost:8096"), "System/Info/Public"),
            SourceKind.Navidrome => (new Uri("http://localhost:4533"), "rest/ping?f=json"),
            _ => (null, ""),
        };
        if (address is null)
            return null;

        try
        {
            using var answer = await http.GetAsync(new Uri(address, path), ct);
            if (!answer.IsSuccessStatusCode)
                return null;
            using var json = JsonDocument.Parse(await answer.Content.ReadAsStreamAsync(ct));
            if (kind == SourceKind.Jellyfin)
                return Str(json.RootElement, "Id").Length > 0
                    ? new FoundServer(kind, Str(json.RootElement, "ServerName") is { Length: > 0 } n ? n : "Jellyfin", address)
                    : null;
            // A Subsonic server answers a ping without a sign-in with an error, inside its own envelope.
            var envelope = Child(json.RootElement, "subsonic-response");
            return envelope.ValueKind == JsonValueKind.Object
                ? new FoundServer(kind, Str(envelope, "type") is "navidrome" ? "Navidrome" : "Subsonic", address)
                : null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
