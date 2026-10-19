using RPM.Core.Library;

namespace RPM.Core.Tests;

public class TitleAsidesTests
{
    private static string Name(string title)
        => string.Concat(TitleAsides.Split(title).Where(p => !p.Aside).Select(p => p.Text));

    private static string[] Asides(string title)
        => TitleAsides.Split(title).Where(p => p.Aside).Select(p => p.Text).ToArray();

    // ── the record this rule is tested against ──

    /// <summary>Youssou N'Dour's The Guide. Ten of its sixteen lines carry the song's Wolof name in
    /// brackets and one carries a mix. They are written identically, but only the mix is a note about
    /// the recording. If all were faded, the reader would be told the record is called things it is
    /// not.</summary>
    [Theory]
    [InlineData("Leaving (Dem)")]
    [InlineData("Old Man (Gorgui)")]
    [InlineData("Without a Smile (Same)")]
    [InlineData("How You Are (No mele)")]
    [InlineData("Generations (Diamono)")]
    [InlineData("Undecided (Japoulo)")]
    [InlineData("Love One Another (Beuguente)")]
    [InlineData("Life (Adouna)")]
    [InlineData("My People (Samay nit)")]
    [InlineData("Silence (Tongo)")]
    [InlineData("The Guide (Wommat)")]
    public void A_songs_other_name_is_not_an_aside(string title)
    {
        Assert.Empty(Asides(title));
        Assert.Equal(title, Name(title));
    }

    [Fact]
    public void The_mix_on_that_record_is_the_one_aside()
    {
        var parts = TitleAsides.Split("Undecided (deep radio mix)");

        Assert.Equal(
            [new TitlePart("Undecided ", false), new TitlePart("(deep radio mix)", true)],
            parts);
    }

    // ── what a version is described in ──

    [Theory]
    [InlineData("Song (Remix)", "(Remix)")]
    [InlineData("Song (Radio Edit)", "(Radio Edit)")]
    [InlineData("Song (Extended Mix)", "(Extended Mix)")]
    [InlineData("Song (Live at Abbey Road Studios)", "(Live at Abbey Road Studios)")]
    [InlineData("Song (Acoustic Version)", "(Acoustic Version)")]
    [InlineData("Song [Remastered]", "[Remastered]")]
    [InlineData("Song (2011 Remaster)", "(2011 Remaster)")]
    [InlineData("Song (Taylor's Version)", "(Taylor's Version)")]
    [InlineData("Song (Instrumental)", "(Instrumental)")]
    [InlineData("Song (Demo)", "(Demo)")]
    [InlineData("Record (Deluxe Edition)", "(Deluxe Edition)")]
    [InlineData("Record (10th Anniversary Reissue)", "(10th Anniversary Reissue)")]
    public void A_version_note_is_an_aside(string title, string aside)
    {
        Assert.Equal([aside], Asides(title));
    }

    /// <summary>Split on everything that is not a letter or a digit, so a phrase whose qualifier is
    /// joined to punctuation still matches a word of the vocabulary.</summary>
    [Fact]
    public void Punctuation_does_not_hide_the_word()
    {
        Assert.Equal(["(12\" Mix)"], Asides("Blue Monday (12\" Mix)"));
    }

    [Fact]
    public void The_brackets_stay_on_the_aside()
    {
        Assert.Equal(["(Live at Abbey Road Studios)"], Asides("Crack Music (Live at Abbey Road Studios)"));
    }

    // ── who is on the recording is never an aside ──

    [Theory]
    [InlineData("Touch the Sky (ft. Lupe Fiasco)")]
    [InlineData("Touch the Sky (feat. Lupe Fiasco)")]
    [InlineData("Touch the Sky (featuring Lupe Fiasco)")]
    [InlineData("Touch the Sky (ft Lupe Fiasco)")]
    [InlineData("Touch the Sky (with Lupe Fiasco)")]
    [InlineData("Touch the Sky (w/ Lupe Fiasco)")]
    public void A_featuring_credit_is_not_an_aside(string title)
    {
        Assert.Empty(Asides(title));
        Assert.Equal(title, Name(title));
    }

