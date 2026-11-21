using RPM.Core.Lyrics;

namespace RPM.Core.Tests;

/// <summary>The second voice: the words a sheet puts in brackets, and the backing lines bracketed from end
/// to end, as the Android phone app's LyricVoicesTest checks them.</summary>
public class LyricVoicesTests
{
    private static LyricSheet Sheet(string lrc) => LyricSheet.Parse(lrc);

    [Fact]
    public void A_whole_bracketed_line_is_an_aside()
    {
        Assert.True(LyricVoices.IsAside("(Oh, fuck 'em)"));
        Assert.True(LyricVoices.IsAside("  (Oh)  "));
        Assert.False(LyricVoices.IsAside("I wish I understood (yeah)"));
        Assert.False(LyricVoices.IsAside("(Alter ego) dead to me, rah"));
        Assert.False(LyricVoices.IsAside("(a) and (b)"));
        Assert.False(LyricVoices.IsAside("()"));
        Assert.False(LyricVoices.IsAside(null));
    }

    [Fact]
    public void The_bracketed_words_go_under_the_verse()
    {
        const string text = "Milan, darling, Milan (fuck, fuck)";
        var groups = LyricVoices.Groups(text);
        Assert.Equal("Milan, darling, Milan", LyricVoices.Verse(text, groups));
        Assert.Equal("fuck, fuck", LyricVoices.AsideText(text, groups));
    }

    [Fact]
    public void Two_groups_are_joined_with_a_comma()
    {
        const string text = "(Alter ego) dead to me (rah)";
        var groups = LyricVoices.Groups(text);
        Assert.Equal("dead to me", LyricVoices.Verse(text, groups));
        Assert.Equal("Alter ego, rah", LyricVoices.AsideText(text, groups));
    }

    [Fact]
    public void Only_the_outermost_brackets_count()
    {
        const string text = "I said (go (go) now) okay";
        var groups = LyricVoices.Groups(text);
        Assert.Equal((7, 20), Assert.Single(groups));
        Assert.Equal("I said okay", LyricVoices.Verse(text, groups));
    }

    [Fact]
    public void A_line_with_no_second_voice_has_no_groups()
    {
        Assert.Empty(LyricVoices.Groups("Plain line"));
        Assert.Empty(LyricVoices.Groups("(All of it)"));
        Assert.Empty(LyricVoices.Groups("Empty ( ) bracket"));
    }

    [Fact]
    public void A_backing_line_loses_its_brackets()
    {
        Assert.Equal("Oh, fuck 'em", LyricVoices.Unbracketed(" (Oh, fuck 'em) "));
        Assert.Equal("Not an aside (yeah)", LyricVoices.Unbracketed("Not an aside (yeah)"));
    }

    [Fact]
    public void A_backing_line_goes_with_the_line_before_it()
    {
        var sheet = Sheet("[00:10.00]To crown you the queen\n[00:12.00](I don't fuck with these)\n[00:13.00]Now with that crown");
        var score = LyricScore.Of(sheet, asides: true);

        var words = score.Parts.Where(p => !p.IsRest).Select(p => sheet.Lines[p.Line].Text).ToList();
        Assert.Equal(["To crown you the queen", "Now with that crown"], words);
        Assert.Equal(new LyricAside(0, 1, 12, 13), Assert.Single(score.Asides));

        // The line before it is swept until the backing line starts, and the next line follows it in the score.
        Assert.Equal(12, score.Parts[0].Until, 3);
        Assert.Equal(0, score.IndexAt(12.5));
        Assert.Equal(1, score.IndexAt(13.0));

        // Without asides, the score is the sheet's.
        Assert.Equal(3, LyricScore.Of(sheet).Parts.Count(p => !p.IsRest));
        Assert.Empty(LyricScore.Of(sheet).Asides);
    }

    [Fact]
    public void A_backing_line_with_no_line_before_it_keeps_its_row()
    {
        var score = LyricScore.Of(Sheet("[00:01.00](Ooh)\n[00:03.00]First line"), asides: true);
        Assert.Equal(2, score.Parts.Count(p => !p.IsRest));
        Assert.Empty(score.Asides);

        // Nor after a break, which closes the stretch before it.
        var after = LyricScore.Of(Sheet("[00:01.00]One\n[00:03.00]♪\n[00:30.00](Ooh)\n[00:32.00]Two"), asides: true);
        Assert.Empty(after.Asides);

        // Nor after a wait no singing could fill.
        var late = LyricScore.Of(Sheet("[00:01.00]One\n[00:40.00](Ooh)\n[00:42.00]Two"), asides: true);
        Assert.Empty(late.Asides);
    }

    [Fact]
    public void Two_backing_lines_in_a_row_both_go_under_the_same_line()
    {
        var sheet = Sheet("[00:10.00]Line\n[00:11.00](one)\n[00:12.00](two)\n[00:14.00]Next");
        var score = LyricScore.Of(sheet, asides: true);
        Assert.Equal([1, 2], score.Asides.Select(a => a.Line));
        Assert.All(score.Asides, a => Assert.Equal(0, a.Part));
        Assert.Equal(3, score.Parts[1].Line);
        Assert.Equal(score.Parts[0].Section, score.Parts[1].Section);
    }

    [Fact]
    public void A_measured_backing_line_shows_as_its_words_are_sung()
    {
        var spans = new[] { new WordSpan(1, 2, 11.6, 12.0), new WordSpan(4, 4, 12.0, 12.8) };
        var (from, until) = KaraokeLines.Shown(12.0, spans, measured: true);
        Assert.Equal(11.6 - KaraokeLines.Lead, from, 9);
        Assert.Equal(12.8, until, 9);

        // Never more than a second before its stamp.
        var early = new[] { new WordSpan(1, 2, 10.0, 12.0), new WordSpan(4, 4, 12.0, 12.8) };
        Assert.Equal(12.0 - KaraokeLines.Earliest, KaraokeLines.Shown(12.0, early, measured: true).From, 9);

        // Unmeasured, it shows from its stamp until its shared-out words are done.
        var swept = LyricTiming.Sweep("(Oh yeah)", 12.0, 14.0);
        var guessed = KaraokeLines.Shown(12.0, swept, measured: false);
        Assert.Equal(12.0, guessed.From, 9);
        Assert.Equal(swept[^1].To, guessed.Until, 9);
    }
}
