namespace RPM.Core.Lyrics;

/// <summary>
/// The second voice in a lyric line: the words a sheet puts in round brackets. The Android phone app's
/// <c>LyricVoices</c> has the same rules.
///
/// <para>Sheets write two singers as one line, with the second singer in brackets. Sometimes the bracket
/// closes the line: "It all falls down (and when it falls down, who you gon' call now?)". Sometimes it is
/// scattered through the line: "Oh, when it all (uh)". Left inline, this is one line of two people's words.
/// It runs off the column and wraps into the verse below, and nothing on the screen says which half belongs
/// to whom. So a screen shows the bracketed words on a smaller row of their own under the line (<see
/// cref="Groups"/>), without the brackets.</para>
///
/// <para>A line bracketed from end to end is a backing vocal on its own (<see cref="IsAside"/>). It has no
/// verse to sit under, so a screen shows it under the line before it while it is sung (see <see
/// cref="LyricAside"/>).</para>
///
/// <para>Pure, and in Core, so tests can check the rules without a window.</para>
/// </summary>
public static class LyricVoices
{
    /// <summary>How much smaller a second voice is drawn than the verse it answers.</summary>
    public const double Scale = 0.76;

    /// <summary>Whether a line is bracketed from end to end, with one bracket pair around the whole line.
    /// This is how every sheet writes a backing vocal. A line that only CONTAINS brackets is not: "I wish I
    /// understood (yeah)" is the verse, with a shout in it.</summary>
    public static bool IsAside(string? text)
    {
        var line = text?.Trim();
        return line is { Length: > 2 } && line[0] == '(' && line[^1] == ')'
               && line.IndexOf(')', 1) == line.Length - 1;   // One bracket pair around the whole line.
    }

    /// <summary>
    /// Every bracketed group of a line, in the order they were written, as half-open ranges from the opening
    /// bracket to after the closing one.
    ///
    /// <para>Only the outermost brackets count: a group inside a group belongs to it. A group must have
    /// words inside it, and the line must have words outside its groups. Otherwise there is no second voice.
    /// A line bracketed from end to end is not this case (see <see cref="IsAside"/>).</para>
    ///
    /// <para><b>Every group counts, however short.</b> An ad-lib is the same kind of thing as a sung answer.
    /// "(uh)" is a second voice in the room, just as "(come on, come on)" is. Keeping the short groups
    /// inline while the long ones moved would put one voice in two places on one screen.</para>
    /// </summary>
    public static IReadOnlyList<(int Open, int Close)> Groups(string? text)
    {
        var found = new List<(int, int)>();
        if (text is not { Length: > 2 } line || IsAside(line))
            return found;

        var depth = 0;
        var open = -1;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '(')
            {
                if (depth++ == 0)
                    open = i;
            }
            else if (line[i] == ')' && depth > 0 && --depth == 0 && open >= 0)
            {
                if (line[(open + 1)..i].Trim().Length > 0)
                    found.Add((open, i + 1));
                open = -1;
            }
        }

        return found.Count > 0 && line.Length > found.Sum(g => g.Item2 - g.Item1) ? found : [];
    }

    /// <summary>The second voice's row for a line: its bracketed groups, without the brackets. Words of one
    /// group are joined by a space and two groups by a comma, so two answers written apart do not read as
    /// one.</summary>
    public static string AsideText(string text, IReadOnlyList<(int Open, int Close)> groups)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(groups);
        return string.Join(", ", groups.Select(g => text[(g.Open + 1)..(g.Close - 1)].Trim()));
    }

    /// <summary>The verse of a line: the line with its bracketed groups taken out, and the double spaces
    /// that leaves behind tidied away.</summary>
    public static string Verse(string text, IReadOnlyList<(int Open, int Close)> groups)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(groups);

        var verse = new System.Text.StringBuilder(text.Length);
        var at = 0;
        foreach (var (open, close) in groups)
        {
            verse.Append(text[at..open]);
            at = close;
        }
        verse.Append(text[at..]);

        return string.Join(' ', verse.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    /// <summary>A backing line without the brackets round it, as its row shows it.</summary>
    public static string Unbracketed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var line = text.Trim();
        return IsAside(line) ? line[1..^1].Trim() : line;
    }
}
