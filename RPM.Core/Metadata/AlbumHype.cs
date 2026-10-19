using System.Text.RegularExpressions;

namespace RPM.Core.Metadata;

/// <summary>
/// A short line that puts a record forward, taken from what the encyclopedia says about it.
///
/// <para><b>Every word of it comes from the article.</b> The temptation is a template, such as "the critically
/// acclaimed album by {artist}". That must not be done. Filled in for every record that has an article, the app
/// would call a bootleg, a demo or a compilation "critically acclaimed". That would be a false claim about real
/// musicians, in a shipped product. So nothing here invents a claim. Each phrase below is used only when the
/// article's own text states the fact. A record whose article says nothing remarkable gets no line, rather than
/// a flattering one.</para>
///
/// <para><b>A phrase, not a sentence, and no commas in it.</b> It sits on one line of a banner card, under a
/// title already set at 24 pt. "Critically acclaimed album from 1973" reads at a glance. The article's own first
/// sentence does not, and it would be cut off mid-clause. So each entry below carries its own noun, because no
/// single suffix fits them all. "Grammy winning album" and "The soundtrack" want different words after them. A
/// shared ", {year}" would hide that problem.</para>
///
/// <para><b>A claim has to be about the record.</b> An encyclopedia lead describes the album, and then its
/// singles, its tour and its reissues. If a needle matches anywhere in that text, it attributes any of those
/// things to the record. For example, an article may say <c>"Hot" and "Amazing" were both number ones in
/// Romania</c>. Those are two singles. Read as a claim about the album, it would make the album a number one. So
/// sentences that name a song in quotation marks are dropped before anything is read. What remains is the text
/// that is about the record. See <see cref="AboutTheRecord"/>.</para>
///
/// <para><b>Known limits.</b> A sentence is read as a whole. So a sentence that names an award and contains a
/// winning word fires whoever it is about. For example, "its producer won a Grammy" reads as the record winning
/// one. Telling these apart means finding the subject of each sentence. That needs a parser, and a wrong parser
/// fails quietly in the direction of flattering the record. This is left as a known limit, with a test, rather
/// than half-solved.</para>
///
/// <para><b>Pure, and kept out of the view</b>, for the same reason as <see cref="Library.HomeShelf"/>. This is a
/// reading of a text, and reading is where mistakes happen. What the surface does with the answer is its own
/// business.</para>
/// </summary>
public static partial class AlbumHype
{
    /// <summary>How long the line may be before it stops being a glance. A longer phrase is dropped, not
    /// trimmed, because half a claim is worse than none.</summary>
    public const int MostCharacters = 42;

    // What the article is worth saying about the record, strongest first. The order is what would make somebody
    // press a card. An award is a fact about the record that nothing else on the shelf can claim. A chart placing
    // is a fact about the world. Acclaim is what critics said. Being a debut is only interesting when nothing
    // better is there.
    //
    // The needles are what an encyclopedia lead actually uses. They are matched without regard to case, and
    // nothing is stemmed. "acclaim" catches "acclaimed" and "to critical acclaim". It does not catch a word that
    // merely starts the same way.

    /// <summary>Words that turn a prize into one that somebody WON. They are required in the same sentence as
    /// the award's own name. "Shortlisted for the Mercury Prize" and "nominated for a Grammy" name the award
    /// without a win. An encyclopedia mentions nominations far more often than wins.</summary>
    private static readonly string[] Won = ["won", "winner", "winning", "received the", "took the"];

    /// <summary>Nothing more is needed. The needle alone is the claim.</summary>
    private static readonly string[] OnItsOwn = [];

    private static readonly (string[] Needles, string[] AlsoNeeds, string Says)[] Reads =
    [
        (["grammy"], Won, "Grammy winning album"),
        (["mercury prize"], Won, "Mercury Prize winning album"),
        (["brit award"], Won, "BRIT Award winning album"),

        // Below the wins and above everything else, because these are the two strongest things an article says
        // about a record that is not a prize it took home. Do not use "one of the greatest albums". Joined with
        // a year, it reads as a claim about that year alone, which no article makes. "An all-time great album
        // from 1968" keeps "all-time" in the right place.
        //
        // This is placed above the Hall of Fame line. Being called one of the greatest albums ever made is a
        // stronger reason to press a card than an induction that most people could not date.
        (["greatest albums of all time", "greatest albums", "greatest records of all time"],
         OnItsOwn, "An all-time great album"),

        // A HALL OF FAME induction is not a Grammy won. That is why it is its own line, rather than a word added
        // to Won. Reading "was inducted into the Grammy Hall of Fame" as "Grammy winning album" would be false.
        // It sits below the wins for the same reason.
        (["grammy hall of fame"], OnItsOwn, "Grammy Hall of Fame album"),

        // A chart placing, and only at the top. "reached number 12" is not a reason to press anything.
        (["number one", "number-one", "no. 1 ", "topped the"], OnItsOwn, "A number one album"),

        // Say "a best-selling album", not "one of the best-selling albums". The second is only true with "of all
        // time" after it. Joined with a year, it reads as a claim about that year alone, which the article did
        // not make.
        // "Certified platinum", not "platinum" alone, which matches too many unrelated things.
        (["certified platinum", "certified multi-platinum", "certified double platinum"],
         OnItsOwn, "A platinum-selling album"),

        (["best-selling", "best selling"], OnItsOwn, "A best-selling album"),

        // "a critical and commercial success" is how an encyclopedia lead most often says acclaim. "Commercial
        // success" alone is NOT here: selling well is not the same claim, and it has its own line above.
        (["critical acclaim", "critically acclaimed", "acclaimed", "critical success",
          "critical and commercial success"], OnItsOwn, "Critically acclaimed album"),
        (["landmark", "seminal", "groundbreaking", "ground-breaking", "pioneering", "influential"],
         OnItsOwn, "A landmark record"),

        // What KIND of record it is. This is the weakest of these, and it is still better than nothing.
        (["debut studio album", "debut album"], OnItsOwn, "The debut album"),
        (["concept album"], OnItsOwn, "A concept album"),
        (["live album"], OnItsOwn, "A live album"),

        // "is the soundtrack", not "soundtrack". Nearly every record that mentions the word is mentioning
        // somebody else's. An article can say that a singer worked on a film's soundtrack. That does not make
        // the singer's record a soundtrack.
        (["is the soundtrack", "is a soundtrack", "soundtrack album", "is the original soundtrack"],
         OnItsOwn, "The soundtrack"),
    ];

