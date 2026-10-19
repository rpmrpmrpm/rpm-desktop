using RPM.Core.Sharing;

namespace RPM.Core.Tests;

/// <summary>
/// Reading a <c>Range</c> header, which is the difference between a borrowed track that plays and
/// one that also seeks. ffmpeg reads the head of a file, then jumps; every jump is one of these.
/// </summary>
public class ByteRangeTests
{
    [Fact]
    public void A_window_in_the_middle()
    {
        var range = ByteRange.Parse("bytes=100-199", 1000);

        Assert.Equal(new ByteRange(100, 199), range);
        Assert.Equal(100, range!.Value.Length);
    }

    [Fact]
    public void From_here_to_the_end()
    {
        Assert.Equal(new ByteRange(900, 999), ByteRange.Parse("bytes=900-", 1000));
    }

    [Fact]
    public void The_last_bytes_which_is_how_a_trailer_is_read()
    {
        // An MP4 written by anything that was not aiming at streaming keeps its index at the end,
        // and this is the request that goes looking for it.
        Assert.Equal(new ByteRange(800, 999), ByteRange.Parse("bytes=-200", 1000));
    }

    [Fact]
    public void Asking_for_more_of_the_tail_than_there_is_gets_the_whole_file()
    {
        Assert.Equal(new ByteRange(0, 999), ByteRange.Parse("bytes=-5000", 1000));
    }

    [Fact]
    public void An_end_past_the_file_stops_at_the_file()
    {
        Assert.Equal(new ByteRange(500, 999), ByteRange.Parse("bytes=500-99999", 1000));
    }

    [Fact]
    public void No_header_is_the_whole_file()
    {
        Assert.Null(ByteRange.Parse(null, 1000));
        Assert.Null(ByteRange.Parse("", 1000));
    }

    [Fact]
    public void Nonsense_is_the_whole_file_rather_than_a_wrong_window()
    {
        Assert.Null(ByteRange.Parse("items=0-10", 1000));
        Assert.Null(ByteRange.Parse("bytes=abc-def", 1000));
        Assert.Null(ByteRange.Parse("bytes=", 1000));
        Assert.Null(ByteRange.Parse("bytes=10", 1000));
    }

    [Fact]
    public void A_start_past_the_end_of_the_file_is_not_a_window()
    {
        Assert.Null(ByteRange.Parse("bytes=2000-3000", 1000));
    }

    [Fact]
    public void A_backwards_window_is_not_a_window()
    {
        Assert.Null(ByteRange.Parse("bytes=500-100", 1000));
    }

    [Fact]
    public void Several_windows_at_once_are_answered_with_the_whole_file()
    {
        // Nothing that plays audio sends one, and answering it as if it were a single window would
        // hand back the wrong bytes with a 206 on them.
        Assert.Null(ByteRange.Parse("bytes=0-99,200-299", 1000));
    }
}
