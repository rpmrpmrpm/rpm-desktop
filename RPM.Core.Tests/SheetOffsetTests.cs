using RPM.Core.Lyrics;

namespace RPM.Core.Tests;

/// <summary>
/// The search for one shift between a lyric sheet and its recording, on made-up model scores: a recording is drawn
/// frame by frame, with each line's letters sung where the test says, and silence (the blank) everywhere else.
/// </summary>
public class SheetOffsetTests
{
    private const double Frame = 0.02;
    private const int Vocab = 10;   // blank, A to H, and the word delimiter

    private static readonly CtcVocabulary Alphabet = new(
        new Dictionary<char, int>
        {
            ['A'] = 1, ['B'] = 2, ['C'] = 3, ['D'] = 4, ['E'] = 5, ['F'] = 6, ['G'] = 7, ['H'] = 8, ['|'] = 9,
        },
        blank: 0, delimiter: '|');

    private static readonly string[] Texts =
    [
        "bad cafe", "feed a dog", "head ache", "a big egg", "deaf hag", "beg bad",
        "face fade", "edge cab", "gag ahead", "bead fad", "dab head", "chef egg",
    ];

    /// <summary>A sheet of the twelve lines, one every five seconds from <paramref name="first"/>.</summary>
    private static List<LyricLine> Sheet(double first = 10, IReadOnlyList<string>? texts = null, double every = 5) =>
        [.. (texts ?? Texts).Select((t, i) => new LyricLine(first + i * every, t))];

    /// <summary>A recording of <paramref name="seconds"/> in which every line of <paramref name="sheet"/> is sung
    /// <paramref name="late"/> seconds after its stamp, each token held for three frames.</summary>
    private static HeardRecord Record(IReadOnlyList<LyricLine> sheet, double late, double seconds = 120, bool sung = true)
    {
        var frames = (int)(seconds / Frame);
        var scores = new float[frames * Vocab];
        for (var f = 0; f < frames; f++)
            Set(scores, f, 0);

        if (sung)
            foreach (var line in sheet.Where(l => !LyricSheet.IsWordless(l.Text)))
            {
                var at = (int)Math.Round((line.Seconds + late) / Frame);
                foreach (var token in Alphabet.Spell(line.Text)!.Tokens)
                    for (var hold = 0; hold < 3; hold++, at++)
                        if (at >= 0 && at < frames)
                            Set(scores, at, token);
            }

        return SheetOffset.Normalised(scores, frames, Vocab, blank: 0, Frame);
    }

