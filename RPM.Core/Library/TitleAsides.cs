namespace RPM.Core.Library;

/// <summary>One stretch of a title, and whether it is an aside. See <see cref="TitleAsides"/>.</summary>
/// <param name="Text">The characters, with the brackets included where this is an aside. The brackets show that
/// the phrase is a note about the recording. On one line there is nothing else to show it with.</param>
/// <param name="Aside">True when this is a note about which recording this is, and not part of what the song is
/// called.</param>
public readonly record struct TitlePart(string Text, bool Aside);

/// <summary>
/// A title cut into the name, and the notes added to it about which recording it is. A list can then draw the
/// notes at less than full strength.
///
/// <para><b>Brackets do not always mean an aside, and that is the main difficulty.</b> Youssou N'Dour's The
/// Guide lists "Leaving (Dem)", "Silence (Tongo)" and "My People (Samay nit)". In each one, the Wolof name of
/// the song is in brackets. One title is "Undecided (deep radio mix)". They are written the same way, but they
/// are opposite things. The Wolof name is what the song is called, and the reader wants it. The mix is a note
/// about which cut this is, and the reader is scanning past it. Fading everything in brackets would fade the
/// record's own titles.</para>
///
/// <para><b>So the test is a vocabulary, not a shape.</b> A group is an aside only when it uses the words that a
/// version is described with: mix, remix, edit, live, remaster, deluxe, and the rest of <see cref="Qualifiers"/>.
/// Everything else stays at full strength. So a phrase this does not recognise is read as part of the name. That
/// is the safe direction to be wrong in. A mix left bright reads as it always did. A song title that is faded
/// tells the reader that their record is called something it is not.</para>
///
/// <para><b>A credit is never an aside,</b> whatever words it contains, because it names somebody on the
/// recording. Without this rule, a guest called Radio Slave or The Mix Masters would be faded by the vocabulary
/// above.</para>
///
/// <para><b>Every group is checked, not only the one at the end.</b> This is where the class differs from
/// <see cref="AlbumTitle"/>. That class decides what to MOVE off the end of a heading. Moving a phrase out of the
/// middle of a name would rewrite the name. This class only decides what to draw faintly, and a phrase drawn
/// faintly stays in its place. Both use <see cref="IsAside"/>, so a phrase that the album page lifts onto its own
/// line is the same phrase that a tracklist fades.</para>
/// </summary>
public static class TitleAsides
{
    /// <summary>
    /// The title in order, with each stretch marked as name or aside. A non-empty title always gives at least one
    /// part. It gives exactly one part where nothing should be drawn faintly. That is the great majority of
    /// titles, and it lets a caller take the simple path and set plain text.
    /// </summary>
    public static IReadOnlyList<TitlePart> Split(string? title)
    {
        var text = title ?? "";
        if (text.Length == 0)
            return [];

        List<TitlePart>? parts = null;
        var plainFrom = 0;

        for (var i = 0; i < text.Length; i++)
        {
            var close = text[i] switch { '(' => ')', '[' => ']', _ => '\0' };
            if (close == '\0')
                continue;

            var end = Closing(text, i, text[i], close);
            if (end < 0)
                continue;   // an opening bracket that never closes is part of the name

            if (!IsAside(text[(i + 1)..end]))
            {
                // Kept in the plain stretch rather than split off as a part of its own. This covers a song's
                // other name, a credit, and an empty group that a tagger left behind. The reader does not need
                // help with any of them.
                i = end;
                continue;
            }

            parts ??= [];
            if (i > plainFrom)
                parts.Add(new TitlePart(text[plainFrom..i], Aside: false));

            parts.Add(new TitlePart(text[i..(end + 1)], Aside: true));
            plainFrom = end + 1;
            i = end;
        }

        if (parts is null)
            return [new TitlePart(text, Aside: false)];

        if (plainFrom < text.Length)
            parts.Add(new TitlePart(text[plainFrom..], Aside: false));

        return parts;
    }

