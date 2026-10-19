using RPM.Core.Library;
using RPM.Core.Playback;

namespace RPM.Core.Tests;

/// <summary>
/// The listening log (schema v43): the one table that grows with use rather than with the collection.
/// What goes in it, what stays out of it, what happens to it when a file is re-filed, and, the part
/// worth being strict about, what bounds it.
/// </summary>
public class ListenLogTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    /// <summary>A fixed local clock, so "half past eleven at night" means the same thing in every run
    /// and on every machine the tests run on.</summary>
    private static readonly DateTimeOffset Evening =
        new(2026, 8, 20, 21, 30, 0, TimeSpan.FromHours(2));

    public ListenLogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmlisten-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private void Add(string relativePath, double seconds = 200)
    {
        var slash = relativePath.LastIndexOf('/');
        _db.Upsert(new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = slash < 0 ? "" : relativePath[..slash],
            FileName = slash < 0 ? relativePath : relativePath[(slash + 1)..],
            Extension = Path.GetExtension(relativePath),
            SizeBytes = 5_000_000,
            ModifiedUtcTicks = 1,
            DurationSeconds = seconds,
            Title = Path.GetFileNameWithoutExtension(relativePath),
        });
    }

    private static ListenEntry Heard(DateTimeOffset when, string path, double heard = 200, double length = 200)
        => ListenEntry.At(when, path, heard, length)
           ?? throw new InvalidOperationException("that listen was not worth recording");

    // ── what is worth writing down ──

    [Fact]
    public void A_flick_past_is_not_written_down_at_all()
    {
        Assert.Null(ListenEntry.At(Evening, "a/01.flac", heardSeconds: 2, lengthSeconds: 200));
    }

    [Fact]
    public void A_track_too_short_to_judge_is_not_written_down_either()
    {
        Assert.Null(ListenEntry.At(Evening, "a/01.flac", heardSeconds: 12, lengthSeconds: 12));
    }

    [Fact]
    public void A_listen_that_ended_early_is_written_down_as_a_skip()
    {
        var entry = Heard(Evening, "a/01.flac", heard: 20, length: 200);
        Assert.False(entry.Counted);
    }

    [Fact]
    public void Half_a_track_counts()
    {
        Assert.True(Heard(Evening, "a/01.flac", heard: 100, length: 200).Counted);
    }

    // ── the clock reading ──

    [Fact]
    public void The_hour_is_the_listeners_own_and_not_the_utc_one()
    {
        // 21:30 at +02:00 is 19:30 UTC. The log has to say nine, which is what the person saw.
        var entry = Heard(Evening, "a/01.flac");

        Assert.Equal(21, entry.LocalHour);
        Assert.Equal((int)DayOfWeek.Thursday, entry.LocalDayOfWeek);
        Assert.Equal(Evening.UtcTicks, entry.AtUtcTicks);
    }

    // ── the write ──

    [Fact]
    public void A_counted_listen_writes_the_row_and_moves_the_totals()
    {
        Add("a/01.flac");

        Assert.True(_db.NoteListen(Heard(Evening, "a/01.flac")));

        var track = _db.FindByRelativePath("a/01.flac")!;
        Assert.Equal(1, track.Plays);
        Assert.Equal(Evening.UtcTicks, track.LastPlayedUtcTicks);
        Assert.Equal(1, _db.ListenCount());
    }

    [Fact]
    public void A_skip_is_logged_and_moves_nothing()
    {
        Add("a/01.flac");

        Assert.True(_db.NoteListen(Heard(Evening, "a/01.flac", heard: 20, length: 200)));

        var track = _db.FindByRelativePath("a/01.flac")!;
        Assert.Equal(0, track.Plays);
        Assert.Equal(0, track.LastPlayedUtcTicks);
        Assert.Equal(1, _db.ListenCount());
    }

    [Fact]
    public void A_track_the_mirror_does_not_carry_writes_nothing_anywhere()
    {
        Assert.False(_db.NoteListen(Heard(Evening, "somebody/elses.flac")));
        Assert.Equal(0, _db.ListenCount());
    }

    [Fact]
    public void One_tracks_history_reads_back_newest_first()
    {
        Add("a/01.flac");
        _db.NoteListen(Heard(Evening, "a/01.flac"));
        _db.NoteListen(Heard(Evening.AddHours(1), "a/01.flac", heard: 20, length: 200));

        var history = _db.ListensOf("a/01.flac");

        Assert.Equal(2, history.Count);
        Assert.False(history[0].Counted);
        Assert.True(history[1].Counted);
    }

    // ── sessions ──

    [Fact]
    public void Listens_close_together_are_one_session()
    {
        Add("a/01.flac");
        Add("a/02.flac");
        _db.NoteListen(Heard(Evening, "a/01.flac"));
        _db.NoteListen(Heard(Evening.AddMinutes(4), "a/02.flac"));

        Assert.Equal(1, _db.ListenSessions());
    }

    [Fact]
    public void A_long_enough_gap_starts_another_session()
    {
        Add("a/01.flac");
        Add("a/02.flac");
        _db.NoteListen(Heard(Evening, "a/01.flac"));
        _db.NoteListen(Heard(Evening.AddMinutes(Listen.SessionGapMinutes + 1), "a/02.flac"));

        Assert.Equal(2, _db.ListenSessions());
    }

    /// <summary>A session is read off the table, so it survives the app being shut between two songs,
    /// which is the case a field on the service would get wrong every time.</summary>
    [Fact]
    public void Sessions_survive_the_library_being_reopened()
    {
        Add("a/01.flac");
        Add("a/02.flac");
        _db.NoteListen(Heard(Evening, "a/01.flac"));
        _db.Dispose();

        using var reopened = new LibraryDatabase(MirrorLocation.InFolder(_root));
        reopened.NoteListen(Heard(Evening.AddMinutes(2), "a/02.flac"));

        Assert.Equal(1, reopened.ListenSessions());
    }

    // ── a re-filed track keeps its history ──

    [Fact]
    public void Moving_a_row_takes_its_listening_with_it()
    {
        Add("Old/01.flac");
        _db.NoteListen(Heard(Evening, "Old/01.flac"));

        var moved = _db.FindByRelativePath("Old/01.flac")! with
        {
            RelativePath = "New/01.flac",
            Folder = "New",
        };
        _db.MoveMany([("Old/01.flac", moved)]);

        Assert.Empty(_db.ListensOf("Old/01.flac"));
        Assert.Single(_db.ListensOf("New/01.flac"));
    }

    // ── what bounds the table ──

    [Fact]
    public void Listens_older_than_the_horizon_are_trimmed()
    {
        Add("a/01.flac");
        var longAgo = Evening.AddDays(-(LibraryDatabase.ListenKeepDays + 10));
        _db.NoteListen(Heard(longAgo, "a/01.flac"));
        _db.NoteListen(Heard(Evening, "a/01.flac"));

        _db.PruneListens(Evening.UtcTicks);

        Assert.Equal(1, _db.ListenCount());
        Assert.Equal(Evening.UtcTicks, _db.ListensOf("a/01.flac")[0].AtUtcTicks);
    }

    [Fact]
    public void Trimming_leaves_everything_inside_the_horizon_alone()
    {
        Add("a/01.flac");
        _db.NoteListen(Heard(Evening.AddDays(-(LibraryDatabase.ListenKeepDays - 10)), "a/01.flac"));

        _db.PruneListens(Evening.UtcTicks);

        Assert.Equal(1, _db.ListenCount());
    }

    /// <summary>
    /// The one thing pruning must never do. A track can be absent for half a dozen ordinary reasons in
    /// this app, and history is the only thing in the mirror that cannot be rebuilt from the files.
    /// </summary>
    [Fact]
    public void Trimming_never_drops_a_listen_because_the_track_has_gone()
    {
        Add("a/01.flac");
        _db.NoteListen(Heard(Evening, "a/01.flac"));
        _db.Remove("a/01.flac");

        _db.PruneListens(Evening.UtcTicks);

        Assert.Equal(1, _db.ListenCount());
    }

    /// <summary>And the history is there when the music comes back.</summary>
    [Fact]
    public void A_track_that_comes_back_finds_its_listening_waiting()
    {
        Add("a/01.flac");
        _db.NoteListen(Heard(Evening, "a/01.flac"));
        _db.Remove("a/01.flac");
        _db.PruneListens(Evening.UtcTicks);

        Add("a/01.flac");

        Assert.Single(_db.ListensOf("a/01.flac"));
    }

    [Fact]
    public void The_log_trims_itself_without_being_asked()
    {
        Add("a/01.flac");
        var longAgo = Evening.AddDays(-(LibraryDatabase.ListenKeepDays + 10));
        _db.NoteListen(Heard(longAgo, "a/01.flac"));
        Assert.Equal(1, _db.ListenCount());

        // The next listen a day later is what notices, because the write is the thing that grows the
        // table and so is the thing that bounds it. No startup hook, nothing to remember to call.
        _db.NoteListen(Heard(Evening, "a/01.flac"));

        Assert.Equal(1, _db.ListenCount());
    }
}
