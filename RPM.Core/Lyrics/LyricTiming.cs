namespace RPM.Core.Lyrics;

/// <summary>One word of a line: where it sits in the text, and the time range it is sung over.
/// <paramref name="Start"/> and <paramref name="Length"/> index the exact string that was passed in. A
/// view can slice its displayed text with them and never needs a second copy of the words.</summary>
public readonly record struct WordSpan(int Start, int Length, double From, double To);

/// <summary>
/// Which word of a line is being sung, for a reader who follows along word by word.
///
/// <para><b>Two sources, and they are not equally good.</b> An enhanced LRC stamps every word, so
/// nothing has to be worked out: the file gives the times. Few sheets carry word times. Of the sheets
/// LRCLIB serves, none has been seen so far. So the usual source is the second one. It spreads the line
/// across its words by their length. That is an estimate. Even on an evenly sung line it lands close,
/// but a held note, a rest in the middle of a line, or a melisma does not. It is right about which half
/// of the line is being sung, and only roughly right about which word.</para>
///
/// <para>That is why the highlight is a fade on each word, not a wipe across the letters. A wipe would
/// state a position inside a word to the pixel, thirty times a second, from a number nobody measured. A
/// word that brightens as the singer reaches it shows the same true thing: the singer is about here.
/// It does not claim more precision than the data has.</para>
///
/// <para>Pure, and in Core, so tests can check the rule without a window.</para>
/// </summary>
public static class LyricTiming
{
    /// <summary>
    /// The slowest rate at which a line is taken to be sung, in characters per second. This sets the
    /// longest a sweep may run.
    ///
    /// <para>An LRC gives the start of each line and never its end. The only end available is the start of
    /// the next line. Between a verse and a chorus that can be half a minute of guitar. Spread across all
    /// of it, a four-word line would crawl one word every eight seconds, which looks like the sheet has
    /// stalled. Past this rate the sweep finishes, and the line stays lit. That matches what the ear
    /// expects: the words were sung, and then the singing stopped.</para>
    ///
    /// <para>The value is seven. Continuous sung English runs at about eleven characters per second, so
    /// eleven is the fastest a line is ever sung. A cap of eleven would let the sweep run ahead of the
    /// singer. A line is not sung at the rate its syllables can be spoken. It has breath in it, rests, and
    /// words held for effect. Seven is a line being sung, not read aloud. A sweep that finishes early and
    /// then holds is a small error that nobody notices. A sweep still crawling through a guitar solo is
    /// the failure this cap exists to stop.</para>
    ///
    /// <para>Changing this value does not fix a highlight that arrives late. A late highlight is a question
    /// of how each word is lit, not of how fast the line is taken to be sung.</para>
    /// </summary>
    private const double SlowestChars = 7.0;