    /// <summary>
    /// The title with its asides removed, not just drawn faintly. For example, "Watch the Throne (Deluxe Edition)"
    /// becomes "Watch the Throne".
    ///
    /// <para>This is for the wall, where a tile is a picture with two short lines under it. The edition is the
    /// least useful thing a reader wants from those lines. The drawer that the tile opens still shows the aside,
    /// faded and in its place. So nothing is hidden. It is only moved to where there is room for it.</para>
    ///
    /// <para>The title is returned unchanged when it has no aside, and when removing the asides would leave
    /// nothing.</para>
    /// </summary>
    public static string NameOnly(string? title)
    {
        var parts = Split(title);
        if (parts.Count < 2)
            return title ?? "";

        var kept = string.Concat(parts.Where(p => !p.Aside).Select(p => p.Text));

        // Closes the seam an aside leaves behind: the double space where one stood in the middle of a name, and the
        // dash or comma that a tagger wrote in front of it.
        var tidied = string.Join(" ", kept.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                           .Trim(' ', '-', '–', ',', ':', '/');

        return tidied.Length > 0 ? tidied : title ?? "";
    }

    /// <summary>
    /// Whether the phrase inside a pair of brackets is a note about which recording this is, and not part of what
    /// the song or the record is called.
    ///
    /// <para>This is public because the album page asks the same question about a heading before it lifts the
    /// phrase onto a line of its own. See <see cref="AlbumTitle"/>. There is one answer, so the two surfaces cannot
    /// disagree about what a bracket means.</para>
    /// </summary>
    /// <param name="inside">The text between the brackets, without them.</param>
    public static bool IsAside(string inside)
    {
        var phrase = inside.Trim();
        if (phrase.Length == 0 || IsCredit(phrase))
            return false;

        foreach (var word in Words(phrase))
            if (Qualifiers.Contains(word))
                return true;

        return false;
    }

    /// <summary>
    /// The title with its featuring credits removed. For example, "Soul Got out of the Box (feat. Portugal. The
    /// Man)" becomes "Soul Got out of the Box".
    ///
    /// <para><b>For the surfaces that name the artist on the line below.</b> The now playing banner and the track
    /// pane both print the credit underneath, such as "Yoko Ono, Portugal. The Man". So the title's own copy of
    /// the credit says nothing that the reader has not already been told. This happens in the one place where the
    /// title is set largest and has the least room. Where the artist is NOT shown beside the song, the credit is
    /// the only thing that names the guest, so it stays.</para>
    ///
    /// <para><b>Inside brackets, any of the marks is accepted. Outside them, only the unmistakable marks are.</b> A
    /// bracket already says that the phrase is a note. So "(with Kate Bush)" can safely be read as a credit.
    /// Without brackets, "with" is not accepted, because "Dancing with Myself" is a song and not a duet. So a
    /// credit without brackets must be written as "feat.", "ft." or "featuring". It runs to the next bracket or the
    /// end of the line.</para>
    ///
    /// <para>The title is returned unchanged when it holds no credit. It is also returned unchanged when removing
    /// the credit would leave nothing. A song filed under only its credit is better shown as it was tagged than as
    /// an empty line.</para>
    /// </summary>
    public static string WithoutCredits(string? title)
    {
        var text = title ?? "";
        if (text.Length == 0)
            return text;

        System.Text.StringBuilder? kept = null;
        var from = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (CreditRun(text, i) is not { } run)
                continue;

            kept ??= new System.Text.StringBuilder(text.Length);
            kept.Append(text, from, i - from);
            from = run.End;
            i = run.End - 1;
        }

        if (kept is null)
            return text;

        kept.Append(text, from, text.Length - from);

        var left = Tidy(kept.ToString());
        return left.Length > 0 ? left : text;
    }

