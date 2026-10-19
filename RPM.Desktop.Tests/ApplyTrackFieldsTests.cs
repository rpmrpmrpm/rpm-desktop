using RPM.Core.Metadata;
using RPM.Core.Metadata.Providers;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Dialogs.Models;
using RPM.Desktop.Presentation.Main;
using RPM.Desktop.Presentation.Main.LeftPane.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// One listing row written onto one track row: the last step of "Track metadata from release". It is
/// easy to get wrong for multi-disc sets without any visible error.
/// </summary>
public class ApplyTrackFieldsTests
{
    /// <summary>A row for a file with no tags, with the given fields written onto it. A field not given
    /// is left alone rather than set to null: the setters take a value, and a row made from an untagged
    /// file already reads empty in all three.</summary>
    private static TrackRow Row(string? track = null, string? disc = null, string? title = null)
    {
        var row = new TrackRow(new TrackInfo { FilePath = "/lib/a/1.mp3", Extension = ".mp3" });
        if (track is not null) row.Track = track;
        if (disc is not null) row.Disc = disc;
        if (title is not null) row.Title = title;
        return row;
    }

    private static ReleaseTrackInfo Listed(
        uint position = 1, string title = "Listed title", uint disc = 0, uint discPosition = 0,
        string? discSubtitle = null, ContentAdvisory advisory = ContentAdvisory.Unknown) =>
        new(position, title, Artist: null, LengthSeconds: null,
            Disc: disc, DiscPosition: discPosition, DiscSubtitle: discSubtitle, Advisory: advisory);

    private static TrackPlace Place(TrackRow row) => new(
        uint.TryParse(row.Disc, out var d) ? d : 0,
        uint.TryParse(row.Track, out var n) ? n : 0,
        row.Title);

    // ── multi-disc sets ───────────────────────────────────────────────────────

    /// <summary>
    /// A two-disc set ripped as one folder: every file carries a track number and none carries a disc
    /// number. They match on the number and take their titles, and must also take the disc, even
    /// though the TRACK number is already there. Without the disc, the record holds two track 1s and
    /// nothing to tell the halves apart.
    /// </summary>
    [Fact]
    public void A_file_that_does_not_say_which_disc_it_is_on_is_told()
    {
        var row = Row(track: "1", title: "Something");

        ReleaseTrackFill.ApplyTrackFields(row, Listed(disc: 2, discPosition: 1), Place(row));

        Assert.Equal("2", row.Disc);
        Assert.Equal("1", row.Track);   // its own number was right, and is left alone
    }

    /// <summary>A file that already has its disc was matched partly BY that, so writing it again
    /// changes nothing, and a listing that disagrees must not overwrite it.</summary>
    [Fact]
    public void A_disc_the_file_already_names_is_left_alone()
    {
        var row = Row(track: "3", disc: "1");

        ReleaseTrackFill.ApplyTrackFields(row, Listed(disc: 2, discPosition: 3), Place(row));

        Assert.Equal("1", row.Disc);
    }

    /// <summary>A single-disc listing has no disc to give, and must not add one.</summary>
    [Fact]
    public void A_listing_with_no_discs_writes_no_disc()
    {
        var row = Row(track: "5");

        ReleaseTrackFill.ApplyTrackFields(row, Listed(position: 5), Place(row));

        Assert.True(string.IsNullOrEmpty(row.Disc));
    }

    /// <summary>An unnumbered rip gets the number printed on the sleeve: the per-disc number, not the
    /// position across the whole set, or disc two of a double album would start at thirteen.</summary>
    [Fact]
    public void An_unnumbered_file_takes_the_number_printed_on_the_sleeve()
    {
        var row = Row();

        ReleaseTrackFill.ApplyTrackFields(row, Listed(position: 14, disc: 2, discPosition: 2), Place(row));

        Assert.Equal("2", row.Track);
        Assert.Equal("2", row.Disc);
    }

    // ── what a listing may and may not overwrite ──────────────────────────────

    [Fact]
    public void The_discs_own_name_comes_across()
    {
        var row = Row(track: "1");

        ReleaseTrackFill.ApplyTrackFields(row, Listed(disc: 1, discPosition: 1, discSubtitle: "The Demos"), Place(row));

        Assert.Equal("The Demos", row.DiscSubtitle);
    }

    /// <summary>A missing value is not an answer, and must not erase one. A catalogue that says nothing
    /// about a track's lyrics cannot turn a file marked explicit into a file marked nothing.</summary>
    [Fact]
    public void An_unknown_advisory_never_clears_one_the_file_carries()
    {
        var row = Row(track: "1");
        row.AdvisoryValue = "explicit";

        ReleaseTrackFill.ApplyTrackFields(row, Listed(advisory: ContentAdvisory.Unknown), Place(row));

        Assert.Equal("explicit", row.AdvisoryValue);
    }

    [Fact]
    public void A_stated_advisory_is_written()
    {
        var row = Row(track: "1");

        ReleaseTrackFill.ApplyTrackFields(row, Listed(advisory: ContentAdvisory.Clean), Place(row));

        Assert.Equal("clean", row.AdvisoryValue);
    }