    /// <summary>
    /// The sentences of the article that are about the record itself. Sentences about its songs are taken out.
    ///
    /// <para>They are kept apart, not joined back into a paragraph. A claim is read one sentence at a time. So
    /// an award named in one sentence and won in another is not read as an award won.</para>
    /// </summary>
    /// <remarks>
    /// <b>A sentence that names a song in quotation marks is about the singles.</b> That is the whole rule. It
    /// is enough, because an encyclopedia puts song titles in quotes and album titles without quotes. For
    /// example, <c>"Hot" and "Amazing" were both number ones in Romania</c> is removed, and <c>It was a critical
    /// and commercial success</c> stays. Without this rule a record is credited with whatever its singles did.
    /// This rule prevents that.
    ///
    /// <para>Curly quotes as well as straight ones are checked, because an encyclopedia extract contains both,
    /// depending on who typed it.</para>
    ///
    /// <para><b>What it deliberately does not do</b> is work out the subject of a sentence. That would need a
    /// parser, and a wrong parser would fail quietly in the direction of flattering the record. That is the one
    /// direction that matters here. Dropping a true claim because its sentence also mentions a single is the
    /// safe error.</para>
    /// </remarks>
    internal static IReadOnlyList<string> AboutTheRecord(string extract)
        => [.. Sentences().Split(extract).Where(sentence => !NamesASong().IsMatch(sentence))];

    /// <summary>Sentence ends. A claim stops applying to the thing a sentence was about, at each one.</summary>
    [GeneratedRegex(@"(?<=[.!?])\s+")]
    private static partial Regex Sentences();

    /// <summary>A song title in quotation marks. The length is bounded, so a long quotation of prose is not
    /// read as a title, and it does not remove a whole paragraph.</summary>
    [GeneratedRegex("[\"\u201c][^\"\u201c\u201d]{1,60}[\"\u201d]")]
    private static partial Regex NamesASong();

    /// <summary>
    /// The line, or null where the article gives no reason to put this record forward.
    /// </summary>
    /// <param name="extract">What the encyclopedia says: <c>EncyclopediaArticle.Extract</c>, or its lead. Empty
    /// or null is the normal answer for most of a collection, and it gives no line.</param>
    /// <param name="year">The record's year, added where it is known. Zero means the record does not say. Zero
    /// is not a date, and it is never printed.</param>
    public static string? For(string? extract, int year)
    {
        if (string.IsNullOrWhiteSpace(extract))
            return null;

        var about = AboutTheRecord(extract);
        if (about.Count == 0)
            return null;

        // Sentence by sentence, because a claim is only worth as much as the sentence it is in. An award named in
        // one sentence and won in another is two facts about two things. The strongest claim comes first,
        // whichever sentence it is in.
        var says = Reads.FirstOrDefault(read => about.Any(
            sentence => read.Needles.Any(
                            needle => sentence.Contains(needle, StringComparison.OrdinalIgnoreCase))
                        && (read.AlsoNeeds.Length == 0
                            || read.AlsoNeeds.Any(
                                also => sentence.Contains(also, StringComparison.OrdinalIgnoreCase)))))
            .Says;

        if (says is null)
            return null;   // an article that says nothing remarkable gives no reason to put the record forward

        // The year helps, because it is the one fact that places a record in time. "Critically acclaimed album from
        // 1973" is a different offer from "Critically acclaimed album". The year is used only where the record
        // states one. Zero is not a date.
        //
        // It is joined with "from", not a comma, so the whole reads as one phrase. Every entry above ends in a
        // noun for that reason.
        var line = year > 0
            ? $"{says} from {year.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : says;

        return line.Length > MostCharacters ? null : line;
    }
}
