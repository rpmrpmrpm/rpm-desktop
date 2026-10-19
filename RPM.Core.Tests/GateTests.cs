using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The database's lock, and the information it reports that a plain <c>object</c> lock cannot.
///
/// <para>Two halves, and they fail differently. The first is that it is still a lock: mutual
/// exclusion, reentrancy, and release at the end of a <c>using</c>. If any of those is wrong, about
/// two hundred call sites are wrong with it, silently, in a way a stall report would never show. The
/// second is the reason it exists: a hold long enough to notice must be reported with a NAME, and a
/// thread that waited behind one must be reported with the name it waited behind. A stall report
/// that only says <c>shelf write 100% of 45</c> does not say where to look.</para>
/// </summary>
[Collection(ScopeCollection.Name)]
public class GateTests
{
    /// <summary>Long enough to pass the hundred-millisecond threshold for a note, with a margin for a
    /// busy machine.</summary>
    private static readonly TimeSpan Noticeable = TimeSpan.FromMilliseconds(180);

    /// <summary>A gate no other test can write notes into, so tests running in parallel cannot add
    /// notes to this one.</summary>
    private static Gate Fresh() => new("t" + Guid.NewGuid().ToString("N")[..8]);

    private static List<Gate.Note> NotesFrom(Gate gate) =>
        Gate.Drain().Where(n => n.Gate == gate.Name).ToList();

    // ── it is still a lock ────────────────────────────────────────────────────

    [Fact]
    public void Only_one_thread_is_inside_at_a_time()
    {
        var gate = Fresh();
        var inside = 0;
        var everSawTwo = false;

        Parallel.For(0, 200, _ =>
        {
            using (gate.Take())
            {
                if (Interlocked.Increment(ref inside) != 1)
                    everSawTwo = true;
                Thread.SpinWait(200);
                Interlocked.Decrement(ref inside);
            }
        });

        Assert.False(everSawTwo, "two threads were inside the gate at once");
        Assert.Equal(0, inside);
    }

