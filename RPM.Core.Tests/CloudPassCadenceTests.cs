using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// How often the background passes that are not uploads may run. The queue is started on every
/// library change, so running them on every run would mean one request per keystroke of a rename.
/// Running them only after an upload would mean a machine that sends nothing never runs them.
/// </summary>
public class CloudPassCadenceTests
{
    private static readonly DateTime Now = new(2026, 8, 9, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_once_a_session_pass_runs_the_first_time_it_is_asked()
        => Assert.True(CloudPassCadence.DueOncePerSession(uploadedSomething: false, alreadyDoneThisSession: false));

    [Fact]
    public void And_not_again_on_a_run_that_sent_nothing()
        => Assert.False(CloudPassCadence.DueOncePerSession(uploadedSomething: false, alreadyDoneThisSession: true));

    /// <summary>A run that sent something runs the pass again: new music is when the playlists that
    /// name it and the covers next to it must be written again.</summary>
    [Fact]
    public void A_run_that_sent_something_does_it_again()
        => Assert.True(CloudPassCadence.DueOncePerSession(uploadedSomething: true, alreadyDoneThisSession: true));

    [Fact]
    public void The_catalog_is_reconciled_on_its_own_clock()
    {
        var justNow = Now - TimeSpan.FromSeconds(30);
        var aWhileAgo = Now - CloudPassCadence.CatalogInterval - TimeSpan.FromSeconds(1);

        Assert.False(CloudPassCadence.DueOnTheClock(uploadedSomething: false, justNow, Now));
        Assert.True(CloudPassCadence.DueOnTheClock(uploadedSomething: false, aWhileAgo, Now));
    }

    /// <summary>
    /// A metadata edit produces no upload at all (the canonical audio does not change), so a run
    /// started by retagging an album would otherwise find nothing to do and the correction would wait
    /// for a restart.
    /// </summary>
    [Fact]
    public void A_run_that_uploaded_reconciles_the_catalog_whatever_the_clock_says()
        => Assert.True(CloudPassCadence.DueOnTheClock(uploadedSomething: true, Now, Now));

    /// <summary>A rename typed one letter at a time is a library change per keystroke, and each one
    /// starts the queue.</summary>
    [Fact]
    public void A_burst_of_library_changes_is_not_a_catalog_listing_each()
    {
        var lastPass = Now;
        var due = 0;

        for (var keystroke = 0; keystroke < 20; keystroke++)
            if (CloudPassCadence.DueOnTheClock(false, lastPass, Now + TimeSpan.FromMilliseconds(200 * keystroke)))
                due++;

        Assert.Equal(0, due);
    }

    [Fact]
    public void The_mirror_reads_the_catalog_when_asked_however_recently_it_read_it()
        => Assert.True(CloudPassCadence.MirrorPassDue(force: true, lastPassUtc: Now, nowUtc: Now));

    [Fact]
    public void And_otherwise_holds_the_answer_it_has()
        => Assert.False(CloudPassCadence.MirrorPassDue(false, Now - TimeSpan.FromMinutes(1), Now));

    [Fact]
    public void The_floor_under_an_edit_made_on_the_other_machine_is_the_mirror_interval()
        => Assert.True(CloudPassCadence.MirrorPassDue(false, Now - CloudPassCadence.MirrorInterval, Now));

    /// <summary>A library that has never been mirrored has a last pass of <see cref="DateTime.MinValue"/>,
    /// which must read as "long overdue" rather than overflowing into the future.</summary>
    [Fact]
    public void A_library_that_has_never_been_read_is_due_at_once()
        => Assert.True(CloudPassCadence.MirrorPassDue(false, DateTime.MinValue, Now));

    /// <summary>The catalogue pass runs when the sync engine starts a run; the mirror runs on a timer.
    /// The mirror is the slower of the two on purpose.</summary>
    [Fact]
    public void The_mirrors_own_clock_is_the_slower_one()
        => Assert.True(CloudPassCadence.MirrorInterval > CloudPassCadence.CatalogInterval);

    /// <summary>The timer ticks far more often than the interval, so a read is never more than one
    /// tick later than due. If the two were equal, a startup read that lands after the timer starts
    /// would push the next read to twice the interval.</summary>
    [Fact]
    public void A_read_that_landed_after_the_timer_started_is_not_skipped_for_a_whole_interval()
    {
        var timerStarted = Now;
        var startupRead = timerStarted + TimeSpan.FromSeconds(31);

        var tick = timerStarted;
        do tick += CloudPassCadence.MirrorTick;
        while (!CloudPassCadence.MirrorPassDue(false, startupRead, tick));

        Assert.True(tick - startupRead < CloudPassCadence.MirrorInterval + CloudPassCadence.MirrorTick);
    }
}
