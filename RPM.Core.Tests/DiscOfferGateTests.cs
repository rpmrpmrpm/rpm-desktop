using RPM.Core.Ripping;

namespace RPM.Core.Tests;

/// <summary>
/// The rule behind "a disc went in, rip it?": ask once, and only when asking is useful.
///
/// <para>Most of these tests check that the gate does NOT ask. The drive is polled every few seconds
/// for ever, so each case here is a poll that must not show a question: the disc is already being
/// ripped, the user already said no, nothing has changed since the last poll. If any of them is
/// wrong, the app asks about the same disc every four seconds, which is the main way a watcher like
/// this can fail.</para>
/// </summary>
public class DiscOfferGateTests
{
    [Fact]
    public void A_disc_appearing_is_asked_about()
    {
        var gate = new DiscOfferGate();

        Assert.False(gate.Poll(present: false, busy: false));
        Assert.True(gate.Poll(present: true, busy: false));
    }

    [Fact]
    public void A_disc_that_stays_in_the_drive_is_asked_about_once()
    {
        var gate = new DiscOfferGate();

        Assert.True(gate.Poll(present: true, busy: false));
        Assert.False(gate.Poll(present: true, busy: false));
        Assert.False(gate.Poll(present: true, busy: false));
    }

    /// <summary>"Busy" means the question is being answered: the offer is on screen, or the disc is
    /// being ripped. Either way the user has already been asked.</summary>
    [Fact]
    public void A_disc_being_dealt_with_is_not_asked_about()
    {
        var gate = new DiscOfferGate();

        Assert.False(gate.Poll(present: true, busy: true));
        Assert.False(gate.Poll(present: true, busy: false));
    }

    /// <summary>The end of a rip leaves the disc in the drive and the app idle again. That must not
    /// read as a disc arriving, or every rip would end by offering to do it again.</summary>
    [Fact]
    public void A_rip_finishing_does_not_offer_the_same_disc_again()
    {
        var gate = new DiscOfferGate();

        Assert.True(gate.Poll(present: true, busy: false));   // asked
        Assert.False(gate.Poll(present: true, busy: true));   // ripping
        Assert.False(gate.Poll(present: true, busy: false));  // done, disc still in
    }

    [Fact]
    public void The_next_disc_is_a_new_question()
    {
        var gate = new DiscOfferGate();

        Assert.True(gate.Poll(present: true, busy: false));
        Assert.False(gate.Poll(present: false, busy: false));  // ejected
        Assert.True(gate.Poll(present: true, busy: false));    // another one goes in
    }

    /// <summary>This includes the same disc going back in: nothing here identifies a disc, and taking
    /// one out and putting it back in is a deliberate action by the user.</summary>
    [Fact]
    public void A_disc_refused_and_put_back_is_asked_about_again()
    {
        var gate = new DiscOfferGate();

        Assert.True(gate.Poll(present: true, busy: false));
        Assert.False(gate.Poll(present: true, busy: false));   // "not now"
        Assert.False(gate.Poll(present: false, busy: false));
        Assert.True(gate.Poll(present: true, busy: false));
    }

    [Fact]
    public void An_empty_drive_is_never_a_question()
    {
        var gate = new DiscOfferGate();

        Assert.False(gate.Poll(present: false, busy: false));
        Assert.False(gate.Poll(present: false, busy: true));
        Assert.False(gate.Poll(present: false, busy: false));
    }
}
