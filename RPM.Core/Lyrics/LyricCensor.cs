using System.Text.RegularExpressions;

namespace RPM.Core.Lyrics;

/// <summary>
/// Masks slurs and other strong words in a lyric for display. For example, "niggas" is drawn as "n****s".
///
/// <para>The first and last letters are kept, and the middle is starred. The word keeps its shape and its
/// length, so the line still scans, and a reader knows what was sung. This softens the word on screen. It
/// does not pretend the word is absent.</para>
///
/// <para><b>Display only.</b> Nothing here changes what was fetched or what is cached. The sheet on disk stays
/// as its author wrote it. That matters. The cache may be read by other code later, and a censored copy saved
/// over the original could never be undone. Masking at drawing time means the decision can be reversed by
/// changing one call.</para>
///
/// <para><b>Three lists, and only one of them always applies.</b> Slurs aimed at people, for race, ethnicity,
/// sexuality or disability, are always masked, on every track. There is no way to turn them off. Ordinary
/// swearing and drug references are the other two lists. They are masked only when the caller passes
/// <paramref name="maskProfanity"/>. Together they make up most of what makes a lyric explicit. Starring them
/// without asking would leave whole verses unreadable.</para>
///
/// <para>The two optional lists share one switch. They answer one question: does the listener want the lyric
/// softened? Two switches would ask that question twice, for a difference that almost nobody draws.</para>
///
/// <para><b>The flag does not decide.</b> A track's advisory flag says whether the pressing is clean. That says
/// nothing about what the listener wants to read. A clean pressing can still show words the listener will not
/// hear, because the sheet is fetched by artist and title and is usually the explicit version. An explicit
/// pressing has swearing that a listener may not want to read. So the listener decides, once, and the choice
/// applies to every record: clean, explicit and unflagged alike.</para>
///
/// <para><b>Why the patterns are tight.</b> The main risk in a filter is the innocent word it changes, and the
/// traps here are real. "raccoon" and "cocoon" contain one entry. "Pakistan" begins with another. "Doo-wop"
/// contains a third, which is why that entry is left off the list. Every entry matches whole words only. Each
/// one spells out the endings it actually takes. None uses a loose "any letters after".</para>
/// </summary>
public static class LyricCensor
{
    /// <summary>
    /// The list, as whole-word patterns. Each one spells out its own endings. A blanket "any letters after" is
    /// what would turn Pakistan into a slur.
    /// </summary>
    private static readonly string[] Words =
    [
        @"nigg(?:a|as|az|er|ers|ah|ahs|uh|uhs|ers)",
        // The short form is on the list by choice. In British English it means a cigarette, so this is the one
        // entry most likely to match an innocent lyric now and then. That cost is accepted knowingly. Words like
        // "Fagin" are safe: the boundary needs the word to end there, or to carry one of the endings listed.
        @"fag(?:s|got|gots|gotry)?",
        @"kike(?:s)?",
        @"spic(?:s)?",
        @"wetback(?:s)?",
        @"chink(?:s)?",
        @"gook(?:s)?",
        @"paki(?:s)?",                 // NOT paki\w*, which would also match Pakistan
        @"trann(?:y|ies)",
        @"retard(?:s|ed)?",
        @"coon(?:s)?",                 // Whole words only. raccoon and cocoon do not match.
        @"beaner(?:s)?",
        @"towelhead(?:s)?",
        @"raghead(?:s)?",
        @"halfbreed(?:s)?",

        // The Italian-American one is spelled however it was heard, so the spellings are listed one by one rather
        // than guessed with a wildcard. The word it was made from, "melanzana", means aubergine and is not in the
        // list.
        @"mulignan(?:s|i|e|o)?",
        @"(?:mool|moul|mul)inyan(?:s|i|e)?",
        // The short form. The endings are required. That keeps "mooli", the radish, out of the match.
        @"mool(?:ie|ies|ey|eys)",
    ];

