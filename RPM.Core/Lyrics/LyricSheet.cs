using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RPM.Core.Lyrics;

/// <summary>One word of a lyric, and the time it is sung. Only an enhanced sheet has these. See
/// <see cref="LyricLine.Words"/>.</summary>
public readonly record struct LyricWord(double Seconds, string Text);

/// <summary>One line of a lyric, and the time it is sung.</summary>
/// <param name="Words">The line's own words with their own times, where the sheet gives them. Null for
/// an ordinary sheet, which only times whole lines. A reader can follow the singer word by word when
/// these exist. When they are null, a view must spread the line across its words itself (see
/// <see cref="LyricTiming"/>). That is an estimate, not a measurement.</param>
public readonly record struct LyricLine(double Seconds, string Text, IReadOnlyList<LyricWord>? Words = null);

/// <summary>
/// A lyric with the timing still attached, read from an LRC sheet.
///
/// <para>Plain lyrics with no timestamps are not a sheet. <see cref="Parse"/> returns <see cref="Empty"/>
/// for them. A caller that wants something to follow along with should be told there is nothing to
/// follow. It should not be given words that never move.</para>
///
/// <para>Blank lines are kept. An LRC file can mark an instrumental break with an empty line at 1:12.
/// That line says that nothing is sung for a while. Dropping such lines would run the verse straight
/// into the chorus, and the highlight would stay on a line nobody is singing.</para>
/// </summary>
public sealed class LyricSheet
{
    /// <summary>No sheet at all. A track with no synced lyric has this.</summary>
    public static readonly LyricSheet Empty = new([], null);

    private LyricSheet(IReadOnlyList<LyricLine> lines, string? source)
    {
        Lines = lines;
        Source = source;
    }

    /// <summary>The lines, in time order, earliest first.</summary>
    public IReadOnlyList<LyricLine> Lines { get; }

    /// <summary>
    /// The LRC text this sheet was read from, kept as it was.
    ///
    /// <para>A cache that keeps the original text can be read again when the parser improves. A cache
    /// that keeps only the parsed lines would have to be thrown away to get that benefit. The original
    /// text is also what a person can open and check.</para>
    /// </summary>
    public string? Source { get; }

    /// <summary>Whether there is anything here worth following. False for <see cref="Empty"/>.</summary>
    public bool IsSynced => Lines.Count > 0;