    /// <summary>
    /// The words of <paramref name="text"/> and when each is sung, given the line's own start time and the
    /// next line's. Empty for a line with nothing to sing.
    /// </summary>
    /// <param name="text">The line as shown: already masked and already unstamped. The offsets returned
    /// index this string, so a view slices what it draws, not what was parsed.</param>
    /// <param name="from">When the line starts.</param>
    /// <param name="until">When the next line starts, or <see cref="double.PositiveInfinity"/> for the last
    /// line of a sheet. That line has no end except the cap.</param>
    /// <param name="stamped">The line's own word times, where the sheet carried them. They are used as they
    /// are when there is exactly one for each word found here. They are ignored otherwise. A list of times
    /// that does not match the words on screen cannot be matched word for word. A near miss would light the
    /// wrong words with full confidence.</param>
    public static IReadOnlyList<WordSpan> Sweep(
        string? text, double from, double until, IReadOnlyList<LyricWord>? stamped = null)
    {
        var words = Split(text);
        if (words.Count == 0)
            return [];

        var end = End(words, from, until);

        // The file gave a time for every word, so use those.
        if (stamped is not null && stamped.Count == words.Count)
        {
            var told = new WordSpan[words.Count];
            for (var i = 0; i < words.Count; i++)
            {
                var (start, length) = words[i];
                var at = stamped[i].Seconds;
                var next = i + 1 < words.Count ? stamped[i + 1].Seconds : Math.Max(at, end);
                told[i] = new WordSpan(start, length, at, Math.Max(at, next));
            }
            return told;
        }

        // Nobody gave times, so the line's time is shared out by word length.
        //
        // By character count, which is a stand-in for syllables. It is the only measure available without a
        // dictionary. "tomorrow" takes longer to sing than "and", and the length of a word is most of why.
        // Whitespace is not counted, because a line is not sung more slowly for having wide gaps in it.
        var chars = 0;
        foreach (var (_, length) in words)
            chars += length;

        var spans = new WordSpan[words.Count];
        var run = Math.Max(0, end - from);
        var so_far = 0;

        for (var i = 0; i < words.Count; i++)
        {
            var (start, length) = words[i];

            // Each word's share is measured from the START of the line, not added up one word at a time. This
            // stops rounding from drifting. The last word ends exactly at end, however many words there are.
            var opens = from + run * so_far / Math.Max(1, chars);
            so_far += length;
            var closes = from + run * so_far / Math.Max(1, chars);

            spans[i] = new WordSpan(start, length, opens, closes);
        }

        // The line's last word holds on into the time the sweep left over, where the next line is known. A
        // line's last word is often the held note ("...and sends it on"). Lit at singing speed, it sat lit
        // while the voice still carried it. It takes the time left before the next line, up to LastHeld, and
        // ends BeforeNext short of it, so the light is still moving through it while it is sung.
        if (double.IsFinite(until))
        {
            var last = spans[^1];
            var held = Math.Min(until - BeforeNext, last.To + LastHeld);
            if (held > last.To)
                spans[^1] = last with { To = held };
        }

        return spans;
    }

    /// <summary>The longest a line's last word is held for, in seconds, past the singing-speed sweep. The
    /// Android TV app holds it the same.</summary>
    public const double LastHeld = 3.0;

    /// <summary>How far short of the next line a held last word ends, in seconds.</summary>
    public const double BeforeNext = 0.25;

    // Words the aligner cannot spell.
    //
    // A view finds words by splitting on whitespace, so every run of non-whitespace is one word. A forced
    // aligner numbers its answer by its own alphabet, and that alphabet has no digits. "12" and "36-25-34"
    // cannot be spelled in it, so they are missing from its answer. Read by position, one missing word moves
    // every word after it onto its neighbour's timing, and the second half of the line lights up wrongly.
    // Over 19,012 cached lyric lines the two splits disagreed on 288 of them: mostly numbers, then censored
    // words and rows of full stops.
    //
    // So a measured answer is matched back onto this split before anything reads it by position. The match
    // uses the one thing both sides agree on: where the word starts in the line.