    /// <summary>The credit rule matters here: without it the vocabulary would fade a guest whose
    /// NAME happens to be one of its words.</summary>
    [Theory]
    [InlineData("Song (feat. Radio Slave)")]
    [InlineData("Song (ft. The Mix Masters)")]
    public void A_guest_named_after_a_qualifier_is_still_a_credit(string title)
    {
        Assert.Empty(Asides(title));
    }

    /// <summary>The test is applied to each GROUP, not to the line: the venue reads the same way on
    /// every line of the record whether or not that track has a guest on it.</summary>
    [Fact]
    public void A_credit_does_not_protect_the_rest_of_the_line()
    {
        var parts = TitleAsides.Split("Touch the Sky (ft. Lupe Fiasco) [Live at Abbey Road Studios]");

        Assert.Equal(
            [new TitlePart("Touch the Sky (ft. Lupe Fiasco) ", false),
             new TitlePart("[Live at Abbey Road Studios]", true)],
            parts);
    }

    /// <summary>The mark has to end where the next word begins, or a song called Feathers is read as a
    /// credit for a guest called "hers".</summary>
    [Fact]
    public void A_word_that_merely_starts_like_a_credit_is_judged_on_the_vocabulary()
    {
        Assert.Empty(Asides("Some Song (Feathers)"));
        Assert.Equal(["(Feathers Remix)"], Asides("Some Song (Feathers Remix)"));
    }

    // ── the shapes a tagger writes ──

    [Fact]
    public void A_plain_name_is_one_part()
    {
        var part = Assert.Single(TitleAsides.Split("Mame Bamba"));

        Assert.Equal("Mame Bamba", part.Text);
        Assert.False(part.Aside);
    }

    [Fact]
    public void An_empty_title_has_no_parts()
    {
        Assert.Empty(TitleAsides.Split(""));
        Assert.Empty(TitleAsides.Split(null));
    }

    [Fact]
    public void Every_version_note_in_the_line_is_marked_not_just_the_last()
    {
        var parts = TitleAsides.Split("Rumours (Deluxe) [Remastered]");

        Assert.Equal(["(Deluxe)", "[Remastered]"], parts.Where(p => p.Aside).Select(p => p.Text));
        Assert.Equal("Rumours", Name("Rumours (Deluxe) [Remastered]").Trim());
    }

    /// <summary>Unlike the album page's heading, a version note in the MIDDLE is marked: it is only
    /// being drawn faintly, not moved, so the name still reads in its own order.</summary>
    [Fact]
    public void A_version_note_in_the_middle_is_an_aside()
    {
        var parts = TitleAsides.Split("(Live) Morning Glory?");

        Assert.Equal(
            [new TitlePart("(Live)", true), new TitlePart(" Morning Glory?", false)],
            parts);
    }

    /// <summary>Not a version note, so the name is shown whole, as Oasis titled it.</summary>
    [Fact]
    public void A_name_that_opens_with_a_bracket_is_left_whole()
    {
        var part = Assert.Single(TitleAsides.Split("(What's the Story) Morning Glory?"));

        Assert.Equal("(What's the Story) Morning Glory?", part.Text);
        Assert.False(part.Aside);
    }

    [Fact]
    public void A_group_with_brackets_inside_it_is_one_aside()
    {
        Assert.Equal(["(Live (1971))"], Asides("Baba O'Riley (Live (1971))"));
    }

    [Fact]
    public void An_unclosed_bracket_is_part_of_the_name()
    {
        var part = Assert.Single(TitleAsides.Split("Rumours (Deluxe"));

        Assert.Equal("Rumours (Deluxe", part.Text);
        Assert.False(part.Aside);
    }

    /// <summary>A tagging accident, not a note. Nothing to fade, and nothing to delete either.</summary>
    [Fact]
    public void An_empty_group_is_not_an_aside()
    {
        var part = Assert.Single(TitleAsides.Split("Rumours ()"));

        Assert.Equal("Rumours ()", part.Text);
        Assert.False(part.Aside);
    }

    // ── taking the credit off the surfaces that name the guest on the next line ──

    [Fact]
    public void A_bracketed_credit_comes_off_the_title()
    {
        Assert.Equal(
            "Soul Got out of the Box",
            TitleAsides.WithoutCredits("Soul Got out of the Box (feat. Portugal. The Man)"));
    }

