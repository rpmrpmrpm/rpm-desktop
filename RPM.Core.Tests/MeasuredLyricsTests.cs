using RPM.Core.Lyrics;

namespace RPM.Core.Tests;

/// <summary>Reading the cloud's measured lyrics onto this machine's sheet, as the Android TV app's
/// MeasuredLyricsTest and SheetOffsetTest check it.</summary>
public class MeasuredLyricsTests
{
    private static readonly IReadOnlyList<MeasuredLine> Measured =
    [
        new(3, 12.5, "hello there you", [new(12.6, 12.9), new(13.4, 13.8), new(14.0, 14.2)]),
    ];

    private static LyricSheet Sheet(params (double Seconds, string Text)[] lines)
        => LyricSheet.Parse(string.Join('\n', lines.Select(l => FormattableString.Invariant(
            $"[{(int)(l.Seconds / 60):00}:{l.Seconds % 60:00.00}]{l.Text}"))));

    private static readonly LyricSheet Ours = Sheet((1, "first line"), (10, "second line"), (20, "third line"));

    private static MeasuredAnswer Answer(double? offset, params (double Seconds, string Text)[] on)
        => new([], offset, [.. on.Select((l, i) => new SheetLine(i, l.Seconds, l.Text))]);

    [Fact]
    public void The_same_line_takes_the_measured_moments()
    {
        var spans = MeasuredLyrics.ForLine(Measured, "hello there you", 12.5);

        Assert.NotNull(spans);
        Assert.Equal([12.6, 13.4, 14.0], spans!.Select(s => s.From));
        Assert.Equal([0, 6, 12], spans.Select(s => s.Start));
    }

    [Fact]
    public void Another_sheets_line_is_not_used()
    {
        Assert.Null(MeasuredLyrics.ForLine(Measured, "hello there", 12.5));
        Assert.Null(MeasuredLyrics.ForLine(Measured, "hello there you", 14.0));
    }

    [Fact]
    public void Words_placed_outside_the_line_are_the_aligners_mistake()
    {
        // Ending well before the line's stamp, or starting after the next line: swept by length instead.
        Assert.NotNull(MeasuredLyrics.Heard(Measured, "hello there you", 12.5, 20));
        Assert.Null(MeasuredLyrics.Heard(Measured, "hello there you", 12.5, 12.55));

        IReadOnlyList<MeasuredLine> early = [new(0, 20.0, "too soon", [new(10.0, 10.5), new(11.0, 11.5)])];
        Assert.Null(MeasuredLyrics.Heard(early, "too soon", 20.0, 30.0));
    }

    [Fact]
    public void Most_lines_measured_is_covered()
    {
        var sheet = Sheet((1, "a b"), (2, ""), (3, "c d"), (4, "e f"), (5, "g h"));
        IReadOnlyList<MeasuredLine> three =
        [
            new(0, 1, "a b", [new(1, 1.2), new(1.3, 1.5)]),
            new(2, 3, "c d", [new(3, 3.2), new(3.3, 3.5)]),
            new(3, 4, "e f", [new(4, 4.2), new(4.3, 4.5)]),
        ];

        Assert.True(MeasuredLyrics.Covers(three, sheet));
        Assert.False(MeasuredLyrics.Covers(three.Take(2).ToList(), sheet));
    }

    [Fact]
    public void An_offset_worked_out_for_this_sheet_is_taken()
    {
        Assert.Equal(2.4, MeasuredLyrics.OffsetFor(Answer(2.4, (1, "first line"), (10, "second line")), Ours));
    }

    [Fact]
    public void An_offset_worked_out_for_another_sheet_is_not()
    {
        Assert.Equal(0, MeasuredLyrics.OffsetFor(Answer(2.4, (1, "first line"), (11, "second line")), Ours));
        Assert.Equal(0, MeasuredLyrics.OffsetFor(Answer(2.4, (1, "other words")), Ours));
    }

    [Fact]
    public void No_offset_is_no_move()
    {
        Assert.Equal(0, MeasuredLyrics.OffsetFor(Answer(null), Ours));
        Assert.Equal(0, MeasuredLyrics.OffsetFor(Answer(3.0), Ours));
    }

    [Fact]
    public void A_shifted_sheet_starts_no_line_before_the_record()
    {
        var moved = Ours.Shifted(-3.0);

        Assert.Equal([0.0, 7.0, 17.0], moved.Lines.Select(l => Math.Round(l.Seconds, 6)));
        Assert.Same(Ours, Ours.Shifted(0));
    }

    [Fact]
    public void Measured_lines_moved_with_the_sheet_still_match_it_and_keep_their_word_times()
    {
        IReadOnlyList<MeasuredLine> measured = [new(1, 10.0, "second line", [new(12.5, 12.9), new(13.0, 13.6)])];
        var sheet = Ours.Shifted(2.4);

        var words = MeasuredLyrics.ForLine(MeasuredLyrics.Shifted(measured, 2.4), "second line", sheet.Lines[1].Seconds);

        Assert.NotNull(words);
        Assert.Equal(12.5, words![0].From);
        Assert.Equal(13.6, words[1].To);
    }
}
