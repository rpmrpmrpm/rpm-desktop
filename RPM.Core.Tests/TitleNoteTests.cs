using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The notes a kind of record puts on its songs: "(Live)" and "(Mixed)". What matters most is the titles each
/// must leave alone: a song already marked must not get a second note, however the first one was spelled.
/// </summary>
public class TitleNoteTests
{
    [Theory]
    [InlineData("Roxanne", "Roxanne (Live)")]
    [InlineData("  Roxanne  ", "Roxanne (Live)")]
    [InlineData("Live Forever", "Live Forever (Live)")]
    [InlineData("Alive", "Alive (Live)")]
    [InlineData("Half-Live", "Half-Live (Live)")]
    [InlineData("Song (feat. Guest)", "Song (feat. Guest) (Live)")]
    [InlineData("Undecided (deep radio mix)", "Undecided (deep radio mix) (Live)")]
    public void A_plain_title_gets_the_live_note_at_its_end(string title, string marked)
        => Assert.Equal(marked, TitleNote.Live.Marked(title));

    [Theory]
    [InlineData("Roxanne (Live)")]
    [InlineData("Roxanne (live)")]
    [InlineData("Roxanne [Live]")]
    [InlineData("Roxanne (Live at the Hollywood Bowl)")]
    [InlineData("Roxanne (Live Version)")]
    [InlineData("Roxanne (2003 Remaster / Live)")]
    [InlineData("Roxanne (MTV Unplugged)")]
    [InlineData("Roxanne - Live")]
    [InlineData("Roxanne – Live 1979")]
    [InlineData("Roxanne — Live at Wembley")]
    public void A_title_that_already_says_live_is_left_as_it_is(string title)
    {
        Assert.True(TitleNote.Live.Says(title));
        Assert.Equal(title, TitleNote.Live.Marked(title));
    }

    [Theory]
    [InlineData("Strings of Life", "Strings of Life (Mixed)")]
    [InlineData("Strings of Life (Original Mix)", "Strings of Life (Original Mix) (Mixed)")]
    [InlineData("Strings of Life (Radio Mix)", "Strings of Life (Radio Mix) (Mixed)")]
    [InlineData("Mix It Up", "Mix It Up (Mixed)")]
    [InlineData("Mixed Emotions", "Mixed Emotions (Mixed)")]
    [InlineData("Song (feat. Guest)", "Song (feat. Guest) (Mixed)")]
    public void A_plain_title_gets_the_mixed_note_at_its_end(string title, string marked)
        => Assert.Equal(marked, TitleNote.Mixed.Marked(title));

    [Theory]
    [InlineData("Strings of Life (Mixed)")]
    [InlineData("Strings of Life [mixed]")]
    [InlineData("Strings of Life (Original Mix) - Mixed")]
    [InlineData("Strings of Life (Mixed by Carl Cox)")]
    [InlineData("Strings of Life – Mixed")]
    public void A_title_that_already_says_mixed_is_left_as_it_is(string title)
    {
        Assert.True(TitleNote.Mixed.Says(title));
        Assert.Equal(title, TitleNote.Mixed.Marked(title));
    }

    [Fact]
    public void Marking_twice_adds_one_note()
    {
        Assert.Equal("Roxanne (Live)", TitleNote.Live.Marked(TitleNote.Live.Marked("Roxanne")));
        Assert.Equal("Track (Mixed)", TitleNote.Mixed.Marked(TitleNote.Mixed.Marked("Track")));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    public void An_empty_title_stays_empty(string? title, string marked)
    {
        Assert.Equal(marked, TitleNote.Live.Marked(title));
        Assert.Equal(marked, TitleNote.Mixed.Marked(title));
    }

    /// <summary>The word in the name is not a note, and neither is the word inside a longer one.</summary>
    [Theory]
    [InlineData("Live Forever")]
    [InlineData("Live and Let Die")]
    [InlineData("Alive (Remastered)")]
    [InlineData("Delivered (Single Edit)")]
    public void The_word_in_the_name_is_not_a_live_note(string title) => Assert.False(TitleNote.Live.Says(title));

    /// <summary>A version's name is not the DJ's blend, and neither is the word in the song's own name.</summary>
    [Theory]
    [InlineData("Mixed Emotions")]
    [InlineData("Strings of Life (Original Mix)")]
    [InlineData("Strings of Life (Remix)")]
    [InlineData("Strings of Life (Unmixed)")]
    public void A_version_or_the_word_in_the_name_is_not_a_mixed_note(string title)
        => Assert.False(TitleNote.Mixed.Says(title));

    [Theory]
    [InlineData(ReleaseType.Live, "(Live)")]
    [InlineData(ReleaseType.Mixtape, "(Mixed)")]
    [InlineData(ReleaseType.Album, null)]
    [InlineData(ReleaseType.Compilation, null)]
    [InlineData(null, null)]
    public void Each_release_type_has_its_note_or_none(ReleaseType? type, string? note)
        => Assert.Equal(note, TitleNote.For(type)?.Text);

    /// <summary>A live record's songs are not mixed, so one note does not stand in for the other.</summary>
    [Fact]
    public void One_note_does_not_count_as_the_other()
    {
        Assert.False(TitleNote.Mixed.Says("Roxanne (Live)"));
        Assert.Equal("Roxanne (Live) (Mixed)", TitleNote.Mixed.Marked("Roxanne (Live)"));
    }
}
