using RPM.Desktop.Presentation.Main.Library;

namespace RPM.Desktop.Tests;

/// <summary>
/// The cloud part of the two right-click menus: a record's menu and a song's menu. This part changes with
/// what the user has already done.
///
/// <para>Taking a record back OUT of the cloud is not here. It is one of the three entries in the Delete
/// submenu, and its rule is in <c>DeleteOffer</c>. What is left here is the way up to the cloud and the way
/// back home.</para>
/// </summary>
public class MenuCloudEntriesTests
{
    /// <summary>
    /// In "all music" mode the track is already going up. In "none" mode nothing is. Picking a record by
    /// hand only says something where picking is what decides. Offering it elsewhere would be an offer the
    /// engine cannot keep.
    /// </summary>
    [Fact]
    public void Outside_selected_mode_the_way_up_is_not_offered()
    {
        var entries = MenuCloudEntries.For(selectedMode: false, canAdd: true);

        Assert.False(entries.AddToCloud);
        Assert.False(entries.Any);
    }

    [Fact]
    public void A_record_that_is_not_up_there_is_offered_the_way_up()
    {
        var entries = MenuCloudEntries.For(selectedMode: true, canAdd: true);

        Assert.True(entries.AddToCloud);
        Assert.True(entries.Any);
    }

    [Fact]
    public void A_record_that_is_already_up_there_is_not_offered_the_way_up_again()
    {
        var entries = MenuCloudEntries.For(selectedMode: true, canAdd: false);

        Assert.False(entries.AddToCloud);
        Assert.False(entries.Any);
    }

    /// <summary>
    /// Read outside the mode check on purpose. Bringing a record home has nothing to do with what this
    /// machine chooses to send up. A second install, the machine most likely to be looking at a record it
    /// does not hold, may have no sync mode set at all.
    /// </summary>
    [Fact]
    public void A_record_that_is_only_in_the_cloud_can_be_brought_home_in_any_mode()
    {
        foreach (var selectedMode in new[] { true, false })
        {
            var entries = MenuCloudEntries.For(selectedMode, canAdd: false, isCloudOnly: true);

            Assert.True(entries.KeepOnComputer);
            Assert.True(entries.Any);
        }
    }

    [Fact]
    public void A_record_whose_files_are_here_is_not_offered_a_download()
        => Assert.False(MenuCloudEntries.For(true, true, isCloudOnly: false).KeepOnComputer);

    /// <summary>
    /// Both menus depend on this rule. In a mode where the user's picks do not decide what is sent, nothing
    /// that would change the cloud is offered. Bringing a record home changes nothing in the cloud, so it
    /// is the exception.
    /// </summary>
    [Fact]
    public void Nothing_that_changes_the_cloud_is_offered_outside_selected_mode()
    {
        foreach (var canAdd in new[] { true, false })
        foreach (var isCloudOnly in new[] { true, false })
        {
            var entries = MenuCloudEntries.For(false, canAdd, isCloudOnly);

            Assert.False(entries.AddToCloud);
            Assert.Equal(isCloudOnly, entries.Any);
        }
    }

    // ── no room ──

    /// <summary>A full store greys out "add to cloud" rather than removing it. Removing it would look as if
    /// the feature were gone. Greying it out shows that the offer exists but cannot be used now. This
    /// matches the engine, which stops instead of showing a queue that is not moving.
    /// </summary>
    [Fact]
    public void A_full_store_greys_the_add_entry_but_keeps_it()
    {
        var entries = MenuCloudEntries.For(true, canAdd: true, canUpload: false);

        Assert.True(entries.AddToCloud);
        Assert.False(entries.AddToCloudEnabled);
        Assert.True(entries.Any);
    }

    /// <summary>A full store does not stop a record coming home. That reads from the cloud, and does not
    /// fill it.</summary>
    [Fact]
    public void A_full_store_never_stops_a_record_coming_home()
        => Assert.True(MenuCloudEntries.For(
            true, canAdd: false, isCloudOnly: true, canUpload: false).KeepOnComputer);

    /// <summary>When there is room, the entry is enabled. This is the normal case. It is tested so that the
    /// greyed state cannot become the default by accident.</summary>
    [Fact]
    public void Room_leaves_the_add_entry_alone()
        => Assert.True(MenuCloudEntries.For(true, canAdd: true).AddToCloudEnabled);

    /// <summary>An entry that is not offered at all is not "enabled" either, whatever the room
    /// says.</summary>
    [Fact]
    public void An_entry_that_is_not_offered_is_never_enabled()
        => Assert.False(MenuCloudEntries.For(true, canAdd: false).AddToCloudEnabled);
}
