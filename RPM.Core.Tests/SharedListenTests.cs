using RPM.Core.Library;
using RPM.Core.Playback;

namespace RPM.Core.Tests;

/// <summary>
/// The listening log travels between the listener's devices through the cloud, so the lists cut from it
/// (On repeat, Late night, First listens) count what was played anywhere. What this library sends, what it
/// takes in, and why taking a listen in never moves a play total.
/// </summary>
public class SharedListenTests : IDisposable
{
    private const string Mine = "this-computer";
    private const string Phone = "the-phone";

    private readonly string _root;
    private readonly LibraryDatabase _db;

    private static readonly DateTimeOffset Evening =
        new(2026, 8, 20, 21, 30, 0, TimeSpan.FromHours(2));

    public SharedListenTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmshared-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private void Add(string relativePath, string? rpmId)
    {
        var slash = relativePath.LastIndexOf('/');
        _db.Upsert(new LibraryTrack
        {
            RelativePath = relativePath,
            Folder = relativePath[..slash],
            FileName = relativePath[(slash + 1)..],
            Extension = ".flac",
            SizeBytes = 5_000_000,
            ModifiedUtcTicks = 1,
            DurationSeconds = 200,
            Title = relativePath,
            RpmId = rpmId,
        });
    }

    private static ListenEntry Heard(DateTimeOffset when, string path, double heard = 200)
        => ListenEntry.At(when, path, heard, 200) ?? throw new InvalidOperationException("not worth recording");

    private static SharedListen FromThePhone(string id, string rpmId, DateTimeOffset when, bool counted = true) =>
        new(id, Phone, rpmId, when.UtcTicks, counted ? 200 : 20, 200, counted, when.Hour, (int)when.DayOfWeek);

    // ── sending ──

    [Fact]
    public void A_listen_on_this_computer_is_offered_to_the_cloud_once()
    {
        Add("a/01.flac", "rpm-a1");
        _db.NoteListen(Heard(Evening, "a/01.flac"));

        var sent = _db.ListensToSend(Mine, 100);
        var listen = Assert.Single(sent);
        Assert.Equal("rpm-a1", listen.RpmId);
        Assert.Equal(Mine, listen.DeviceId);
        Assert.Equal(Evening.UtcTicks, listen.AtUtcTicks);
        Assert.Equal(21, listen.LocalHour);
        Assert.True(listen.Counted);
        Assert.False(string.IsNullOrEmpty(listen.ListenId));

        _db.NoteListensSent(sent);

        Assert.Empty(_db.ListensToSend(Mine, 100));
    }

    [Fact]
    public void A_skip_travels_too()
    {
        Add("a/01.flac", "rpm-a1");
        _db.NoteListen(Heard(Evening, "a/01.flac", heard: 20));

        Assert.False(Assert.Single(_db.ListensToSend(Mine, 100)).Counted);
    }

    /// <summary>A track with no cloud id has nothing the other devices could join a listen to. Its listens
    /// wait, and go when the track gets an id, so music uploaded later keeps its history.</summary>
    [Fact]
    public void A_listen_of_a_track_the_cloud_does_not_know_waits_until_it_does()
    {
        Add("a/01.flac", rpmId: null);
        _db.NoteListen(Heard(Evening, "a/01.flac"));

        Assert.Empty(_db.ListensToSend(Mine, 100));

        Add("a/01.flac", "rpm-a1");

        Assert.Equal("rpm-a1", Assert.Single(_db.ListensToSend(Mine, 100)).RpmId);
    }

    [Fact]
    public void Sending_stops_at_the_limit_and_the_rest_go_next_time()
    {
        Add("a/01.flac", "rpm-a1");
        for (int i = 0; i < 5; i++)
            _db.NoteListen(Heard(Evening.AddMinutes(i * 10), "a/01.flac"));

        var first = _db.ListensToSend(Mine, 3);
        Assert.Equal(3, first.Count);
        _db.NoteListensSent(first);

        Assert.Equal(2, _db.ListensToSend(Mine, 3).Count);
    }

    // ── taking in ──