    /// <summary>
    /// Who the title says is on the recording: the phrase after each featuring mark, with the mark itself removed.
    /// "Field Trip (ft. Don Toliver, Kodak Black &amp; Playboi Carti)" gives back one phrase that names three
    /// people.
    ///
    /// <para><b>Phrases, not names.</b> Splitting a credit into separate people is a judgement about where each name
    /// ends. "Earth, Wind &amp; Fire" reads as three names to any splitter that has ever been written. The app
    /// already has one place that makes this judgement, and it applies to every artist field it reads. This method
    /// finds the credit and hands it over. Deciding how many people are in it is the job of
    /// <see cref="RPM.Core.Metadata.Providers.ArtistCredit.Split"/>, as it is for the artist tag.</para>
    ///
    /// <para><b>Why it is worth asking the title at all.</b> On many records the guest is named nowhere else in the
    /// file. A record can list the artist in the tag, and name the guests only in the song's title.</para>
    /// </summary>
    public static IReadOnlyList<string> Credits(string? title)
    {
        var text = title ?? "";
        if (text.Length == 0)
            return [];

        List<string>? found = null;

        for (var i = 0; i < text.Length; i++)
        {
            if (CreditRun(text, i) is not { } run)
                continue;

            if (run.Phrase.Length > 0)
                (found ??= []).Add(run.Phrase);

            i = run.End - 1;
        }

        return found ?? (IReadOnlyList<string>)[];
    }

    /// <summary>Where a credit that starts at <paramref name="at"/> ends (exclusive), and who it names. Returns null
    /// where no credit starts.</summary>
    private static (int End, string Phrase)? CreditRun(string text, int at)
    {
        var close = text[at] switch { '(' => ')', '[' => ']', _ => '\0' };
        if (close != '\0')
        {
            var end = Closing(text, at, text[at], close);
            if (end < 0)
                return null;

            var inside = text[(at + 1)..end].Trim();
            var mark = MarkLength(inside, CreditMarks);
            return mark < 0 ? null : (end + 1, Named(inside.AsSpan(mark)));
        }

        // Bare in the line. It has to start a word. Otherwise "Defeat. The End" would be read as a credit. It also
        // has to be one of the marks that nothing else is spelled like.
        if (at > 0 && char.IsLetterOrDigit(text[at - 1]))
            return null;

        var plain = MarkLength(text.AsSpan(at), PlainCreditMarks);
        if (plain < 0)
            return null;

        // To the next bracket or the end of the line, so "Song feat. X (Live)" keeps its (Live). The credit is a run
        // of names, and the bracket after it is a different fact.
        var stop = text.IndexOfAny(Openers, at);
        var stopped = stop < 0 ? text.Length : stop;
        return (stopped, Named(text.AsSpan()[(at + plain)..stopped]));
    }

    /// <summary>The names left after the mark has been cut off the front of a credit.</summary>
    private static string Named(ReadOnlySpan<char> phrase) => phrase.Trim().Trim(MarkTail).Trim().ToString();

    /// <summary>What a mark can leave behind once its letters are cut. "feat" matched on "feat: Somebody" leaves
    /// the colon.</summary>
    private static readonly char[] MarkTail = ['.', ':', '-'];

