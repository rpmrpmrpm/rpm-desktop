using System.Text.RegularExpressions;

namespace RPM.Core.Library;

/// <summary>
/// A note a kind of record puts on each of its songs: "(Live)" on a live record, "(Mixed)" on a mixtape. Each
/// note says whether a title already carries it and gives the title with it added.
///
/// <para><b>Asked for, never assumed.</b> The album editor and the import's tag panel offer the note once a
/// record's release type names one (see <see cref="For"/>). Nothing adds it on its own. A record's songs are
/// often tagged with their plain names on purpose, and the release type already shelves the record. The note is
/// for the listener who wants the concert take or the DJ's blend told apart from the studio track in a playlist
/// or a search.</para>
///
/// <para><b>A title that already says so is left alone.</b> "Song (Live)", "Song [Live at Wembley]", "Song (MTV
/// Unplugged)" and "Song - Live 1986" are all already live. "Song (Mixed)" and "Song - Mixed", as stores write a
/// track of a DJ mix, are already mixed. A second note would be noise, and pressing the offer twice must not
/// stack them. Only a note counts: the word in brackets, or after a dash. "Live Forever" is the song's name, so
/// it becomes "Live Forever (Live)".</para>
/// </summary>
public sealed class TitleNote
{
    /// <summary>A song played in front of an audience. "Unplugged" says the same thing, so it counts as
    /// already marked.</summary>
    public static readonly TitleNote Live = new("(Live)", "live|unplugged");

    /// <summary>A song blended into the next one on a mixtape or DJ mix. Only "mixed" counts as already
    /// marked. "Mix" alone names a version of the song: "(Original Mix)" and "(Radio Mix)" are the cut the DJ
    /// played, not a sign that this is the DJ's blend of it. Stores write "Song (Original Mix) - Mixed" for
    /// exactly that reason.</summary>
    public static readonly TitleNote Mixed = new("(Mixed)", "mixed");

    /// <summary>The note a release type puts on its songs, or null for a type that has none.</summary>
    public static TitleNote? For(ReleaseType? type) => type switch
    {
        ReleaseType.Live => Live,
        ReleaseType.Mixtape => Mixed,
        _ => null,
    };

    private readonly Regex _said;

    private TitleNote(string text, string words)
    {
        Text = text;
        // One of the words, whole, inside a pair of brackets, or straight after a dash that sets a note off from
        // the name. The dash needs a space before it, so a hyphenated name such as "Half-Live" is not a note.
        _said = new Regex(
            $@"[\(\[][^\)\]]*\b(?:{words})\b[^\)\]]*[\)\]]|\s[-–—]\s*(?:{words})\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>The note, as stores and taggers write it. It is not translated. It is a tag, and a tag reads the
    /// same in every language the file is played in.</summary>
    public string Text { get; }

    /// <summary>Whether this title already carries the note, in any of the ways it is written.</summary>
    public bool Says(string? title)
        => !string.IsNullOrWhiteSpace(title) && _said.IsMatch(title);

    /// <summary>The title with the note at its end, or the title as it was when it is empty or already says so.
    /// The note goes last, after any credit: "Song (feat. Guest) (Live)", as stores write it.</summary>
    public string Marked(string? title)
    {
        var trimmed = (title ?? "").Trim();
        return trimmed.Length == 0 || Says(trimmed) ? title ?? "" : $"{trimmed} {Text}";
    }

    public override string ToString() => Text;
}