    // [mm:ss.xx], [mm:ss.xxx] or [mm:ss]. Some writers use a colon before the fraction.
    private static readonly Regex Stamp = new(
        @"\[(?<m>\d{1,3}):(?<s>\d{1,2})(?:[.:](?<f>\d{1,3}))?\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // [offset:+250]. The value is in milliseconds. This is the one metadata tag that changes what is drawn.
    private static readonly Regex Offset = new(
        @"^\s*\[offset:\s*(?<v>[+-]?\d+)\s*\]\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // <mm:ss.xx>, the word-level stamp of enhanced LRC. It marks where inside a line each word falls.
    // Left in, these would show on screen: "<00:12.30>Hello <00:12.90>darkness". Unstamp removes them.
    // They are not noise in the sheet, though. Read before they are removed, they are the only exact
    // answer to which word is being sung now.
    private static readonly Regex WordStamp = new(
        @"\s*<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // The same stamp with named parts, for reading the times instead of removing them.
    private static readonly Regex WordStampAt = new(
        @"<(?<m>\d{1,3}):(?<s>\d{1,2})(?:[.:](?<f>\d{1,3}))?>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // A stamp written where a stamp should not be: at the start of a line without brackets, or in round
    // brackets, which some editors use. The fraction is required for the bare form. That keeps
    // "3:15 in the morning" as a lyric and not as a timestamp.
    private static readonly Regex LooseStamp = new(
        @"^\s*(?:\[\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?\]|\(\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?\)|\d{1,3}:\d{1,2}[.:]\d{1,3})\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// One line of lyric with the timing removed: the word stamps inside it, and a stamp left at its start.
    ///
    /// <para>For text shown on screen. Some files hold stamps in a form the sheet reader cannot use: the
    /// bare form, the round-bracket form, or the word-by-word form. Those would be printed with the timing
    /// still in them. A lyric should never show a timecode.</para>
    /// </summary>
    public static string Unstamp(string? line)
        => line is null or "" ? "" : LooseStamp.Replace(WordStamp.Replace(line, " "), "").Trim();

    /// <summary>
    /// Whether a line has nothing to read on it. It is empty, or it holds only the note symbols that
    /// writers use to mark a break.
    ///
    /// <para>The parser does not use this, because it keeps those lines on purpose. See the note on this
    /// class. This is for a view that decides whether a line gets a row. The two questions are different.
    /// The sheet needs the line so that the timing still lands somewhere. A reader needs a row only where
    /// there are words.</para>
    ///
    /// <para>A note symbol and an empty line mean the same thing to a writer, and both are used, often in
    /// the same file: a note before the first verse, blank lines between verses. A view that drops one
    /// kind and keeps the other leaves gaps in half the songs it shows.</para>
    /// </summary>
    public static bool IsWordless(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        // Counted by code point, not by char. Two of the six note symbols are emoji, and each one is a
        // surrogate pair. Walking chars would split those pairs, and every one of them would count as
        // words.
        foreach (var rune in line.EnumerateRunes())
            if (!Rune.IsWhiteSpace(rune) && !IsNote(rune))
                return false;

        return true;
    }

    /// <summary>The characters that mean nothing is sung here: four musical note symbols from the
    /// Miscellaneous Symbols block, and two emoji. Accidentals are not included. A sharp or a flat is
    /// something a lyric can be about.</summary>
    private static bool IsNote(Rune rune) => rune.Value
        is 0x2669      // ♩ quarter note
        or 0x266A      // ♪ eighth note
        or 0x266B      // ♫ beamed eighth notes
        or 0x266C      // ♬ beamed sixteenth notes
        or 0x1F3B5     // 🎵 musical note
        or 0x1F3B6;    // 🎶 multiple musical notes

    /// <summary>
    /// Reads an LRC sheet. Text that is not one (null, blank, or plain lyrics with no timestamps) returns
    /// <see cref="Empty"/>. That is not an error, and it is the usual case.
    /// </summary>
    public static LyricSheet Parse(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc))
            return Empty;

        var lines = new List<LyricLine>();
        double offset = 0;

        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            // [offset:] shifts the whole sheet. The sign follows the LRC convention, not plain arithmetic.
            // A POSITIVE offset means the words come early, so the times are pulled back by the offset.
            if (Offset.Match(line) is { Success: true } off)
            {
                offset = int.Parse(off.Groups["v"].Value, CultureInfo.InvariantCulture) / 1000.0;
                continue;
            }

            var stamps = Stamp.Matches(line);
            if (stamps.Count == 0)
                continue;   // a metadata tag, or prose in a file that is not really an LRC

            // The words are whatever follows the LAST timestamp. One line can carry several timestamps.
            // That is how a refrain is written once and sung four times.
            var body = line[(stamps[^1].Index + stamps[^1].Length)..];
            var text = Unstamp(body);

            // The word stamps inside the line, read before Unstamp removes them. Only for a line with ONE
            // timestamp. A refrain written once with four timestamps has one set of word times. That can be
            // true for at most one of the four sung versions, and the file does not say which. A guess
            // there would put the highlight on the wrong word in three choruses out of four. That is worse
            // than following the whole line.
            var words = stamps.Count == 1 ? ReadWords(body, offset) : null;

            foreach (Match s in stamps)
                lines.Add(new LyricLine(Math.Max(0, StampSeconds(s) - offset), text, words));
        }

        if (lines.Count == 0)
            return Empty;

        // A sheet with several timestamps per line is out of order by construction. Nothing below may assume
        // otherwise. The search is a binary search.
        lines.Sort(static (a, b) => a.Seconds.CompareTo(b.Seconds));
        return new LyricSheet(lines, lrc);
    }

    /// <summary>
    /// The time a stamp names, in seconds from the start of the recording, before any offset.
    ///
    /// <para>One digit in the fraction means tenths, two mean hundredths, and three mean thousandths. The
    /// number of digits sets the scale, so sheets with two-digit and three-digit fractions are both read
    /// correctly.</para>
    /// </summary>
    private static double StampSeconds(Match s)
    {
        double minutes = double.Parse(s.Groups["m"].Value, CultureInfo.InvariantCulture);
        double seconds = double.Parse(s.Groups["s"].Value, CultureInfo.InvariantCulture);

        double fraction = 0;
        if (s.Groups["f"].Success)
        {
            var f = s.Groups["f"].Value;
            fraction = double.Parse(f, CultureInfo.InvariantCulture) / Math.Pow(10, f.Length);
        }

        return minutes * 60 + seconds + fraction;
    }

    /// <summary>
    /// The words of one enhanced line, each with its own time. Null for an ordinary line with no word
    /// stamps inside it. That is nearly every line of nearly every sheet.
    ///
    /// <para>A word is the text from its own stamp up to the next stamp, trimmed. Text before the first
    /// stamp belongs to no word and is dropped. Some writers put the line's opening stamp outside the
    /// brackets. Any fragment there was never timed.</para>
    /// </summary>
    private static IReadOnlyList<LyricWord>? ReadWords(string body, double offset)
    {
        var stamps = WordStampAt.Matches(body);
        if (stamps.Count == 0)
            return null;

        var words = new List<LyricWord>(stamps.Count);
        for (var i = 0; i < stamps.Count; i++)
        {
            var from = stamps[i].Index + stamps[i].Length;
            var to = i + 1 < stamps.Count ? stamps[i + 1].Index : body.Length;
            var text = body[from..to].Trim();
            if (text.Length > 0)
                words.Add(new LyricWord(Math.Max(0, StampSeconds(stamps[i]) - offset), text));
        }

        return words.Count > 0 ? words : null;
    }

    /// <summary>
    /// The line being sung at this moment, or -1 before the first line. An intro is part of the song, so
    /// the sheet does not claim that the first line has started yet.
    /// </summary>
    /// <remarks>Binary search, because this is asked on every frame.</remarks>
    public int IndexAt(double seconds)
    {
        int lo = 0, hi = Lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Lines[mid].Seconds <= seconds) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return found;
    }

    /// <summary>
    /// This sheet moved by <paramref name="seconds"/>, to where a record sings it: every line and every
    /// word stamp, and no moment before the start of the record. Positive moves the words later.
    ///
    /// <para>The <see cref="Source"/> stays the text as it was written, unmoved. A moved sheet is for
    /// showing. It is not a new answer to keep or to send anywhere.</para>
    /// </summary>
    public LyricSheet Shifted(double seconds)
    {
        if (seconds == 0 || Lines.Count == 0)
            return this;

        static double Moved(double at, double by) => Math.Max(0, at + by);

        var lines = new LyricLine[Lines.Count];
        for (var i = 0; i < lines.Length; i++)
        {
            var line = Lines[i];
            lines[i] = line with
            {
                Seconds = Moved(line.Seconds, seconds),
                Words = line.Words is { } words
                    ? [.. words.Select(w => w with { Seconds = Moved(w.Seconds, seconds) })]
                    : null,
            };
        }
        return new LyricSheet(lines, Source);
    }
}
