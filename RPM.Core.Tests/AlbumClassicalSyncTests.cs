using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The classical mark travelling between this library and the store, so the phone can draw a classical
/// record as works and movements.
///
/// <para>What has to hold: an edit made here is sent and is never overwritten by an older answer from
/// the store; an edit made while a send or an accept is in flight is not lost; marks that have no
/// edit stamp are sent anyway; and an answer taken from the store never moves the stamp the LAN
/// merge uses to choose whole rows.</para>
/// </summary>
public class AlbumClassicalSyncTests : IDisposable
{
    private const string Folder = "Mozart/Piano Sonatas";
    private const string Key = "0123456789abcdef0123456789abcdef";

    private readonly string _root;
    private readonly LibraryDatabase _db;

    public AlbumClassicalSyncTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmcls-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private AlbumClassicalSync State() => _db.AlbumClassicalSyncStates().Single(s => s.Folder == Folder);

    private static CloudAlbumMark Stored(bool? classical, long updatedAt = 100)
        => new(Key, Favorite: false, FavoritedAt: 0, UpdatedAt: updatedAt, Classical: classical);

    private static AlbumClassicalSync Here(bool? classical, long edited = 0, long synced = 0, long cloud = 0)
        => new(Folder, classical, edited, synced, cloud);

    // ── the database ──

    [Fact]
    public void Marking_a_record_classical_leaves_it_to_be_sent()
    {
        _db.SetAlbumClassical(Folder, true);

        Assert.True(State().ChangedHere);
        Assert.True(State().IsClassical);
    }

    [Fact]
    public void A_sent_mark_is_no_longer_outstanding()
    {
        _db.SetAlbumClassical(Folder, true);

        _db.NoteAlbumClassicalSent(State(), cloudUpdatedAt: 42);

        Assert.False(State().ChangedHere);
        Assert.Equal(42, State().CloudUpdatedAt);
    }

    /// <summary>The send is a network round trip. A mark changed during it must not be recorded as
    /// sent, or it never goes out.</summary>
    [Fact]
    public void A_mark_changed_while_the_send_was_in_flight_stays_outstanding()
    {
        _db.SetAlbumClassical(Folder, true);
        var sending = State();
        Thread.Sleep(2);
        _db.SetAlbumClassical(Folder, false);

        _db.NoteAlbumClassicalSent(sending, cloudUpdatedAt: 42);

        Assert.True(State().ChangedHere);
    }

    [Fact]
    public void The_stores_answer_is_taken()
    {
        _db.SetAlbumReview(Folder, 3, null, 1);

        Assert.True(_db.AcceptAlbumClassical(State(), classical: true, cloudUpdatedAt: 77));

        Assert.True(_db.GetAlbumMeta(Folder).Classical);
        Assert.False(State().ChangedHere);
        Assert.Equal(77, State().CloudUpdatedAt);
    }

    [Fact]
    public void The_stores_answer_is_taken_for_a_record_with_no_row_yet()
    {
        Assert.True(_db.AcceptAlbumClassical(Here(null), classical: true, cloudUpdatedAt: 77));

        Assert.True(_db.GetAlbumMeta(Folder).Classical);
        Assert.False(State().ChangedHere);
    }

    /// <summary>The accept is decided from states read before it is written. A mark set in the editor
    /// in between is the newer statement and must survive.</summary>
    [Fact]
    public void The_stores_answer_does_not_overwrite_an_edit_made_after_the_read()
    {
        _db.SetAlbumReview(Folder, 3, null, 1);
        var read = State();
        _db.SetAlbumClassical(Folder, false);

        Assert.False(_db.AcceptAlbumClassical(read, classical: true, cloudUpdatedAt: 77));

        Assert.False(_db.GetAlbumMeta(Folder).Classical);
        Assert.True(State().ChangedHere);
    }

    /// <summary>The LAN merge takes whole rows by edited_utc_ticks. A row moved by the store's answer
    /// would replace another machine's review and rating with this one's.</summary>
    [Fact]
    public void The_stores_answer_does_not_move_the_rows_merge_stamp()
    {
        _db.SetAlbumReview(Folder, 3, null, 1);
        var before = _db.AlbumMetaEdits().Single().EditedUtcTicks;

        _db.AcceptAlbumClassical(State(), classical: true, cloudUpdatedAt: 77);

        Assert.Equal(before, _db.AlbumMetaEdits().Single().EditedUtcTicks);
    }

    [Fact]
    public void A_row_made_by_the_stores_answer_loses_every_merge()
    {
        _db.AcceptAlbumClassical(Here(null), classical: true, cloudUpdatedAt: 77);

        Assert.Equal(0, _db.AlbumMetaEdits().Single().EditedUtcTicks);
    }

    /// <summary>A mark that arrived over the LAN from another of the listener's machines is owed to
    /// the store as well, as the heart is.</summary>
    [Fact]
    public void A_mark_changed_by_the_LAN_merge_is_left_to_be_sent()
    {
        _db.SetAlbumReview(Folder, 3, null, 1);
        _db.NoteAlbumClassicalSent(State(), 1);
        var mine = _db.AlbumMetaEdits().Single().EditedUtcTicks;

        _db.ApplyAlbumMetaEdits(
            [new AlbumMetaEdit(AlbumMeta.Default(Folder) with { Classical = true }, mine + 1)],
            ArrivingAlbumMarks.Whole);

        Assert.True(State().ChangedHere);
    }

