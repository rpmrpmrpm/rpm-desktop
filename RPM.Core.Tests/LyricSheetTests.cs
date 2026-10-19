using RPM.Core.Lyrics;

namespace RPM.Core.Tests;

/// <summary>
/// An LRC sheet is written by hand by strangers, and every one of them writes it slightly differently:
/// two digits of fraction or three, a colon where a point belongs, the same line stamped four times for
/// a refrain, an offset tag to nudge a lazy transcription into place. A parser that only reads the shape
/// it expected drifts a line behind and the highlight sits on the wrong words, which is worse than
/// showing none at all, because it looks like it is working.
/// </summary>
public class LyricSheetTests
{
    [Fact]
    public void Plain_lyrics_are_not_a_sheet()
    {
        // Words with no clock cannot be followed, and the caller has to be told so plainly rather than
        // handed a list of lines that all claim to be sung at nought seconds.
        Assert.False(LyricSheet.Parse("Just some words\nAnd some more").IsSynced);
        Assert.False(LyricSheet.Parse(null).IsSynced);
        Assert.False(LyricSheet.Parse("   ").IsSynced);
        Assert.Same(LyricSheet.Empty, LyricSheet.Parse("[ar:Someone]\n[ti:Something]"));
    }

    [Fact]
    public void Timestamps_are_read_however_the_fraction_is_written()
    {
        var sheet = LyricSheet.Parse(
            "[00:05.5]half a second past five\n" +
            "[00:10.25]a quarter past ten\n" +
            "[00:15.125]an eighth past fifteen\n" +
            "[00:20]dead on twenty\n" +
            "[01:00:50]a colon where a point belongs");

        Assert.Equal(5.5, sheet.Lines[0].Seconds, 3);
        Assert.Equal(10.25, sheet.Lines[1].Seconds, 3);
        Assert.Equal(15.125, sheet.Lines[2].Seconds, 3);
        Assert.Equal(20.0, sheet.Lines[3].Seconds, 3);
        Assert.Equal(60.5, sheet.Lines[4].Seconds, 3);
    }

    [Fact]
    public void A_refrain_written_once_is_sung_every_time_it_is_stamped()
    {
        // The economical way to write a chorus, and a parser that takes only the first stamp loses
        // every repeat of it.
        var sheet = LyricSheet.Parse("[00:10.00][01:20.00][02:30.00]the chorus");

        Assert.Equal(3, sheet.Lines.Count);
        Assert.All(sheet.Lines, line => Assert.Equal("the chorus", line.Text));
        Assert.Equal([10.0, 80.0, 150.0], sheet.Lines.Select(l => l.Seconds));
    }

    [Fact]
    public void Lines_come_back_in_time_order_whatever_order_they_were_written_in()
    {
        // Multiple stamps per line produce them out of order by construction, and the search below is a
        // binary one: it would quietly return nonsense on an unsorted list.
        var sheet = LyricSheet.Parse("[02:00.00][00:30.00]late and early\n[01:00.00]between");

        Assert.Equal([30.0, 60.0, 120.0], sheet.Lines.Select(l => l.Seconds));
    }

    [Fact]
    public void An_offset_pulls_the_whole_sheet_with_it()
    {
        // LRC's convention, not arithmetic's: a POSITIVE offset means the words are written early and
        // have to be pulled back.
        var sheet = LyricSheet.Parse("[offset:+500]\n[00:10.00]a line");
        Assert.Equal(9.5, sheet.Lines[0].Seconds, 3);

        var late = LyricSheet.Parse("[offset:-500]\n[00:10.00]a line");
        Assert.Equal(10.5, late.Lines[0].Seconds, 3);
    }

    [Fact]
    public void An_offset_can_never_push_a_line_before_the_record_starts()
    {
        var sheet = LyricSheet.Parse("[offset:+5000]\n[00:01.00]a line");
        Assert.Equal(0.0, sheet.Lines[0].Seconds, 3);
    }

