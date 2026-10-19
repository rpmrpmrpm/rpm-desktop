using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// What the network is owed when a setting changes.
///
/// <para>Every setting in the app arrives through one event, so this is asked on every flip of the
/// library's view switch. Without this the sharing service would withdraw and re-publish its multicast
/// record for any change at all, which blocks the UI thread for a full second. A wall of covers and a
/// list of records are, to the neighbours, the same computer offering the same music under the same
/// name.</para>
/// </summary>
public class BeaconReactionTests
{
    [Fact]
    public void A_setting_the_beacon_says_nothing_about_says_nothing()
    {
        // The library's view switch, the cover-size slider, the import action: serving before, serving
        // after, called the same thing throughout. Without this, each click would block for a second.
        Assert.Equal(
            BeaconChange.None,
            BeaconReaction.For(wanted: true, serving: true, announced: "Alex's iMac", name: "Alex's iMac", announcedFace: "", face: ""));
    }

    [Fact]
    public void Being_renamed_is_worth_saying_again()
        => Assert.Equal(
            BeaconChange.Reannounce,
            BeaconReaction.For(wanted: true, serving: true, announced: "iMac", name: "The good one", announcedFace: "", face: ""));

    [Theory]
    [InlineData(true, false)]    // sharing switched on, or a library opened under it
    [InlineData(false, true)]    // sharing switched off
    public void Sharing_starting_or_stopping_takes_the_whole_thing_down_and_up(bool wanted, bool serving)
        => Assert.Equal(
            BeaconChange.Restart,
            BeaconReaction.For(wanted, serving, announced: "iMac", name: "iMac", announcedFace: "", face: ""));

    [Fact]
    public void A_restart_owns_the_announcement_even_when_the_name_moved_too()
    {
        // Renamed at the same time as being switched on: one Restart, which puts the beacon up with
        // the new name itself. A Reannounce after that would repeat the expensive part.
        Assert.Equal(
            BeaconChange.Restart,
            BeaconReaction.For(wanted: true, serving: false, announced: "iMac", name: "The good one", announcedFace: "", face: ""));
    }

    [Fact]
    public void A_machine_that_is_not_serving_has_no_beacon_to_correct()
    {
        // Renaming a computer that is not sharing changes nothing on the wire: there is nothing on it.
        Assert.Equal(
            BeaconChange.None,
            BeaconReaction.For(wanted: false, serving: false, announced: "", name: "The good one", announcedFace: "", face: ""));
    }

    [Fact]
    public void The_name_is_compared_as_a_string_on_a_wire_and_not_as_a_word()
    {
        // Not a question about which spellings a reader would call the same: whether what is being
        // broadcast is what the settings hold. A case change is a different record.
        Assert.Equal(
            BeaconChange.Reannounce,
            BeaconReaction.For(wanted: true, serving: true, announced: "imac", name: "iMac", announcedFace: "", face: ""));
    }

    [Fact]
    public void Nothing_announced_yet_and_nothing_asked_for_is_still_nothing()
        => Assert.Equal(
            BeaconChange.None,
            BeaconReaction.For(wanted: true, serving: true, announced: null, name: "", announcedFace: "", face: ""));

    [Fact]
    public void Renaming_the_person_re_announces_as_surely_as_renaming_the_machine()
    {
        // The display name travels in the record too, so a profile renamed in the settings has to be
        // said again. Without this the new name reaches every screen except the neighbours'.
        Assert.Equal(
            BeaconChange.Reannounce,
            BeaconReaction.For(wanted: true, serving: true, announced: "ALEX-MBP", name: "ALEX-MBP",
                               announcedFace: "Alex", face: "DJ Alex"));
    }

    [Fact]
    public void Signing_out_takes_the_face_off_the_wire_and_that_is_a_change()
    {
        Assert.Equal(
            BeaconChange.Reannounce,
            BeaconReaction.For(wanted: true, serving: true, announced: "ALEX-MBP", name: "ALEX-MBP",
                               announcedFace: "Alex", face: ""));
    }

    [Fact]
    public void A_face_that_has_not_moved_is_still_not_worth_a_second_of_multicast()
    {
        Assert.Equal(
            BeaconChange.None,
            BeaconReaction.For(wanted: true, serving: true, announced: "ALEX-MBP", name: "ALEX-MBP",
                               announcedFace: "Alex", face: "Alex"));
    }
}
