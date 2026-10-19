using RPM.Core.Library;
using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// What two of one listener's computers do with the same album when both have edited it.
///
/// <para>The rule is the whole feature: the later edit wins and takes the whole row, the stamp that
/// arrives is the stamp that is kept, and a row nobody has edited on this machine does not mean the
/// row was cleared on the other one.</para>
/// </summary>
public class PeerAlbumMetaTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public PeerAlbumMetaTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmalb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static AlbumMetaEdit Edit(string folder, long at, bool? classical = null, int rating = 0)
        => new(AlbumMeta.Default(folder) with { Classical = classical, Rating = rating }, at);

    // ── the merge ──

    [Fact]
    public void An_album_this_machine_has_never_heard_of_is_taken()
    {
        var taken = _db.ApplyAlbumMetaEdits([Edit("Cher/Half Breed", at: 500, classical: true)], ArrivingAlbumMarks.Whole);

        Assert.Equal(1, taken);
        Assert.True(_db.GetAlbumMeta("Cher/Half Breed").Classical);
    }

    [Fact]
    public void The_later_edit_wins()
    {
        _db.SetAlbumClassical("Cher/Half Breed", false);
        var mine = _db.AlbumMetaEdits().Single().EditedUtcTicks;

        _db.ApplyAlbumMetaEdits([Edit("Cher/Half Breed", at: mine + 1, classical: true)], ArrivingAlbumMarks.Whole);

        Assert.True(_db.GetAlbumMeta("Cher/Half Breed").Classical);
    }

    [Fact]
    public void The_earlier_edit_is_left_where_it_is()
    {
        _db.SetAlbumClassical("Cher/Half Breed", true);
        var mine = _db.AlbumMetaEdits().Single().EditedUtcTicks;

        var taken = _db.ApplyAlbumMetaEdits([Edit("Cher/Half Breed", at: mine - 1, classical: false)], ArrivingAlbumMarks.Whole);

        Assert.Equal(0, taken);
        Assert.True(_db.GetAlbumMeta("Cher/Half Breed").Classical);
    }

    /// <summary>The stamp that arrives is the stamp that is kept, which makes the merge stable: the
    /// second pass in either direction has nothing left to do.</summary>
    [Fact]
    public void A_merge_settles_rather_than_bouncing()
    {
        _db.SetAlbumClassical("Cher/Half Breed", false);
        var theirs = _db.AlbumMetaEdits().Single().EditedUtcTicks + 1;
        var edit = Edit("Cher/Half Breed", at: theirs, classical: true);

        _db.ApplyAlbumMetaEdits([edit], ArrivingAlbumMarks.Whole);

        Assert.Equal(theirs, _db.AlbumMetaEdits().Single().EditedUtcTicks);
        Assert.Equal(0, _db.ApplyAlbumMetaEdits([edit], ArrivingAlbumMarks.Whole));
    }

    /// <summary>A whole row, not a field: the stars from one machine and the classical mark from the
    /// other would give a record neither listener has.</summary>
    [Fact]
    public void The_row_travels_whole()
    {
        _db.SetAlbumReview("Cher/Half Breed", rating: 5, reviewMarkdown: "mine", updatedUtcTicks: 1);
        var mine = _db.AlbumMetaEdits().Single().EditedUtcTicks;

        _db.ApplyAlbumMetaEdits([Edit("Cher/Half Breed", at: mine + 1, classical: true, rating: 2)], ArrivingAlbumMarks.Whole);

        var meta = _db.GetAlbumMeta("Cher/Half Breed");
        Assert.Equal(2, meta.Rating);
        Assert.Null(meta.ReviewMarkdown);
        Assert.True(meta.Classical);
    }

    [Fact]
    public void An_album_the_other_machine_says_nothing_about_keeps_what_it_has()
    {
        _db.SetAlbumClassical("Cher/Half Breed", true);

        _db.ApplyAlbumMetaEdits([Edit("James Blunt/Back to Bedlam", at: long.MaxValue)], ArrivingAlbumMarks.Whole);

        Assert.True(_db.GetAlbumMeta("Cher/Half Breed").Classical);
    }

    // ── the stamp ──

    [Fact]
    public void Saying_something_stamps_the_row()
    {
        _db.SetAlbumClassical("Cher/Half Breed", true);

        Assert.True(_db.AlbumMetaEdits().Single().EditedUtcTicks > 0);
    }

    /// <summary>Internal bookkeeping is not a user edit: the store accepting a mark this machine
    /// already sent must not make the row look newer than another machine's real edit.</summary>
    [Fact]
    public void Filing_what_the_store_took_does_not()
    {
        _db.SetAlbumFavorite("Cher/Half Breed", true);
        var said = _db.AlbumMetaEdits().Single().EditedUtcTicks;

        _db.NoteAlbumMarkSent("Cher/Half Breed", cloudUpdatedAt: 999);

        Assert.Equal(said, _db.AlbumMetaEdits().Single().EditedUtcTicks);
    }

    // ── the version, which tells a peer that a change is worth fetching ──

    /// <summary>An album mark changes no track row, so a version computed over the tracks alone would
    /// answer "still the same catalogue" to a machine that has something to collect.</summary>
    [Fact]
    public void The_catalog_version_moves_when_only_an_album_changed()
    {
        var albums = PeerCatalogBuilder.From([Edit("Cher/Half Breed", at: 500, classical: true)], Owner);
        var after = PeerCatalogBuilder.From([Edit("Cher/Half Breed", at: 600, classical: false)], Owner);

        Assert.NotEqual(PeerCatalog.VersionOf([], albums, [], []), PeerCatalog.VersionOf([], after, [], []));
        Assert.NotEqual(PeerCatalog.VersionOf([], albums, [], []), PeerCatalog.VersionOf([], [], [], []));
    }

    [Fact]
    public void An_album_survives_the_round_trip_through_the_wire()
    {
        _db.SetAlbumReview("Cher/Half Breed", rating: 4, reviewMarkdown: "the best of it", updatedUtcTicks: 7);
        _db.SetAlbumClassical("Cher/Half Breed", false);
        _db.SetAlbumPressing("Cher/Half Breed", "discogs", "12345");

        var sent = PeerCatalogBuilder.From(_db.AlbumMetaEdits(), Owner);
        var back = PeerCatalogBuilder.To(sent).Single();

        Assert.Equal(_db.AlbumMetaEdits().Single(), back);
    }

    // ── what a guest receives, and what they do not ──

    /// <summary>A borrowed classical box must be shown as works and movements, and a borrowed record
    /// must know which pressing it is. Both are facts about the record.</summary>
    [Fact]
    public void A_guest_is_sent_what_the_record_is()
    {
        _db.SetAlbumClassical("Cher/Half Breed", true);
        _db.SetAlbumPressing("Cher/Half Breed", "discogs", "12345");
        _db.SetAlbumRelease("Cher/Half Breed", catno: "MCA-2104", released: "1973");

        var sent = PeerCatalogBuilder.From(_db.AlbumMetaEdits(), Guest).Single();

        Assert.True(sent.Classical);
        Assert.Equal("discogs", sent.ReleaseProvider);
        Assert.Equal("12345", sent.ReleaseId);
        Assert.Equal("MCA-2104", sent.Catno);
        Assert.Equal("1973", sent.Released);
        Assert.True(sent.EditedUtcTicks > 0);
    }

    /// <summary>But nothing of the owner's own opinion of it. A shelf is only what is on it.</summary>
    [Fact]
    public void A_guest_is_sent_nothing_of_what_the_owner_thinks()
    {
        _db.SetAlbumFavorite("Cher/Half Breed", true);
        _db.SetAlbumReview("Cher/Half Breed", rating: 5, reviewMarkdown: "the best of it", updatedUtcTicks: 7);
        _db.SetAlbumNormalize("Cher/Half Breed", false);

        var sent = PeerCatalogBuilder.From(_db.AlbumMetaEdits(), Guest).Single();

        Assert.False(sent.Favorite);
        Assert.Equal(0, sent.Rating);
        Assert.Null(sent.Review);
        Assert.Equal(0, sent.ReviewUpdatedUtcTicks);
        Assert.Null(sent.Normalize);
    }

    /// <summary>The owner's own other computer is the same collection, and receives everything.</summary>
    [Fact]
    public void The_owner_s_other_machine_is_sent_everything()
    {
        _db.SetAlbumFavorite("Cher/Half Breed", true);
        _db.SetAlbumReview("Cher/Half Breed", rating: 5, reviewMarkdown: "the best of it", updatedUtcTicks: 7);

        var sent = PeerCatalogBuilder.From(_db.AlbumMetaEdits(), Owner).Single();

        Assert.True(sent.Favorite);
        Assert.Equal(5, sent.Rating);
        Assert.Equal("the best of it", sent.Review);
    }

    private const PeerCatalogBuilder.AlbumAudience Owner = PeerCatalogBuilder.AlbumAudience.TheOwner;
    private const PeerCatalogBuilder.AlbumAudience Guest = PeerCatalogBuilder.AlbumAudience.AGuest;

    // ── a record that moved ───────────────────────────────────────────────────
    //
    // A re-file moves the row to the folder the tracks were moved to. That is an arrival, and this
    // merge cannot see a departure (nothing is ever deleted), so the move must also be sent as a
    // row: the old folder, emptied, stamped now.

    /// <summary>The marks arrive at the new folder and the old one is left empty.</summary>
    [Fact]
    public void A_moved_record_leaves_an_emptied_row_behind()
    {
        _db.SetAlbumReview("Old/Record", 4, "Lovely", 10);
        _db.SetAlbumPressing("Old/Record", "discogs", "12345");
        _db.SetAlbumFavorite("Old/Record", true);

        _db.MoveAlbumMeta("Old/Record", "The Band/The Record");

        var landed = _db.GetAlbumMeta("The Band/The Record");
        Assert.Equal(4, landed.Rating);
        Assert.Equal("Lovely", landed.ReviewMarkdown);
        Assert.Equal("12345", landed.ReleaseId);
        Assert.True(landed.IsFavorite);

        var left = _db.GetAlbumMeta("Old/Record");
        Assert.Equal(0, left.Rating);
        Assert.Null(left.ReviewMarkdown);
        Assert.Null(left.ReleaseId);
    }

    /// <summary>
    /// The heart is the exception, because of the cloud.
    ///
    /// <para>The merge treats any row it takes as a mark to send to the store. On the machine that has
    /// not re-filed, this folder still has music in it. So an emptied row that turned the heart off
    /// would make that machine tell the store the record is no longer a favourite, while the machine
    /// that did re-file has no tracks under the new folder yet and cannot correct it. Leaving the flag
    /// unchanged makes the arriving value equal to the one already there, and the mark pass finds
    /// nothing to send.</para>
    /// </summary>
    [Fact]
    public void A_move_never_changes_whether_a_record_is_a_favourite()
    {
        _db.SetAlbumReview("Old/Record", 4, "Lovely", 10);
        _db.SetAlbumFavorite("Old/Record", true);

        _db.MoveAlbumMeta("Old/Record", "The Band/The Record");

        Assert.True(_db.GetAlbumMeta("The Band/The Record").IsFavorite);
        Assert.True(_db.GetAlbumMeta("Old/Record").IsFavorite);
    }

    /// <summary>That emptied row is what is sent. Without it the other machine takes the arrival,
    /// keeps its own copy of the old row, and holds the record's stars twice: once under a folder with
    /// no music in it, which the next record filed there would inherit.</summary>
    [Fact]
    public void The_emptied_row_clears_the_other_machines_copy()
    {
        // This machine has NOT re-filed: it still holds the record in the old place.
        _db.SetAlbumReview("Old/Record", 5, "Mine", 10);
        _db.SetAlbumPressing("Old/Record", "discogs", "12345");

        // What the machine that did re-file sends: the marks at their new folder, and the old folder
        // emptied at a later moment.
        var moved = DateTime.UtcNow.Ticks;
        _db.ApplyAlbumMetaEdits(
        [
            new AlbumMetaEdit(
                AlbumMeta.Default("The Band/The Record") with
                {
                    Rating = 5, ReviewMarkdown = "Mine", ReleaseId = "12345", ReleaseProvider = "discogs",
                },
                moved - 1),
            new AlbumMetaEdit(AlbumMeta.Default("Old/Record"), moved),
        ], ArrivingAlbumMarks.Whole);

        Assert.Equal(5, _db.GetAlbumMeta("The Band/The Record").Rating);
        Assert.Equal(0, _db.GetAlbumMeta("Old/Record").Rating);
        Assert.Null(_db.GetAlbumMeta("Old/Record").ReleaseId);
    }

    /// <summary>The emptied row is stamped at the moment of the move, so it beats what the peer still
    /// holds. That is the reason it is written.</summary>
    [Fact]
    public void The_emptied_row_is_stamped_later_than_what_it_replaces()
    {
        _db.SetAlbumReview("Old/Record", 3, null, 10);
        var said = _db.AlbumMetaEdits().Single(e => e.Meta.Folder == "Old/Record").EditedUtcTicks;

        _db.MoveAlbumMeta("Old/Record", "The Band/The Record");

        var left = _db.AlbumMetaEdits().Single(e => e.Meta.Folder == "Old/Record");
        Assert.True(left.EditedUtcTicks >= said);
    }

    /// <summary>The moved row keeps the time a USER last edited it. Re-filing a file is not a user
    /// edit about the record, and updating the stamp would let this machine's copy beat a later edit
    /// the other machine already made to that folder.</summary>
    [Fact]
    public void Moving_a_record_is_not_somebody_saying_something_about_it()
    {
        _db.SetAlbumReview("Old/Record", 3, null, 10);
        var said = _db.AlbumMetaEdits().Single(e => e.Meta.Folder == "Old/Record").EditedUtcTicks;

        _db.MoveAlbumMeta("Old/Record", "The Band/The Record");

        Assert.Equal(
            said, _db.AlbumMetaEdits().Single(e => e.Meta.Folder == "The Band/The Record").EditedUtcTicks);
    }

    /// <summary>A record nobody has ever edited has no departure to send, and two machines should
    /// not both end up holding an empty row for it.</summary>
    [Fact]
    public void A_record_with_nothing_written_about_it_leaves_nothing_behind()
    {
        _db.MoveAlbumMeta("Old/Record", "The Band/The Record");

        Assert.Empty(_db.AlbumMetaEdits());
    }
}