    [Fact]
    public void Blank_lines_are_kept()
    {
        // An empty line at 1:12 says "nothing is sung for a while", and dropping it would run the verse
        // into the chorus and leave the highlight on words nobody is singing.
        var sheet = LyricSheet.Parse("[00:10.00]a verse\n[00:20.00]\n[00:40.00]the chorus");

        Assert.Equal(3, sheet.Lines.Count);
        Assert.Equal("", sheet.Lines[1].Text);
    }

    [Fact]
    public void The_line_being_sung_is_the_last_one_to_have_started()
    {
        var sheet = LyricSheet.Parse("[00:10.00]one\n[00:20.00]two\n[00:30.00]three");

        Assert.Equal(0, sheet.IndexAt(10.0));    // exactly on it counts as started
        Assert.Equal(0, sheet.IndexAt(19.9));
        Assert.Equal(1, sheet.IndexAt(20.0));
        Assert.Equal(2, sheet.IndexAt(999.0));   // the last line holds to the end of the record
    }

    [Fact]
    public void Before_the_first_line_nothing_is_being_sung()
    {
        // An intro is a real part of a song. Returning line nought here would light the opening words
        // for however long the intro runs, which reads as the sheet being out of time.
        var sheet = LyricSheet.Parse("[00:10.00]one\n[00:20.00]two");

        Assert.Equal(-1, sheet.IndexAt(0.0));
        Assert.Equal(-1, sheet.IndexAt(9.99));
    }

    [Fact]
    public void An_empty_sheet_is_never_being_sung()
    {
        Assert.Equal(-1, LyricSheet.Empty.IndexAt(0));
        Assert.Equal(-1, LyricSheet.Empty.IndexAt(123.4));
    }

    [Fact]
    public void A_sheet_keeps_the_text_it_was_read_from()
    {
        // What anything remembering a sheet writes down. Keeping the ORIGINAL rather than this parser's
        // reading of it is what lets a cache survive the parser learning to read something new, and a
        // sheet that could not hand its own source back would have to be re-fetched to be stored.
        const string lrc = "[00:10.00]one\n[00:20.00]two";
        var sheet = LyricSheet.Parse(lrc);

        Assert.Equal(lrc, sheet.Source);
        Assert.Null(LyricSheet.Empty.Source);
        Assert.Null(LyricSheet.Parse("no timestamps here").Source);
    }

    [Fact]
    public void A_sheet_read_back_from_its_own_source_is_the_same_sheet()
    {
        // The cache round trip, in one assertion: written out as Source, read back with Parse.
        var original = LyricSheet.Parse(
            "[ar:Someone]\r\n[offset:+250]\r\n[00:10.00][01:00.00]a refrain\r\n[00:30.50]a verse\r\n");
        var restored = LyricSheet.Parse(original.Source);

        Assert.Equal(original.Lines, restored.Lines);
        Assert.Equal(original.IndexAt(65), restored.IndexAt(65));
    }

    [Fact]
    public void Windows_line_endings_and_metadata_do_not_derail_it()
    {
        // Nearly every sheet in the wild arrives with CRLF and a header of tags.
        var sheet = LyricSheet.Parse("[ar:Someone]\r\n[ti:Something]\r\n[00:10.00]a line\r\n[00:20.00]another\r\n");

        Assert.True(sheet.IsSynced);
        Assert.Equal(2, sheet.Lines.Count);
        Assert.Equal("a line", sheet.Lines[0].Text);
    }

    /// <summary>Which lines have nothing on them to read. The sheet keeps them all (the clock has to
    /// land somewhere), so this is the question a view asks before giving a line a row of its own.
    /// </summary>
    [Fact]
    public void A_line_with_no_words_on_it_is_wordless()
    {
        Assert.True(LyricSheet.IsWordless(null));
        Assert.True(LyricSheet.IsWordless(""));
        Assert.True(LyricSheet.IsWordless("   \t "));
    }