    /// <summary>
    /// General swearing. It is added to the list above only when the listener asks for it, and then it applies to
    /// every track they play. The same rules as the slurs apply: whole words, with endings spelled out.
    ///
    /// <para><b>Six entries carry an innocent meaning too, and they are on the list anyway.</b> Each of these has
    /// an ordinary meaning as well as a rude one. A case-insensitive whole-word match cannot tell which was meant.
    /// They are listed on purpose, and the cost is written down here:</para>
    ///
    /// <list type="bullet">
    /// <item><c>hell</c>: also a place. A spiritual about it will be starred.</item>
    /// <item><c>cock</c>: also the bird. The rooster will crow as "c**k" in a folk song.</item>
    /// <item><c>dick</c>: also a name, and a common one. Anyone called Dick has their name starred.</item>
    /// <item><c>prick</c>: also what a thorn does. Only the bare word and its plural are listed, because the -ed
    /// and -ing forms are nearly always the verb and almost never the insult.</item>
    /// <item><c>hoe</c>: also a garden tool, so a folk song about a hoe will be starred.</item>
    /// <item><c>bloody</c>: an intensifier, and also a word for something covered in blood.</item>
    /// </list>
    ///
    /// <para>The trade is acceptable because this whole list is asked for. A listener who has turned swearing off
    /// has said what they want the screen to do. A starred "hell" then honours that request a little too eagerly.
    /// The word stays readable, and the switch that caused it is one press away. None of it applies to a listener
    /// who never asked, which is the state the app starts in.</para>
    ///
    /// <para>The word boundaries matter even more for these six than for the rest. Words that contain one of them
    /// inside a longer word, such as "hello", "shell", "cocktail", "peacock", "Dickens", "shoes" and "blood", are
    /// not changed.</para>
    /// </summary>
    private static readonly string[] Profanity =
    [
        // The trailing \b protects each alternation, as it does in the slur list. "ass" matches inside "asshole",
        // then fails the boundary, and the engine falls through to the entry that spells the longer word out. This
        // is why the compounds can be listed separately without the short entry swallowing them.
        @"fuck(?:s|ed|er|ers|in|ing|up|ups|wit|wits)?",
        @"motherfuck(?:er|ers|in|ing)?",
        @"shit(?:s|ted|ter|ters|tin|ting|ty|head|heads|hole|holes)?",
        @"bullshit(?:ting|ted)?",
        @"bitch(?:es|ed|in|ing|y)?",
        @"ass(?:es)?",
        @"ass(?:hole|holes|wipe|wipes)",
        @"(?:dumb|jack|bad|smart|fat|hard|lard)ass(?:es)?",
        @"bastard(?:s|ly)?",
        @"cunt(?:s|y)?",
        @"piss(?:es|ed|er|ers|in|ing)?",
        @"damn(?:ed|it)?",              // catches the "damn" of "god damn" on its own
        @"goddamn(?:ed|it)?",           // The joined spelling. There is no word boundary inside it.
        @"goddammit",                   // The spelling with two m's is also common.
        @"dammit",
        @"crap(?:s|py|ped|ping)?",
        @"whore(?:s|d|ing)?",
        @"slut(?:s|ty)?",
        @"skank(?:s|y)?",
        @"douche(?:bag|bags)?",
        @"twat(?:s)?",
        @"wank(?:s|er|ers|ed|ing)?",
        @"bollock(?:s)?",
        @"arse(?:s|hole|holes)?",
        @"pussy",
        @"pussies",
        // The ending is required. That keeps out the bird, the tit, which appears in nature songs.
        @"tit(?:s|ty|ties)",

        // The six with an innocent meaning. See the remarks above. Each one is kept to the bare word and its
        // plural. The insult is almost always the bare word, so more endings would add little. Each ending added
        // would catch more ordinary words.
        @"hell(?:s)?",
        @"cock(?:s)?",                 // Not cocky, which is a different word.
        @"dick(?:s|head|heads)?",
        @"prick(?:s)?",                // Not pricked or pricking, which mean a finger or ears.
        @"hoe(?:s)?",
        @"bloody",                     // The intensifier alone. "blood" and "bloodied" are not matched.
    ];

