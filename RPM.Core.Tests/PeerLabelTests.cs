using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// What a computer is called, on both ends of the wire.
///
/// <para>Two rules, tested here because they decide everything a user reads when another machine
/// appears: get the order wrong and a friend appears as GIOVANNI-PC-WIN, or as a name they replaced
/// months ago.</para>
/// </summary>
public class PeerLabelTests
{
    private static PeerFace Face(string name) => new(name, null);

    // ── what to call somebody else ──

    [Fact]
    public void The_profile_is_the_first_answer()
    {
        Assert.Equal(
            "Giovanni",
            PeerLabel.For(Face("Giovanni"), broadcastFace: "Gio", machineName: "GIOVANNI-PC-WIN"));
    }

    [Fact]
    public void What_they_broadcast_stands_in_until_the_profile_lands()
    {
        // The pairing case: nobody has an account id for them yet, so there is nothing to look up.
        Assert.Equal(
            "Giovanni",
            PeerLabel.For(PeerFace.Nobody, broadcastFace: "Giovanni", machineName: "GIOVANNI-PC-WIN"));
    }

    [Fact]
    public void The_machine_is_the_last_answer()
    {
        Assert.Equal(
            "GIOVANNI-PC-WIN",
            PeerLabel.For(PeerFace.Nobody, broadcastFace: "", machineName: "GIOVANNI-PC-WIN"));
    }

    [Fact]
    public void A_broadcast_of_nothing_but_spaces_is_a_broadcast_of_nothing()
    {
        Assert.Equal(
            "GIOVANNI-PC-WIN",
            PeerLabel.For(PeerFace.Nobody, broadcastFace: "   ", machineName: "GIOVANNI-PC-WIN"));
    }

    [Fact]
    public void An_older_peer_that_says_nothing_still_gets_named()
    {
        // A build that does not send a display name sends no face at all. That is not an error and it
        // is not blank on screen: it shows the machine name.
        Assert.Equal("OLD-LAPTOP", PeerLabel.For(PeerFace.Nobody, "", "OLD-LAPTOP"));
    }

    // ── what to let this machine be called ──

    [Fact]
    public void Signed_in_and_unnamed_the_display_name_goes_out()
    {
        var (name, face) = PeerLabel.Announce(typedName: null, profileName: "Alex", machineName: "ALEX-MBP");

        Assert.Equal("ALEX-MBP", name);
        Assert.Equal("Alex", face);
    }

    [Fact]
    public void A_typed_name_is_the_whole_answer()
    {
        // A user who has typed into "This computer is called" has said what they want to be called.
        // The profile name must not be sent without their knowledge and win on the other end.
        var (name, face) = PeerLabel.Announce("Studio Mac", "Alex", "ALEX-MBP");

        Assert.Equal("Studio Mac", name);
        Assert.Equal("", face);
    }

    [Fact]
    public void A_typed_name_is_trimmed_and_blank_does_not_count_as_typed()
    {
        Assert.Equal("Studio Mac", PeerLabel.Announce("  Studio Mac  ", "Alex", "ALEX-MBP").Name);
        Assert.Equal("Alex", PeerLabel.Announce("   ", "Alex", "ALEX-MBP").Face);
    }

    [Fact]
    public void Signed_out_there_is_no_face_to_broadcast()
    {
        var (name, face) = PeerLabel.Announce(null, profileName: null, machineName: "ALEX-MBP");

        Assert.Equal("ALEX-MBP", name);
        Assert.Equal("", face);
    }

    [Fact]
    public void The_two_ends_agree()
    {
        // The important round trip: what this machine announces is what the other end reads back for
        // it, before either has looked up an account.
        var (name, face) = PeerLabel.Announce(null, "Alex", "ALEX-MBP");

        Assert.Equal("Alex", PeerLabel.For(PeerFace.Nobody, face, name));
    }
}
