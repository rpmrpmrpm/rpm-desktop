using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main.Library.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// What one breath can drive.
///
/// <para>The marks on the lines inside a record are updated in place when the cloud is read again, so the
/// page does not have to rebuild the tracklist. The record's own mark, on its tile, is shown in the same
/// way.</para>
///
/// <para>A record and its songs are shown at once and can be busy at once, so ONE clock drives both (see
/// <see cref="CloudLineBreath"/>). That needs the record's tile to be a badged item, as its lines are.
/// These tests check that both answer the two questions a breath asks, and that both settle to rest when
/// they stop working, instead of stopping halfway through a beat.</para>
/// </summary>
public class CloudBreathSubjectsTests
{
    private static TrackInfo Track(string title, CloudState state, bool selected) => new()
    {
        FilePath = $"/music/Lender/Record/{title}.flac",
        Extension = ".flac",
        Title = title,
        Album = "Record",
        CloudState = state,
        CloudSelected = selected,
    };

    /// <summary>A record whose songs disagree. This is the only case where a line shows a mark. A record
    /// where every song is in the same state shows that state once, on its tile.</summary>
    private static List<AlbumTrackItem> HalfUp() => AlbumTrackItem.Build([
        Track("One", CloudState.Queued, selected: true),
        Track("Two", CloudState.Local, selected: false),
    ]);

    /// <summary>A record's own tile answers the two questions a breath asks, in the same way as a line
    /// inside it. This lets one clock drive the sleeve and its tracklist together, so they never pulse
    /// out of step.</summary>
    [Fact]
    public void A_record_and_a_line_are_both_things_a_breath_can_drive()
    {
        Assert.True(typeof(AlbumCard).IsAssignableTo(typeof(ICloudBadged)));
        Assert.True(typeof(AlbumTrackItem).IsAssignableTo(typeof(ICloudBadged)));
    }

    /// <summary>Only a mark that is working breathes. A song at rest (on this disk, nothing chosen) is not
    /// given a beat, so an idle window costs nothing.</summary>
    [Fact]
    public void Only_a_mark_that_is_working_is_busy()
    {
        var lines = HalfUp();
        var queued = lines.First(l => l.Info?.Title == "One");
        var resting = lines.First(l => l.Info?.Title == "Two");

        Assert.True(queued.IsCloudBusy);
        Assert.False(resting.IsCloudBusy);
    }

    /// <summary>When work stops in the middle of a beat, the mark settles back to full. It is not left at
    /// whatever opacity the last tick wrote, because a half-faded mark would look as if the work were
    /// still going on.</summary>
    [Fact]
    public void A_mark_that_stops_working_settles_rather_than_sticking()
    {
        var line = HalfUp().First(l => l.Info?.Title == "One");
        Assert.True(line.IsCloudBusy);

        line.CloudPulse = 0.35;
        line.CloudBadge = AlbumCloudBadge.Full;

        Assert.False(line.IsCloudBusy);
        Assert.Equal(CloudBadgePulse.Rest, line.CloudPulse);
    }

    /// <summary>Reading the folder again moves the marks. The lines are re-badged where they stand, not
    /// rebuilt, so a tracklist that someone is reading does not flicker when one of its songs
    /// lands.</summary>
    [Fact]
    public void Reading_the_cloud_again_moves_the_marks_on_the_lines_already_there()
    {
        var lines = HalfUp();
        var line = lines.First(l => l.Info?.Title == "One");
        Assert.True(line.IsCloudBusy);

        // The upload landed: that song is up, the other is still not.
        AlbumTrackItem.ApplyCloud(lines, [
            Track("One", CloudState.Synced, selected: true),
            Track("Two", CloudState.Local, selected: false),
        ]);

        Assert.False(line.IsCloudBusy);
        Assert.Equal(CloudBadgePulse.Rest, line.CloudPulse);
        Assert.Same(line, lines.First(l => l.Info?.Title == "One"));   // re-badged, not rebuilt
    }
}
