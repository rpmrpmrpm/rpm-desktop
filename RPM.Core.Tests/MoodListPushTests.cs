using RPM.Core.Analysis;
using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// What the computer sends the phone, so that both show the same generated lists.
///
/// <para>The phone displays these but does not compute them: the calculation is percentiles over a
/// whole collection, and the phone does not have one. It knows the tracks in the cloud and nothing
/// about the records that exist only on this disk. These tests check that a cut is taken from one
/// reading of the shelf, that every list in it names records the phone can request, and that an empty
/// list is not sent as a row that promises music.</para>
/// </summary>
public class MoodListPushTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public MoodListPushTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmpush-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>A ramp of measured tracks, one per record, each with its own id: a collection whose
    /// quiet end and loud end are known because the numbers run from zero to one.
    ///
    /// <para>One record per track, not one folder of four hundred, because a mood list takes at most
    /// one song from any record (<see cref="SmartPlaylists.MostFromOneRecord"/>): four hundred tracks in
    /// one folder give a list of one, which is below every minimum.</para></summary>
    private void Ramp(int count, bool withIds = true)
    {
        for (var i = 0; i < count; i++)
        {
            Record(i, withIds ? Guid.NewGuid().ToString("D") : null);

            var at = (double)i / (count - 1);
            _db.SaveTrackMood($"rock/{i}/song.flac", 100, 1, new MoodReading(at, at, at));
        }
    }

    /// <summary>One record of the ramp, written or rewritten. The mood is saved against the path and
    /// is kept, so this can give a row an id it did not have.</summary>
    private void Record(int i, string? rpmId) =>
        _db.Upsert(new LibraryTrack
        {
            RelativePath = $"rock/{i}/song.flac",
            Folder = $"rock/{i}",
            FileName = "song.flac",
            Extension = ".flac",
            SizeBytes = 100,
            ModifiedUtcTicks = 1,
            DurationSeconds = 240,
            Genre = "Rock",
            Year = 1990,
            RpmId = rpmId,
        });

    /// <summary>Gives the first <paramref name="count"/> of the ramp's records an RPM id, which a
    /// track gets when it reaches the cloud.</summary>
    private void WithIds(int count)
    {
        for (var i = 0; i < count; i++)
            Record(i, Guid.NewGuid().ToString("D"));
    }

    private static long Now => DateTime.UtcNow.Ticks;

    [Fact]
    public void A_cut_names_its_lists_and_the_records_in_them()
    {
        Ramp(400);

        var cut = MoodListPush.Build(_db, SmartPlaylists.Moods, Now);

        Assert.Equal(MoodListPush.Recipe, cut.Recipe);
        Assert.NotEmpty(cut.Lists);

        var calm = cut.Lists.First(l => l.Kind == nameof(SmartPlaylistKind.Calm) && l.Genre is null);
        Assert.NotEmpty(calm.TrackIds);
        // The ids the phone requests the audio with, not paths on this disk.
        Assert.All(calm.TrackIds, id => Assert.True(Guid.TryParseExact(id, "D", out _)));
    }

    /// <summary>
    /// A list is sent as a RUN of several readings, together with the length of one list.
    ///
    /// <para>The phone cannot cut a list, so it is sent more than it shows, and it takes its own window
    /// when the user presses refresh there. Every record in the run is different (one song per record
    /// holds across the whole run), so each slice is a different list, not the same songs in another
    /// order.</para>
    /// </summary>
    [Fact]
    public void A_list_crosses_as_a_run_the_phone_can_window()
    {
        Ramp(400);

        var calm = MoodListPush.Build(_db, SmartPlaylists.Moods, Now).Lists
            .First(l => l.Kind == nameof(SmartPlaylistKind.Calm) && l.Genre is null);

        // The size of one list, sent explicitly so the phone does not have to guess.
        Assert.Equal(SmartPlaylists.MostSongs(SmartPlaylistKind.Calm), calm.Shows);

        // Longer than one list, and no longer than the number of readings requested.
        Assert.True(calm.TrackIds.Count > calm.Shows,
            $"a run of {calm.TrackIds.Count} is not deeper than the {calm.Shows} it shows");
        Assert.True(calm.TrackIds.Count <= calm.Shows * SmartPlaylists.ReadingsSent);

        // Every slice is a different list, which is only true if the run repeats nothing.
        Assert.Equal(calm.TrackIds.Count, calm.TrackIds.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The lists cut from the owner's listening go to the owner's phone and to nobody else.
    /// </summary>
    [Fact]
    public void Lists_cut_from_the_owners_listening_are_taken_out_for_others()
    {
        var cut = new MoodListsPayload(MoodListPush.Recipe,
        [
            new MoodListPayload(nameof(SmartPlaylistKind.RightNow), null, 0, ["a", "b"]),
            new MoodListPayload(nameof(SmartPlaylistKind.Calm), null, 0, ["c"]),
            new MoodListPayload(nameof(SmartPlaylistKind.Decade), null, 1980, ["d"]),
        ]);

        var shared = MoodListPush.ForOthers(cut);

        Assert.Equal([nameof(SmartPlaylistKind.Calm), nameof(SmartPlaylistKind.Decade)],
                     shared.Lists.Select(l => l.Kind));
    }

    /// <summary>
    /// The listening lists that home shows after Your mix go to the owner's phone and television,
    /// one list each, and to nobody else.
    /// </summary>
    [Fact]
    public void The_listening_lists_are_sent_to_the_owner_alone_and_one_list_each()
    {
        Assert.Equal(
            [SmartPlaylistKind.RightNow, SmartPlaylistKind.OnRepeat, SmartPlaylistKind.FavoriteTracks,
             SmartPlaylistKind.FirstListens, SmartPlaylistKind.Rediscover, SmartPlaylistKind.LovedAndForgotten,
             SmartPlaylistKind.LateNight],
            SmartPlaylists.OwnerOnly);

        foreach (var kind in SmartPlaylists.Listening)
        {
            Assert.Contains(kind, SmartPlaylists.SentOut);
            Assert.Equal(1, SmartPlaylists.ReadingsSentFor(kind));
        }

        // The moods keep their runs.
        Assert.True(SmartPlaylists.ReadingsSentFor(SmartPlaylistKind.Calm) > 1);

        var cut = new MoodListsPayload(MoodListPush.Recipe,
        [
            new MoodListPayload(nameof(SmartPlaylistKind.OnRepeat), null, 0, ["a"]),
            new MoodListPayload(nameof(SmartPlaylistKind.FavoriteTracks), null, 0, ["b"]),
            new MoodListPayload(nameof(SmartPlaylistKind.Calm), null, 0, ["c"]),
        ]);
        Assert.Equal([nameof(SmartPlaylistKind.Calm)], MoodListPush.ForOthers(cut).Lists.Select(l => l.Kind));
    }

    /// <summary>
    /// The cut sent from this machine includes the owner-only lists as well as the moods.
    /// </summary>
    [Fact]
    public void The_cut_sent_out_includes_the_owner_only_lists()
    {
        foreach (var kind in SmartPlaylists.OwnerOnly)
            Assert.Contains(kind, SmartPlaylists.SentOut);
        foreach (var kind in SmartPlaylists.Moods)
            Assert.Contains(kind, SmartPlaylists.SentOut);
    }

    /// <summary>
    /// A reader that cannot step through a run is given one list from it.
    ///
    /// <para>A borrowed shelf on another computer shows what it receives as one playlist, so if it
    /// received the run it would show a Chill of two hundred songs where the owner has fifty.</para>
    /// </summary>
    [Fact]
    public void A_reader_that_cannot_window_a_run_is_given_one_list_of_it()
    {
        Ramp(400);

        var cut = MoodListPush.Build(_db, SmartPlaylists.Moods, Now);
        var trimmed = MoodListPush.OneListEach(cut);

        Assert.Equal(cut.Lists.Count, trimmed.Lists.Count);

        for (var i = 0; i < cut.Lists.Count; i++)
        {
            var (run, one) = (cut.Lists[i], trimmed.Lists[i]);

            // The first list of the run, in the run's own order, and nothing else changed.
            Assert.Equal(Math.Min(run.Shows, run.TrackIds.Count), one.TrackIds.Count);
            Assert.Equal(run.TrackIds.Take(one.TrackIds.Count), one.TrackIds);
            Assert.Equal(run.Kind, one.Kind);
            Assert.Equal(run.Shows, one.Shows);
        }

        // At least one list was longer than one list and was really trimmed. Otherwise this test
        // would pass on a cut that had nothing to trim.
        Assert.Contains(cut.Lists, l => l.TrackIds.Count > l.Shows);
    }

    /// <summary>
    /// The first reading of a run is the list this computer shows: the same records, in the same
    /// order.
    ///
    /// <para><c>MoodFlow.Ordered</c> walks nearest-sounding-first and normalises its distances over
    /// whatever it is given. If a run were ordered as a whole, the first fifty of a run of two hundred
    /// would be fifty songs taken from all over it, in an order that belongs to the two hundred, and
    /// the phone would show different songs from the desktop. The picking still runs over the whole
    /// run, which makes each reading a different set of records; only the ordering is per
    /// reading.</para>
    ///
    /// <para><b>This is the end-to-end test, not the guard.</b> The shelf here is a ramp, so a song's
    /// sound rises with its rank and the flow ordering is nearly the ranked order already. This test
    /// therefore passes whether the run is ordered whole or one reading at a time. The ordering rule
    /// itself is checked by <c>MoodFlowTests.A_run_is_ordered_one_reading_at_a_time</c>, over a pool
    /// whose sound does not follow its rank. Both are needed: that one checks the rule, this one checks
    /// that the rule still holds after the cut, the ids and the wire.</para>
    /// </summary>
    [Fact]
    public void The_first_reading_of_a_run_is_the_list_the_desktop_shows()
    {
        Ramp(400);

        var id = SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm);

        // What this screen shows, read without changing it: the request must not advance the window
        // the run is taken from, or the two would be one press apart and disagree for that reason.
        var onScreen = _db.QuerySmartPlaylist(id, Now, advance: false)
                          .Select(t => t.RpmId)
                          .ToList();

        var crossed = MoodListPush.Build(_db, SmartPlaylists.Moods, Now).Lists
            .First(l => l.Kind == nameof(SmartPlaylistKind.Calm) && l.Genre is null);

        Assert.NotEmpty(onScreen);
        Assert.Equal(onScreen, crossed.TrackIds.Take(crossed.Shows));
    }

    /// <summary>
    /// No list in a cut names more records than the store accepts.
    ///
    /// <para>Not every kind shows fifty: Classical shows <see cref="SmartPlaylists.MaxTracks"/>, so a
    /// run of four readings would be eight hundred records, and the store accepts five hundred on a
    /// list. A cut is sent with one PUT, so one oversized list does not get dropped: the whole push
    /// is refused, and every generated list on the phone stays unchanged while the desktop continues
    /// to cut new ones. The desktop does not report it, because a failed push keeps the last good cut
    /// on purpose.</para>
    ///
    /// <para>Checked over every list in the cut, not over Classical alone: the rule is what must hold,
    /// so a kind whose limit is raised later is caught here and not at the store.</para>
    /// </summary>
    [Fact]
    public void No_list_crosses_deeper_than_the_store_takes()
    {
        Ramp(400);

        var cut = MoodListPush.Build(_db, SmartPlaylists.Moods, Now);
        Assert.NotEmpty(cut.Lists);

        foreach (var list in cut.Lists)
            Assert.True(
                list.TrackIds.Count <= SmartPlaylists.MostSentOnOneList,
                $"{list.Kind} {list.Genre} {list.Decade} names {list.TrackIds.Count} records, and the "
              + $"store takes {SmartPlaylists.MostSentOnOneList}");
    }

    /// <summary>A kind that shows more than a quarter of the store's limit is sent as fewer readings,
    /// not as a run nothing can accept. Two for Classical, which shows two hundred.</summary>
    [Fact]
    public void A_kind_that_shows_more_sends_fewer_readings()
    {
        Assert.Equal(4, SmartPlaylists.ReadingsSentFor(SmartPlaylistKind.Calm));
        Assert.Equal(2, SmartPlaylists.ReadingsSentFor(SmartPlaylistKind.Classical));

        // Whole readings only, and never zero: a slice is a whole list, or it is a short row on the phone.
        foreach (var kind in SmartPlaylists.Moods)
        {
            var readings = SmartPlaylists.ReadingsSentFor(kind);
            Assert.InRange(readings, 1, SmartPlaylists.ReadingsSent);
            Assert.True(SmartPlaylists.MostSongs(kind) * readings <= SmartPlaylists.MostSentOnOneList);
        }
    }

    [Fact]
    public void A_record_with_no_RPM_id_is_left_out_rather_than_sent_as_a_blank()
    {
        // The id is what the phone requests the audio with. A row without one names nothing it could
        // play, and an empty string in the list would be an entry the phone cannot even skip cleanly.
        Ramp(400, withIds: false);

        var cut = MoodListPush.Build(_db, SmartPlaylists.Moods, Now);

        Assert.Empty(cut.Lists);
    }

    [Fact]
    public void An_empty_list_is_not_sent()
    {
        // On the phone it would be a row that promises music and opens on nothing, and the phone cannot
        // tell it apart from a list whose records it does not have, which is a real state.
        Ramp(400);

        var cut = MoodListPush.Build(_db, SmartPlaylists.Moods, Now);

        Assert.All(cut.Lists, l => Assert.NotEmpty(l.TrackIds));
    }

    [Fact]
    public void A_collection_nobody_has_measured_sends_nothing()
    {
        // Not a cut of nothing, which would replace a good cut from an earlier launch.
        _db.Upsert(new LibraryTrack
        {
            RelativePath = "rock/1.flac", Folder = "rock", FileName = "1.flac", Extension = ".flac",
            SizeBytes = 100, ModifiedUtcTicks = 1, DurationSeconds = 240,
            RpmId = Guid.NewGuid().ToString("D"),
        });

        Assert.Empty(MoodListPush.Build(_db, SmartPlaylists.Moods, Now).Lists);
    }

    [Fact]
    public void Building_a_cut_does_not_step_the_window_the_listener_has_not_opened_yet()
    {
        // A mood list shows a window of its pool and advances that window on every OPENING, so a user
        // who asks again gets a different fifty. The push reads every list at launch; if it read them
        // as openings it would move the window past hundreds of songs before the listener opened
        // anything, and the first list they opened would start somewhere unexpected.
        //
        // So two builds of an unchanged shelf give the same cut. That is the whole test: it fails if
        // the push is passed advance: true.
        Ramp(400);

        var first = MoodListPush.Build(_db, SmartPlaylists.Moods, Now);
        var second = MoodListPush.Build(_db, SmartPlaylists.Moods, Now);

        Assert.Equal(first.Lists.Count, second.Lists.Count);
        for (var i = 0; i < first.Lists.Count; i++)
        {
            Assert.Equal(first.Lists[i].Kind, second.Lists[i].Kind);
            Assert.Equal(first.Lists[i].Genre, second.Lists[i].Genre);
            Assert.Equal(first.Lists[i].TrackIds, second.Lists[i].TrackIds);
        }
    }

    [Fact]
    public void The_kinds_offered_are_the_callers_to_state()
    {
        // No default: a push that silently offered everything would have contents nobody chose, and a
        // borrowed shelf offers none of the moods.
        Ramp(400);

        var one = MoodListPush.Build(_db, [SmartPlaylistKind.Calm], Now);

        Assert.All(one.Lists, l =>
            Assert.True(l.Kind == nameof(SmartPlaylistKind.Calm) || l.Decade > 0));
    }

    [Fact]
    public void A_list_the_computer_would_not_draw_is_not_sent()
    {
        // The pane shows a generated list only when it holds SmartPlaylists.LeastToShow songs, and the
        // phone shows what it receives. Without the same minimum here the phone would show rows the
        // computer hides: a mood of one record, with a name and one song.
        Ramp(400);

        var cut = MoodListPush.Build(_db, SmartPlaylists.Moods, Now);

        Assert.NotEmpty(cut.Lists);
        Assert.All(cut.Lists, l => Assert.True(
            l.TrackIds.Count >= SmartPlaylists.LeastToShow(IdOf(l)),
            $"{l.Kind}/{l.Genre}/{l.Decade} crossed with {l.TrackIds.Count}"));
    }

    [Fact]
    public void The_floor_counts_the_records_the_phone_can_ask_for()
    {
        // The minimum applies to what is SENT, not to what the computer holds: a shelf of four hundred
        // records with five of them in the cloud is five songs on the phone, whatever the row on this
        // screen says, and five is below every minimum.
        Ramp(400, withIds: false);
        WithIds(5);

        var cut = MoodListPush.Build(_db, SmartPlaylists.Moods, Now);

        Assert.Empty(cut.Lists);
    }

    [Fact]
    public void A_record_that_reaches_the_cloud_is_in_the_next_cut()
    {
        // The purpose of a re-cut. A track gets its RPM id when it is uploaded, and until the cut is
        // taken again the phone sees the collection as it was before that.
        Ramp(400, withIds: false);
        Assert.Empty(MoodListPush.Build(_db, SmartPlaylists.Moods, Now).Lists);

        WithIds(400);

        Assert.NotEmpty(MoodListPush.Build(_db, SmartPlaylists.Moods, Now).Lists);
    }

    [Fact]
    public void The_session_cut_stands_until_it_is_taken_again()
    {
        // One cut is one answer: the phone and a borrower read the same object, so nothing may rebuild
        // it without them knowing. A record reaching the cloud does not change what is published until
        // a new cut is requested.
        Ramp(400, withIds: false);
        WithIds(200);

        var first = _db.SessionMoodLists;
        WithIds(400);

        Assert.Same(first, _db.SessionMoodLists);
    }

    [Fact]
    public void A_re_cut_replaces_the_one_everybody_reads()
    {
        // So that the phone and a borrowed shelf change together. A re-cut kept separately from the
        // session's would be two cuts of one collection, which SessionMoodLists exists to prevent.
        Ramp(400, withIds: false);
        WithIds(200);

        var first = _db.SessionMoodLists;
        WithIds(400);

        var again = _db.RecutMoodLists();

        Assert.NotSame(first, again);
        Assert.Same(again, _db.SessionMoodLists);
        // The quiet half of the ramp was in the cloud and the loud half was not, so the second cut has
        // songs the first could not name.
        Assert.True(again.Lists.Sum(l => l.TrackIds.Count) > first.Lists.Sum(l => l.TrackIds.Count));
    }

    [Fact]
    public void Two_cuts_of_an_unchanged_shelf_say_the_same_thing()
    {
        // What the push checks before it sends anything. A record's own equality says "not equal"
        // here (the lists are a list on the payload, which compares by reference), so an unchanged
        // shelf would be sent again on every check.
        Ramp(400);

        Assert.True(MoodListPush.Same(
            MoodListPush.Build(_db, SmartPlaylists.Moods, Now),
            MoodListPush.Build(_db, SmartPlaylists.Moods, Now)));
    }

    [Fact]
    public void A_cut_naming_different_records_is_a_different_cut()
    {
        Ramp(400, withIds: false);
        WithIds(200);
        var before = MoodListPush.Build(_db, SmartPlaylists.Moods, Now);

        WithIds(400);

        Assert.False(MoodListPush.Same(before, MoodListPush.Build(_db, SmartPlaylists.Moods, Now)));
    }

    private static SmartPlaylistId IdOf(MoodListPayload list)
    {
        var kind = Enum.Parse<SmartPlaylistKind>(list.Kind);

        if (list.Decade > 0) return SmartPlaylistId.In(list.Decade);
        return list.Genre is { Length: > 0 } genre
            ? SmartPlaylistId.Within(kind, genre)
            : SmartPlaylistId.WholeShelf(kind);
    }
}
