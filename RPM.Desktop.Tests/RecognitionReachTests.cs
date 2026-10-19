using RPM.Desktop.Presentation.Main.Models;
using RPM.Desktop.Services.Account;

namespace RPM.Desktop.Tests;

/// <summary>
/// How far a recognition run may reach, given what is known about the account.
///
/// <para>This is the whole of the entitlement, now that the paid and free options are gone. The question it
/// answers, "who gets the advanced layer", has exactly one place to be wrong.</para>
/// </summary>
public class RecognitionReachTests
{
    private static SupporterService.Status Supporter => new(true, DateTimeOffset.UtcNow.AddMonths(6));
    private static SupporterService.Status NotSupporter => new(false, null);

    [Fact]
    public void A_supporter_reaches_the_advanced_layer()
    {
        Assert.True(RecognitionReach.For(Supporter, advancedLayerExists: true).UsesAdvanced);
    }

    [Fact]
    public void Anybody_else_gets_the_basic_ladder()
    {
        Assert.False(RecognitionReach.For(NotSupporter, advancedLayerExists: true).UsesAdvanced);
    }

    /// <summary>Signed out, or offline. The server is the one that refuses; guessing "no" here would
    /// deny a supporter over a dropped request.</summary>
    [Fact]
    public void An_unread_standing_still_reaches()
    {
        Assert.True(RecognitionReach.For(null, advancedLayerExists: true).UsesAdvanced);
    }

    /// <summary>A build with no clip backend has nothing to reach for, whoever is asking.</summary>
    [Fact]
    public void No_advanced_layer_in_the_build_means_basic_for_everyone()
    {
        Assert.False(RecognitionReach.For(Supporter, advancedLayerExists: false).UsesAdvanced);
        Assert.False(RecognitionReach.For(null, advancedLayerExists: false).UsesAdvanced);
    }

    [Fact]
    public void Basic_is_basic()
    {
        Assert.False(RecognitionReach.Basic.UsesAdvanced);
    }

    // ── the split window's Recognize selected ──

    /// <summary>A recording of a record needs the advanced layer to be named at all, so the split
    /// window offers recognition to supporters, with it.</summary>
    [Fact]
    public void The_split_window_offers_a_supporter_the_advanced_layer()
    {
        Assert.True(RecognitionReach.ForSplit(Supporter, advancedLayerExists: true)?.UsesAdvanced);
    }

    [Fact]
    public void The_split_window_offers_nothing_to_anybody_else()
    {
        Assert.Null(RecognitionReach.ForSplit(NotSupporter, advancedLayerExists: true));
    }

    /// <summary>The opposite of <see cref="An_unread_standing_still_reaches"/>, on purpose: this decides
    /// whether a button is shown, not how far a run reaches.</summary>
    [Fact]
    public void The_split_window_offers_nothing_on_an_unread_standing()
    {
        Assert.Null(RecognitionReach.ForSplit(null, advancedLayerExists: true));
    }

    [Fact]
    public void The_split_window_offers_nothing_without_an_advanced_layer()
    {
        Assert.Null(RecognitionReach.ForSplit(Supporter, advancedLayerExists: false));
    }
}