    [Theory]
    [InlineData("Song (feat. Guest)")]
    [InlineData("Song (featuring Guest)")]
    [InlineData("Song [feat. Guest]")]
    [InlineData("Song (ft. Guest)")]
    [InlineData("Song (ft Guest)")]
    [InlineData("Song (with Guest)")]
    [InlineData("Song (w/ Guest)")]
    public void Every_bracketed_form_of_the_credit_comes_off(string title)
    {
        Assert.Equal("Song", TitleAsides.WithoutCredits(title));
    }

    /// <summary>A bracket has already said the phrase is a note. Bare in a line it has not, so only the
    /// unmistakable marks count: "Dancing with Myself" is a song, not a duet.</summary>
    [Theory]
    [InlineData("Song feat. Guest", "Song")]
    [InlineData("Song featuring Guest", "Song")]
    [InlineData("Song ft. Guest", "Song")]
    [InlineData("Song - feat. Guest", "Song")]
    [InlineData("Dancing with Myself", "Dancing with Myself")]
    [InlineData("Defeat. The End", "Defeat. The End")]
    public void An_unbracketed_credit_has_to_be_unmistakable(string title, string left)
    {
        Assert.Equal(left, TitleAsides.WithoutCredits(title));
    }

    /// <summary>The credit is a run of names; a bracket after it is a different fact and survives.</summary>
    [Fact]
    public void A_bare_credit_stops_at_the_next_bracket()
    {
        Assert.Equal("Song (Live)", TitleAsides.WithoutCredits("Song feat. Guest (Live)"));
    }

    [Fact]
    public void A_version_note_beside_the_credit_survives()
    {
        Assert.Equal(
            "Touch the Sky [Live at Abbey Road Studios]",
            TitleAsides.WithoutCredits("Touch the Sky (ft. Lupe Fiasco) [Live at Abbey Road Studios]"));
    }

    /// <summary>The seam a removal leaves: no double space where the credit stood between two halves of
    /// a name, and no dash left hanging off the end of one.</summary>
    [Theory]
    [InlineData("Song (feat. Guest) Reprise", "Song Reprise")]
    [InlineData("Song, feat. Guest", "Song")]
    [InlineData("Song  (feat. Guest)  ", "Song")]
    public void The_seam_closes_up(string title, string left)
    {
        Assert.Equal(left, TitleAsides.WithoutCredits(title));
    }

    [Theory]
    [InlineData("My People (Samay nit)")]
    [InlineData("Undecided (deep radio mix)")]
    [InlineData("Mame Bamba")]
    [InlineData("(What's the Story) Morning Glory?")]
    [InlineData("")]
    public void A_title_with_no_credit_is_returned_as_it_was(string title)
    {
        Assert.Equal(title, TitleAsides.WithoutCredits(title));
    }

    [Fact]
    public void An_untitled_track_survives_the_removal()
    {
        Assert.Equal("", TitleAsides.WithoutCredits(null));
    }

    /// <summary>A song filed under nothing but its credit is shown as it was tagged, not as an empty
    /// line.</summary>
    [Fact]
    public void A_title_that_is_only_a_credit_is_left_alone()
    {
        Assert.Equal("(feat. Guest)", TitleAsides.WithoutCredits("(feat. Guest)"));
    }

    // ── reading the guests out of the title ──

    /// <summary>Vultures 2 files this under album artist and track artist "Kanye West &amp; Ty Dolla
    /// $ign" alike. The three guests are in the song's name and nowhere else in the file.</summary>
    [Fact]
    public void The_title_gives_up_who_it_says_is_on_the_recording()
    {
        Assert.Equal(
            ["Don Toliver, Kodak Black & Playboi Carti"],
            TitleAsides.Credits("Field Trip (ft. Don Toliver, Kodak Black & Playboi Carti)"));
    }

    /// <summary>A phrase, not a list of names: where one name ends is decided in one place, on every
    /// artist field the app reads, and this is not a second place.</summary>
    [Fact]
    public void The_credit_comes_back_whole_for_the_splitter_to_judge()
    {
        Assert.Equal(["Earth, Wind & Fire"], TitleAsides.Credits("Song (feat. Earth, Wind & Fire)"));
    }

