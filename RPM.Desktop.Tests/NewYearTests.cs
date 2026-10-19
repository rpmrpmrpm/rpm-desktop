using RPM.Desktop.Presentation.Pegasus;

namespace RPM.Desktop.Tests;

/// <summary>
/// The one moment rpm plays a record on its own, and the one record it plays.
///
/// <para>The tests give the clock as an input. A test that read the real clock could only run on
/// New Year's Eve. The rule takes the time as an argument rather than reading it, so it is a plain
/// rule and not a service.</para>
/// </summary>
public class NewYearTests
{
    private static DateTimeOffset At(int year, int month, int day, int hour, int minute, int second = 0)
        => new(year, month, day, hour, minute, second, TimeSpan.Zero);

    // ── the moment ──

    /// <summary>Midnight, and the minutes either side of it. A polled clock and a laptop waking from
    /// sleep need this margin. The window makes sure the midnight check cannot fall between two
    /// looks at the clock.</summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 0, 1)]
    [InlineData(0, 1, 30)]
    public void The_stroke_of_midnight_is_the_moment(int hour, int minute, int second)
        => Assert.True(NewYear.IsTheStroke(At(2027, 1, 1, hour, minute, second)));

    /// <summary>Other times on 1 January are not the moment. Someone who opens rpm at breakfast gets
    /// no midnight greeting.</summary>
    [Theory]
    [InlineData(0, 3)]
    [InlineData(0, 30)]
    [InlineData(9, 0)]
    [InlineData(23, 59)]
    public void The_rest_of_new_years_day_is_an_ordinary_day(int hour, int minute)
        => Assert.False(NewYear.IsTheStroke(At(2027, 1, 1, hour, minute)));

    /// <summary>Midnight on any other night of the year is not the moment. That includes 23:59 on
    /// 31 December, the near miss that matters most.</summary>
    [Theory]
    [InlineData(12, 31)]
    [InlineData(1, 2)]
    [InlineData(7, 1)]
    public void Midnight_on_any_other_night_is_just_midnight(int month, int day)
        => Assert.False(NewYear.IsTheStroke(At(2026, month, day, 0, 0, 30)));

    /// <summary>The edges of the window are tested on both sides, because the width of the window is
    /// the whole meaning of the constant.</summary>
    [Fact]
    public void The_window_ends_where_the_grace_does()
    {
        var midnight = At(2027, 1, 1, 0, 0);
        Assert.True(NewYear.IsTheStroke(midnight + NewYear.Grace - TimeSpan.FromSeconds(1)));
        Assert.False(NewYear.IsTheStroke(midnight + NewYear.Grace));
    }

    // ── the record ──

    /// <summary>The record, however it was tagged. Both title and artist must match. A title alone is
    /// a common phrase, and the artist alone would match every ABBA record.</summary>
    [Theory]
    [InlineData("Happy New Year", "ABBA")]
    [InlineData("happy new year", "abba")]
    [InlineData("  Happy New Year  ", "ABBA")]
    [InlineData("Happy New Year", "ABBA feat. Frida")]
    public void The_record_is_that_song_by_that_band(string title, string artist)
        => Assert.True(NewYear.IsTheRecord(title, artist));

    /// <summary>Another band's song of the same name is another band's song, and ABBA's other records are
    /// not this one. Neither is a title that merely begins with it.</summary>
    [Theory]
    [InlineData("Happy New Year", "Judy Garland")]
    [InlineData("Dancing Queen", "ABBA")]
    [InlineData("Happy New Year's Day", "ABBA")]
    [InlineData("Happy New Year (Live)", "ABBA")]
    [InlineData(null, "ABBA")]
    [InlineData("Happy New Year", null)]
    [InlineData(null, null)]
    public void Anything_else_is_an_ordinary_record(string? title, string? artist)
        => Assert.False(NewYear.IsTheRecord(title, artist));

    // ── the gate ──

    /// <summary>The greeting is due at midnight and is given once. A clock checked about every twenty
    /// seconds passes through the window several times, so the gate must stop repeats.</summary>
    [Fact]
    public void The_year_is_seen_in_exactly_once()
    {
        var gate = new NewYearGate();
        var midnight = At(2027, 1, 1, 0, 0);

        Assert.True(gate.Due(midnight));

        gate.Celebrated(midnight);

        Assert.False(gate.Due(midnight));
        Assert.False(gate.Due(midnight + TimeSpan.FromSeconds(20)));
        Assert.False(gate.Due(midnight + TimeSpan.FromSeconds(100)));
    }

    /// <summary>A year seen in does not settle the next one. The gate remembers a year, not the fact of
    /// having ever fired.</summary>
    [Fact]
    public void Next_year_is_owed_its_own_greeting()
    {
        var gate = new NewYearGate();

        gate.Celebrated(At(2027, 1, 1, 0, 0));

        Assert.True(gate.Due(At(2028, 1, 1, 0, 0)));
    }

    /// <summary>Nothing is due on an ordinary day, before or after a year has been settled. The gate
    /// is the moment rule plus a record of the last year that was settled.</summary>
    [Fact]
    public void An_ordinary_day_is_never_due()
    {
        var gate = new NewYearGate();

        Assert.False(gate.Due(At(2027, 6, 14, 0, 0)));
        Assert.False(gate.Due(At(2027, 1, 1, 9, 0)));
        Assert.False(gate.Due(At(2026, 12, 31, 23, 59, 59)));
    }

    /// <summary>The two calls are separate on purpose. The caller asks whether the year is due, looks
    /// for the record, and settles the year only after it has found one. A library still loading at
    /// midnight is checked again on the next poll, so it does not lose the New Year greeting.</summary>
    [Fact]
    public void A_year_that_was_never_answered_stays_owed()
    {
        var gate = new NewYearGate();
        var midnight = At(2027, 1, 1, 0, 0);

        Assert.True(gate.Due(midnight));                              // asked…
        Assert.True(gate.Due(midnight + TimeSpan.FromSeconds(20)));   // …no record yet, so still owed
        Assert.True(gate.Due(midnight + TimeSpan.FromSeconds(40)));

        gate.Celebrated(midnight + TimeSpan.FromSeconds(40));

        Assert.False(gate.Due(midnight + TimeSpan.FromSeconds(60)));
    }
}
