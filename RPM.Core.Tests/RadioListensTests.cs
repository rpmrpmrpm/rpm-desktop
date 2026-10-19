using RPM.Core.Radio;

namespace RPM.Core.Tests;

/// <summary>
/// The station listening log, and the tally that feeds it.
///
/// <para>This is what the home screen's radio card is drawn from. Nothing else in the app keeps this
/// data. If a rule quietly stopped holding (a listen dropped, a total that went backwards, a log that
/// grew without limit), it would look like a card that shows the wrong station.</para>
/// </summary>
public class RadioListensTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "rpm-radio-listens-" + Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_dir, "radio-listens.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static RadioStation Station(string uuid, string name) => new()
    {
        Uuid = uuid,
        Name = name,
        StreamUrl = "https://example.invalid/" + uuid,
    };

    [Fact]
    public void The_station_heard_most_comes_first()
    {
        var log = new RadioListens(Path_);
        log.Note(Station("a", "A"), 600);
        log.Note(Station("b", "B"), 4000);
        log.Note(Station("c", "C"), 60);

        Assert.Equal(["b", "a", "c"], log.Top(3).Select(l => l.Station.Uuid));
    }

    /// <summary>Two listens of one station are one total, not two rows. "Most listened" is measured in
    /// hours, so a station left on all morning must beat one that was tuned in ten times and
    /// abandoned.</summary>
    [Fact]
    public void Listens_of_one_station_add_up()
    {
        var log = new RadioListens(Path_);
        log.Note(Station("a", "A"), 600);
        log.Note(Station("a", "A"), 900);

        var top = Assert.Single(log.Top(5));
        Assert.Equal(1500, top.HeardSeconds);
        Assert.Equal(2, top.Tunes);
    }

    /// <summary>Turning the dial through six stations is not six listens.</summary>
    [Fact]
    public void A_listen_too_short_to_notice_is_not_written_down()
    {
        var log = new RadioListens(Path_);

        Assert.False(log.Note(Station("a", "A"), RadioListens.NoticedSeconds - 1));
        Assert.Empty(log.Top(5));

        Assert.True(log.Note(Station("a", "A"), RadioListens.NoticedSeconds));
        Assert.Single(log.Top(5));
    }

    /// <summary>A station that was renamed or moved keeps its hours and gets its current details: the
    /// name, the stream and above all the logo that the card is made of.</summary>
    [Fact]
    public void A_renamed_station_keeps_its_hours()
    {
        var log = new RadioListens(Path_);
        log.Note(Station("a", "Old name"), 600);
        log.Note(Station("a", "New name"), 600);

        var top = Assert.Single(log.Top(5));
        Assert.Equal("New name", top.Station.Name);
        Assert.Equal(1200, top.HeardSeconds);
    }

    /// <summary>A listener who tries a hundred stations does not get a hundred rows. What is dropped
    /// from the end is what was heard least.</summary>
    [Fact]
    public void The_log_is_bounded_and_drops_the_least_heard()
    {
        var log = new RadioListens(Path_);

        for (var i = 0; i < RadioListens.Kept + 10; i++)
            log.Note(Station("s" + i, "S" + i), 60 + i);

        var top = log.Top(RadioListens.Kept + 50);
        Assert.Equal(RadioListens.Kept, top.Count);

        // The ten smallest are the ten that went in first.
        Assert.DoesNotContain("s0", top.Select(l => l.Station.Uuid));
        Assert.Contains("s" + (RadioListens.Kept + 9), top.Select(l => l.Station.Uuid));
    }

    [Fact]
    public void What_was_written_is_there_on_the_next_run()
    {
        new RadioListens(Path_).Note(Station("a", "A"), 600);

        var reopened = new RadioListens(Path_);
        Assert.Equal(600, Assert.Single(reopened.Top(5)).HeardSeconds);
    }

    /// <summary>A corrupt log is not a reason to refuse to start. It is rewritten whole on the next
    /// listen.</summary>
    [Fact]
    public void A_corrupt_log_reads_as_an_empty_one()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, "{ not json");

        var log = new RadioListens(Path_);
        Assert.Empty(log.Top(5));

        log.Note(Station("a", "A"), 600);
        Assert.Single(log.Top(5));
    }

    [Fact]
    public void Nothing_is_asked_for_nothing()
        => Assert.Empty(new RadioListens(Path_).Top(0));

    /// <summary>The account was deleted: what was sent to it is forgotten, so a later account is sent
    /// everything again. The listening stays.</summary>
    [Fact]
    public void Forgetting_the_account_keeps_the_listening_and_sends_it_all_again()
    {
        var log = new RadioListens(Path_);
        log.Note(Station("a", "A"), 600);
        log.NoteSent(log.Unsent(10));
        log.NoteCursor(42);
        Assert.Empty(log.Unsent(10));

        log.ForgetAccount();

        Assert.Single(log.Unsent(10));
        Assert.Equal(0, log.Cursor);
        Assert.Equal(600, log.Top(1)[0].HeardSeconds);

        var again = new RadioListens(Path_);
        Assert.Single(again.Unsent(10));
        Assert.Equal(0, again.Cursor);
    }
}

