using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Tests;

/// <summary>
/// Half a character, held back until it is whole.
///
/// <para>Typing an emoji into a field can end the app. Windows types a character outside the basic plane in
/// two keystrokes. If the host's TextBox writes and measures the first half on its own, it throws where
/// nothing can catch it. This test checks the rule that keeps a fragment out of a box, while still
/// letting the character be typed. See TextBoxSurrogates.</para>
/// </summary>
public class TextBoxSurrogatesTests
{
    private const string Grin = "\U0001F600";           // 😀, two chars: D83D DE00
    private const char First = '\uD83D';                // …its first half, as the first keystroke sends it
    private const char Second = '\uDE00';               // …and its second

    // ── the ordinary edit, which must not be touched ──

    /// <summary>Every line of every ordinary edit goes through this, and none of it is halves. An accented
    /// letter is one character. "é" is not the thing that breaks.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Beyoncé - Halo")]
    [InlineData("Sigur Rós")]
    [InlineData("Tchaikovsky: Symphony No. 6 \"Pathétique\"")]
    [InlineData("102.7 KIIS FM")]
    public void Ordinary_text_passes_through_untouched(string text)
    {
        var read = TextBoxSurrogates.Read(text, null);

        Assert.Equal(text, read.Text);
        Assert.Null(read.Held);
        Assert.Equal(-1, read.Caret);
    }

    /// <summary>A character already whole is whole: the guard is about fragments, not about emoji.
    /// </summary>
    [Theory]
    [InlineData("Sun 😀")]
    [InlineData("😀")]
    [InlineData("😀 in front and 🎵 behind")]
    public void A_whole_character_passes_through_untouched(string text)
    {
        var read = TextBoxSurrogates.Read(text, null);

        Assert.Equal(text, read.Text);
        Assert.Null(read.Held);
    }

    // ── the two keystrokes ──

    /// <summary>The first keystroke. The half is kept back rather than written, so the box is never
    /// asked to measure it.</summary>
    [Fact]
    public void The_first_half_is_held_and_not_written()
    {
        var read = TextBoxSurrogates.Read("abc" + First, null);

        Assert.Equal("abc", read.Text);
        Assert.Equal(First, read.Held);
        Assert.Equal(-1, read.Caret);
    }

    /// <summary>The second keystroke, with the first half in hand: the character is whole, and it goes
    /// in where it was typed with the caret after it.</summary>
    [Fact]
    public void The_second_half_completes_the_character()
    {
        var read = TextBoxSurrogates.Read("abc" + Second, First);

        Assert.Equal("abc" + Grin, read.Text);
        Assert.Null(read.Held);
        Assert.Equal(5, read.Caret);        // after the two chars the character is written as
    }

    /// <summary>The pair typed in the middle of a line, which is where a title is usually edited.
    /// </summary>
    [Fact]
    public void A_character_typed_into_the_middle_lands_where_it_was_typed()
    {
        var first = TextBoxSurrogates.Read("Sun" + First + " Ra", null);
        Assert.Equal("Sun Ra", first.Text);
        Assert.Equal(First, first.Held);

        var second = TextBoxSurrogates.Read("Sun" + Second + " Ra", first.Held);
        Assert.Equal("Sun" + Grin + " Ra", second.Text);
        Assert.Equal(5, second.Caret);
        Assert.Null(second.Held);
    }

    /// <summary>What is already in the box stays in it while a new character is typed beside it.
    /// </summary>
    [Fact]
    public void Characters_already_there_survive_the_typing_of_another()
    {
        var read = TextBoxSurrogates.Read(Grin + "x" + Second, First);

        Assert.Equal(Grin + "x" + Grin, read.Text);
        Assert.Null(read.Held);
    }

    // ── halves that will never be whole ──

    /// <summary>A second half with no first half is not a character. It is dropped rather than written,
    /// because a box measured holding one throws exactly as it does on the other half.</summary>
    [Fact]
    public void A_second_half_with_nothing_held_is_dropped()
    {
        var read = TextBoxSurrogates.Read("abc" + Second, null);

        Assert.Equal("abc", read.Text);
        Assert.Null(read.Held);
    }

    /// <summary>A first half with the rest of the line after it is the ordinary case, not the odd one:
    /// a title is edited in the middle. It comes out of the text and is held for its other half.
    /// </summary>
    [Fact]
    public void A_first_half_in_the_middle_is_held_like_any_other()
    {
        var read = TextBoxSurrogates.Read("ab" + First + "cd", null);

        Assert.Equal("abcd", read.Text);
        Assert.Equal(First, read.Held);
    }

    /// <summary>A half that never gets its other half, such as a paste cut through a character, leaves the
    /// text whole. That is the only thing that matters. It is dropped at the next change.</summary>
    [Fact]
    public void A_half_that_is_never_completed_leaves_the_text_whole()
    {
        var broken = TextBoxSurrogates.Read("ab" + First + "cd", null);
        var next = TextBoxSurrogates.Read("ab" + "cde", broken.Held);

        Assert.Equal("abcde", next.Text);
        Assert.Null(next.Held);
    }

    /// <summary>A half being held is forgotten the moment something else is typed: it belongs to the
    /// keystroke that followed it, and nothing else.</summary>
    [Fact]
    public void A_held_half_does_not_attach_itself_to_the_next_thing_typed()
    {
        var read = TextBoxSurrogates.Read("abcd", First);

        Assert.Equal("abcd", read.Text);
        Assert.Null(read.Held);
        Assert.Equal(-1, read.Caret);
    }

    [Fact]
    public void Nothing_is_nothing()
    {
        var read = TextBoxSurrogates.Read(null, null);

        Assert.Equal("", read.Text);
        Assert.Null(read.Held);
    }

    // ── cutting to fit ──

    /// <summary>A paste into a box with a limit is cut to fit, and the cut may not fall between the
    /// halves of a character: one char shorter rather than one character broken.</summary>
    [Fact]
    public void A_cut_that_would_fall_inside_a_character_takes_one_char_less()
    {
        Assert.Equal(3, TextBoxSurrogates.Fits("abc" + Grin, 4));   // 4 would keep only the first half
        Assert.Equal(5, TextBoxSurrogates.Fits("abc" + Grin, 5));   // 5 keeps the whole of it
    }

    /// <summary>And an ordinary cut is exactly where it was asked for.</summary>
    [Theory]
    [InlineData("abcdef", 3, 3)]
    [InlineData("abcdef", 6, 6)]
    [InlineData("abcdef", 9, 6)]
    [InlineData("abcdef", 0, 0)]
    [InlineData("abcdef", -2, 0)]
    public void An_ordinary_cut_is_where_it_was_asked_for(string text, int room, int expected)
        => Assert.Equal(expected, TextBoxSurrogates.Fits(text, room));
}
