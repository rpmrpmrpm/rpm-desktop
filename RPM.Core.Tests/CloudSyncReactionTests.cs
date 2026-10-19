using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// What the sync engine does when a setting changes.
///
/// <para>Every setting in the app arrives through one event, so this is asked on every keystroke of a
/// rename and every drag of the cover-size slider. So the handler must not start the queue for every
/// change.</para>
/// </summary>
public class CloudSyncReactionTests
{
    private static CloudReaction For(CloudSyncMode was, CloudSyncMode now)
        => CloudSyncReaction.For(was, now);

    [Fact]
    public void A_setting_that_is_none_of_the_engines_business_kicks_the_queue_and_no_more()
    {
        // The cover-size slider, a theme, a keyboard shortcut: nothing about the cloud changed, and the
        // queue is started because the library may have.
        Assert.Equal(CloudReaction.KickQueue, For(CloudSyncMode.All, CloudSyncMode.All));
        Assert.Equal(CloudReaction.KickQueue, For(CloudSyncMode.Selected, CloudSyncMode.Selected));
    }

    [Fact]
    public void With_syncing_off_there_is_nothing_to_kick()
        => Assert.Equal(CloudReaction.None, For(CloudSyncMode.None, CloudSyncMode.None));

    [Theory]
    [InlineData(CloudSyncMode.None, CloudSyncMode.All)]
    [InlineData(CloudSyncMode.None, CloudSyncMode.Selected)]
    [InlineData(CloudSyncMode.All, CloudSyncMode.Selected)]
    [InlineData(CloudSyncMode.Selected, CloudSyncMode.All)]
    [InlineData(CloudSyncMode.All, CloudSyncMode.None)]
    public void Changing_the_mode_moves_the_queue_to_it(CloudSyncMode was, CloudSyncMode now)
        => Assert.Equal(CloudReaction.ApplyMode, For(was, now));

    /// <summary>
    /// Applying a mode owns the queue: it stops the worker, rewrites the rows and starts the queue when
    /// it is done. Starting it at the same time would be a second run against rows being rewritten.
    /// Then switching away from "all music" in the middle of a library would keep uploading the rest.
    /// </summary>
    [Fact]
    public void A_mode_change_is_never_accompanied_by_a_kick()
    {
        foreach (var was in Enum.GetValues<CloudSyncMode>())
        foreach (var now in Enum.GetValues<CloudSyncMode>())
        {
            if (was == now) continue;
            Assert.False(For(was, now).HasFlag(CloudReaction.KickQueue));
        }
    }
}
