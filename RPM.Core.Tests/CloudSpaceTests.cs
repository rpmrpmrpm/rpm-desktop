using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// The room in the cloud, and what running out of it does.
///
/// <para>Each of these is a decision the uploader makes without asking the user: whether to send a
/// file, whether to stop, and whether a mode the app changed for the user may be changed back. They
/// are here, not in the engine, because none of them needs a network, a database or a real
/// clock.</para>
/// </summary>
public class CloudSpaceTests
{
    private const long Gb = 1024L * 1024 * 1024;

    // ── what is left ──

    [Fact]
    public void A_fresh_account_has_room_for_anything()
    {
        var space = new CloudSpace(0, 100 * Gb);

        Assert.True(space.Known);
        Assert.False(space.IsFull);
        Assert.False(space.IsAlmostFull);
        Assert.True(space.Fits(500L * 1024 * 1024));
        Assert.Equal(100 * Gb, space.FreeBytes);
    }

    [Fact]
    public void Used_up_to_the_ceiling_is_full()
    {
        Assert.True(new CloudSpace(100 * Gb, 100 * Gb).IsFull);

        // Over the limit, which the store can be: usage is measured after the upload, so a last upload
        // can go a few bytes past the limit.
        Assert.True(new CloudSpace(101 * Gb, 100 * Gb).IsFull);
        Assert.Equal(0, new CloudSpace(101 * Gb, 100 * Gb).FreeBytes);
    }

    [Fact]
    public void Under_two_gigabytes_left_is_almost_full()
    {
        Assert.True(new CloudSpace(99 * Gb, 100 * Gb).IsAlmostFull);
        Assert.False(new CloudSpace(97 * Gb, 100 * Gb).IsAlmostFull);

        // Full is also almost full. One is a warning and the other is a stop, and only the "nearly"
        // wording tells them apart.
        Assert.True(new CloudSpace(100 * Gb, 100 * Gb).IsAlmostFull);
        Assert.False(new CloudSpace(100 * Gb, 100 * Gb).IsNearlyFull);
        Assert.True(new CloudSpace(99 * Gb, 100 * Gb).IsNearlyFull);
    }

    [Fact]
    public void A_file_bigger_than_what_is_left_does_not_fit()
    {
        var space = new CloudSpace(100 * Gb - 10_000_000, 100 * Gb);

        Assert.True(space.Fits(9_000_000));
        Assert.False(space.Fits(11_000_000));
    }

    // ── the two states that must never refuse anything ──

    [Fact]
    public void An_unread_quota_refuses_nothing()
    {
        // Signed out, offline, or a request that did not come back. Treating this as "no room" would
        // stop a working library from syncing because one HTTP call failed.
        var space = CloudSpace.Unknown;

        Assert.False(space.Known);
        Assert.False(space.IsFull);
        Assert.False(space.IsAlmostFull);
        Assert.True(space.Fits(long.MaxValue / 2));
        Assert.Equal(0, space.UsedFraction);
    }

    [Fact]
    public void Unlimited_room_is_never_full_however_much_is_in_it()
    {
        var space = new CloudSpace(900 * Gb, 100 * Gb, Unlimited: true);

        Assert.True(space.Known);
        Assert.False(space.IsFull);
        Assert.False(space.IsAlmostFull);
        Assert.True(space.Fits(500L * 1024 * 1024));

        // No bar to draw: there is no limit to take a fraction of.
        Assert.Equal(0, space.UsedFraction);
    }

    [Fact]
    public void The_fraction_is_clamped_to_the_bar()
    {
        Assert.Equal(0.5, new CloudSpace(50 * Gb, 100 * Gb).UsedFraction, 3);
        Assert.Equal(1, new CloudSpace(150 * Gb, 100 * Gb).UsedFraction, 3);
    }

    // ── what a full store does to the sync mode ──

    [Fact]
    public void All_music_falls_back_to_the_selection()
        => Assert.Equal(CloudSyncMode.Selected, CloudFullPolicy.WhenFull(CloudSyncMode.All));

    [Fact]
    public void Any_other_mode_is_left_alone()
    {
        Assert.Equal(CloudSyncMode.Selected, CloudFullPolicy.WhenFull(CloudSyncMode.Selected));
        Assert.Equal(CloudSyncMode.None, CloudFullPolicy.WhenFull(CloudSyncMode.None));
    }

    [Fact]
    public void All_music_comes_back_when_the_ceiling_is_bought_away()
        => Assert.True(CloudFullPolicy.ShouldRestoreAll(
            pausedByFull: true, unlimited: true, mode: CloudSyncMode.Selected));

    [Fact]
    public void Room_alone_does_not_bring_all_music_back()
        // A few gigabytes freed by removing a record would send the whole library up to the same limit
        // again. Only unlimited storage restores "all music".
        => Assert.False(CloudFullPolicy.ShouldRestoreAll(
            pausedByFull: true, unlimited: false, mode: CloudSyncMode.Selected));

    [Fact]
    public void A_mode_the_user_chose_is_never_restored_over()
    {
        // The user turned syncing off after the fall-back. Buying storage does not turn it back on.
        Assert.False(CloudFullPolicy.ShouldRestoreAll(
            pausedByFull: true, unlimited: true, mode: CloudSyncMode.None));

        // And nothing to restore when the fall-back never happened.
        Assert.False(CloudFullPolicy.ShouldRestoreAll(
            pausedByFull: false, unlimited: true, mode: CloudSyncMode.Selected));
    }

    // ── how often the user is told ──

    [Fact]
    public void The_full_window_is_shown_once_a_week()
    {
        var now = new DateTime(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(CloudFullNotice.Due(null, now));
        Assert.False(CloudFullNotice.Due(now.AddDays(-1), now));
        Assert.False(CloudFullNotice.Due(now.AddDays(-6), now));
        Assert.True(CloudFullNotice.Due(now.AddDays(-7), now));
        Assert.True(CloudFullNotice.Due(now.AddDays(-30), now));
    }

    [Fact]
    public void Never_show_again_is_final()
    {
        var now = new DateTime(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(CloudFullNotice.Due(null, now, suppressed: true));
        Assert.False(CloudFullNotice.Due(now.AddYears(-1), now, suppressed: true));
    }

    [Fact]
    public void The_opt_out_box_appears_on_the_second_showing()
    {
        // Offered the first time, it would be an option to hide a message the user has not read yet.
        Assert.False(CloudFullNotice.MayOptOut(shownBefore: 0));
        Assert.True(CloudFullNotice.MayOptOut(shownBefore: 1));
        Assert.True(CloudFullNotice.MayOptOut(shownBefore: 5));
    }

    [Fact]
    public void A_stamp_in_the_future_counts_as_never()
    {
        // A clock set back, or a settings file copied from another machine. Otherwise the notice would
        // stay silent until the date catches up, which could be years.
        var now = new DateTime(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(CloudFullNotice.Due(now.AddYears(1), now));
    }
}
