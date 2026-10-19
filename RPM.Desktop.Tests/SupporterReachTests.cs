using RPM.Desktop.Presentation.NowPlaying;
using RPM.Desktop.Services.Account;

namespace RPM.Desktop.Tests;

/// <summary>
/// The gate on the features rpm enforces itself, the DJ and the word alignment, and the standing it
/// reads.
///
/// <para>Both halves are here because they are one decision split in two only so it can be tested:
/// <see cref="SupporterService.StandingFrom"/> decides what is believed about the account, and
/// <see cref="SupporterReach"/> decides what that allows. A bug in either is the same bug on screen.</para>
/// </summary>
public class SupporterReachTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 24, 21, 0, 0, TimeSpan.Zero);

    private static SupporterService.Status Supporter => new(true, Now.AddMonths(6));
    private static SupporterService.Status NotSupporter => new(false, null);

    // ── what the reach allows ──

    [Fact]
    public void A_supporter_may_dj_and_align()
    {
        var reach = SupporterReach.For(Supporter);
        Assert.True(reach.MayDj);
        Assert.True(reach.MayAlignWords);
    }

    [Fact]
    public void Anybody_else_may_not()
    {
        var reach = SupporterReach.For(NotSupporter);
        Assert.False(reach.MayDj);
        Assert.False(reach.MayAlignWords);
    }

    /// <summary>
    /// The difference from <see cref="RecognitionReachTests"/>, and the reason this is a separate type:
    /// recognition lets an unknown standing through, because the Lambda is the one that really refuses.
    /// Nothing refuses these. So an account nobody has ever been told about gets the free reach. That
    /// happens only after <see cref="SupporterService.StandingFrom"/> has also failed to find a
    /// remembered answer.
    /// </summary>
    [Fact]
    public void An_account_nobody_has_heard_of_gets_the_free_reach()
    {
        var reach = SupporterReach.For(null);
        Assert.False(reach.MayDj);
        Assert.False(reach.MayAlignWords);
    }

    [Fact]
    public void The_free_reach_allows_nothing()
    {
        Assert.False(SupporterReach.Free.MayDj);
        Assert.False(SupporterReach.Free.MayAlignWords);
    }

    // ── what is believed about the account ──

    /// <summary>What the server said this session beats anything written down, including a note that
    /// disagrees with it: support bought ten minutes ago is on the wire, not on the disk.</summary>
    [Fact]
    public void This_sessions_answer_wins()
    {
        var standing = SupporterService.StandingFrom(signedIn: true, NotSupporter, Now.AddYears(1).ToUnixTimeSeconds(), Now);
        Assert.False(standing!.IsSupporter);
    }

    /// <summary>The whole point of remembering: offline, or signed in but unable to reach the accounts
    /// service, a supporter still gets the DJ.</summary>
    [Fact]
    public void An_offline_supporter_is_still_a_supporter()
    {
        var standing = SupporterService.StandingFrom(signedIn: true, null, Now.AddMonths(2).ToUnixTimeSeconds(), Now);
        Assert.True(standing!.IsSupporter);
        Assert.True(SupporterReach.For(standing).MayDj);
    }

    /// <summary>And the limit on it: the note carries a date so it cannot keep an account going for ever
    /// by never coming back online.</summary>
    [Fact]
    public void A_remembered_supporter_runs_out_on_the_date_it_carries()
    {
        var standing = SupporterService.StandingFrom(signedIn: true, null, Now.AddDays(-1).ToUnixTimeSeconds(), Now);
        Assert.False(standing!.IsSupporter);
        Assert.False(SupporterReach.For(standing).MayDj);
    }

    /// <summary>A machine that has never been told anything. Not the same value as a lapsed account, so
    /// that a screen can tell "unknown" from "no" even though the gate treats them alike.</summary>
    [Fact]
    public void Never_having_been_told_is_null_rather_than_a_no()
    {
        Assert.Null(SupporterService.StandingFrom(signedIn: true, null, 0, Now));
    }

    /// <summary>
    /// Signed out is nobody to be a supporter, whatever the last account bought and however long it
    /// had left to run. Not the same as being unable to ask. That one is a session with an account in it,
    /// and it is the case the remembered date exists for.
    /// </summary>
    [Fact]
    public void Signing_out_is_not_the_same_as_being_unable_to_ask()
    {
        var remembered = Now.AddYears(1).ToUnixTimeSeconds();

        Assert.True(SupporterService.StandingFrom(signedIn: true, null, remembered, Now)!.IsSupporter);
        Assert.Null(SupporterService.StandingFrom(signedIn: false, null, remembered, Now));

        // Nor does an answer the server gave the last account survive the account leaving.
        Assert.Null(SupporterService.StandingFrom(signedIn: false, Supporter, remembered, Now));
    }

    /// <summary>And the whole of what that costs the window: the gold comes down and the default goes
    /// up, while the stored choice is left alone so signing back in puts it straight back.</summary>
    [Fact]
    public void A_signed_out_window_is_not_gold()
    {
        var out_ = SupporterReach.For(
            SupporterService.StandingFrom(signedIn: false, Supporter, Now.AddYears(1).ToUnixTimeSeconds(), Now));

        Assert.Equal(Backdrop.BuiltIn, Backdrop.Resolve(Backdrop.GoldId, out_));
        Assert.Equal("", Backdrop.EffectiveId(Backdrop.GoldId, out_));
        Assert.False(out_.MayDj);
        Assert.False(out_.MayAlignWords);
        Assert.False(out_.MayWearTheBadge);
    }

    // ── the supporter's backdrop ──
    //
    // The gate that is NOT on a button. A backdrop is picked once and stored, so the id outlives the
    // support that chose it: gating the picker alone would leave a lapsed supporter looking at gold for
    // ever, and would put it back behind an account that reinstalled with its old settings file. So the
    // refusal lives where the picture is resolved, which is what these pin.

    [Fact]
    public void A_supporter_gets_the_gold_backdrop()
    {
        Assert.Equal(
            Backdrop.Gold,
            Backdrop.Resolve(Backdrop.GoldId, SupporterReach.For(Supporter)));
    }

    [Fact]
    public void Anybody_else_asking_for_gold_gets_the_default()
    {
        Assert.Equal(
            Backdrop.BuiltIn,
            Backdrop.Resolve(Backdrop.GoldId, SupporterReach.For(NotSupporter)));
    }

    /// <summary>The one this exists for: support ran out, the stored id did not, and the app must fall
    /// back rather than keep serving the picture.</summary>
    [Fact]
    public void A_stored_gold_outlives_the_support_and_falls_back()
    {
        var lapsed = SupporterService.StandingFrom(signedIn: true, null, Now.AddDays(-1).ToUnixTimeSeconds(), Now);
        Assert.Equal(Backdrop.BuiltIn, Backdrop.Resolve(Backdrop.GoldId, SupporterReach.For(lapsed)));
    }

    /// <summary>Everything else is nobody's: a free backdrop does not become unavailable because the
    /// account is, and an id from a later version leaves the app lit rather than blank.</summary>
    [Fact]
    public void The_free_backdrops_are_untouched_by_the_gate()
    {
        var free = SupporterReach.Free;
        Assert.Equal(Backdrop.Pegasus, Backdrop.Resolve(Backdrop.PegasusId, free));
        Assert.Equal(Backdrop.SheetMusic, Backdrop.Resolve(Backdrop.SheetMusicId, free));
        Assert.Equal(Backdrop.SheetMusicId, Backdrop.EffectiveId(Backdrop.SheetMusicId, free));
        Assert.Equal(Backdrop.BuiltIn, Backdrop.Resolve("", free));
        Assert.Equal(Backdrop.BuiltIn, Backdrop.Resolve(null, free));
        Assert.Equal(Backdrop.BuiltIn, Backdrop.Resolve("something-from-a-later-version", free));
    }

    /// <summary>RPaMp's ground is the default. The night table is a named tile.</summary>
    [Fact]
    public void The_default_is_RPaMps_ground_and_night_is_a_named_tile()
    {
        var free = SupporterReach.Free;
        Assert.Equal(Backdrop.RPaMp, Backdrop.BuiltIn);
        Assert.Equal(Backdrop.Night, Backdrop.Resolve(Backdrop.NightId, free));
        Assert.Equal(Backdrop.NightId, Backdrop.EffectiveId(Backdrop.NightId, free));
        Assert.Contains(Backdrop.Presets, p => p.Id == Backdrop.NightId && p.Uri == Backdrop.Night);
    }

    /// <summary>A stored "rpamp" id shows the same picture and lights the Default tile, because no RPaMp
    /// tile exists.</summary>
    [Fact]
    public void A_stored_rpamp_reads_as_the_default()
    {
        var free = SupporterReach.Free;
        Assert.Equal(Backdrop.RPaMp, Backdrop.Resolve(Backdrop.RPaMpId, free));
        Assert.Equal("", Backdrop.EffectiveId(Backdrop.RPaMpId, free));
        Assert.DoesNotContain(Backdrop.Presets, p => p.Id == Backdrop.RPaMpId);
    }

    /// <summary>Exactly one preset is a supporter's, and it is the one the row marks with a heart. A
    /// second one appearing without the picker being looked at again would go unmarked.</summary>
    [Fact]
    public void Only_the_gold_preset_is_marked_as_a_supporters()
    {
        var marked = Backdrop.Presets.Where(p => p.Supporters).ToList();
        Assert.Equal(Backdrop.GoldId, Assert.Single(marked).Id);
    }

    /// <summary>
    /// What the row of tiles lights, which has to be the picture that is actually up. The stored id is
    /// deliberately left saying "gold", so it comes back with the support. Everything that DRAWS must ask
    /// what is standing, not what is stored.
    /// </summary>
    [Fact]
    public void A_lapsed_supporter_sees_the_default_tile_lit_not_the_gold_one()
    {
        var lapsed = SupporterReach.For(
            SupporterService.StandingFrom(signedIn: true, null, Now.AddDays(-1).ToUnixTimeSeconds(), Now));

        Assert.Equal("", Backdrop.EffectiveId(Backdrop.GoldId, lapsed));
        Assert.Equal(Backdrop.GoldId, Backdrop.EffectiveId(Backdrop.GoldId, SupporterReach.For(Supporter)));
    }

    /// <summary>The two answers are one decision, so they can never disagree: whatever tile is lit is a
    /// tile whose own picture is the one hanging.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void What_is_lit_and_what_is_hung_are_the_same_backdrop(bool supports)
    {
        var reach = SupporterReach.For(supports ? Supporter : NotSupporter);

        foreach (var stored in new[] { null, "", Backdrop.NightId, Backdrop.PegasusId, Backdrop.SheetMusicId,
                     Backdrop.RPaMpId, Backdrop.GoldId, "from-a-later-version" })
        {
            var lit = Backdrop.Presets.Single(p => p.Id == Backdrop.EffectiveId(stored, reach));
            Assert.Equal(Backdrop.Resolve(stored, reach), lit.Uri);
        }
    }
}
