using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// A record's levelling and series travelling between this library and the store, so the phone, the
/// television and the web app can show a record as one volume of its series.
///
/// <para>The rules are the classical mark's (see <see cref="AlbumClassicalSyncTests"/>) for three values
/// that travel together. The difference worth testing: on a store row that no machine has set the settings
/// on, a null levelling and a missing series are silence, not an answer.</para>
/// </summary>
public class AlbumSettingsSyncTests : IDisposable
{
    private const string Folder = "Various/Now 43";
    private const string Key = "11111111111111111111111111111111";

    private readonly string _root;
    private readonly LibraryDatabase _db;

    public AlbumSettingsSyncTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmset-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static readonly AlbumSeries Now = new("Now That's What I Call Music", "43");

    private AlbumSettingsSync State() => _db.AlbumSettingsSyncStates().Single(s => s.Folder == Folder);

    private static CloudAlbumMark Stored(bool? normalize, AlbumSeries? series, long updatedAt = 100, long settingsAt = 100)
        => new(Key, false, 0, updatedAt, null, normalize, series?.Name, series?.Volume, settingsAt);

    private static AlbumSettingsSync Here(bool? normalize, AlbumSeries? series, long edited = 0, long synced = 0, long cloud = 0)
        => new(Folder, normalize, series, edited, synced, cloud);

    // ── the database ──

    [Fact]
    public void Setting_the_series_or_the_levelling_leaves_the_record_to_be_sent()
    {
        _db.SetAlbumSeries(Folder, Now);
        Assert.True(State().ChangedHere);
        Assert.Equal(Now, State().Series);

        _db.NoteAlbumSettingsSent(State(), 5);
        Assert.False(State().ChangedHere);

        _db.SetAlbumNormalize(Folder, false);
        Assert.True(State().ChangedHere);
        Assert.False(State().Normalize);
    }

    [Fact]
    public void The_stores_answer_is_taken_and_does_not_move_the_rows_merge_stamp()
    {
        _db.SetAlbumReview(Folder, 3, null, 1);
        var before = _db.AlbumMetaEdits().Single().EditedUtcTicks;

        Assert.True(_db.AcceptAlbumSettings(State(), true, Now, 200));

        var meta = _db.GetAlbumMeta(Folder);
        Assert.True(meta.Normalize);
        Assert.Equal(Now, meta.InSeries);
        Assert.Equal(200, State().CloudUpdatedAt);
        Assert.False(State().ChangedHere);
        Assert.Equal(before, _db.AlbumMetaEdits().Single().EditedUtcTicks);
    }

    [Fact]
    public void The_stores_answer_is_taken_for_a_record_with_no_row_yet()
    {
        Assert.True(_db.AcceptAlbumSettings(Here(null, null), null, Now, 200));
        Assert.Equal(Now, _db.GetAlbumMeta(Folder).InSeries);
    }

    [Fact]
    public void The_stores_answer_does_not_overwrite_an_edit_made_after_the_read()
    {
        _db.SetAlbumSeries(Folder, Now);
        _db.NoteAlbumSettingsSent(State(), 5);
        var read = State();
        Thread.Sleep(2);
        _db.SetAlbumSeries(Folder, new AlbumSeries("Mine"));

        Assert.False(_db.AcceptAlbumSettings(read, null, null, 200));
        Assert.Equal("Mine", _db.GetAlbumMeta(Folder).Series);
    }

    [Fact]
    public void A_series_changed_by_the_LAN_merge_is_left_to_be_sent()
    {
        _db.SetAlbumReview(Folder, 3, null, 1);
        _db.NoteAlbumSettingsSent(State(), 1);
        var mine = _db.AlbumMetaEdits().Single().EditedUtcTicks;

        _db.ApplyAlbumMetaEdits(
            [new AlbumMetaEdit(AlbumMeta.Default(Folder) with { Series = Now.Name, SeriesVolume = Now.Volume }, mine + 1)],
            ArrivingAlbumMarks.Whole);

        Assert.True(State().ChangedHere);
    }

    [Fact]
    public void A_merge_that_leaves_the_settings_as_they_were_sends_nothing()
    {
        _db.SetAlbumSeries(Folder, Now);
        _db.NoteAlbumSettingsSent(State(), 1);
        var mine = _db.AlbumMetaEdits().Single().EditedUtcTicks;

        _db.ApplyAlbumMetaEdits(
            [new AlbumMetaEdit(AlbumMeta.Default(Folder) with { Series = Now.Name, SeriesVolume = Now.Volume, Rating = 4 }, mine + 1)],
            ArrivingAlbumMarks.Whole);

        Assert.False(State().ChangedHere);
    }

    [Fact]
    public void Signing_out_of_the_account_leaves_the_settings_to_be_sent_to_the_next()
    {
        _db.SetAlbumSeries(Folder, Now);
        _db.NoteAlbumSettingsSent(State(), 42);

        _db.ForgetCloudAccount();

        Assert.True(State().ChangedHere);
        Assert.Equal(0, State().CloudUpdatedAt);
    }

    // ── the rules ──

    [Fact]
    public void An_answer_from_the_store_that_moved_on_is_taken()
        => Assert.True(AlbumSettingsPlan.Takes(Here(null, null, cloud: 50), Stored(false, Now)));

    [Fact]
    public void A_row_no_machine_set_the_settings_on_is_not_an_answer()
        => Assert.False(AlbumSettingsPlan.Takes(Here(true, Now, cloud: 50), Stored(null, null, settingsAt: 0)));

    [Fact]
    public void An_answer_the_store_had_already_given_is_not_taken_again()
        => Assert.False(AlbumSettingsPlan.Takes(Here(null, null, cloud: 100), Stored(false, Now)));

    [Fact]
    public void Settings_changed_here_beat_the_store()
        => Assert.False(AlbumSettingsPlan.Takes(Here(true, null, edited: 9, synced: 1, cloud: 50), Stored(false, Now)));

    [Fact]
    public void Settings_changed_here_are_sent_and_agreement_is_recorded()
    {
        Assert.Equal(AlbumClassicalPlan.Outbound.Send, AlbumSettingsPlan.Out(Here(true, Now, edited: 9), Stored(false, Now)));
        Assert.Equal(AlbumClassicalPlan.Outbound.Agreed, AlbumSettingsPlan.Out(Here(false, Now, edited: 9), Stored(false, Now)));
    }

    [Fact]
    public void Nothing_set_onto_no_answer_is_agreement()
        => Assert.Equal(AlbumClassicalPlan.Outbound.Agreed, AlbumSettingsPlan.Out(Here(null, null, edited: 9), null));

    [Fact]
    public void A_series_with_no_answer_in_the_store_is_sent_unchanged()
    {
        Assert.Equal(AlbumClassicalPlan.Outbound.Send, AlbumSettingsPlan.Out(Here(null, Now), null));
        Assert.Equal(AlbumClassicalPlan.Outbound.Send, AlbumSettingsPlan.Out(Here(null, Now), Stored(null, null, settingsAt: 0)));
    }

    [Fact]
    public void An_unchanged_record_the_store_answered_otherwise_is_left_for_the_pull()
        => Assert.Equal(AlbumClassicalPlan.Outbound.Leave, AlbumSettingsPlan.Out(Here(null, Now), Stored(true, null)));

    [Fact]
    public void A_record_nobody_set_anything_on_sends_nothing()
        => Assert.Equal(AlbumClassicalPlan.Outbound.Leave, AlbumSettingsPlan.Out(Here(null, null), null));
}
