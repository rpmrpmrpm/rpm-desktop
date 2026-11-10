using System.Text;
using System.Text.Json;
using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// The key, session and media ticket rules checked against values made outside .NET, with openssl and
/// Python. The collection daemon in Rust checks the same file, so the two implementations of the
/// protocol cannot drift apart without one of these tests failing.
/// </summary>
public class PeerVectorTests
{
    private static JsonElement Vectors()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Sharing", "peer-vectors.json");
        return JsonDocument.Parse(File.ReadAllText(path)).RootElement;
    }

    private static string S(JsonElement v, string name) => v.GetProperty(name).GetString()!;

    [Fact]
    public void Device_id_and_public_key_match_the_vector_file()
    {
        var v = Vectors();
        using var identity = PeerIdentity.Import(SharingProtocol.FromBase64Url(S(v, "pkcs8"))!);

        Assert.Equal(S(v, "publicKey"), SharingProtocol.ToBase64Url(identity.PublicKey));
        Assert.Equal(S(v, "deviceId"), identity.DeviceId);
    }

    [Fact]
    public void Signed_bytes_and_signature_match_the_vector_file()
    {
        var v = Vectors();
        var data = PeerSessions.SignedBytes(S(v, "serverDeviceId"), S(v, "challenge"), S(v, "account"));

        Assert.Equal(S(v, "data"), SharingProtocol.ToBase64Url(data));
        Assert.True(PeerIdentity.Verify(
            SharingProtocol.FromBase64Url(S(v, "publicKey"))!, data,
            SharingProtocol.FromBase64Url(S(v, "signature"))!));
    }

    [Fact]
    public void Media_handle_and_ticket_match_the_vector_file()
    {
        var v = Vectors();
        var token = S(v, "token");

        Assert.Equal(S(v, "handle"), PeerSessions.Handle(token));
        Assert.Equal(S(v, "ticket"), PeerSessions.Ticket(token, S(v, "ticketRoute"), S(v, "ticketId")));
    }

    [Fact]
    public void A_signature_made_here_verifies_with_the_vector_key()
    {
        var v = Vectors();
        using var identity = PeerIdentity.Import(SharingProtocol.FromBase64Url(S(v, "pkcs8"))!);
        var data = Encoding.UTF8.GetBytes("anything");

        var signature = identity.Sign(data);

        Assert.Equal(64, signature.Length);
        Assert.True(PeerIdentity.Verify(identity.PublicKey, data, signature));
    }
}
