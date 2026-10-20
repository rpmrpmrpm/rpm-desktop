using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>Which computers get a row under "On this network", and what each row offers.</summary>
public class NearbyComputersTests
{

    private static DiscoveredPeer Seen(PeerIdentity identity, string name = "Anna's iMac", int? version = null) => new()
    {
        DeviceId = identity.DeviceId,
        Name = name,
        PublicKey = identity.PublicKey,
        Host = "192.168.1.7",
        Port = 51733,
        Version = version ?? SharingProtocol.Version,
    };

    private static Pairing Asked(PeerIdentity identity, bool dismissed = false) => new()
    {
        Peer = new PairingPeer(identity.DeviceId, "from the request", "", identity.PublicKey),
        Direction = PairingDirection.Asked,
        Attempt = 1,
        State = PairingState.ShowingCode,
        Dismissed = dismissed,
    };

    private static IReadOnlyList<NearbyComputer> Build(
        IReadOnlyList<DiscoveredPeer> seen, IReadOnlyList<TrustedPeer>? trusted = null,
        IReadOnlyList<Pairing>? pairings = null) =>
        NearbyComputers.Build(seen, "me", trusted ?? [], [], pairings ?? []);

    [Fact]
    public void An_unpaired_computer_can_be_asked()
    {
        using var anna = PeerIdentity.Create();

        var row = Assert.Single(Build([Seen(anna)]));

        Assert.Equal(NearbyKind.CanAsk, row.Kind);
        Assert.Null(row.Pairing);
    }

    [Fact]
    public void A_paired_computer_has_no_row_here()
    {
        using var anna = PeerIdentity.Create();
        var trusted = new TrustedPeer { DeviceId = anna.DeviceId, PublicKey = anna.PublicKey, Name = "Anna" };

        Assert.Empty(Build([Seen(anna)], [trusted]));
    }

    [Fact]
    public void A_computer_on_another_version_says_which_one_needs_updating()
    {
        using var older = PeerIdentity.Create();
        using var newer = PeerIdentity.Create();

        var rows = Build([
            Seen(older, "older", SharingProtocol.Version - 1),
            Seen(newer, "newer", SharingProtocol.Version + 1)]);

        Assert.Equal(NearbyKind.NeedsUpdateThere, rows.Single(r => r.Name == "older").Kind);
        Assert.Equal(NearbyKind.NeedsUpdateHere, rows.Single(r => r.Name == "newer").Kind);
    }

    [Fact]
    public void A_paired_computer_that_fell_behind_is_shown_so_somebody_can_update_it()
    {
        using var anna = PeerIdentity.Create();
        var trusted = new TrustedPeer { DeviceId = anna.DeviceId, PublicKey = anna.PublicKey, Name = "Anna" };

        var row = Assert.Single(Build([Seen(anna, version: SharingProtocol.Version - 1)], [trusted]));

        Assert.Equal(NearbyKind.NeedsUpdateThere, row.Kind);
    }

    [Fact]
    public void A_pairing_is_the_row_while_it_stands_even_off_the_beacon()
    {
        using var anna = PeerIdentity.Create();

        var row = Assert.Single(Build([], pairings: [Asked(anna)]));

        Assert.Equal(NearbyKind.Pairing, row.Kind);
        Assert.Equal(PairingState.ShowingCode, row.Pairing!.State);
    }

    [Fact]
    public void A_dismissed_pairing_gives_the_row_back_to_asking()
    {
        using var anna = PeerIdentity.Create();

        var row = Assert.Single(Build([Seen(anna)], pairings: [Asked(anna, dismissed: true)]));

        Assert.Equal(NearbyKind.CanAsk, row.Kind);
    }

    [Fact]
    public void With_five_places_taken_a_new_computer_cannot_be_asked()
    {
        using var anna = PeerIdentity.Create();
        var full = Enumerable.Range(0, PeerTrust.Limit)
            .Select(i => new TrustedPeer { DeviceId = $"p{i}", PublicKey = [(byte)i], Name = $"p{i}" })
            .ToList();

        Assert.Equal(NearbyKind.Full, Assert.Single(Build([Seen(anna)], full)).Kind);
    }

    [Fact]
    public void This_computer_and_a_beacon_that_does_not_add_up_have_no_row()
    {
        using var anna = PeerIdentity.Create();
        using var other = PeerIdentity.Create();

        var lying = Seen(anna) with { PublicKey = other.PublicKey };
        var self = Seen(anna) with { DeviceId = "me", PublicKey = anna.PublicKey };

        Assert.Empty(Build([lying]));
        Assert.DoesNotContain(NearbyComputers.Build([self], "me", [], [], []), r => r.DeviceId == "me");
    }
}