    [Fact]
    public void A_listen_from_the_phone_joins_the_log_without_moving_the_play_totals()
    {
        Add("a/01.flac", "rpm-a1");

        Assert.Equal(1, _db.AcceptListens(Mine, [FromThePhone("p1", "rpm-a1", Evening)]));

        // The phone's play arrives as its own total through the play rows. Counting it here too would
        // count it twice.
        Assert.Equal(0, _db.FindByRelativePath("a/01.flac")!.Plays);
        var (at, _, counted) = Assert.Single(_db.ListensOf("a/01.flac"));
        Assert.Equal(Evening.UtcTicks, at);
        Assert.True(counted);
    }

    [Fact]
    public void The_same_listen_taken_in_twice_is_one_listen()
    {
        Add("a/01.flac", "rpm-a1");
        var listen = FromThePhone("p1", "rpm-a1", Evening);

        _db.AcceptListens(Mine, [listen]);
        Assert.Equal(0, _db.AcceptListens(Mine, [listen]));

        Assert.Equal(1, _db.ListenCount());
    }

    [Fact]
    public void This_computers_own_listens_coming_back_are_not_taken_in_again()
    {
        Add("a/01.flac", "rpm-a1");
        _db.NoteListen(Heard(Evening, "a/01.flac"));
        var echo = _db.ListensToSend(Mine, 100);

        Assert.Equal(0, _db.AcceptListens(Mine, echo));
        Assert.Equal(1, _db.ListenCount());
    }

    [Fact]
    public void A_listen_taken_in_is_never_sent_back()
    {
        Add("a/01.flac", "rpm-a1");
        _db.AcceptListens(Mine, [FromThePhone("p1", "rpm-a1", Evening)]);

        Assert.Empty(_db.ListensToSend(Mine, 100));
    }

    /// <summary>The cursor moves past a listen of music this computer does not hold yet. If the listen
    /// were dropped, it could not be asked for again, and the track would arrive with no history.</summary>
    [Fact]
    public void A_listen_of_music_not_here_yet_is_kept_and_joins_when_the_music_arrives()
    {
        _db.AcceptListens(Mine, [FromThePhone("p1", "rpm-later", Evening)]);

        Add("b/01.flac", "rpm-later");
        _db.AcceptListens(Mine, []);

        Assert.Single(_db.ListensOf("b/01.flac"));
    }

    /// <summary>The phone's listens count for the lists that read the log, as this computer's own do.</summary>
    [Fact]
    public void On_repeat_counts_what_was_played_on_the_phone()
    {
        Add("a/01.flac", "rpm-a1");
        var now = Evening.AddDays(1);
        _db.AcceptListens(Mine,
        [
            FromThePhone("p1", "rpm-a1", Evening),
            FromThePhone("p2", "rpm-a1", Evening.AddHours(1)),
            FromThePhone("p3", "rpm-a1", Evening.AddHours(2)),
        ]);

        var onRepeat = _db.QuerySmartPlaylist(
            SmartPlaylistId.WholeShelf(SmartPlaylistKind.OnRepeat), now.UtcTicks, advance: false);

        Assert.Equal("a/01.flac", Assert.Single(onRepeat).RelativePath);
    }

    /// <summary>A session is this computer's run of listening. The phone's listens, taken in later and out
    /// of order, must not split or join this computer's sessions.</summary>
    [Fact]
    public void The_phones_listens_do_not_change_this_computers_sessions()
    {
        Add("a/01.flac", "rpm-a1");
        Add("a/02.flac", "rpm-a2");
        _db.NoteListen(Heard(Evening, "a/01.flac"));
        _db.AcceptListens(Mine, [FromThePhone("p1", "rpm-a2", Evening.AddHours(3))]);
        _db.NoteListen(Heard(Evening.AddMinutes(4), "a/02.flac"));

        Assert.Equal(1, _db.ListenSessions());
    }

    [Fact]
    public void What_was_heard_lately_leaves_out_listens_of_music_not_here()
    {
        Add("a/01.flac", "rpm-a1");
        _db.NoteListen(Heard(Evening, "a/01.flac"));
        _db.AcceptListens(Mine, [FromThePhone("p1", "rpm-elsewhere", Evening.AddMinutes(10))]);

        Assert.Equal(["a/01.flac"], _db.RecentlyHeard(10));
    }
}
