using System.Text.RegularExpressions;

namespace RPM.Core.Library;

/// <summary>Reads what a record's own name says it is: a live recording, a single, a soundtrack or a
/// compilation. Each kind has its own shelf on an artist's page. Only the name is read, because no tag in
/// the file says any of these things.
///
/// <para><b>Only the name is used.</b> Tags do not carry these facts. MusicBrainz and Discogs hold this
/// data, but it is not in the file on disk. So the app reads the name on the folder and does not claim more
/// than that.</para>
///
/// <para><b>Use the name as tagged, never a tidied one.</b> Both markers sit in the part that
/// <see cref="TitleAsides.NameOnly"/> removes: "(Live)" is a bracketed note and "- Single" is a trailing
/// note. A caller that tidies the name first would test the part that holds the answer.</para>
/// </summary>
public static class AlbumKind
{
    /// <summary>Whether this title names a record played in front of an audience rather than made in a
    /// studio: "Live at the Apollo", "MTV Unplugged", "Live 1975", "The Abbey Road Sessions".
    ///
    /// <para><b>Whole words only.</b> The words appear inside many others, such as "Alive", "Olive" and
    /// "Delivered". A match inside a word would file those wrongly, which is a worse error than missing a
    /// live record. With word boundaries none of them match. Every spelling of the real thing still does,
    /// because brackets, dashes and exclamation marks all count as boundaries.</para>
    ///
    /// <para><b>Sessions are on the same shelf.</b> A record sold as sessions is a band playing through in
    /// one room, in front of whoever is there. Examples are the Peel Sessions and recordings made at
    /// Daytrotter. These belong with the concerts, not with the studio albums. Only the plural counts,
    /// because that is how such a release is named. "Session" on its own appears in ordinary titles.</para>
    ///
    /// <para><b>Unplugged on its own.</b> Many releases are tagged "MTV Unplugged [Live]" and others are
    /// tagged "Unplugged in New York", but both are the same kind of night. Outside this format the word
    /// rarely names a record, so matching it alone costs little. It catches the releases that use the short
    /// form.</para>
    ///
    /// <para><b>Storytellers, for the same reason.</b> "VH1 Storytellers" is a set played before an
    /// audience, with the talking kept in. Only the plural counts. "Storyteller" on its own is a box of
    /// studio recordings, so it stays off this shelf. The compilation check finds it through "anthology" in
    /// its subtitle.</para>
    ///
    /// <para><b>Known false matches.</b> A studio album whose name starts with the word lands on the live
    /// shelf. Examples are Hole's "Live Through This" and Oasis' "Live Forever". Requiring "at" or a year
    /// after the word would also miss "Live '75" and other spellings that real collections hold. If a
    /// collection shows this rule is wrong, start here.</para>
    /// </summary>
    public static bool IsLive(string? title)
        => !string.IsNullOrWhiteSpace(title) && Live.IsMatch(title);

    /// <summary>Whether this title names one song released on its own: "Runaway - Single".
    ///
    /// <para><b>The dash is half the rule.</b> "Single" on its own is an ordinary word in titles, such as
    /// "Single Ladies" or the "Singles" soundtrack. Matching it alone would file those as singles. A single
    /// is named with a trailing note: the record's name, a dash and the word. Stores and exports all write
    /// it this way. So a dash is required before the word. Any of the three dash types (hyphen, en dash, em
    /// dash) is accepted.</para>
    /// </summary>
    public static bool IsSingle(string? title)
        => !string.IsNullOrWhiteSpace(title) && Single.IsMatch(title);

