using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// What one install is to another: a key, and a name derived from it.
///
/// <para>These tests check that nobody else can use the name. A device id is the shortened public
/// key, so "that is machine X" and "that is the holder of key K" mean the same thing. That is why the
/// trust list can be a list of keys and the beacon can be untrusted.</para>
/// </summary>
public class PeerIdentityTests
{
    [Fact]
    public void An_identity_survives_being_written_down_and_read_back()
    {
        using var made = PeerIdentity.Create();
        using var loaded = PeerIdentity.Import(made.Export());

        Assert.Equal(made.DeviceId, loaded.DeviceId);
        Assert.Equal(made.PublicKey, loaded.PublicKey);
    }

    [Fact]
    public void Two_installs_are_two_devices()
    {
        using var one = PeerIdentity.Create();
        using var other = PeerIdentity.Create();

        Assert.NotEqual(one.DeviceId, other.DeviceId);
    }

    [Fact]
    public void The_device_id_is_the_key_shortened_so_it_cannot_be_claimed()
    {
        using var identity = PeerIdentity.Create();

        Assert.Equal(identity.DeviceId, PeerIdentity.IdFor(identity.PublicKey));
    }

    [Fact]
    public void A_signature_checks_out_against_the_key_that_made_it()
    {
        using var identity = PeerIdentity.Create();
        var data = "the challenge"u8.ToArray();

        Assert.True(PeerIdentity.Verify(identity.PublicKey, data, identity.Sign(data)));
    }

    [Fact]
    public void A_signature_from_somebody_else_does_not()
    {
        using var mine = PeerIdentity.Create();
        using var theirs = PeerIdentity.Create();
        var data = "the challenge"u8.ToArray();

        Assert.False(PeerIdentity.Verify(mine.PublicKey, data, theirs.Sign(data)));
    }

    [Fact]
    public void A_signature_over_something_else_does_not()
    {
        using var identity = PeerIdentity.Create();

        Assert.False(PeerIdentity.Verify(
            identity.PublicKey, "another challenge"u8, identity.Sign("the challenge"u8)));
    }

    [Fact]
    public void A_key_that_cannot_be_read_is_a_peer_that_is_not_believed()
    {
        using var identity = PeerIdentity.Create();
        var data = "the challenge"u8.ToArray();

        Assert.False(PeerIdentity.Verify([1, 2, 3], data, identity.Sign(data)));
        Assert.False(PeerIdentity.Verify(identity.PublicKey, data, [1, 2, 3]));
    }

    [Fact]
    public void An_account_fingerprint_is_the_same_everywhere_and_is_not_the_account()
    {
        const string userId = "eu-west-1:4f0c1b7e-0000-4a11-9b2f-1a2b3c4d5e6f";

        var fingerprint = PeerIdentity.AccountFingerprint(userId);

        Assert.Equal(fingerprint, PeerIdentity.AccountFingerprint(userId));
        Assert.NotEqual(userId, fingerprint);
        Assert.DoesNotContain(userId, fingerprint, StringComparison.Ordinal);
    }

    [Fact]
    public void Signed_out_has_no_fingerprint()
    {
        Assert.Equal("", PeerIdentity.AccountFingerprint(null));
        Assert.Equal("", PeerIdentity.AccountFingerprint("  "));
    }

    [Fact]
    public void A_key_makes_the_round_trip_through_a_txt_record()
    {
        using var identity = PeerIdentity.Create();

        var text = SharingProtocol.ToBase64Url(identity.PublicKey);

        Assert.DoesNotContain("+", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/", text, StringComparison.Ordinal);
        Assert.DoesNotContain("=", text, StringComparison.Ordinal);
        Assert.Equal(identity.PublicKey, SharingProtocol.FromBase64Url(text));
    }

    [Fact]
    public void Nonsense_off_the_network_reads_as_no_key_rather_than_throwing()
    {
        Assert.Null(SharingProtocol.FromBase64Url("not base 64 url!!"));
        Assert.Null(SharingProtocol.FromBase64Url(""));
        Assert.Null(SharingProtocol.FromBase64Url(null));
    }
}
