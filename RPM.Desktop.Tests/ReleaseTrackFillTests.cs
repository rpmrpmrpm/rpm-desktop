using RPM.Core.Metadata.Providers;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Presentation.Dialogs.Models;
using RPM.Desktop.Presentation.Main.LeftPane.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// A release's listing laid against the tracks a record actually holds. This is the page the user reads
/// before anything is written, and the write it then performs.
///
/// <para>The two ways the counts can disagree are the point of the thing: a record with more files
/// than the release has tracks must show every file, marked, and leave the extra ones alone; a
/// release with more tracks than the record has files must simply stop when the files run out.</para>
/// </summary>
public class ReleaseTrackFillTests
{
    private static TrackRow Row(int number, string? title = null, string? path = null) =>
        new(new TrackInfo { FilePath = path ?? $"/lib/a/{number:00}.mp3", Extension = ".mp3" })
        {
            Track = number.ToString(),
            Title = title ?? "",
        };

    private static ReleaseTrackInfo Listed(uint position, string title, string? artist = null) =>
        new(position, title, artist, LengthSeconds: null);

    private static ReleaseDetail Release(params string[] titles) => new(
        new ReleaseRef("musicbrainz", "r1"), "The Record", "The Band", 1973,
        Country: "GB", Format: "Vinyl",
        Tracks: [.. titles.Select((t, i) => Listed((uint)(i + 1), t))],
        Label: "Harvest");

    private static ReleaseTrackFill Fill(
        IReadOnlyList<TrackRow> rows, ReleaseDetail release, bool withAlbumFields = false) =>
        ReleaseTrackFill.Pair(
            rows, release.Tracks,
            withAlbumFields
                ? ReleaseTrackFill.AlbumFieldsOf(null, release)
                : new Dictionary<TagFieldKind, string>());

    // ── the counts disagreeing ────────────────────────────────────────────────

    /// <summary>The record has four files and the release lists two. Every file keeps a line, the two
    /// the release cannot answer for are marked, and nothing is written onto them.</summary>
    [Fact]
    public void Files_the_release_has_no_track_for_are_shown_and_marked()
    {
        List<TrackRow> rows = [Row(1), Row(2), Row(3, "Kept"), Row(4, "Also kept")];
        var fill = Fill(rows, Release("One", "Two"));

        Assert.Equal(4, fill.Lines.Count);
        Assert.Equal([true, true, false, false], [.. fill.Lines.Select(l => l.Matched)]);
        Assert.Equal(2, fill.MatchedCount);

        fill.Apply(albumFields: false);

        Assert.Equal("One", rows[0].Title);
        Assert.Equal("Two", rows[1].Title);
        Assert.Equal("Kept", rows[2].Title);
        Assert.Equal("Also kept", rows[3].Title);
    }

    /// <summary>The other way round is the ordinary case of naming half a record: the release's extra
    /// tracks are simply not shown, and nothing is invented to hold them.</summary>
    [Fact]
    public void A_release_with_more_tracks_than_the_record_stops_where_the_files_run_out()
    {
        List<TrackRow> rows = [Row(1), Row(2)];
        var fill = Fill(rows, Release("One", "Two", "Three", "Four"));

        Assert.Equal(2, fill.Lines.Count);
        Assert.All(fill.Lines, l => Assert.True(l.Matched));
    }

    /// <summary>An unmatched line has nothing to accept, so its box cannot be turned on: by the user
    /// or by the header tick.</summary>
    [Fact]
    public void An_unmatched_line_cannot_be_ticked()
    {
        List<TrackRow> rows = [Row(1), Row(2)];
        var fill = Fill(rows, Release("One"));

        var stray = fill.Lines[1];
        Assert.False(stray.IsSelected);

        stray.IsSelected = true;
        Assert.False(stray.IsSelected);

        fill.TickAll(true);
        Assert.False(stray.IsSelected);
    }

    // ── what Apply writes ─────────────────────────────────────────────────────

    /// <summary>A line the user unticked is a line they said no to.</summary>
    [Fact]
    public void An_unticked_line_is_left_alone()
    {
        List<TrackRow> rows = [Row(1, "Mine"), Row(2)];
        var fill = Fill(rows, Release("One", "Two"));
        fill.Lines[0].IsSelected = false;

        var result = fill.Apply(albumFields: false);

        Assert.Equal("Mine", rows[0].Title);
        Assert.Equal("Two", rows[1].Title);
        Assert.Equal(1, result.Named);
        Assert.Equal(2, result.Total);
    }

    /// <summary>The boxes are what is written, not the listing: a title corrected in the grid is the
    /// answer, which is the whole reason the grid is editable.</summary>
    [Fact]
    public void A_title_corrected_in_the_grid_is_what_lands()
    {
        List<TrackRow> rows = [Row(1)];
        var fill = Fill(rows, Release("Speak To Me"));
        fill.Lines[0].Title = "Speak to Me";

        fill.Apply(albumFields: false);

        Assert.Equal("Speak to Me", rows[0].Title);
    }

