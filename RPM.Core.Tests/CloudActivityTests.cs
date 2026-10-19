using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The cloud's log book: what it keeps, what it throws away, and how it says when.
/// </summary>
public class CloudActivityTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public CloudActivityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cloudlog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static long Now => new DateTime(2026, 7, 31, 12, 0, 0, DateTimeKind.Utc).Ticks;

    private void Add(string title, long at, CloudOp op = CloudOp.Upload,
        CloudOutcome outcome = CloudOutcome.Done, string? detail = null, string? fullError = null) =>
        _db.AddCloudActivity(op, title, "OK Computer", outcome, detail, fullError, 4_000_000, at);

    // ── what it keeps ──

    [Fact]
    public void An_entry_reads_back_as_it_was_written()
    {
        Add("Airbag", Now);

        var entry = Assert.Single(_db.QueryCloudActivity());
        Assert.Equal(CloudOp.Upload, entry.Op);
        Assert.Equal("Airbag", entry.Title);
        Assert.Equal("OK Computer", entry.Subtitle);
        Assert.Equal(CloudOutcome.Done, entry.Outcome);
        Assert.Equal(4_000_000, entry.Bytes);
        Assert.Equal(Now, entry.AtUtcTicks);
    }

    [Fact]
    public void A_failure_keeps_its_reason()
    {
        Add("Airbag", Now, outcome: CloudOutcome.Failed, detail: "Cloud storage is full.");

        var entry = Assert.Single(_db.QueryCloudActivity());
        Assert.Equal(CloudOutcome.Failed, entry.Outcome);
        Assert.Equal("Cloud storage is full.", entry.Detail);
    }

    /// <summary>The sentence is what the card shows; the full text is what a report sends.</summary>
    [Fact]
    public void A_failure_keeps_everything_that_was_said_beside_its_reason()
    {
        Add("Airbag", Now, outcome: CloudOutcome.Failed, detail: "Audio could not be converted",
            fullError: "FfmpegFailedException: ffmpeg could not canonicalize 'Airbag.flac'\n\nffmpeg:\n...");

        var entry = Assert.Single(_db.QueryCloudActivity());
        Assert.Equal("Audio could not be converted", entry.Detail);
        Assert.StartsWith("FfmpegFailedException:", entry.FullError);
    }

    [Fact]
    public void A_success_keeps_no_error()
    {
        Add("Airbag", Now);

        Assert.Null(Assert.Single(_db.QueryCloudActivity()).FullError);
    }

    /// <summary>
    /// The column is added on every open rather than under a version, as cloud_partial_error is: a
    /// log book from an older build must take a new row as soon as it is opened, and an older
    /// build must still be able to open the library afterwards.
    /// </summary>
    [Fact]
    public void A_log_book_from_before_the_full_error_gains_it_on_open()
    {
        var older = Path.Combine(_root, "older");
        Directory.CreateDirectory(older);
        using (var db = new LibraryDatabase(MirrorLocation.InFolder(older)))
            db.AddCloudActivity(CloudOp.Upload, "Lucky", null, CloudOutcome.Failed, "Upload failed", null, 0, Now);

        var file = Directory.GetFiles(older, "*.db", SearchOption.AllDirectories).First();
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={file}"))
        {
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "ALTER TABLE cloud_activity DROP COLUMN full_error";
            cmd.ExecuteNonQuery();
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using var reopened = new LibraryDatabase(MirrorLocation.InFolder(older));
        reopened.AddCloudActivity(CloudOp.Upload, "Airbag", null, CloudOutcome.Failed, "Upload failed", "the rest", 0, Now + 1);

        var log = reopened.QueryCloudActivity();
        Assert.Equal("the rest", log[0].FullError);
        Assert.Null(log[1].FullError);
    }

    [Fact]
    public void Newest_first()
    {
        Add("First", Now - TimeSpan.FromHours(2).Ticks);
        Add("Second", Now - TimeSpan.FromHours(1).Ticks);
        Add("Third", Now);

        Assert.Equal(["Third", "Second", "First"], _db.QueryCloudActivity().Select(e => e.Title));
    }

    [Fact]
    public void An_untouched_library_has_no_history()
    {
        Assert.Empty(_db.QueryCloudActivity());
    }

    [Fact]
    public void Clearing_empties_the_book()
    {
        Add("Airbag", Now);
        _db.ClearCloudActivity();

        Assert.Empty(_db.QueryCloudActivity());
    }

    // ── what it throws away ──

    [Fact]
    public void Anything_past_the_retention_window_is_dropped_on_the_next_write()
    {
        var old = Now - TimeSpan.FromDays(CloudActivityRetention.Days + 1).Ticks;
        Add("Ancient", old);
        Assert.Single(_db.QueryCloudActivity());   // still there: nothing has been written since

        Add("Fresh", Now);

        Assert.Equal(["Fresh"], _db.QueryCloudActivity().Select(e => e.Title));
    }

    [Fact]
    public void A_day_inside_the_window_survives()
    {
        Add("Recent", Now - TimeSpan.FromDays(CloudActivityRetention.Days - 1).Ticks);
        Add("Fresh", Now);

        Assert.Equal(2, _db.QueryCloudActivity().Count);
    }

    [Fact]
    public void The_row_cap_holds_even_when_everything_is_inside_the_window()
    {
        // The first sync of a big library: every row is minutes old, so the age prune removes
        // nothing and only the cap stops the mirror from holding a hundred thousand rows.
        for (var i = 0; i < CloudActivityRetention.MaxRows + 40; i++)
            Add($"Track {i}", Now + i);

        var rows = _db.QueryCloudActivity(limit: 10_000);
        Assert.Equal(CloudActivityRetention.MaxRows, rows.Count);
        // The ones dropped are the oldest, so the newest write is still at the top.
        Assert.Equal($"Track {CloudActivityRetention.MaxRows + 39}", rows[0].Title);
    }

    // ── how it says when ──

    [Theory]
    [InlineData(0, AgeUnit.JustNow, 0)]
    [InlineData(59, AgeUnit.JustNow, 0)]
    [InlineData(60, AgeUnit.Minutes, 1)]
    [InlineData(90, AgeUnit.Minutes, 1)]       // rounded down: 90s is "1 min ago", never "1.5"
    [InlineData(59 * 60, AgeUnit.Minutes, 59)]
    [InlineData(60 * 60, AgeUnit.Hours, 1)]
    [InlineData(23 * 3600, AgeUnit.Hours, 23)] // still today
    [InlineData(25 * 3600, AgeUnit.Days, 1)]   // and now it is a day
    [InlineData(30 * 24 * 3600, AgeUnit.Days, 30)]
    public void Age_is_stated_in_the_unit_worth_saying_it_in(int secondsAgo, AgeUnit unit, int value)
    {
        var age = CloudActivityAge.Of(Now - TimeSpan.FromSeconds(secondsAgo).Ticks, Now);

        Assert.Equal(unit, age.Unit);
        Assert.Equal(value, age.Value);
    }

    /// <summary>A clock correction, or a row written on another machine, must not read as a negative
    /// age: "in -3 minutes" is worse than saying nothing useful.</summary>
    [Fact]
    public void A_stamp_from_the_future_reads_as_just_now()
    {
        var age = CloudActivityAge.Of(Now + TimeSpan.FromHours(2).Ticks, Now);

        Assert.Equal(AgeUnit.JustNow, age.Unit);
    }

    [Fact]
    public void The_cutoff_is_a_month_back()
    {
        Assert.Equal(Now - TimeSpan.FromDays(30).Ticks, CloudActivityRetention.CutoffTicks(Now));
    }

    // ── which failures still stand ──

    private static readonly long Minute = TimeSpan.FromMinutes(1).Ticks;

    /// <summary>A track that failed soon after import and uploaded four minutes later must not stay
    /// listed under Errors.</summary>
    [Fact]
    public void A_failure_followed_by_a_success_is_not_outstanding()
    {
        Add("Airbag", Now - 4 * Minute, outcome: CloudOutcome.Failed, detail: "Upload failed");
        Add("Airbag", Now);

        Assert.Empty(CloudActivityFailures.Outstanding(_db.QueryCloudActivity()));
    }

    [Fact]
    public void A_failure_after_a_success_is_outstanding()
    {
        Add("Airbag", Now - 4 * Minute);
        Add("Airbag", Now, outcome: CloudOutcome.Failed, detail: "Upload failed");

        var entry = Assert.Single(CloudActivityFailures.Outstanding(_db.QueryCloudActivity()));
        Assert.Equal(Now, entry.AtUtcTicks);
    }

    [Fact]
    public void A_track_that_failed_several_times_is_listed_once_with_its_newest_reason()
    {
        Add("Airbag", Now - 2 * Minute, outcome: CloudOutcome.Failed, detail: "Upload failed");
        Add("Airbag", Now, outcome: CloudOutcome.Failed, detail: "Audio could not be converted");

        var entry = Assert.Single(CloudActivityFailures.Outstanding(_db.QueryCloudActivity()));
        Assert.Equal("Audio could not be converted", entry.Detail);
    }

    /// <summary>A restore that worked says nothing about an upload that did not.</summary>
    [Fact]
    public void A_success_of_another_operation_does_not_settle_a_failure()
    {
        Add("Airbag", Now - Minute, outcome: CloudOutcome.Failed, detail: "Upload failed");
        Add("Airbag", Now, op: CloudOp.Restore);

        Assert.Single(CloudActivityFailures.Outstanding(_db.QueryCloudActivity()));
    }

    [Fact]
    public void The_answer_does_not_depend_on_the_order_it_was_read_in()
    {
        Add("Airbag", Now - Minute, outcome: CloudOutcome.Failed, detail: "Upload failed");
        Add("Airbag", Now);

        Assert.Empty(CloudActivityFailures.Outstanding(_db.QueryCloudActivity().Reverse()));
    }
}