    /// <summary>Frame <paramref name="frame"/> is fairly sure of <paramref name="token"/> and unsure of the rest.</summary>
    private static void Set(float[] scores, int frame, int token)
    {
        for (var v = 0; v < Vocab; v++)
            scores[frame * Vocab + v] = v == token ? 4f : 0f;
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(-2.5)]
    [InlineData(11.3)]
    [InlineData(0.8)]
    public void A_sheet_out_of_time_by_a_constant_is_found_to_the_frame(double late)
    {
        var sheet = Sheet();
        var found = SheetOffset.Find(Record(sheet, late), sheet, Alphabet);

        Assert.True(Math.Abs(late - found.Seconds) < 0.041, found.ToString());
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.2)]
    [InlineData(-0.3)]
    public void A_sheet_in_time_or_nearly_so_keeps_its_own_stamps(double late)
    {
        var sheet = Sheet();
        Assert.Equal(0, SheetOffset.Find(Record(sheet, late), sheet, Alphabet).Seconds);
    }

    [Fact]
    public void A_shift_at_the_end_of_the_search_is_not_trusted()
    {
        // On a record whose sheet does not fit, the best shift often runs to the end of the search, so one found there
        // is not used, even when it is right.
        var sheet = Sheet(first: 25);
        var found = SheetOffset.Find(Record(sheet, -19.6), sheet, Alphabet);
        Assert.Equal(-19.6, found.Best, 0.041);
        Assert.Equal(0, found.Seconds);
    }

    [Fact]
    public void An_instrumental_gives_no_shift()
    {
        var sheet = Sheet();
        var found = SheetOffset.Find(Record(sheet, 0, sung: false), sheet, Alphabet);
        Assert.Equal(0, found.Seconds);
    }

    [Fact]
    public void A_sheet_that_fits_two_places_gives_no_shift()
    {
        // One line repeated every four seconds and sung six seconds late. A shift of two seconds fits every line but
        // one as well as six does, so neither can be trusted.
        var sheet = Sheet(first: 10, texts: Enumerable.Repeat("bad cafe", 20).ToList(), every: 4);
        var found = SheetOffset.Find(Record(sheet, 6.0), sheet, Alphabet);
        Assert.Equal(0, found.Seconds);
    }

    [Fact]
    public void A_sheet_whose_singing_is_mostly_elsewhere_gives_no_shift()
    {
        // Half the lines are sung three seconds late and half are sung three seconds early: no one shift holds.
        var sheet = Sheet();
        var late = Record(sheet.Where((_, i) => i % 2 == 0).ToList(), 3.0);
        var early = Record(sheet.Where((_, i) => i % 2 == 1).ToList(), -3.0);
        var frames = late.Frames;
        var scores = new float[frames * Vocab];
        for (var f = 0; f < frames; f++)
            for (var v = 0; v < Vocab; v++)
                scores[f * Vocab + v] = (float)Math.Max(late.At(f, v), early.At(f, v));

        var found = SheetOffset.Find(SheetOffset.Normalised(scores, frames, Vocab, 0, Frame), sheet, Alphabet);
        Assert.Equal(0, found.Seconds);
    }

    [Fact]
    public void Too_few_sung_lines_give_no_shift()
    {
        var sheet = new List<LyricLine>
        {
            new(5, "♪"), new(10, "bad cafe"), new(15, ""), new(20, "feed a dog"), new(25, "head ache"),
        };
        var found = SheetOffset.Find(Record(sheet, 4), sheet, Alphabet);
        Assert.Equal(0, found.Seconds);
        Assert.Equal(0, found.Lines);
    }

    [Fact]
    public void Wordless_lines_are_skipped_and_still_bound_the_line_before()
    {
        var sheet = Sheet();
        sheet.Insert(6, new LyricLine(38.5, "♪"));
        var found = SheetOffset.Find(Record(sheet, 2.0), sheet, Alphabet);
        Assert.True(Math.Abs(2.0 - found.Seconds) < 0.041, found.ToString());
        Assert.Equal(Texts.Length, found.Lines);
    }

    [Fact]
    public void Reading_in_chunks_puts_every_frame_back_where_it_was_heard()
    {
        // The fake model answers each frame with a token that names the frame's place in the whole recording. The
        // audio itself carries the sample index, so the model can tell where its window sits.
        const int rate = 1000, perFrame = 10;   // 100 frames a second
        var mono = new float[(int)(47.3 * rate)];
        for (var i = 0; i < mono.Length; i++)
            mono[i] = i;

        var calls = 0;
        HeardWindow? Hear(ReadOnlySpan<float> window)
        {
            calls++;
            var first = (int)window[0] / perFrame;
            var frames = window.Length / perFrame;
            var scores = new float[frames * Vocab];
            for (var f = 0; f < frames; f++)
                scores[f * Vocab + (first + f) % Vocab] = 5f;
            return new HeardWindow(scores, frames, Vocab);
        }

        var heard = SheetOffset.Hear(mono, rate, perFrame, blank: 0, Hear)!;

        Assert.Equal(3, calls);
        Assert.Equal(mono.Length / perFrame, heard.Frames);
        Assert.Equal(0.01, heard.FrameSeconds, 9);
        for (var f = 0; f < heard.Frames; f++)
        {
            var loudest = Enumerable.Range(0, Vocab).MaxBy(v => heard.At(f, v));
            Assert.Equal(f % Vocab, loudest);
        }
    }

    [Fact]
    public void A_chunk_the_model_will_not_read_gives_no_record()
    {
        var mono = new float[50_000];
        var calls = 0;
        HeardWindow? Hear(ReadOnlySpan<float> window) =>
            ++calls == 2 ? null : new HeardWindow(new float[window.Length / 10 * Vocab], window.Length / 10, Vocab);

        Assert.Null(SheetOffset.Hear(mono, 1000, 10, 0, Hear));
    }

    [Fact]
    public void Frames_outside_the_recording_are_silence()
    {
        var heard = Record(Sheet(), 0, seconds: 1);
        Assert.Equal(0, heard.At(-1, 0));
        Assert.Equal(SheetOffset.Outside, heard.At(heard.Frames, 3));
    }
}
