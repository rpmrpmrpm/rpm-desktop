using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// The face of whoever is at this computer, and what happens to it when the lookup does not come
/// back.
///
/// <para>The failure is silent and lasts a whole session: if the one profile request at startup
/// times out (for example against a cold Lambda), the machine has no owner on its own row until the
/// app is restarted, although the name is in the settings and goes out on the beacon.</para>
/// </summary>
public class SelfFaceTests
{
    private const string Me = "user-1";
    private const string Somebody = "user-2";

    private static readonly long Wait = (long)SelfFace.RetryAfter.TotalMilliseconds;

    private static PeerFace Face(string name) => new(name, null);

    [Fact]
    public void The_name_written_down_stands_in_until_the_profile_lands()
    {
        var self = new SelfFace();

        var ask = self.Asked(Me, writtenName: "Giovanni", SelfLookup.Now, now: 0);

        Assert.True(ask.Fetch);
        Assert.True(ask.Changed);
        Assert.Equal("Giovanni", self.Face.DisplayName);
    }

    [Fact]
    public void A_failed_lookup_is_a_wait_and_not_a_verdict()
    {
        var self = new SelfFace();
        self.Asked(Me, "Giovanni", SelfLookup.Now, now: 0);

        self.Failed(Me, now: 0);

        // The redraw a moment later must not ask again, and the one after the wait must.
        Assert.False(self.Asked(Me, "Giovanni", SelfLookup.IfDue, now: Wait - 1).Fetch);
        Assert.True(self.Asked(Me, "Giovanni", SelfLookup.IfDue, now: Wait).Fetch);

        // And the name is still there through all of it: a failure does not mean the name changed.
        Assert.Equal("Giovanni", self.Face.DisplayName);
    }

    [Fact]
    public void A_lookup_in_the_air_is_not_asked_for_twice()
    {
        var self = new SelfFace();
        Assert.True(self.Asked(Me, null, SelfLookup.Now, now: 0).Fetch);

        // Two redraws in the same moment, and a third much later: the answer is still coming.
        Assert.False(self.Asked(Me, null, SelfLookup.IfDue, now: 0).Fetch);
        Assert.False(self.Asked(Me, null, SelfLookup.IfDue, now: Wait * 100).Fetch);
    }

    [Fact]
    public void An_answer_is_not_asked_for_again()
    {
        var self = new SelfFace();
        self.Asked(Me, null, SelfLookup.Now, now: 0);

        Assert.True(self.Learned(Me, Face("Giovanni")));

        Assert.False(self.Asked(Me, null, SelfLookup.IfDue, now: Wait * 100).Fetch);
        Assert.Equal("Giovanni", self.Face.DisplayName);
    }

    [Fact]
    public void An_account_that_just_changed_asks_whatever_happened_last_time()
    {
        var self = new SelfFace();
        self.Asked(Me, null, SelfLookup.Now, now: 0);
        self.Failed(Me, now: 0);

        // Signed in again a second later: the wait belongs to the failure, not to the person.
        Assert.True(self.Asked(Me, null, SelfLookup.Now, now: 1).Fetch);
    }

    [Fact]
    public void An_answer_for_somebody_who_has_left_is_dropped()
    {
        var self = new SelfFace();
        self.Asked(Me, null, SelfLookup.Now, now: 0);

        self.SignedOut();

        Assert.False(self.Learned(Me, Face("Giovanni")));
        Assert.Equal(PeerFace.Nobody, self.Face);
    }

    [Fact]
    public void Signing_out_is_news_once()
    {
        var self = new SelfFace();
        self.Asked(Me, "Giovanni", SelfLookup.Now, now: 0);

        Assert.True(self.SignedOut());

        // Further sign-outs after that change nothing, so they cause no redraw.
        Assert.False(self.SignedOut());
    }

    [Fact]
    public void The_next_person_does_not_inherit_the_last_one_s_name()
    {
        var self = new SelfFace();
        self.Asked(Me, "Giovanni", SelfLookup.Now, now: 0);
        self.Learned(Me, Face("Giovanni"));

        var ask = self.Asked(Somebody, writtenName: "Giovanni", SelfLookup.IfDue, now: 1);

        Assert.True(ask.ForgetWrittenName);
        Assert.True(ask.Fetch);
        Assert.Equal(PeerFace.Nobody, self.Face);
    }

    [Fact]
    public void A_face_that_did_not_move_is_not_news()
    {
        var self = new SelfFace();
        self.Asked(Me, "Giovanni", SelfLookup.Now, now: 0);
        self.Learned(Me, Face("Giovanni"));

        // The redraw that the answer caused comes straight back through here.
        var ask = self.Asked(Me, "Giovanni", SelfLookup.IfDue, now: 1);

        Assert.False(ask.Changed);
        Assert.False(ask.Fetch);
    }

    [Fact]
    public void An_account_with_no_profile_at_all_is_nobody()
    {
        var self = new SelfFace();
        self.Asked(Me, null, SelfLookup.Now, now: 0);

        // The server's answer, not a failure: nothing has been named here.
        Assert.True(self.Learned(Me, PeerFace.Nobody));
        Assert.Equal(PeerFace.Nobody, self.Face);
    }
}
