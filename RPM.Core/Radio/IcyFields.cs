namespace RPM.Core.Radio;

/// <param name="Spoken">The announcement as one line, for everything that prints one or reads one: the
/// dial's readout, the ad marker, the split. Null where the station announced nothing.</param>
/// <param name="Named">The halves, where the fields NAMED them rather than leaving them to be guessed at.
/// Null where there were no fields, which is most stations. Those are read the usual way, by <see
/// cref="IcyOrders"/>.</param>
public readonly record struct Fielded(string? Spoken, Announced? Named);

/// <summary>
/// The fields that some US stations write their announcements in. These are read, not printed.
///
/// <para><b>ICY carries one string.</b> A station with more to say than a title says it in the same place.
/// The encoders from Triton, used by iHeart and the other stations on that platform, write the artist, the
/// separator and then the title in a field. The schedule ids come after it. A sample from 102.7 KIIS FM,
/// verbatim:</para>
///
/// <code>
/// Kehlani - text="Folded" song_spot="M" MediaBaseId="3133844" length="00:03:57" …
/// LA's #1 Hit Music Station - text="102.7 KIIS FM" song_spot="T" MediaBaseId="0" length="00:00:00" …
/// - text="Spot Block End" amgTrackId="9876543" length="00:00:00"
/// </code>
///
/// <para><b>The title is in the field, not in front of it.</b> This is what makes the format need its own
/// type. Read by the usual convention, with everything before the dash as the artist and everything after
/// it as the title, the line gives an artist and no title. The header then prints "Kehlani" where the song
/// goes. Read as the station wrote it, the pair is stated outright and nothing has to be guessed. <see
/// cref="Fielded.Named"/> carries the pair. The order learning that is used for stations with ambiguous
/// pairs is not consulted.</para>
///
/// <para><b>Not every line is a record.</b> <c>song_spot</c> says which. "M" and "F" are music. The
/// station's own idents and spots are marked "T". Those print as a line, never as a record by an
/// artist.</para>
///
/// <para><b>A second shape.</b> Some iHeart streams write every part as a field, separated by commas. The
/// fields of the first shape are inside <c>url</c>, and their quotes are not escaped. A sample from 99.9
/// Kiss Country, verbatim:</para>
///
/// <code>
/// title="Good Vibes",artist="CHRIS JANSON",url="song_spot="F" MediaBaseId="0" … length="00:02:42" …"
/// </code>
///
/// <para>102.7 KIIS FM switches between the two shapes.</para>
///
/// <para><b>Read in one place.</b> <see cref="IcyConnection"/> passes every announcement through <see
/// cref="Read"/> before handing it to anyone, and the raw string does not leave it. So the tuner, the home
/// screen's peek, the split, the order, the ad marker and the header all read what the station said, not
/// how it was written.</para>
/// </summary>
public static class IcyFields
{
    /// <summary>
    /// Reads one announcement.
    /// </summary>
    /// <remarks>
    /// A string with no fields comes back unchanged and with no names. Most stations send a plain title,
    /// and this method must leave that title alone.
    /// </remarks>
    public static Fielded Read(string? announced)
    {
        var text = announced?.Trim();
        if (string.IsNullOrEmpty(text))
            return default;

        if (IsListed(text))
            return ReadListed(text);

        var fields = FirstField(text);

        // No fields, so the whole string is what was said. The exception is a string that is only the
        // separator, which is a station announcing nothing, in the format's own punctuation.
        if (fields < 0)
            return Head(text).Length == 0 ? default : new Fielded(text, null);

        var head = Head(text[..fields]);
        var said = Field(text[fields..], "text")?.Trim() ?? "";

        // Nothing in front of the separator: a marker, which is all the station wrote and all there is
        // to print. "Spot Block End" arrives this way, and so does "Spot Block Start".
        if (head.Length == 0)
            return said.Length == 0 ? default : new Fielded(said, new Announced(null, said, IsSong: false));

        // Something in front and nothing in the field: the head is the whole announcement.
        if (said.Length == 0)
            return new Fielded(head, null);

        // Both parts are present. This is the usual case. Whether it is a record is for the station to
        // say, not for this code to guess. An ident has a slogan as its head and the station's name as
        // its title. Printed as a pair, it would be a song called "102.7 KIIS FM" by "LA's #1 Hit Music
        // Station".
        return IsRecord(text[fields..])
            ? new Fielded($"{head} - {said}", new Announced(head, said, IsSong: true))
            : new Fielded(said, new Announced(null, said, IsSong: false));
    }

    /// <summary>
    /// Reads the second shape: <c>title="…",artist="…",url="…"</c>.
    /// </summary>
    /// <remarks>
    /// This follows the same rules as the first shape. Both halves and a music mark make a record. A title
    /// with no artist, or a title that the mark says is not music, is a line. An artist with no title is
    /// the whole announcement, and neither half is claimed.
    /// </remarks>
    private static Fielded ReadListed(string text)
    {
        var title = Listed(text, "title")?.Trim() ?? "";
        var artist = Listed(text, "artist")?.Trim() ?? "";

        if (title.Length == 0)
            return artist.Length == 0 ? default : new Fielded(artist, null);

        // The music mark is inside url, which holds the first shape's fields.
        if (artist.Length == 0 || !IsRecord(Listed(text, "url") ?? ""))
            return new Fielded(title, new Announced(null, title, IsSong: false));

        return new Fielded($"{artist} - {title}", new Announced(artist, title, IsSong: true));
    }

