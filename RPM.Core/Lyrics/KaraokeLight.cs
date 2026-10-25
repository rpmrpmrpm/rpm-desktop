namespace RPM.Core.Lyrics;

/// <summary>
/// How lit each letter of the sung line is, for karaoke. Pegasus and the Android TV app light a line with
/// this same rule, so a line lights the same on the TV as on the PC.
///
/// <para>The light moves THROUGH a word, letter by letter, rather than switching the word on. A word lit as
/// one thing pops. A word lit a letter at a time reads as a wipe passing over it. A word's moment is shared
/// evenly between its letters, but never slower than <see cref="SlowestGlyph"/> a letter for a word sung in
/// its letters' time. Each letter arrives over <see cref="GlyphSoftness"/> or its own share, whichever is
/// longer, so three or four letters are always part-way lit and the eye sees one soft edge, not a row of
/// lights. The arrival FINISHES on the letter's moment, so the letter is at full strength as it is sung,
/// not after.</para>
///
/// <para><b>A held word is swept over its hold.</b> A word that lasts longer than its letters take to sing
/// is a carried note. The light keeps moving through it for the whole time it is held, at most <see
/// cref="HeldGlyph"/> a letter, so a gap in the timing does not crawl. A light that crossed the word at
/// singing speed and then stopped read as the sweep hanging on the word.</para>
///
/// <para><b>The last word of a line reaches back.</b> A line's last word is where a note is held, and a
/// measurement often catches only its tail: an aligner can put a held "on" in the last 40 ms of its note.
/// So the last word is swept from where the word before it ended, through the whole hold, at up to <see
/// cref="LastGlyph"/> a letter. Any other word shorter than <see cref="ShortestWord"/> borrows time from the
/// pause before it, so it eases in and still ends on its own moment, instead of sitting grey through the
/// pause and then snapping on.</para>
///
/// <para>Letters are counted in reading order over the spans, the gaps between words left out (<see
/// cref="Letters"/>). Pure, and in Core, so tests can check the rule without a window.</para>
/// </summary>
public static class KaraokeLight
{
    /// <summary>How strong a letter is before the light reaches it, on the sung line. It is fainter than
    /// another line, so the eye can see where the voice has got to, but readable, so a reader running ahead
    /// can see where it goes.</summary>
    public const double Rest = 0.42;

    /// <summary>The shortest time the light takes to arrive at one letter, in seconds: the softness of the
    /// edge. A letter of an ordinary sung word lasts forty or fifty milliseconds. At a fifth of a second,
    /// three or four letters are part-way lit at once, and the eye sees one soft edge travelling.</summary>
    public const double GlyphSoftness = 0.20;

    /// <summary>The slowest the light crosses a letter of a word sung in its letters' time, in seconds: the
    /// same seven a second that <see cref="LyricTiming"/> caps a line at.</summary>
    public const double SlowestGlyph = 1.0 / 7.0;

    /// <summary>The slowest the light may cross a letter of a held word, in seconds.</summary>
    public const double HeldGlyph = 0.6;

    /// <summary>The shortest a word is swept over, in seconds. A shorter word borrows time from the pause
    /// before it.</summary>
    public const double ShortestWord = 0.5;

    /// <summary>The slowest the light may cross a letter of a line's last word, in seconds.</summary>
    public const double LastGlyph = 1.5;

    /// <summary>How long a sung line takes to fade back to <see cref="Rest"/> once its last word has been
    /// sung, in seconds, where a screen lets it fade (see <see cref="Light"/>).</summary>
    public const double LineFade = 1.5;

    /// <summary>How many letters the spans hold, which is the size <see cref="Light"/> fills.</summary>
    public static int Letters(IReadOnlyList<WordSpan> spans)
    {
        var letters = 0;
        foreach (var span in spans)
            letters += span.Length;
        return letters;
    }

    /// <summary>
    /// How far the light has arrived at letter <paramref name="c"/> of <paramref name="word"/> at
    /// <paramref name="now"/>, from nought (not yet) to one (lit).
    /// </summary>
    /// <param name="last">Whether the word ends the line, the only place a word is taken as a held
    /// note.</param>
    /// <param name="earliest">Where the word before it ended: a word that reaches back into the pause before
    /// it never reaches past this. For a line's first word, its own start.</param>
    public static double Through(WordSpan word, int c, bool last, double now, double earliest)
    {
        var of = word.Length;

        // A word too short to sweep starts earlier, into the pause before it, but never before the word
        // before it ended.
        var shortest = Math.Max(ShortestWord, of * SlowestGlyph);
        double from;
        if (last)
            from = Math.Max(earliest, Math.Min(word.From, word.To - of * LastGlyph));
        else if (word.To - word.From >= shortest)
            from = word.From;
        else
            from = Math.Max(earliest, Math.Min(word.From, word.To - shortest));

        var held = word.To - from;
        var share = held / Math.Max(1, of);

        // A word sung in its letters' time is crossed at its own rate. A held one is swept over its hold,
        // no slower than the held rate.
        var step = share > SlowestGlyph ? Math.Min(share, last ? LastGlyph : HeldGlyph) : share;
        var opens = from + step * c;
        var arriving = Math.Max(GlyphSoftness, step);
        return Math.Clamp((now - (opens - arriving)) / arriving, 0, 1);
    }

    /// <summary>
    /// The strength of every letter at <paramref name="now"/>, from <see cref="Rest"/> to one, written into
    /// <paramref name="into"/> in reading order. It fills an array the caller keeps, so a frame allocates
    /// nothing.
    /// </summary>
    /// <param name="fades">Whether the line goes back to rest after its last word (<see cref="LineFade"/>).
    /// Without it the line stays lit, for a line that is still the one being sung.</param>
    public static void Light(IReadOnlyList<WordSpan> spans, double now, double[] into, bool fades = true)
    {
        var after = spans.Count == 0 || !fades ? 0 : now - spans[^1].To;
        var fade = 1.0;
        if (after > 0)
        {
            var t = Math.Clamp(after / LineFade, 0, 1);
            fade = 1 - (t * t * (3 - (2 * t)));
        }

        var k = 0;
        for (var w = 0; w < spans.Count; w++)
        {
            var word = spans[w];
            var earliest = w == 0 ? word.From : spans[w - 1].To;
            for (var c = 0; c < word.Length; c++)
            {
                if (k >= into.Length)
                    return;
                into[k++] = Rest + ((1 - Rest) * Through(word, c, w == spans.Count - 1, now, earliest) * fade);
            }
        }
    }
}
