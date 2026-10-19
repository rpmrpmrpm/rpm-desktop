using RPM.Desktop.Presentation.NowPlaying;

namespace RPM.Desktop.Tests;

/// <summary>
/// How far the header's photograph dissolves at its sides, and what changes when the record is not on the
/// band.
///
/// <para>The record takes the right of the header: the disc's arc, and the air kept clear of it. Switched
/// off, all of that is the picture's, so the ceiling on its edges is raised. What must NOT change is the
/// picture that had no room to begin with, which is most of them, and that is the half these tests are
/// really for.</para>
/// </summary>
public class PictureDissolveTests
{
    private const float WithRecord = 150, WithoutRecord = 240;

    [Fact]
    public void The_cap_follows_the_record_and_not_a_switch()
    {
        // A slot narrower than the window is a disc reaching in from the right; equal is a band with
        // nothing on it. Read off the geometry the picture is baked for, so it cannot disagree with it.
        Assert.Equal(WithRecord, ArtDisplay.SideDissolveCap(slotWidth: 1279, windowWidth: 1440));
        Assert.Equal(WithoutRecord, ArtDisplay.SideDissolveCap(slotWidth: 1440, windowWidth: 1440));
    }

    [Fact]
    public void The_record_is_read_off_the_slot_by_one_rule_and_not_two()
    {
        // Two things ask whether there is a disc on the band: how far the edges may dissolve, and whether
        // the right edge is an edge at all (the disc laps over it, so it is not dissolved). They ask the
        // same function, because two derivations of one fact are two chances to disagree about one bake.
        Assert.True(ArtDisplay.RecordOnTheBand(slotWidth: 1279, windowWidth: 1440));
        Assert.False(ArtDisplay.RecordOnTheBand(slotWidth: 1440, windowWidth: 1440));

        Assert.Equal(ArtDisplay.RecordOnTheBand(1279, 1440) ? WithRecord : WithoutRecord,
                     ArtDisplay.SideDissolveCap(1279, 1440));
    }

    [Fact]
    public void A_picture_with_no_air_dissolves_the_same_either_way()
    {
        // The subject standing hard against both edges: the floor decides, and the floor is not what the
        // record moved. This is the promise that taking the disc away cannot change a header that was
        // already tight.
        var tight = ArtDisplay.SideDissolveRamp(leftAir: 4, rightAir: 9, cap: WithRecord);

        Assert.Equal(tight, ArtDisplay.SideDissolveRamp(leftAir: 4, rightAir: 9, cap: WithoutRecord));
    }

    [Fact]
    public void Only_a_picture_with_room_spends_more_of_it()
    {
        // Air the old ceiling cut off, and the new one does not.
        Assert.Equal(WithRecord, ArtDisplay.SideDissolveRamp(leftAir: 60, rightAir: 300, cap: WithRecord));
        Assert.Equal(WithoutRecord, ArtDisplay.SideDissolveRamp(leftAir: 60, rightAir: 300, cap: WithoutRecord));

        // Air that fits under both is spent in full. The ceiling above it does not change this.
        Assert.Equal(120, ArtDisplay.SideDissolveRamp(leftAir: 120, rightAir: 30, cap: WithRecord));
        Assert.Equal(120, ArtDisplay.SideDissolveRamp(leftAir: 120, rightAir: 30, cap: WithoutRecord));
    }

    [Fact]
    public void Both_edges_are_measured_from_the_longer_air()
    {
        // One ramp for the two sides, so a subject framed off centre does not come out soft on one edge
        // and cut on the other. Which side the air is on makes no difference to the length.
        Assert.Equal(ArtDisplay.SideDissolveRamp(leftAir: 90, rightAir: 30, cap: WithRecord),
                     ArtDisplay.SideDissolveRamp(leftAir: 30, rightAir: 90, cap: WithRecord));
    }
}
