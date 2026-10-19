namespace RPM.Fingerprint.Tests;

using RateOffer = AudioLadder.RateOffer;

/// <summary>
/// What a chooser is allowed to put in front of one account.
///
/// <para>The point of the type is that what is offered and what arrives cannot disagree, so most of
/// what is checked here is that pairing: everything <see cref="RateOffer.Free"/> offers is something
/// a free account is actually served, and nothing it offers is quietly downgraded on the way out.
/// </para>
/// </summary>
public class RateOfferTests
{
    [Fact]
    public void Free_offers_only_the_rate_a_free_account_is_served()
    {
        Assert.Equal([AudioLadder.FreeKilobits], RateOffer.Free.Rungs);
        Assert.True(RateOffer.Free.Capped);
    }

    [Fact]
    public void Whole_offers_the_original_first_then_every_rung_down()
    {
        Assert.Equal([AudioLadder.Original, 320, 256, 192, 128, 96], RateOffer.Whole.Rungs);
        Assert.False(RateOffer.Whole.Capped);
    }

    [Fact]
    public void Every_rung_offered_is_a_rung_actually_served()
    {
        foreach (var offer in new[] { RateOffer.Free, RateOffer.Whole })
            foreach (var rung in offer.Rungs)
                Assert.Equal(rung, offer.Serves(rung));
    }

    [Fact]
    public void A_free_account_is_served_the_free_rate_whatever_it_has_remembered()
    {
        // The case the screen exists for: somebody picked 320 as a supporter, their support lapsed,
        // and the preference still says 320.
        Assert.Equal(AudioLadder.FreeKilobits, RateOffer.Free.Serves(320));
        Assert.Equal(AudioLadder.FreeKilobits, RateOffer.Free.Serves(AudioLadder.Original));
    }

    [Fact]
    public void A_supporter_is_served_what_they_asked_for()
    {
        Assert.Equal(320, RateOffer.Whole.Serves(320));
        Assert.Equal(AudioLadder.Original, RateOffer.Whole.Serves(AudioLadder.Original));
    }

    [Fact]
    public void A_remembered_rate_that_is_not_on_the_offer_lands_on_the_first_rung()
    {
        // 320 is off a free account's offer entirely, and the only rung there is is the one it is
        // served, so the box shows what is actually playing rather than an empty selection.
        Assert.Equal(0, RateOffer.Free.IndexOf(320));
        Assert.Equal(AudioLadder.FreeKilobits, RateOffer.Free.Rungs[RateOffer.Free.IndexOf(320)]);
    }

    [Fact]
    public void A_remembered_rate_on_the_offer_is_found_where_it_sits()
    {
        Assert.Equal(0, RateOffer.Whole.IndexOf(AudioLadder.Original));
        Assert.Equal(1, RateOffer.Whole.IndexOf(320));
        Assert.Equal(5, RateOffer.Whole.IndexOf(96));
    }

    [Fact]
    public void An_unread_standing_is_the_whole_ladder()
    {
        // Not a third state. A plan that could not be read must not read as the free one.
        Assert.Same(RateOffer.Whole, RateOffer.Unread);
        Assert.False(RateOffer.Unread.Capped);
    }

    [Fact]
    public void For_picks_the_offer_from_the_standing()
    {
        Assert.Same(RateOffer.Whole, RateOffer.For(isSupporter: true));
        Assert.Same(RateOffer.Free, RateOffer.For(isSupporter: false));
    }
}