    /// <summary>Whether this title names a collection of earlier recordings rather than a new album: "The
    /// Essential Aretha Franklin", "Greatest Hits", "The Collection".
    ///
    /// <para><b>A short list of phrases, not a clever rule.</b> A compilation cannot be told from an album
    /// by its sound or its tags. Record labels have used a few set phrases for this for decades, and a
    /// title with one of them is almost always a compilation. So the check is only that list. Nothing else
    /// is inferred.</para>
    ///
    /// <para><b>Whole words, and two-word phrases kept together.</b> "Best" on its own is in many ordinary
    /// titles, while "Best Of" is in very few. So the pair is what counts. "Recollection" and "Collective"
    /// contain the letters of an entry, but a word boundary is required at both ends, so neither
    /// matches.</para>
    ///
    /// <para><b>What it misses.</b> A compilation with an ordinary name, such as "Legend", "Hot Rocks" or
    /// "Chronicle". No list of phrases finds those. They were named like albums so they would sell like
    /// albums, and they stay on the album shelf. Adding a phrase is cheap. Adding one that also names real
    /// albums is not.</para>
    /// </summary>
    public static bool IsCompilation(string? title)
        => !string.IsNullOrWhiteSpace(title) && Compilation.IsMatch(title);

    /// <summary>Whether this title names music written for a film, a series or a game: "Purple Rain
    /// (Original Soundtrack)", "The Dark Knight - Original Score", "Music from the HBO Series".
    ///
    /// <para><b>Its own shelf, because it is a different kind of work.</b> A soundtrack is on an artist's
    /// page because the artist's name is on it. But someone looking for the artist's albums does not mean
    /// the film the music was written for. A discography that starts with two soundtracks looks like an
    /// artist who stopped making records. So soundtracks come off the album shelf and go to the shelf under
    /// the live one, as compilations do.</para>
    ///
    /// <para><b>The word alone, and "score" only when another word says what kind.</b> "Soundtrack" always
    /// names one, so it is matched alone. "Score" on its own does not. "The Score" by the Fugees is not a
    /// soundtrack. So "score" counts only after "original", "film" or "motion picture". "Motion picture"
    /// and "cast recording" always name one.</para>
    ///
    /// <para><b>"Music from" is the other way these are announced.</b> Whatever follows it, such as the
    /// motion picture, the film, a TV series or a streaming series, the phrase itself marks the record. It
    /// catches records that the rest of this list would miss.</para>
    ///
    /// <para><b>Known false matches.</b> "OST" is the usual tag on a ripped game or anime soundtrack, so it
    /// is matched too. That also matches a German record whose title contains the word "Ost" (east), such
    /// as "Ost + West". The abbreviation is kept because it is usually a soundtrack. If a collection shows
    /// otherwise, start here.</para>
    ///
    /// <para><b>"Music from" also matches some studio albums.</b> The Band's "Music from Big Pink" is a
    /// studio record named after the house where it was made, and it lands on the soundtrack shelf. The
    /// phrase is kept because it catches far more soundtracks than it misfiles. If a collection shows
    /// otherwise, start here.</para>
    ///
    /// <para><b>What it misses.</b> A soundtrack named only after its film, such as "Purple Rain",
    /// "Saturday Night Fever" or "Trainspotting". These are sold and tagged as albums, so they stay on the
    /// album shelf, where people look for them.</para>
    /// </summary>
    public static bool IsSoundtrack(string? title)
        => !string.IsNullOrWhiteSpace(title) && Soundtrack.IsMatch(title);

    /// <summary>The words that name a live recording. These are built once, because they are asked of every
    /// record on an artist's page, and a page can hold a hundred.</summary>
    private static readonly Regex Live =
        new(@"\b(?:live|sessions|unplugged|storytellers)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Single =
        new(@"[-–—]\s*singles?\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The word "soundtrack", "score" after a word that says what kind, and the phrases that
    /// announce a film or TV release.
    /// </summary>
    private static readonly Regex Soundtrack =
        new(@"\b(?:soundtracks?|(?:original|film|motion\s+picture)\s+scores?"
            + @"|motion\s+picture|music\s+from|cast\s+recording|ost)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Compilation =
        new(@"\b(?:greatest\s+hits|best\s+of|essentials?|collections?|anthology|anthologies"
            + @"|compilations?|retrospective|definitive|hits)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