/// <summary>
/// The log between two devices: this log's own rows and other devices' rows are kept apart, a
/// station's total is their sum, and what was sent is recorded as it was sent. A listen noted while
/// rows are being sent must still be due afterwards.
/// </summary>
public class RadioListensSyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "rpm-radio-sync-" + Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_dir, "radio-listens.json");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static RadioStation Station(string uuid, string name = "S") => new()
    {
        Uuid = uuid,
        Name = name,
        StreamUrl = "https://example.invalid/" + uuid,
    };

    private static ForeignRadioListen From(string device, string uuid, double heard, int tunes = 1,
                                           long at = 1_700_000_000, string name = "S")
        => new(device, new RadioListenRow(Station(uuid, name), heard, tunes, at));

    [Fact]
    public void A_new_log_names_itself_and_keeps_the_name()
    {
        var log = new RadioListens(Path_);
        var name = log.Device;

        Assert.False(string.IsNullOrEmpty(name));
        Assert.Equal(name, new RadioListens(Path_).Device);
    }

    /// <summary>
    /// The listing includes the cursor's second, so the newest row comes back on every pass. If it
    /// were taken in again every time, it would log "1 radio listening rows arrived" on every pass,
    /// write the log to disk and redraw the list each time.
    /// </summary>
    [Fact]
    public void A_row_already_held_as_it_is_is_not_taken_again()
    {
        var log = new RadioListens(Path_);
        var changed = 0;
        log.Changed += () => changed++;

        Assert.Equal(1, log.Apply([From("phone", "a", 400, tunes: 2)]));
        Assert.Equal(0, log.Apply([From("phone", "a", 400, tunes: 2)]));
        Assert.Equal(1, changed);

        // A new total counts as a change.
        Assert.Equal(1, log.Apply([From("phone", "a", 460, tunes: 2)]));
        Assert.Equal(460, Assert.Single(log.Top(5)).HeardSeconds);
    }

    /// <summary>A station's total is this log's listening plus every other device's.</summary>
    [Fact]
    public void Another_devices_listening_adds_to_this_logs_own()
    {
        var log = new RadioListens(Path_);
        log.Note(Station("a"), 600);

        log.Apply([From("phone", "a", 400, tunes: 2)]);

        var top = Assert.Single(log.Top(5));
        Assert.Equal(1000, top.HeardSeconds);
        Assert.Equal(3, top.Tunes);
    }

    /// <summary>A row is that device's total: taken in twice, or taken in again with a larger total, it
    /// replaces itself rather than adding up.</summary>
    [Fact]
    public void A_devices_row_replaces_its_last_value()
    {
        var log = new RadioListens(Path_);

        log.Apply([From("phone", "a", 400)]);
        log.Apply([From("phone", "a", 400)]);
        Assert.Equal(400, Assert.Single(log.Top(5)).HeardSeconds);

        log.Apply([From("phone", "a", 900)]);
        Assert.Equal(900, Assert.Single(log.Top(5)).HeardSeconds);
    }

    /// <summary>This log's own row coming back from the store is already counted here.</summary>
    [Fact]
    public void This_logs_own_row_coming_back_is_left_out()
    {
        var log = new RadioListens(Path_);
        log.Note(Station("a"), 600);

        Assert.Equal(0, log.Apply([From(log.Device!, "a", 600)]));
        Assert.Equal(600, Assert.Single(log.Top(5)).HeardSeconds);
    }

    /// <summary>A station heard only on another device is on the list, with the station that device
    /// tuned, so it can be drawn and played here.</summary>
    [Fact]
    public void A_station_heard_only_elsewhere_can_be_played_here()
    {
        var log = new RadioListens(Path_);

        log.Apply([From("phone", "b", 900, name: "Only on the phone")]);

        var top = Assert.Single(log.Top(5));
        Assert.Equal("Only on the phone", top.Station.Name);
        Assert.Equal("https://example.invalid/b", top.Station.StreamUrl);
    }

    [Fact]
    public void Only_this_logs_own_listening_is_sent()
    {
        var log = new RadioListens(Path_);
        log.Note(Station("a"), 600);
        log.Apply([From("phone", "b", 900)]);

        var due = Assert.Single(log.Unsent(10));
        Assert.Equal("a", due.Station.Uuid);
        Assert.Equal(600, due.HeardSeconds);
    }

    /// <summary>What was sent is recorded as it was SENT: a listen noted between reading the rows and
    /// recording them is still unsent and goes next pass.</summary>
    [Fact]
    public void A_listen_noted_while_sending_is_still_due()
    {
        var log = new RadioListens(Path_);
        log.Note(Station("a"), 600);

        var due = log.Unsent(10);
        log.Note(Station("a"), 60);
        log.NoteSent(due);

        var still = Assert.Single(log.Unsent(10));
        Assert.Equal(660, still.HeardSeconds);

        log.NoteSent(log.Unsent(10));
        Assert.Empty(log.Unsent(10));
    }

    [Fact]
    public void What_was_sent_and_taken_in_is_there_on_the_next_run()
    {
        var log = new RadioListens(Path_);
        log.Note(Station("a"), 600);
        log.NoteSent(log.Unsent(10));
        log.Apply([From("phone", "b", 900)]);
        log.NoteCursor(1_700_000_123);

        var reopened = new RadioListens(Path_);
        Assert.Empty(reopened.Unsent(10));
        Assert.Equal(1_700_000_123, reopened.Cursor);
        Assert.Equal(["b", "a"], reopened.Top(5).Select(l => l.Station.Uuid));
    }

    /// <summary>A cursor only moves forward: an older stamp that arrives late does not ask again for
    /// rows that were already taken in.</summary>
    [Fact]
    public void The_cursor_does_not_go_back()
    {
        var log = new RadioListens(Path_);
        log.NoteCursor(200);
        log.NoteCursor(100);

        Assert.Equal(200, log.Cursor);
    }

    /// <summary>A log written before syncing existed reads as this log's own listening, and gets a
    /// name.</summary>
    [Fact]
    public void A_log_from_before_the_sync_is_this_logs_own()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path_, """
            {
              "Heard": [
                {
                  "Station": { "Uuid": "a", "Name": "A", "StreamUrl": "https://example.invalid/a" },
                  "HeardSeconds": 600,
                  "Tunes": 2,
                  "LastHeardUtcTicks": 638000000000000000
                }
              ]
            }
            """);

        var log = new RadioListens(Path_);

        Assert.False(string.IsNullOrEmpty(log.Device));
        Assert.Equal(600, Assert.Single(log.Unsent(10)).HeardSeconds);
    }

    /// <summary>A log whose name could not be written down does not sync: next session it would have a
    /// new name, and this session's rows would come back as another device's and count twice.</summary>
    [Fact]
    public void A_log_that_cannot_be_written_does_not_sync()
    {
        // A directory where the file should be: nothing can be written there.
        Directory.CreateDirectory(Path_);

        Assert.Null(new RadioListens(Path_).Device);
    }

    /// <summary>A log that is there and could not be read is not written this session, so it does not
    /// sync either.</summary>
    [Fact]
    public void A_log_that_could_not_be_read_does_not_sync()
    {
        if (OperatingSystem.IsWindows())
            return;

        var first = new RadioListens(Path_);
        first.Note(Station("a"), 600);
        File.SetUnixFileMode(Path_, UnixFileMode.None);

        try
        {
            Assert.Null(new RadioListens(Path_).Device);
        }
        finally
        {
            File.SetUnixFileMode(Path_, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }
}

/// <summary>
/// The sampler behind the log: what it counts as one listen, and what it refuses to lose.
/// </summary>
public class RadioTallyTests
{
    /// <summary>The reason this is not <c>ListenTally</c>: a stream that drops and comes back is one
    /// listen. Given to that type it would count as two, and on a station that drops every minute it
    /// would count as none, because each fragment is under the minimum.</summary>
    [Fact]
    public void A_reconnect_carries_the_same_listen_on()
    {
        var tally = new RadioTally();

        Assert.Null(tally.Sample("a", session: 1, heardSeconds: 100));

        // The stream reopened: a new session, and the counter below is back at zero.
        Assert.Null(tally.Sample("a", session: 2, heardSeconds: 0));
        Assert.Null(tally.Sample("a", session: 2, heardSeconds: 50));

        Assert.Equal(150, tally.HeardSeconds);
        Assert.Equal(new RadioTally.Listened("a", 150), tally.Close());
    }

    [Fact]
    public void Changing_station_ends_the_listen()
    {
        var tally = new RadioTally();
        tally.Sample("a", 1, 100);

        var finished = tally.Sample("b", 2, 0);

        Assert.Equal(new RadioTally.Listened("a", 100), finished);
        Assert.Equal("b", tally.Uuid);
        Assert.Equal(0, tally.HeardSeconds);
    }

    [Fact]
    public void Switching_off_ends_the_listen()
    {
        var tally = new RadioTally();
        tally.Sample("a", 1, 100);

        Assert.Equal(new RadioTally.Listened("a", 100), tally.Sample(null, 2, 0));
        Assert.Null(tally.Uuid);
    }

    /// <summary>A reading taken while the engine is between streams reports less than the last one.
    /// Taking it would throw away what this listen has already earned.</summary>
    [Fact]
    public void A_reading_never_goes_backwards_within_a_stretch()
    {
        var tally = new RadioTally();
        tally.Sample("a", 1, 100);
        tally.Sample("a", 1, 3);

        Assert.Equal(100, tally.HeardSeconds);
    }

    [Fact]
    public void Closing_twice_does_not_write_the_listen_twice()
    {
        var tally = new RadioTally();
        tally.Sample("a", 1, 100);

        Assert.NotNull(tally.Close());
        Assert.Null(tally.Close());
    }

    [Fact]
    public void Nothing_tuned_ends_nothing()
        => Assert.Null(new RadioTally().Sample(null, 0, 0));
}
