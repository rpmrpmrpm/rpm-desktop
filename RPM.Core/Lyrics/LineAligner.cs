namespace RPM.Core.Lyrics;

/// <summary>What the model heard in one window: its scores, frame by frame, over its alphabet.</summary>
public readonly record struct HeardWindow(float[] Scores, int Frames, int Vocab);

/// <summary>Runs the model over one window of mono audio at its own rate. Returns null when it cannot.</summary>
public delegate HeardWindow? HearWindow(ReadOnlySpan<float> window);

/// <summary>
/// Places one lyric line's words on the recording.
///
/// <para>This does the steps that <c>LyricAlignment</c> in the desktop app does internally. They are kept
/// here so that the server-side measurement applies the same rule. The steps are: choose the part of the
/// recording to search (<see cref="LyricWindow"/>), run the model over it, find the most likely placement
/// of the sheet's letters (<see cref="CtcAligner"/>), check whether a parked word is doubtful
/// (<see cref="LyricDoubt"/>), share out the tail that ran out of room (<see cref="Rescue"/>), and fill in
/// the words the model could not spell (<see cref="LyricTiming.Whole"/>).</para>
/// </summary>
public static class LineAligner
{
    /// <summary>What placing a line came to.</summary>
    public abstract record Outcome
    {
        private Outcome() { }

        /// <summary>The words, in the reader's split of the line. <paramref name="Rescued"/> is true when a
        /// tail was shared out. <paramref name="Spelled"/> is how many words the model placed itself.</summary>
        public sealed record Placed(IReadOnlyList<WordSpan> Words, double Window, double Wall, bool Rescued, int Spelled) : Outcome;

        /// <summary>The placement contradicted itself: a word was parked on a hold, and the words after it were
        /// crushed. <paramref name="Raw"/> is that placement, for the log.</summary>
        public sealed record Doubted(IReadOnlyList<WordSpan> Raw) : Outcome;

        /// <summary>Nothing to place. There was no spelling, the window was outside the recording, or the
        /// model did not read it. <paramref name="Why"/> says which, for the log.</summary>
        public sealed record Nothing(string Why) : Outcome;
    }

    /// <summary>Places line <paramref name="line"/> of <paramref name="lines"/>. Its text as shown is
    /// <paramref name="text"/>. The audio is <paramref name="mono"/>, the whole recording at
    /// <paramref name="sampleRate"/>.</summary>
    public static Outcome Place(
        float[] mono, int sampleRate, int samplesPerFrame,
        IReadOnlyList<LyricLine> lines, int line, string text,
        CtcVocabulary alphabet, HearWindow hear)
    {
        if (line < 0 || line >= lines.Count || string.IsNullOrWhiteSpace(text))
            return new Outcome.Nothing("no text");

        var spelling = alphabet.Spell(text);
        if (spelling is null)
            return new Outcome.Nothing("no spelling");

        var from = lines[line].Seconds;
        var until = line + 1 < lines.Count ? lines[line + 1].Seconds : double.MaxValue;
        var window = LyricWindow.For(text, from, until);
        var opens = window.Opens;

        var at = (int)(opens * sampleRate);
        if (at < 0 || at >= mono.Length)
            return new Outcome.Nothing("window outside the record");
        var samples = Math.Min((int)(window.Length * sampleRate), mono.Length - at);

        if (hear(mono.AsSpan(at, samples)) is not { } heard)
            return new Outcome.Nothing("model did not read");

        var aligned = CtcAligner.Align(heard.Scores, heard.Frames, heard.Vocab, spelling.Tokens, alphabet.Blank);
        var words = CtcVocabulary.Words(spelling, aligned);
        if (words.Count == 0)
            return new Outcome.Nothing("no words aligned");

        // Frames are turned back into times in the recording. The model was only shown the window.
        var seconds = (double)samplesPerFrame / sampleRate;
        var placed = new WordSpan[words.Count];
        for (var i = 0; i < words.Count; i++)
            placed[i] = new WordSpan(
                words[i].Start, words[i].Length,
                opens + words[i].FromFrame * seconds,
                opens + (words[i].ToFrame + 1) * seconds);

        // Judged on the raw placement, before a tail is shared out. See LyricDoubt.
        if (LyricDoubt.Parked(placed))
            return new Outcome.Doubted(placed);

        var wall = opens + heard.Frames * seconds;
        var rescued = Rescue(placed, wall);
        var told = LyricTiming.Whole(rescued, text, from, until, wall);
        return new Outcome.Placed(told, window.Length, wall, !ReferenceEquals(rescued, placed), rescued.Length);
    }

    // When the model runs out of room.
    //
    // Forced alignment must place every word somewhere. So a line that runs past its window parks on one word
    // for a long time, and stacks the rest against the far wall a few frames apart. The words before the fault
    // are placed properly and kept. Only the run from the fault onwards is spread again across the time it has
    // room for.

    /// <summary>How close to the window's end the last word must finish before the tail is distrusted.</summary>
    private const double AgainstTheWall = 0.15;

    /// <summary>Below this length, a word has been parked, not sung.</summary>
    private const double Crammed = 0.15;

    /// <summary>At or above this length, a word is the long hold that usually comes before a crammed tail.</summary>
    private const double Parked = 2.0;

    /// <summary>
    /// Spreads again the tail of a line that the model could not place. Only a line whose last word ends
    /// against the wall is changed.
    ///
    /// <para>The walk goes back over crammed words and long holds, and stops at the first word of a normal
    /// length. That word and the ones after it are shared out by length up to the wall. Words before that
    /// point keep their placement.</para>
    /// </summary>
    public static WordSpan[] Rescue(WordSpan[] placed, double wall)
    {
        if (placed.Length == 0 || wall - placed[^1].To >= AgainstTheWall)
            return placed;

        var first = placed.Length - 1;
        while (first > 0)
        {
            var held = placed[first - 1].To - placed[first - 1].From;
            if (held > Crammed && held < Parked)
                break;
            first--;
        }

        var opens = placed[first].From;
        if (wall - opens <= 0)
            return placed;

        var chars = 0;
        for (var i = first; i < placed.Length; i++)
            chars += placed[i].Length;

        var rescued = (WordSpan[])placed.Clone();
        var soFar = 0;
        for (var i = first; i < placed.Length; i++)
        {
            var start = opens + (wall - opens) * soFar / Math.Max(1, chars);
            soFar += placed[i].Length;
            var end = opens + (wall - opens) * soFar / Math.Max(1, chars);
            rescued[i] = placed[i] with { From = start, To = end };
        }

        return rescued;
    }
}