    /// <summary>
    /// Drugs. They are on the same switch as the swearing, and they are masked with it.
    ///
    /// <para><b>This list is short on purpose. It is the hardest of the three to write.</b> Swearing has its own
    /// vocabulary, and a word on that list is almost always the word itself. Drug slang is almost entirely
    /// borrowed. It is ordinary English used for something else, and the ordinary meaning is still there. So this
    /// list holds names only: pharmacological, botanical and brand names. It also holds a few slang terms that
    /// have only ever meant one thing.</para>
    ///
    /// <para><b>Left off on purpose.</b> Each of these is an ordinary word in most lyrics: high, trip, blow, line,
    /// dope, pot, grass, weed, crack, smack, coke, snow, ice, speed, acid, bud, joint, molly, crystal, ecstasy,
    /// hash, junk, horse, lean, blunt and stoned. Each one does an ordinary job in far more lyrics than not. A
    /// crack of dawn, a line of verse, a trip home, a blunt knife, a bud on a branch, a girl called Molly, and the
    /// ecstasy of a love song.</para>
    ///
    /// <para>Two of them are worse than ordinary here. Rock and roll are drug slang, and they are also the name of
    /// the music. A filter that stars "rock and roll" has not failed at the margin. It has failed at the
    /// centre.</para>
    ///
    /// <para>The result catches a song that names a drug, and it misses one that talks around it. That is the
    /// honest trade. The other choice would catch the talking around, but at the cost of damaging ordinary
    /// language. Any word above can be moved into this list if it is wanted. The cost is written out here, so the
    /// choice can be made with it in view.</para>
    /// </summary>
    private static readonly string[] Drugs =
    [
        // Named substances.
        @"cocaine",
        @"heroin",                     // Not heroine. The boundary already excludes it.
        @"marijuana",
        @"cannabis",
        @"hashish",                    // The full word only. "hash" is excluded: it is a meal and a symbol.
        @"ganja",
        @"amphetamine(?:s)?",
        @"methamphetamine(?:s)?",      // Listed before the short form, so the longer word is tried first.
        @"meth",                       // Not method or Methodist. The boundary excludes both.
        @"methadone",
        @"opium",
        @"opiate(?:s)?",
        @"opioid(?:s)?",
        @"morphine",
        @"codeine",
        @"fentanyl",
        @"ketamine",
        @"mescaline",
        @"psilocybin",
        @"barbiturate(?:s)?",
        @"quaalude(?:s)?",
        @"benzedrine",
        @"dexedrine",
        @"narcotic(?:s)?",
        @"pcp",
        @"lsd",
        @"mdma",
        @"thc",

        // Brand names.
        @"xanax",
        @"valium",
        @"percocet(?:s)?",
        @"oxycontin",
        @"oxycodone",
        @"vicodin",
        @"adderall",
        @"ritalin",
        @"ambien",                     // Not ambient. The boundary excludes it.

        // Slang that has only one meaning.
        @"reefer(?:s)?",
        @"spliff(?:s)?",
        @"doobie(?:s)?",               // Also used in band names, which cannot be told apart from the drug.
        @"bong(?:s)?",                 // Not bongo, which is a drum.
        @"junkie(?:s)?",
        @"junky",
        @"kush",
        @"sativa",
        @"indica",
    ];

    /// <summary>
    /// Built once. The boundaries are the important part. A \b on either side means a match has to BE the word,
    /// and not sit inside a longer one.
    /// </summary>
    private static readonly Regex Pattern = Build(Words);

    /// <summary>
    /// The wider pattern, built from all three lists at once rather than run as three passes. A word cannot be
    /// starred twice, and the lists cannot disagree about where a boundary falls.
    /// </summary>
    private static readonly Regex WidePattern = Build([.. Words, .. Profanity, .. Drugs]);

    private static Regex Build(string[] words) => new(
        @"\b(?:" + string.Join('|', words) + @")\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// The line as it should be shown. Returns the same string when there is nothing to mask. That is nearly
    /// every line of nearly every song.
    /// </summary>
    /// <param name="maskProfanity">Also mask ordinary swearing and drug references. This is the listener's own
    /// setting. It does not depend on the track's advisory flag. A listener who does not want to read swearing
    /// wants it masked most on the explicit pressing, which is the one that has swearing on it. Slurs are masked
    /// either way. See the class remarks.</param>
    public static string Mask(string? line, bool maskProfanity = false)
        => string.IsNullOrEmpty(line)
            ? line ?? ""
            : (maskProfanity ? WidePattern : Pattern).Replace(line, m => Star(m.Value));

    /// <summary>
    /// The first letter, the last letter, and stars between them. The word keeps its length and its shape. The
    /// two letters kept are the ones that were there, so their case is kept too.
    /// </summary>
    private static string Star(string word)
        => word.Length <= 2 ? word : string.Create(word.Length, word, static (span, w) =>
        {
            span[0] = w[0];
            span[^1] = w[^1];
            span[1..^1].Fill('*');
        });
}