    /// <summary>A box emptied by hand is not an instruction to erase the file's own title. A blank
    /// never overwrites a name anywhere else in this path, and it does not here either.</summary>
    [Fact]
    public void A_title_emptied_in_the_grid_leaves_the_file_as_it_was()
    {
        List<TrackRow> rows = [Row(1, "What the file says")];
        var fill = Fill(rows, Release("One"));
        fill.Lines[0].Title = "";

        fill.Apply(albumFields: false);

        Assert.Equal("What the file says", rows[0].Title);
    }

    /// <summary>The track's own artist rides along where the release names one: a compilation's row,
    /// a guest credit.</summary>
    [Fact]
    public void A_tracks_own_artist_is_written_where_the_release_names_one()
    {
        List<TrackRow> rows = [Row(1), Row(2)];
        var release = new ReleaseDetail(
            new ReleaseRef("musicbrainz", "r1"), "A Compilation", "Various", 1998,
            Country: null, Format: null,
            Tracks: [Listed(1, "One", "First Band"), Listed(2, "Two")]);

        ReleaseTrackFill.Pair(rows, release.Tracks, new Dictionary<TagFieldKind, string>())
                        .Apply(albumFields: false);

        Assert.Equal("First Band", rows[0].Artist);
        // The release artist is NOT stamped on a row the listing says nothing about: that is how a
        // various-artists record ends up filed under one name.
        Assert.Equal("", rows[1].Artist);
    }

    // ── the record's own fields ───────────────────────────────────────────────

    /// <summary>Ticked, the album fields go onto every file, including the ones the release has no track
    /// for. They are on the same record whether or not it lists them.</summary>
    [Fact]
    public void Album_fields_go_to_every_file_including_the_unmatched_ones()
    {
        List<TrackRow> rows = [Row(1), Row(2), Row(3)];
        var fill = Fill(rows, Release("One", "Two"), withAlbumFields: true);

        var result = fill.Apply(albumFields: true);

        Assert.All(rows, r => Assert.Equal("The Record", r.Album));
        Assert.All(rows, r => Assert.Equal("The Band", r.AlbumArtist));
        Assert.All(rows, r => Assert.Equal("1973", r.Year));
        Assert.All(rows, r => Assert.Equal("Harvest", r.Publisher));
        // The third file took the album fields and nothing else, and still counts as written.
        Assert.Equal(3, result.Named);
    }

    /// <summary>Unticked, a lookup meant to fix twelve titles does not rename the record.</summary>
    [Fact]
    public void Album_fields_left_unticked_are_not_written()
    {
        List<TrackRow> rows = [Row(1)];
        rows[0].Album = "What it was called";
        var fill = Fill(rows, Release("One"), withAlbumFields: true);

        fill.Apply(albumFields: false);

        Assert.Equal("What it was called", rows[0].Album);
        Assert.Equal("One", rows[0].Title);
    }

    /// <summary>Nothing ticked and no album fields is a page the user walked away from, and it must
    /// not report having written anything.</summary>
    [Fact]
    public void Nothing_ticked_writes_nothing()
    {
        List<TrackRow> rows = [Row(1, "Mine"), Row(2, "Also mine")];
        var fill = Fill(rows, Release("One", "Two"));
        fill.TickAll(false);

        var result = fill.Apply(albumFields: false);

        Assert.Equal(0, result.Named);
        Assert.Equal("Mine", rows[0].Title);
        Assert.Equal("Also mine", rows[1].Title);
    }

    // ── the order the lines come out in ───────────────────────────────────────

    /// <summary>The lines are in the record's running order, not in the order the grid handed the rows
    /// over in: a selection sorted by title would otherwise pair the listing against an order nobody
    /// meant, and every file would take a neighbour's name.</summary>
    [Fact]
    public void Lines_are_in_the_records_running_order()
    {
        List<TrackRow> rows = [Row(3), Row(1), Row(2)];
        var fill = Fill(rows, Release("One", "Two", "Three"));

        Assert.Equal([1, 2, 3], [.. fill.Lines.Select(l => l.Position)]);
        Assert.Equal(["One", "Two", "Three"], [.. fill.Lines.Select(l => l.Title)]);

        fill.Apply(albumFields: false);

        Assert.Equal("Three", rows[0].Title);
        Assert.Equal("One", rows[1].Title);
        Assert.Equal("Two", rows[2].Title);
    }

    /// <summary>The release's own fields, read off whichever half of the answer has them.</summary>
    [Fact]
    public void The_records_fields_come_off_the_listing_when_there_is_no_search_row()
    {
        var fields = ReleaseTrackFill.AlbumFieldsOf(null, Release("One"));

        Assert.Equal("The Record", fields[TagFieldKind.Album]);
        Assert.Equal("The Band", fields[TagFieldKind.AlbumArtist]);
        Assert.Equal("1973", fields[TagFieldKind.Year]);
        Assert.Equal("Harvest", fields[TagFieldKind.Publisher]);
        // A release that does not say how many discs it spans says nothing, rather than saying one.
        Assert.False(fields.ContainsKey(TagFieldKind.DiscCount));
    }
}