    [Fact]
    public void It_can_be_taken_again_by_the_thread_that_holds_it()
    {
        // Reentrancy, as with `lock`, which the calling code relies on: a public method takes the gate
        // and calls another that takes it again. A deadlock here would hang the app, not fail a test,
        // so it has its own timeout.
        var gate = Fresh();
        var got = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            using (gate.Take())
            using (gate.Take())
            using (gate.Take())
                got.Set();
        }) { IsBackground = true };
        thread.Start();

        Assert.True(got.Wait(TimeSpan.FromSeconds(5)), "taking the gate a second time deadlocked");
        thread.Join(TimeSpan.FromSeconds(5));
        Assert.False(gate.Now.Busy, "the gate was still held after the outermost using ended");
    }

    [Fact]
    public void The_gate_is_free_again_when_the_using_ends()
    {
        var gate = Fresh();
        using (gate.Take())
            Assert.True(gate.Now.Busy);

        Assert.False(gate.Now.Busy);
    }

    // ── and it says who ───────────────────────────────────────────────────────

    [Fact]
    public void While_it_is_held_it_names_the_member_holding_it()
    {
        var gate = Fresh();

        using (gate.Take())
        {
            var look = gate.Now;
            Assert.True(look.Busy);
            Assert.Equal(nameof(While_it_is_held_it_names_the_member_holding_it), look.By);
        }

        Assert.Equal(default, gate.Now);
    }

    [Fact]
    public void The_outermost_name_is_the_one_kept()
    {
        // Who STARTED the work, not which inner call was running at the moment it was checked.
        var gate = Fresh();

        using (gate.Take())
        {
            Inner();
            Assert.Equal(nameof(The_outermost_name_is_the_one_kept), gate.Now.By);
        }

        void Inner()
        {
            using (gate.Take())
                Assert.Equal(nameof(The_outermost_name_is_the_one_kept), gate.Now.By);
        }
    }

    [Fact]
    public void A_hold_worth_noticing_is_written_down_with_its_name()
    {
        var gate = Fresh();
        Gate.Drain();

        using (gate.Take())
            Thread.Sleep(Noticeable);

        var notes = NotesFrom(gate);
        var held = Assert.Single(notes, n => n.What == "held");
        Assert.Equal(nameof(A_hold_worth_noticing_is_written_down_with_its_name), held.Who);
        Assert.True(held.Ms >= 150, $"the hold was timed at {held.Ms:0}ms");
    }

    [Fact]
    public void A_hold_nobody_would_feel_is_not_written_down()
    {
        // The other half of the same property. A gate taken per row would fill the report with notes,
        // and a report nobody can read is the same as no report.
        var gate = Fresh();
        Gate.Drain();

        for (int i = 0; i < 500; i++)
            using (gate.Take()) { }

        Assert.Empty(NotesFrom(gate));
    }

    [Fact]
    public void A_thread_that_waited_records_what_it_waited_behind()
    {
        // The useful sentence. "The write gate was busy" is one bit of information; "the wall waited
        // behind MirrorPeerCatalog" says where to look.
        var gate = Fresh();
        Gate.Drain();

        var holding = new ManualResetEventSlim(false);
        var holder = new Thread(() => Hog(gate, holding)) { IsBackground = true };
        holder.Start();

        Assert.True(holding.Wait(TimeSpan.FromSeconds(5)));
        using (gate.Take())  // waits behind Hog, and gets in when Hog releases
        {
        }

        holder.Join(TimeSpan.FromSeconds(5));

        var waited = Assert.Single(NotesFrom(gate), n => n.What == "waited");
        Assert.Equal(nameof(A_thread_that_waited_records_what_it_waited_behind), waited.Who);
        Assert.Equal(nameof(Hog), waited.Behind);
    }

    private static void Hog(Gate gate, ManualResetEventSlim holding)
    {
        using (gate.Take())
        {
            holding.Set();
            Thread.Sleep(Noticeable);
        }
    }

    /// <summary>
    /// <b>Nothing this class does to describe a hold may happen inside one.</b>
    ///
    /// <para>Writing the note about a wait as soon as the waiter gets in means writing it while holding
    /// the gate that every read in the library uses. That is a stack walk and two calls into the debug
    /// channel, and each of them stops the runtime under a debugger. A wait over the threshold would
    /// then make the hold longer, the longer hold would push the next waiter over the threshold, and
    /// the diagnostic would become a feedback loop that stops the app from recovering. So the note is
    /// collected on entry and written on release; this test checks that.</para>
    /// </summary>
    [Fact]
    public void A_wait_is_written_down_only_after_the_gate_is_let_go()
    {
        var gate = Fresh();
        Gate.Drain();

        var holding = new ManualResetEventSlim(false);
        var holder = new Thread(() => Hog(gate, holding)) { IsBackground = true };
        holder.Start();
        Assert.True(holding.Wait(TimeSpan.FromSeconds(5)));

        List<Gate.Note> inside;
        using (gate.Take())
            inside = NotesFrom(gate);

        holder.Join(TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(inside, n => n.What == "waited");
        Assert.Contains(NotesFrom(gate), n => n.What == "waited");
    }

    [Fact]
    public void While_somebody_waits_the_look_says_how_many()
    {
        var gate = Fresh();
        var holding = new ManualResetEventSlim(false);
        var queued = new CountdownEvent(3);
        var go = new ManualResetEventSlim(false);

        var holder = new Thread(() =>
        {
            using (gate.Take())
            {
                holding.Set();
                go.Wait(TimeSpan.FromSeconds(5));
            }
        }) { IsBackground = true };
        holder.Start();
        Assert.True(holding.Wait(TimeSpan.FromSeconds(5)));

        var waiters = Enumerable.Range(0, 3).Select(_ => new Thread(() =>
        {
            queued.Signal();
            using (gate.Take()) { }
        }) { IsBackground = true }).ToList();
        foreach (var w in waiters) w.Start();

        Assert.True(queued.Wait(TimeSpan.FromSeconds(5)));
        // The signal is sent just BEFORE the take, so wait a moment for them to actually queue.
        var seen = 0;
        for (int i = 0; i < 100 && seen < 3; i++)
        {
            seen = gate.Now.Waiting;
            if (seen < 3) Thread.Sleep(10);
        }

        Assert.Equal(3, seen);

        go.Set();
        foreach (var w in waiters) w.Join(TimeSpan.FromSeconds(5));
        holder.Join(TimeSpan.FromSeconds(5));
        Assert.Equal(0, gate.Now.Waiting);
    }

    // ── and it says whether the time was spent WORKING ────────────────────────

    [Fact]
    public void A_hold_that_was_working_says_it_burned_the_processor()
    {
        var gate = Fresh();
        Gate.Drain();

        using (gate.Take())
        {
            var until = DateTime.UtcNow + Noticeable;
            while (DateTime.UtcNow < until)
                Thread.SpinWait(1000);
        }

        var held = Assert.Single(NotesFrom(gate), n => n.What == "held");
        Assert.True(held.Cpu > held.Ms * 0.7,
            $"a hold that spun for {held.Ms:0}ms reported only {held.Cpu:0}ms of processor");
    }

    [Fact]
    public void A_hold_that_was_not_running_says_so_too()
    {
        // The important half. Sixteen seconds of wall time with twelve milliseconds of processor is not
        // a slow query: it is a thread that was suspended, and making the query faster would not help.
        // Nothing outside the thread can tell the two apart, so the note must report both.
        var gate = Fresh();
        Gate.Drain();

        using (gate.Take())
            Thread.Sleep(Noticeable);

        var held = Assert.Single(NotesFrom(gate), n => n.What == "held");
        Assert.True(held.Cpu < held.Ms * 0.2,
            $"a hold that slept for {held.Ms:0}ms reported {held.Cpu:0}ms of processor");
    }

    [Fact]
    public void A_note_is_one_line_however_much_it_has_to_say()
    {
        // A note on two lines can lose its second half (the one with the stack) when copied into a bug
        // report.
        var gate = Fresh();
        Gate.Drain();

        HangFor(gate, TimeSpan.FromMilliseconds(1100));

        var held = Assert.Single(NotesFrom(gate), n => n.What == "held");
        var line = held.ToString();
        Assert.DoesNotContain("\n", line);
        Assert.Contains("via ", line);
        Assert.Contains("cpu ", line);
    }

    // ── a hang comes back with the chain, not just the leaf ───────────────────

    [Fact]
    public void A_hold_long_enough_to_be_a_hang_carries_the_stack_it_happened_on()
    {
        // After a second the name of one member is not enough: SaveTrackMood says what was running,
        // not what called it. Slow on purpose: this is the only test that waits past the threshold it
        // checks.
        var gate = Fresh();
        Gate.Drain();

        HangFor(gate, TimeSpan.FromMilliseconds(1100));

        var held = Assert.Single(NotesFrom(gate), n => n.What == "held");
        Assert.Equal(nameof(HangFor), held.Who);
        Assert.NotNull(held.Where);
        Assert.Contains(nameof(GateTests), held.Where);
        Assert.Contains(nameof(A_hold_long_enough_to_be_a_hang_carries_the_stack_it_happened_on), held.Where);
        // And the gate's own frames are not in it: if the first two entries of a trace were the code
        // that wrote the trace, every hang report would start with two useless lines.
        Assert.DoesNotContain("Gate.Take", held.Where);
        Assert.DoesNotContain("Gate.Release", held.Where);
        Assert.StartsWith($"{nameof(GateTests)}.{nameof(HangFor)}", held.Where);
    }

    private static void HangFor(Gate gate, TimeSpan how)
    {
        using (gate.Take())
            Thread.Sleep(how);
    }

    [Fact]
    public void A_hold_that_is_merely_slow_does_not_pay_for_a_stack()
    {
        var gate = Fresh();
        Gate.Drain();

        using (gate.Take())
            Thread.Sleep(Noticeable);

        var held = Assert.Single(NotesFrom(gate), n => n.What == "held");
        Assert.Null(held.Where);
    }

    // ── and it reads as one line in a stall report ────────────────────────────

    [Fact]
    public void The_notes_are_taken_once_and_then_they_are_gone()
    {
        var gate = Fresh();
        Gate.Drain();

        using (gate.Take())
            Thread.Sleep(Noticeable);

        Assert.NotEmpty(NotesFrom(gate));
        Assert.Empty(NotesFrom(gate));
    }

    [Fact]
    public void The_stall_line_names_the_worst_hold()
    {
        var gate = Fresh();
        Gate.Drain();

        // The longest in the period, so it sorts to the front of the line whatever else the suite is
        // writing to the shared list at the same time.
        using (gate.Take())
            Thread.Sleep(TimeSpan.FromMilliseconds(400));

        var line = Gate.Line();
        Assert.StartsWith("db: ", line);
        Assert.Contains(gate.Name, line);
        Assert.Contains(nameof(The_stall_line_names_the_worst_hold), line);
    }

    [Fact]
    public void A_quiet_period_says_so_rather_than_saying_nothing()
    {
        Gate.Drain();
        Assert.Equal("db quiet", Gate.Line());
    }

    // ── and it can be shut ────────────────────────────────────────────────────
    //
    // The database's Dispose uses this. A background pass holds the library for the length of a pass
    // and returns to it after work that can take up to a minute, so closing the window happens in the
    // middle of one. The refusal must happen at the gate, before the caller reaches a connection that
    // is about to be disposed.

    [Fact]
    public void A_closed_gate_lets_nobody_in()
    {
        var gate = Fresh();

        Assert.True(gate.Close(Noticeable));
        Assert.True(gate.IsClosed);
        Assert.Throws<ObjectDisposedException>(() => gate.Take());
    }

    [Fact]
    public void An_idle_gate_says_it_was_idle_and_a_held_one_says_it_was_not()
    {
        var gate = Fresh();
        var inside = new ManualResetEventSlim();
        var mayLeave = new ManualResetEventSlim();

        var holder = Task.Run(() =>
        {
            using (gate.Take())
            {
                inside.Set();
                mayLeave.Wait();
            }
        });

        inside.Wait();

        // Closed while somebody holds it. It returns false, which tells the database to leave that
        // connection alone and not close it under a statement in progress.
        Assert.False(gate.Close(TimeSpan.FromMilliseconds(100)));
        Assert.True(gate.IsClosed);

        mayLeave.Set();
        holder.Wait();

        // And it stays closed after the holder releases.
        Assert.Throws<ObjectDisposedException>(() => gate.Take());
    }

    [Fact]
    public void A_thread_already_inside_a_closed_gate_can_still_take_it_again()
    {
        // The reentrant call is one piece of work: a public member takes the gate and calls another
        // that takes it again. Refusing the inner take would fail the outer one halfway through, which
        // a close must never do to a caller that got in before it.
        var gate = Fresh();
        var inside = new ManualResetEventSlim();
        var closed = new ManualResetEventSlim();
        Exception? refused = null;

        var holder = Task.Run(() =>
        {
            using (gate.Take())
            {
                inside.Set();
                closed.Wait();
                try
                {
                    using (gate.Take()) { }
                }
                catch (Exception ex)
                {
                    refused = ex;
                }
            }
        });

        inside.Wait();
        Assert.False(gate.Close(TimeSpan.FromMilliseconds(100)));
        closed.Set();
        holder.Wait();

        Assert.Null(refused);
    }
}