    /// <summary>
    /// The line's every word, in reading order. Words the measurement did not cover are filled in from the
    /// gap between their neighbours.
    ///
    /// <para>Words are matched on <see cref="WordSpan.Start"/>. That is an offset into the same text on both
    /// sides, so it means the same thing on each. An index into a list does not. It would mean "the fourth
    /// item each of us kept", and the two lists did not keep the same items.</para>
    ///
    /// <para>A word nobody measured takes the gap between the measured words either side of it. Where several
    /// are in a row, the gap is shared out by length. This keeps the times in order. A guessed time, spread
    /// across the line, and a measured time can disagree. A word dropped between two measured words can then
    /// start before the word before it has finished. A sweep would show that as running backwards.</para>
    ///
    /// <para>Where nothing was dropped, which is almost every line, the measured list is returned as it is.
    /// Where the two do not line up, the measured list is also returned unchanged. A measurement of another
    /// line cannot be put onto this one, and guessing would be worse than returning the measured words as they
    /// are.</para>
    /// </summary>
    /// <param name="measured">What was placed, in order. It may be short.</param>
    /// <param name="text">The line as shown. Both splits index this same string.</param>
    /// <param name="from">The line's own start time. It bounds an unmeasured word at the very start.</param>
    /// <param name="until">The next line's start time. Used for the shape only.</param>
    /// <param name="wall">The end of what was searched. It bounds an unmeasured word at the very end.</param>
    public static IReadOnlyList<WordSpan> Whole(
        IReadOnlyList<WordSpan> measured, string text, double from, double until, double wall)
    {
        // The same split as everything else here, called rather than copied. Two versions of "what a word
        // is" would agree until one of them changed.
        var shape = Sweep(text, from, until);

        // Equal counts mean nothing was dropped. A measurement only ever leaves words out, it never adds one.
        // An ordinary line goes through here unchanged.
        if (shape.Count == 0 || measured.Count == 0 || shape.Count == measured.Count)
            return measured;

        var whole = new WordSpan?[shape.Count];
        var m = 0;
        for (var i = 0; i < shape.Count && m < measured.Count; i++)
            if (measured[m].Start == shape[i].Start && measured[m].Length == shape[i].Length)
                whole[i] = measured[m++];

        // Every measured word must find its place, not just some of them. Two unrelated lines can both have a
        // word at offset zero, so one match proves nothing. A measured word that matches nothing shows the
        // measurement is not for this text. Then there is no honest way to lay it over the line.
        if (m != measured.Count)
            return measured;

        var filled = new WordSpan[shape.Count];
        for (var i = 0; i < shape.Count; i++)
        {
            if (whole[i] is { } known)
            {
                filled[i] = known;
                continue;
            }

            // The whole run of unmeasured words at once, so several in a row share one gap rather than each
            // taking all of it.
            var run = i;
            while (run < shape.Count && whole[run] is null)
                run++;

            var opens = i > 0 ? filled[i - 1].To : from;
            var closes = Math.Max(opens, run < shape.Count ? whole[run]!.Value.From : wall);

            var chars = 0;
            for (var w = i; w < run; w++)
                chars += shape[w].Length;

            // Measured from the start of the run, not added up one at a time, for the same reason as in Sweep.
            // Rounding must not push the last of them off the end of the gap.
            var so_far = 0;
            for (var w = i; w < run; w++)
            {
                var opened = opens + (closes - opens) * so_far / Math.Max(1, chars);
                so_far += shape[w].Length;
                var closed = opens + (closes - opens) * so_far / Math.Max(1, chars);

                filled[w] = shape[w] with { From = opened, To = closed };
            }

            i = run - 1;
        }

        return filled;
    }

    /// <summary>
    /// Where the sweep finishes: at the next line's start time, or where this line would finish at
    /// <see cref="SlowestChars"/>, whichever comes first.
    /// </summary>
    private static double End(IReadOnlyList<(int Start, int Length)> words, double from, double until)
    {
        var chars = 0;
        foreach (var (_, length) in words)
            chars += length;

        var capped = from + chars / SlowestChars;
        return double.IsFinite(until) ? Math.Min(Math.Max(from, until), capped) : capped;
    }

    /// <summary>
    /// The words of a line, as offsets into it: runs of anything that is not whitespace.
    ///
    /// <para>Punctuation stays attached to the word it is next to. "night," is one word, not two. That is
    /// what a reader sees, so it should light as one word.</para></summary>
    private static List<(int Start, int Length)> Split(string? text)
    {
        var words = new List<(int, int)>();
        if (string.IsNullOrWhiteSpace(text))
            return words;

        var at = 0;
        while (at < text.Length)
        {
            while (at < text.Length && char.IsWhiteSpace(text[at]))
                at++;

            var start = at;
            while (at < text.Length && !char.IsWhiteSpace(text[at]))
                at++;

            if (at > start)
                words.Add((start, at - start));
        }

        return words;
    }
}