    /// <summary>Closes up the seam that a removal leaves. That is the double space where a credit stood between two
    /// halves of a name, and the dangling dash or comma that a tagger wrote in front of it.</summary>
    private static string Tidy(string text)
    {
        var run = new System.Text.StringBuilder(text.Length);
        var space = false;

        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space && run.Length > 0)
                run.Append(' ');
            space = false;
            run.Append(c);
        }

        return run.ToString().TrimEnd(Danglers);
    }

    /// <summary>What a title is left ending in when the credit a tagger hung off it is taken away.</summary>
    private static readonly char[] Danglers = [' ', '-', ',', ';', '·', '/'];

    private static readonly char[] Openers = ['(', '['];

    /// <summary>The marks that can be read as a credit with no brackets round them. "with" is not among them, on
    /// purpose. Bare in a title it is a preposition far more often than it is a credit.</summary>
    private static readonly string[] PlainCreditMarks = ["feat.", "featuring", "ft."];

    /// <summary>
    /// Where the group opened at <paramref name="open"/> closes, or -1 where it never does.
    ///
    /// <para>The count goes up and down, so a group with brackets inside it is one group. "(Live (1971))" is one
    /// group, and not one group with a stray bracket after it.</para>
    /// </summary>
    private static int Closing(string text, int open, char opener, char closer)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == opener)
                depth++;
            else if (text[i] == closer && --depth == 0)
                return i;
        }
        return -1;
    }

    /// <summary>The phrase's words, folded for case. The phrase is split on every character that is not a letter or
    /// a digit. So <c>12" Mix</c>, <c>2011 Remaster</c> and <c>Taylor's Version</c> each give up the word that the
    /// vocabulary looks for.</summary>
    private static IEnumerable<string> Words(string phrase)
    {
        var start = -1;
        for (var i = 0; i <= phrase.Length; i++)
        {
            var letter = i < phrase.Length && char.IsLetterOrDigit(phrase[i]);
            if (letter && start < 0)
                start = i;
            else if (!letter && start >= 0)
            {
                yield return phrase[start..i].ToLowerInvariant();
                start = -1;
            }
        }
    }

    /// <summary>
    /// Whether the phrase names somebody on the recording.
    ///
    /// <para>The mark has to end where the phrase's next word begins. Otherwise "(Feathers)" would read as a
    /// featuring credit for a guest called "hers".</para>
    /// </summary>
    private static bool IsCredit(string inside) => MarkLength(inside, CreditMarks) >= 0;

    /// <summary>How many characters of a credit mark the phrase opens with, or -1 where it opens with none of them.
    /// This is the one place where the marks are matched. So the rule that fades a bracket, the rule that hides one,
    /// and the rule that reads the names out of one cannot disagree about what a credit is.</summary>
    private static int MarkLength(ReadOnlySpan<char> phrase, string[] marks)
    {
        foreach (var mark in marks)
        {
            if (!phrase.StartsWith(mark, StringComparison.OrdinalIgnoreCase))
                continue;

            // A mark that ends in punctuation has already said where it ends. A bare word has not. So what follows
            // it must be a break, not more of a longer word.
            if (!char.IsLetter(mark[^1]))
                return mark.Length;

            var after = phrase.Length > mark.Length ? phrase[mark.Length] : '\0';
            if (after is '\0' or '.' or ':' || char.IsWhiteSpace(after))
                return mark.Length;
        }

        return -1;
    }

    /// <summary>How a tagger writes "this person is on it". The dotted forms are listed next to the bare forms,
    /// because both are written. "with" is here because a duet is often credited that way, as well as with a
    /// "feat.".</summary>
    private static readonly string[] CreditMarks =
        ["feat.", "feat", "featuring", "ft.", "ft", "with", "w/"];

    /// <summary>
    /// The words that a version is described with. One of them anywhere in the phrase makes it an aside.
    ///
    /// <para>English only, on purpose. The list is a set of phrases that somebody decided are notes and not names.
    /// Guessing at that in a language nobody here reads is how "(Samay nit)" came to be faded in the first place. A
    /// record tagged in another language keeps its brackets at full strength. That is the state every record was in
    /// before this rule existed.</para>
    /// </summary>
    private static readonly HashSet<string> Qualifiers = new(StringComparer.Ordinal)
    {
        // what was done to the recording
        "mix", "mixes", "mixed", "remix", "remixes", "remixed", "edit", "edits", "edited",
        "version", "versions", "remaster", "remastered", "remasters", "rerecorded", "rerecording",
        "dub", "instrumental", "acoustic", "unplugged", "acappella", "cappella", "karaoke", "reprise",

        // where and how it was taken down
        "live", "demo", "rehearsal", "session", "sessions", "take", "outtake", "bootleg", "concert",

        // which cut of it this is
        "radio", "single", "album", "extended", "original", "alternate", "clean", "explicit",
        "censored", "mono", "stereo", "club", "dance", "vocal",

        // which pressing carries it
        "deluxe", "edition", "expanded", "anniversary", "reissue", "bonus", "unreleased",
    };
}
