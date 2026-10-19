using RPM.Core.Models;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Presentation.Main.LeftPane.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// What the mass-edit window may offer.
///
/// <para>The window separates per-file fields from per-selection fields. A field that names ONE file, such
/// as a title, would give all twelve selected files the same identity if it were set across them. So
/// per-file fields are never offered across a selection. The album fields are on the other side of this
/// line. They are always offered, because a record whose tracks are scattered is the problem this window
/// fixes. A caution band shows how far a tick reaches. The album editor is the window that stays inside one
/// record.</para>
/// </summary>
public class MassEditFieldsTests
{
    /// <summary>The title and the track number name a single file. So do the sort name of the title, the
    /// movement this file is, the musical key, and the recording id.</summary>
    [Theory]
    [InlineData(TagFieldKind.Title)]
    [InlineData(TagFieldKind.TrackNo)]
    [InlineData(TagFieldKind.Lyrics)]
    [InlineData(TagFieldKind.TitleSort)]
    [InlineData(TagFieldKind.Movement)]
    [InlineData(TagFieldKind.MovementName)]
    [InlineData(TagFieldKind.MusicalKey)]
    [InlineData(TagFieldKind.RecordingId)]
    public void A_field_that_names_one_file_is_never_offered_across_a_selection(TagFieldKind kind)
    {
        Assert.True(MassEdit.IsPerFile(kind));
        Assert.DoesNotContain(MassEdit.FieldsFor(), f => f.Kind == kind);
    }

    /// <summary>
    /// Deliberately NOT per-file. Selecting the four movements of a symphony and naming the work once is
    /// what this window is for. It is the fastest way to file a classical record.
    /// </summary>
    [Fact]
    public void The_work_a_track_is_part_of_is_offered_across_a_selection()
    {
        Assert.False(MassEdit.IsPerFile(TagFieldKind.Work));
        Assert.Contains(MassEdit.FieldsFor(), f => f.Kind == TagFieldKind.Work);
    }

    /// <summary>The record's own fields are always offered, whatever the selection is. This includes a
    /// selection spread across folders. That case is why the caution band exists.</summary>
    [Fact]
    public void The_records_own_fields_are_always_offered()
    {
        var fields = MassEdit.FieldsFor().Select(f => f.Kind).ToList();

        Assert.Contains(TagFieldKind.Album, fields);
        Assert.Contains(TagFieldKind.AlbumArtist, fields);
        Assert.Contains(TagFieldKind.Year, fields);
    }

    /// <summary>What raises the mass-edit window's caution band. The window offers every field either way,
    /// because a record whose tracks are spread over four folders is the problem it exists to fix. So this
    /// check no longer decides what may be edited. It only decides whether the user is told that a ticked
    /// field will land on more than one record.
    /// </summary>
    [Fact]
    public void A_sweep_across_folders_is_what_raises_the_caution()
    {
        Assert.Equal(
            Path.Combine("Library", "Oasis", "Definitely Maybe"),
            MassEdit.AlbumFolderOf([
                Row(Path.Combine("Library", "Oasis", "Definitely Maybe", "01.mp3")),
                Row(Path.Combine("Library", "Oasis", "Definitely Maybe", "02.mp3")),
            ]));

        Assert.Null(MassEdit.AlbumFolderOf([
            Row(Path.Combine("Library", "Oasis", "Definitely Maybe", "01.mp3")),
            Row(Path.Combine("Library", "Blur", "Parklife", "01.mp3")),
        ]));
    }

    private static TrackRow Row(string path) =>
        new(new TrackInfo { FilePath = path, Extension = Path.GetExtension(path) });

    /// <summary>The album fields are added to the track fields, not used in their place. A record's tracks
    /// still get artist, genre and the other track fields.</summary>
    [Fact]
    public void An_albums_fields_are_offered_on_top_of_the_track_fields()
    {
        var kinds = MassEdit.FieldsFor().Select(f => f.Kind).ToHashSet();

        Assert.Contains(TagFieldKind.Artist, kinds);    // the track's own
        Assert.Contains(TagFieldKind.Album, kinds);     // the record's
        Assert.Contains(TagFieldKind.Disc, kinds);      // and the disc's
    }

    [Fact]
    public void Everything_offered_is_a_field_the_catalogue_knows()
    {
        foreach (var field in MassEdit.FieldsFor())
            Assert.Same(field, TagFields.Get(field.Kind));
    }

    /// <summary>No field is offered twice. If a window drew one field in two rows, the second row would
    /// silently win.</summary>
    [Fact]
    public void Nothing_is_offered_twice()
    {
        var kinds = MassEdit.FieldsFor().Select(f => f.Kind).ToList();
        Assert.Equal(kinds.Count, kinds.Distinct().Count());
    }
}