    /// <summary>A field the listing has nothing to say about leaves what the file had.</summary>
    [Fact]
    public void Silence_in_the_listing_leaves_the_file_as_it_was()
    {
        var row = Row(track: "1", title: "What the file says");

        ReleaseTrackFill.ApplyTrackFields(row, Listed(title: "  "), Place(row));

        Assert.Equal("What the file says", row.Title);
    }
}

/// <summary>
/// The order a selection is read in when nothing is numbered. The matcher pairs files against the
/// listing by position, so this order decides which file gets which name. It must not depend on the
/// order the grid passes the rows in.
/// </summary>
public class RunningOrderTests
{
    /// <summary>An untagged file in the album folder, with a track and a disc number where given. See
    /// <see cref="ApplyTrackFieldsTests"/>'s own Row for why a field not given is left alone.</summary>
    private static TrackRow File(string name, string? track = null, string? disc = null)
    {
        var row = new TrackRow(new TrackInfo { FilePath = "/lib/album/" + name, Extension = ".flac" });
        if (track is not null) row.Track = track;
        if (disc is not null) row.Disc = disc;
        return row;
    }

    private static string[] Order(params TrackRow[] rows) =>
        [.. ReleaseTrackFill.RunningOrder(rows).Select(i => Path.GetFileName(rows[i].FilePath))];

    /// <summary>An untagged rip has nothing but its file names, and those give the running order, read
    /// the way a person reads them, so 10 does not sort between 1 and 2.</summary>
    [Fact]
    public void An_untagged_rip_is_read_in_file_name_order()
    {
        Assert.Equal(
            ["Track 1.flac", "Track 2.flac", "Track 10.flac"],
            Order(File("Track 10.flac"), File("Track 1.flac"), File("Track 2.flac")));
    }

    /// <summary>The grid may pass them in any order (sorted by a title half of them do not have yet, or
    /// by the tree's grouping). That must not decide which file takes which name.</summary>
    [Fact]
    public void The_order_the_grid_hands_them_over_in_does_not_decide()
    {
        Assert.Equal(
            ["a1.flac", "a2.flac", "a3.flac"],
            Order(File("a3.flac"), File("a1.flac"), File("a2.flac")));
    }

    /// <summary>Numbers win over names: a file's own number is trusted over its file name.</summary>
    [Fact]
    public void Track_numbers_are_believed_before_file_names()
    {
        Assert.Equal(
            ["zzz.flac", "aaa.flac"],
            Order(File("aaa.flac", track: "2"), File("zzz.flac", track: "1")));
    }

    /// <summary>Disc first, so the second disc follows the first however the files are named.</summary>
    [Fact]
    public void Discs_come_before_track_numbers()
    {
        Assert.Equal(
            ["d1t1.flac", "d1t2.flac", "d2t1.flac"],
            Order(
                File("d2t1.flac", track: "1", disc: "2"),
                File("d1t2.flac", track: "2", disc: "1"),
                File("d1t1.flac", track: "1", disc: "1")));
    }

    /// <summary>
    /// A hundred-track compilation where no file has a track number, and a few still have a disc tag
    /// from the record each was originally taken from. Sorting on those tags would move them out of
    /// sequence, and every file after the first would take its neighbour's name. With no track
    /// numbers anywhere, the paths give the running order and a stray disc tag is ignored.
    /// </summary>
    [Fact]
    public void A_stray_disc_tag_does_not_reorder_a_set_that_has_no_track_numbers()
    {
        Assert.Equal(
            ["01. a.mp3", "02. b.mp3", "03. c.mp3", "04. d.mp3"],
            Order(
                File("03. c.mp3", disc: "3"),      // the stray tag, from another compilation
                File("01. a.mp3", disc: "1"),
                File("04. d.mp3", disc: "1"),
                File("02. b.mp3", disc: "1")));
    }

    /// <summary>A set filed one folder per disc keeps its discs in order on the path alone, with no tag
    /// needed to say so.</summary>
    [Fact]
    public void Disc_folders_order_themselves()
    {
        var rows = new[]
        {
            new TrackRow(new TrackInfo { FilePath = "/lib/album/Disc 2/01.flac", Extension = ".flac" }),
            new TrackRow(new TrackInfo { FilePath = "/lib/album/Disc 1/02.flac", Extension = ".flac" }),
            new TrackRow(new TrackInfo { FilePath = "/lib/album/Disc 1/01.flac", Extension = ".flac" }),
        };

        Assert.Equal(
            ["/lib/album/Disc 1/01.flac", "/lib/album/Disc 1/02.flac", "/lib/album/Disc 2/01.flac"],
            ReleaseTrackFill.RunningOrder(rows).Select(i => rows[i].FilePath).ToArray());
    }

    /// <summary>A file with no number sorts after every file that has one.</summary>
    [Fact]
    public void Unnumbered_files_follow_numbered_ones()
    {
        Assert.Equal(
            ["b.flac", "a.flac"],
            Order(File("a.flac"), File("b.flac", track: "1")));
    }
}
