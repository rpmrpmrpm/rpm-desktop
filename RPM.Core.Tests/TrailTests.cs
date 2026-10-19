using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using RPM.Core.Playback;

namespace RPM.Core.Tests;

/// <summary>
/// The record of what the app did, which is what answers "it just does not play". The two things
/// worth testing are that it cannot grow without bound on a phone left running for a week, and
/// that an exception reads as the thing that went wrong, not as the wrapper around it.
/// </summary>
public class TrailTests
{
    private static Trail Fixed(int kept = Trail.DefaultKept)
        => new(kept, () => new DateTimeOffset(2026, 7, 30, 16, 41, 12, 318, TimeSpan.Zero));

    [Fact]
    public void Writes_the_time_the_topic_and_what_happened()
    {
        var trail = Fixed();
        trail.Write("play", "track 4a2f \"Kid A\"");

        Assert.Equal("16:41:12.318  play    track 4a2f \"Kid A\"", trail.Lines[0]);
    }

    [Fact]
    public void Keeps_only_the_last_lines()
    {
        var trail = Fixed(kept: 3);

        foreach (var n in Enumerable.Range(1, 10)) trail.Write("play", $"line {n}");

        Assert.Equal(3, trail.Count);
        Assert.Equal(["line 8", "line 9", "line 10"], trail.Lines.Select(Said));
    }

    [Fact]
    public void Hands_out_what_has_not_been_written_down_once()
    {
        var trail = Fixed();

        trail.Write("play", "one");
        trail.Write("play", "two");

        Assert.Equal(["one", "two"], trail.TakeUnwritten().Select(Said));

        // Taken means taken: a second drain must not write the same lines to the file again.
        Assert.Empty(trail.TakeUnwritten());

        trail.Write("play", "three");
        Assert.Equal(["three"], trail.TakeUnwritten().Select(Said));
    }

    [Fact]
    public void Does_not_hoard_lines_nobody_is_draining()
    {
        var trail = Fixed(kept: 4);

        // Nothing has drained this for a long time. The pending queue is bounded like the ring is: a log
        // written to find a leak must not be one.
        foreach (var n in Enumerable.Range(1, 1000)) trail.Write("play", $"line {n}");

        Assert.True(trail.TakeUnwritten().Length <= 4 * 4);
    }

    [Fact]
    public void Shows_the_tail_for_a_screen_with_room_for_a_few_lines()
    {
        var trail = Fixed();
        foreach (var n in Enumerable.Range(1, 10)) trail.Write("play", $"line {n}");

        Assert.Equal(["line 8", "line 9", "line 10"], trail.Tail(3).Select(Said));

        // Asked for more than there is, it gives what there is instead of throwing.
        Assert.Equal(10, trail.Tail(50).Count);
    }

    [Fact]
    public void Clears()
    {
        var trail = Fixed();
        trail.Write("play", "one");

        trail.Clear();

        Assert.Equal(0, trail.Count);
        Assert.Empty(trail.TakeUnwritten());
        Assert.Equal("", trail.Text());
    }

    [Fact]
    public void Reads_an_exception_down_to_what_actually_went_wrong()
    {
        // The shape a failed fetch arrives in: the outer type says nothing useful, and the cause is one
        // level down.
        var error = new HttpRequestException(
            "An error occurred while sending the request.",
            new SocketException(61));

        var said = Trail.Describe(error);

        Assert.StartsWith("HttpRequestException: An error occurred while sending the request.", said);
        Assert.Contains("<- SocketException:", said);
    }

    [Fact]
    public void Spells_out_a_status_code()
    {
        // The single most useful fact about a refused fetch, and it is not in the message.
        var said = Trail.Describe(new HttpRequestException("no", null, HttpStatusCode.NotFound));

        Assert.Contains("HttpRequestException(404)", said);
    }

    [Fact]
    public void Keeps_a_description_to_one_readable_line()
    {
        var said = Trail.Describe(new InvalidOperationException("first\r\n  second\n\nthird"));

        Assert.Equal("InvalidOperationException: first second third", said);
        Assert.DoesNotContain('\n', said);
    }

    [Fact]
    public void Stops_before_a_chain_fills_the_screen()
    {
        Exception error = new InvalidOperationException("deepest");
        for (var n = 0; n < 12; n++) error = new InvalidOperationException($"level {n}", error);

        var said = Trail.Describe(error);

        Assert.EndsWith("<- ...", said);
        Assert.DoesNotContain("deepest", said);
    }

    [Fact]
    public void Says_so_when_there_is_no_exception_and_no_message()
    {
        Assert.Equal("(none)", Trail.Describe(null));
        Assert.Contains("(no message)", Trail.Describe(new EmptyMessage()));
    }

    /// <summary>What is left of a line after the timestamp and the topic column.</summary>
    private static string Said(string line) => line.Split("  ", 3, StringSplitOptions.None)[^1].Trim();

    private sealed class EmptyMessage : Exception
    {
        public override string Message => "   ";
    }
}
