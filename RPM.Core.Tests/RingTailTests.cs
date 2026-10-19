using RPM.Core.Crashes;
using RPM.Core.Diagnostics;

namespace RPM.Core.Tests;

/// <summary>A finished run's ring, read by the next launch: did it begin closing, when did it last
/// write, and what did it say last.</summary>
public class RingTailTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ring-tail-{Guid.NewGuid():N}");
    private string RingPath => Path.Combine(_dir, "scope.77.ring");
    private string NamesPath => Path.Combine(_dir, "scope.77.names");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>Writes records as the app would. Nanos are the ring clock; the sync maps them to wall
    /// time.</summary>
    private void Write(int capacity, Action<ScopeRing, ScopeNames> fill)
    {
        using var names = ScopeNames.Fresh(NamesPath);
        using var ring = ScopeRing.Create(RingPath, capacity);
        fill(ring, names);
    }

    private static ScopeRecord Said(ScopeNames names, long nanos, string channel, string line) => new()
    {
        Kind = (ushort)ScopeKind.Said, Nanos = nanos, NameId = names.Id(channel), Detail = names.Id(line),
    };

    private static ScopeRecord Input(ScopeNames names, long nanos, string what, string? where = null) => new()
    {
        Kind = (ushort)ScopeKind.Input, Nanos = nanos, NameId = names.Id(what),
        Detail = where is null ? 0 : names.Id(where),
    };

    private static ScopeRecord Sync(long nanos, long unixNanos) => new()
    {
        Kind = (ushort)ScopeKind.ClockSync, Nanos = nanos, A = unixNanos,
    };

    [Fact]
    public void A_ring_with_a_closing_record_says_so()
    {
        Write(4096, (ring, names) =>
        {
            ring.Write(Said(names, 10, "boot", "app ctor"));
            ring.Write(new ScopeRecord { Kind = (ushort)ScopeKind.Closing, Nanos = 20 });
        });

        Assert.True(RingTail.Read(RingPath, NamesPath).Closing);
    }

    [Fact]
    public void A_ring_without_one_does_not()
    {
        Write(4096, (ring, names) => ring.Write(Said(names, 10, "boot", "app ctor")));

        Assert.False(RingTail.Read(RingPath, NamesPath).Closing);
    }

    /// <summary>The ring clock stops while the machine sleeps; the wall clock does not. Each record is
    /// placed against the last sync at or before it.</summary>
    [Fact]
    public void Wall_time_comes_from_the_last_clock_sync_before_each_record()
    {
        var wall = DateTimeOffset.Parse("2026-09-30T21:00:00Z").ToUnixTimeMilliseconds() * 1_000_000;
        var afterSleep = wall + TimeSpan.FromHours(3).Ticks * 100;

        Write(4096, (ring, names) =>
        {
            ring.Write(Sync(1_000, wall));
            ring.Write(Said(names, 2_000_001_000, "db", "before the sleep"));   // two seconds after the first sync
            ring.Write(Sync(3_000_000_000, afterSleep));
            ring.Write(Said(names, 3_500_000_000, "db", "after the sleep"));    // half a second after the second
        });

        var tail = RingTail.Read(RingPath, NamesPath);

        Assert.Equal(
            ["2026-09-30 21:00:02.000 said db: before the sleep", "2026-10-01 00:00:00.500 said db: after the sleep"],
            tail.Recent);
        Assert.Equal(afterSleep + 500_000_000, tail.LastUnixNanos);
    }

    [Fact]
    public void Only_the_last_lines_of_interest_are_kept_oldest_first()
    {
        Write(4096, (ring, names) =>
        {
            for (var i = 0; i < RingTail.RecentLines + 50; i++)
            {
                ring.Write(Said(names, 1_000 + i, "net", $"line {i}"));
                ring.Write(new ScopeRecord { Kind = (ushort)ScopeKind.FrameEnd, Nanos = 1_000 + i });
            }
        });

        var recent = RingTail.Read(RingPath, NamesPath).Recent;

        Assert.Equal(RingTail.RecentLines, recent.Count);
        Assert.EndsWith("said net: line 50", recent[0]);
        Assert.EndsWith($"said net: line {RingTail.RecentLines + 49}", recent[^1]);
    }

    [Fact]
    public void A_ring_that_wrapped_is_read_from_its_oldest_surviving_record()
    {
        Write(8, (ring, names) =>
        {
            for (var i = 0; i < 20; i++)
                ring.Write(Said(names, 1_000 + i, "net", $"line {i}"));
        });

        var recent = RingTail.Read(RingPath, NamesPath).Recent;

        Assert.Equal(8, recent.Count);
        Assert.EndsWith("line 12", recent[0]);
    }

    /// <summary>The ring records every key pressed in the main window, so a report that named them all
    /// would let text typed into a search box be read back. Keys that type a character are shown as
    /// "key"; modifiers and navigation keys keep their names, because a stuck modifier can be seen
    /// from them.</summary>
    [Fact]
    public void A_key_that_types_a_character_is_not_named_in_the_report()
    {
        Write(4096, (ring, names) =>
        {
            foreach (var key in new[] { "Shift", "H", "Number1", "NumberPad2", "190", "Decimal", "Escape", "Space", "NumberKeyLock" })
                ring.Write(Input(names, 10, key));
            ring.Write(Input(names, 20, "J", "up"));
            ring.Write(Input(names, 30, "tap", "Border/PlayButton"));
        });

        // Each line starts with a 23-character UTC time and a space.
        var recent = RingTail.Read(RingPath, NamesPath).Recent.Select(line => line[24..]);

        Assert.Equal(
            [
                "input Shift", "input key", "input key", "input key", "input key", "input key",
                "input Escape", "input Space", "input NumberKeyLock", "input key: up", "input tap: Border/PlayButton",
            ],
            recent);
    }

    [Fact]
    public void A_missing_name_table_leaves_the_words_out_but_keeps_the_lines()
    {
        Write(4096, (ring, names) => ring.Write(Said(names, 10, "boot", "app ctor")));
        File.Delete(NamesPath);

        var tail = RingTail.Read(RingPath, NamesPath);

        Assert.Single(tail.Recent);
        Assert.False(File.Exists(NamesPath));
    }
}