    /// <summary>A note on its own is an empty line somebody made visible, and the same file uses both.
    /// </summary>
    [Fact]
    public void A_line_of_nothing_but_notes_is_wordless()
    {
        Assert.True(LyricSheet.IsWordless("♪"));               // ♪
        Assert.True(LyricSheet.IsWordless("♫ ♫ ♫")); // ♫ ♫ ♫
        Assert.True(LyricSheet.IsWordless(" ♩♬ "));       // ♩♬
    }

    /// <summary>The emoji notes are a surrogate pair each. Walking chars rather than runes would compare
    /// half of one against a whole note, find no match, and call the line wordy, which is the bug this
    /// test prevents.</summary>
    [Fact]
    public void The_emoji_notes_count_as_notes()
    {
        Assert.True(LyricSheet.IsWordless("\U0001F3B5"));                 // 🎵
        Assert.True(LyricSheet.IsWordless("\U0001F3B6 \U0001F3B5"));      // 🎶 🎵
    }

    /// <summary>Anything with a word on it keeps its row, including a word next to a note:
    /// a writer who marks the singing WITH the words has still written words.</summary>
    [Fact]
    public void A_line_with_anything_to_read_is_not_wordless()
    {
        Assert.False(LyricSheet.IsWordless("a line"));
        Assert.False(LyricSheet.IsWordless("♪ a line ♪"));
        Assert.False(LyricSheet.IsWordless("..."));
        Assert.False(LyricSheet.IsWordless("♯"));   // ♯ is an accidental, and a lyric can be about one
    }
    // ── enhanced LRC: the word stamps, which are kept ──

    /// <summary>The text still comes out clean (nobody wants to read a timecode in a lyric),
    /// and the times are kept beside it.</summary>
    [Fact]
    public void An_enhanced_line_keeps_its_words_and_still_reads_clean()
    {
        var sheet = LyricSheet.Parse("[00:12.00]<00:12.30>Hello <00:12.90>darkness <00:13.50>friend");

        var line = Assert.Single(sheet.Lines);
        Assert.Equal("Hello darkness friend", line.Text);

        Assert.NotNull(line.Words);
        Assert.Equal(["Hello", "darkness", "friend"], line.Words!.Select(w => w.Text));
        Assert.Equal(12.30, line.Words![0].Seconds, 3);
        Assert.Equal(12.90, line.Words![1].Seconds, 3);
        Assert.Equal(13.50, line.Words![2].Seconds, 3);
    }

    /// <summary>The ordinary sheet, which is all of them in practice, carries no words and says so.</summary>
    [Fact]
    public void A_plain_line_has_no_words_of_its_own()
        => Assert.Null(Assert.Single(LyricSheet.Parse("[00:12.00]Hello darkness").Lines).Words);

    /// <summary>[offset:] moves the word stamps with the line. A sheet shifted a quarter of a second
    /// whose words stayed put would have the highlight drifting off the singer inside every line.</summary>
    [Fact]
    public void The_offset_moves_the_words_too()
    {
        var sheet = LyricSheet.Parse("[offset:+500]\n[00:12.00]<00:12.30>Hello <00:12.90>darkness");

        var line = Assert.Single(sheet.Lines);
        Assert.Equal(11.50, line.Seconds, 3);
        Assert.Equal(11.80, line.Words![0].Seconds, 3);
        Assert.Equal(12.40, line.Words![1].Seconds, 3);
    }

    /// <summary>A refrain written once and stamped four times gets no words at all. One set of word
    /// times can be true of at most one of the four performances, and nothing in the file says which,
    /// so the line is followed whole rather than lighting the wrong word for three choruses out of
    /// four.</summary>
    [Fact]
    public void A_line_sung_more_than_once_keeps_no_word_times()
    {
        var sheet = LyricSheet.Parse("[00:12.00][01:04.00]<00:12.30>Hello <00:12.90>darkness");

        Assert.Equal(2, sheet.Lines.Count);
        Assert.All(sheet.Lines, l => Assert.Null(l.Words));
    }
}