    /// <summary>Whether the text is in the second shape: it opens with the title or the artist
    /// field.</summary>
    private static bool IsListed(string text) =>
        text.StartsWith("title=\"", StringComparison.OrdinalIgnoreCase)
        || text.StartsWith("artist=\"", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// One field's value in the second shape, or null where the text does not carry it.
    /// </summary>
    /// <remarks>
    /// A value ends at <c>",</c> followed by the next field's name, not at the next quotation mark. The url
    /// field holds quoted values of its own, unescaped, and a title may contain a quotation mark. The last
    /// field ends at the closing quote, or at the end of the text where the block was cut short.
    /// </remarks>
    private static string? Listed(string text, string name)
    {
        var key = name + "=\"";

        for (var at = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
             at >= 0;
             at = text.IndexOf(key, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            // At the start, or straight after the comma that ends the field before it.
            if (at > 0 && text[at - 1] != ',')
                continue;

            var start = at + key.Length;
            for (var end = start; end + 1 < text.Length; end++)
            {
                if (text[end] == '"' && text[end + 1] == ',' && StartsField(text, end + 2))
                    return text[start..end];
            }

            var value = text[start..];
            return value.EndsWith('"') ? value[..^1] : value;
        }

        return null;
    }

    /// <summary>Whether a field name followed by <c>="</c> starts at this index.</summary>
    private static bool StartsField(string text, int i)
    {
        var end = i;
        while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
            end++;

        return end > i && end + 1 < text.Length && text[end] == '=' && text[end + 1] == '"';
    }

    /// <summary>
    /// Whether the fields say this is a record.
    /// </summary>
    /// <remarks>
    /// <c>song_spot="M"</c> and <c>song_spot="F"</c> are music. "M" is what the first shape marks songs
    /// with. "F" is what every song in the second shape was marked with in the sample (see the class
    /// remarks). Anything else is the station: "T" is what it marks its idents, liners and ads with. A line
    /// that does not carry the field at all is taken as a record when it has both halves. The field comes
    /// from Triton. A station on another encoder that writes its title in a field does not say anything
    /// about idents by leaving it out.
    /// </remarks>
    private static bool IsRecord(string fields)
    {
        var spot = Field(fields, "song_spot")?.Trim();
        return spot is null
               || spot.Equals("M", StringComparison.OrdinalIgnoreCase)
               || spot.Equals("F", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The words in front of the fields, with the separator they were left hanging off taken away.
    ///
    /// <para>A station with no artist to name still writes the separator ("- text=…"). The line is built as
    /// artist, dash, title, whether or not there is an artist. A lone dash names nobody.</para>
    /// </summary>
    private static string Head(string text)
    {
        var head = text.Trim();
        while (head.Length > 0 && IsDash(head[^1]))
            head = head[..^1].TrimEnd();

        return head;
    }

    /// <summary>
    /// Where the fields begin: the first <c>key="</c> standing on its own.
    ///
    /// <para>A key is letters, digits and underscores, and it must START a word. A title may contain an
    /// equals sign and a quotation mark. <c>Symphony No. 5 ="Fate"</c> is a title, not a field. The test
    /// that tells them apart is the space in front.</para>
    /// </summary>
    private static int FirstField(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsLetter(text[i]) || (i > 0 && !char.IsWhiteSpace(text[i - 1])))
                continue;

            var end = i;
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
                end++;

            if (end > i && end + 1 < text.Length && text[end] == '=' && text[end + 1] == '"')
                return i;
        }

        return -1;
    }

    /// <summary>
    /// One field's value, or null where the string does not carry it.
    /// </summary>
    /// <remarks>
    /// This is matched at the start of a word, for the same reason as above and for one more. Field names
    /// run into each other's tails. "itunesTrackId" ends with "TrackId". A search for the shorter name that
    /// did not check what came before it would return the longer name's value.
    /// </remarks>
    private static string? Field(string fields, string name)
    {
        var key = name + "=\"";

        for (var at = fields.IndexOf(key, StringComparison.OrdinalIgnoreCase);
             at >= 0;
             at = fields.IndexOf(key, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (at > 0 && !char.IsWhiteSpace(fields[at - 1]))
                continue;

            var start = at + key.Length;
            var end = fields.IndexOf('"', start);

            // No closing quote: the block was cut short by the 16-byte padding that ICY adds to a block.
            // What is there is what was said.
            return end < 0 ? fields[start..] : fields[start..end];
        }

        return null;
    }

    /// <summary>The dash characters that stations use as the separator: the hyphen, the en dash and the em
    /// dash. The double hyphen that <see cref="IcyTitle"/> also splits on is not here, because this checks
    /// one character at a time.</summary>
    private static bool IsDash(char c) => c is '-' or '–' or '—';
}