    [Fact]
    public void A_merge_that_leaves_the_mark_as_it_was_sends_nothing()
    {
        _db.SetAlbumClassical(Folder, true);
        _db.NoteAlbumClassicalSent(State(), 1);
        var mine = _db.AlbumMetaEdits().Single().EditedUtcTicks;

        _db.ApplyAlbumMetaEdits(
            [new AlbumMetaEdit(AlbumMeta.Default(Folder) with { Classical = true, Rating = 4 }, mine + 1)],
            ArrivingAlbumMarks.Whole);

        Assert.False(State().ChangedHere);
    }

    /// <summary>A borrowed shelf's mirror is never synced to the store, so nothing there is owed.</summary>
    [Fact]
    public void A_borrowed_shelfs_mark_is_not_left_to_be_sent()
    {
        _db.SetAlbumReview(Folder, 3, null, 1);
        _db.NoteAlbumClassicalSent(State(), 1);
        var mine = _db.AlbumMetaEdits().Single().EditedUtcTicks;

        _db.ApplyAlbumMetaEdits(
            [new AlbumMetaEdit(AlbumMeta.Default(Folder) with { Classical = true }, mine + 1)],
            ArrivingAlbumMarks.NotTheHeart);

        Assert.True(_db.GetAlbumMeta(Folder).Classical);
        Assert.False(State().ChangedHere);
    }

    [Fact]
    public void Signing_out_of_the_account_leaves_every_mark_to_be_sent_to_the_next()
    {
        _db.SetAlbumClassical(Folder, true);
        _db.NoteAlbumClassicalSent(State(), 42);

        _db.ForgetCloudAccount();

        Assert.True(State().ChangedHere);
        Assert.Equal(0, State().CloudUpdatedAt);
    }

    // ── the rules ──

    [Fact]
    public void An_answer_from_the_store_that_moved_on_is_taken()
        => Assert.True(AlbumClassicalPlan.Takes(Here(false, cloud: 50), Stored(true, updatedAt: 60)));

    [Fact]
    public void An_answer_the_store_had_already_given_is_not_taken_again()
        => Assert.False(AlbumClassicalPlan.Takes(Here(false, cloud: 60), Stored(true, updatedAt: 60)));

    [Fact]
    public void No_answer_in_the_store_is_not_an_answer()
        => Assert.False(AlbumClassicalPlan.Takes(Here(true, cloud: 0), Stored(null, updatedAt: 60)));

    [Fact]
    public void A_mark_changed_here_beats_the_store()
        => Assert.False(AlbumClassicalPlan.Takes(Here(false, edited: 9, synced: 1), Stored(true, updatedAt: 60)));

    [Fact]
    public void A_mark_changed_here_is_sent()
        => Assert.Equal(AlbumClassicalPlan.Outbound.Send,
            AlbumClassicalPlan.Out(Here(true, edited: 9, synced: 1), Stored(false)));

    [Fact]
    public void A_mark_the_store_already_holds_is_recorded_as_agreed()
        => Assert.Equal(AlbumClassicalPlan.Outbound.Agreed,
            AlbumClassicalPlan.Out(Here(true, edited: 9, synced: 1), Stored(true)));

    /// <summary>Absent reads as not classical on every client, so writing false onto it would be a
    /// write per album for nothing.</summary>
    [Fact]
    public void Not_classical_onto_no_answer_is_agreement()
    {
        Assert.Equal(AlbumClassicalPlan.Outbound.Agreed,
            AlbumClassicalPlan.Out(Here(false, edited: 9, synced: 1), stored: null));
        Assert.Equal(AlbumClassicalPlan.Outbound.Agreed,
            AlbumClassicalPlan.Out(Here(false, edited: 9, synced: 1), Stored(null)));
    }

    /// <summary>A mark can have no edit stamp, and a re-filed record has no row under its new key.
    /// Both are sent.</summary>
    [Fact]
    public void A_classical_record_with_no_answer_in_the_store_is_sent_unchanged()
    {
        Assert.Equal(AlbumClassicalPlan.Outbound.Send, AlbumClassicalPlan.Out(Here(true), stored: null));
        Assert.Equal(AlbumClassicalPlan.Outbound.Send, AlbumClassicalPlan.Out(Here(true), Stored(null)));
    }

    [Fact]
    public void An_unchanged_mark_the_store_answered_otherwise_is_left_for_the_pull()
        => Assert.Equal(AlbumClassicalPlan.Outbound.Leave, AlbumClassicalPlan.Out(Here(true), Stored(false)));

    [Fact]
    public void An_ordinary_record_nobody_changed_sends_nothing()
        => Assert.Equal(AlbumClassicalPlan.Outbound.Leave, AlbumClassicalPlan.Out(Here(false), stored: null));
}