    [Theory]
    [InlineData("Song (feat. Guest)")]
    [InlineData("Song (featuring Guest)")]
    [InlineData("Song [ft. Guest]")]
    [InlineData("Song (with Guest)")]
    [InlineData("Song (w/ Guest)")]
    [InlineData("Song feat. Guest")]
    [InlineData("Song (feat: Guest)")]
    public void The_mark_is_cut_off_the_front_of_the_credit(string title)
    {
        Assert.Equal(["Guest"], TitleAsides.Credits(title));
    }

    [Fact]
    public void A_title_that_names_nobody_gives_up_nobody()
    {
        Assert.Empty(TitleAsides.Credits("Undecided (deep radio mix)"));
        Assert.Empty(TitleAsides.Credits("My People (Samay nit)"));
        Assert.Empty(TitleAsides.Credits("Dancing with Myself"));
        Assert.Empty(TitleAsides.Credits(null));
    }

    /// <summary>A version note beside the credit is not a person.</summary>
    [Fact]
    public void Only_the_credit_is_read_as_a_credit()
    {
        Assert.Equal(
            ["Lupe Fiasco"],
            TitleAsides.Credits("Touch the Sky (ft. Lupe Fiasco) [Live at Abbey Road Studios]"));
    }

    [Fact]
    public void Two_credits_on_one_line_both_come_back()
    {
        Assert.Equal(
            ["Guest One", "Guest Two"],
            TitleAsides.Credits("Song (feat. Guest One) (with Guest Two)"));
    }

    /// <summary>An empty credit names nobody, and a card about nobody is not worth showing.</summary>
    [Fact]
    public void A_credit_with_no_name_after_it_is_dropped()
    {
        Assert.Empty(TitleAsides.Credits("Song (feat.)"));
    }

    /// <summary>Whatever is marked, the characters come back in order and none are lost: the line is
    /// being recoloured, not rewritten.</summary>
    [Theory]
    [InlineData("Heard 'Em Say (ft. Adam Levine) [Live at Abbey Road Studios]")]
    [InlineData("Undecided (deep radio mix)")]
    [InlineData("My People (Samay nit)")]
    [InlineData("(What's the Story) Morning Glory?")]
    [InlineData("Rumours ()")]
    [InlineData("Plain Name")]
    public void The_parts_put_the_line_back_together(string title)
    {
        Assert.Equal(title, string.Concat(TitleAsides.Split(title).Select(p => p.Text)));
    }

    // ── the wall, where the edition comes out altogether ──

    /// <summary>A tile is a sleeve with two short lines under it. The drawer it opens still shows the
    /// edition, faded and in its place: it is moved, not hidden.</summary>
    [Theory]
    [InlineData("Watch the Throne (Deluxe Edition)", "Watch the Throne")]
    [InlineData("Rumours (Deluxe) [Remastered]", "Rumours")]
    [InlineData("Undecided (deep radio mix)", "Undecided")]
    [InlineData("Kid A [Remastered]", "Kid A")]
    public void The_wall_prints_the_name_without_its_edition(string title, string shown)
    {
        Assert.Equal(shown, TitleAsides.NameOnly(title));
    }

    /// <summary>What was never an aside is never removed: a song's other name, a credit, a bare title.</summary>
    [Theory]
    [InlineData("My People (Samay nit)")]
    [InlineData("The Guide (Wommat)")]
    [InlineData("Touch the Sky (ft. Lupe Fiasco)")]
    [InlineData("(What's the Story) Morning Glory?")]
    [InlineData("Arrival")]
    [InlineData("")]
    public void A_title_with_no_edition_is_untouched(string title)
    {
        Assert.Equal(title, TitleAsides.NameOnly(title));
    }

    /// <summary>An aside in the middle leaves a seam, and the seam is closed up.</summary>
    [Fact]
    public void The_seam_an_aside_leaves_is_closed()
    {
        Assert.Equal("Morning Glory?", TitleAsides.NameOnly("(Live) Morning Glory?"));
    }

    /// <summary>A record whose whole name is an edition keeps it: a blank tile is worse than a long
    /// one.</summary>
    [Fact]
    public void A_title_that_is_all_edition_keeps_it()
    {
        Assert.Equal("(Remastered)", TitleAsides.NameOnly("(Remastered)"));
    }
}
