using System.Globalization;
using System.Text;

namespace RPM.Core.Radio;

/// <summary>
/// The kinds of station that a dial is sorted by, and the tags that the directory's users enter for each of
/// them.
///
/// <para><b>Radio's own list, not the record collection's.</b> The app files ALBUMS under <see
/// cref="Metadata.Genres"/>, and that list is about music, because a record is music. A dial is not only
/// music. A listener choosing a station picks Talk, News, Sports, Top 40, Oldies or Adult Contemporary as
/// readily as Jazz. A filter that offered only the shelves that a record collection uses would leave out a
/// large part of what is on the air.</para>
///
/// <para><b>Twenty genres, because a menu is read and not searched.</b> Radio Browser has tens of thousands
/// of tags. Most of them are one station's spelling of another station's tag. A filter that listed them all
/// is not practical. The genres here are the formats that a dial really divides into. Religious and World
/// are left out of the table.</para>
///
/// <para><b>The tags are matched, not shown.</b> A station is tagged by whoever added it, as "hiphop", "hip
/// hop", "rap" or "urban". So every spelling worth knowing is listed against one label. The label is the
/// only thing that a listener sees.</para>
///
/// <para><b>The table was written from the directory, not from memory.</b> The entries are the tags that
/// Radio Browser actually carries. They were read from its own tag list and from the pages that the pane
/// lists, biggest first. That is why the table holds Spanish, German and Russian spellings. A large part of
/// the directory is not tagged in English. A menu that matched only English would leave those stations
/// under no genre at all.</para>
/// </summary>
public static class RadioGenres
{
    /// <summary>The first of <see cref="All"/> that this tag names, or null if it names none of them. A tag
    /// can name more than one. See <see cref="Names"/>.</summary>
    public static string? Match(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        var folded = Fold(tag);
        if (folded.Length == 0)
            return null;

        if (Whole(folded) is { } one)
            return one;

        foreach (var genre in InWords(tag))
            return genre;

        return null;
    }

    /// <summary>Every one of <see cref="All"/> that this tag names, in the order they are written in it.
    /// Empty where it names none.</summary>
    public static IReadOnlyList<string> Names(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return [];

        var folded = Fold(tag);
        if (folded.Length == 0)
            return [];

        // The whole tag first, always. "Classic rock" is its own format, and it holds the word "rock". So a
        // tag that matches whole is answered whole, and is never taken apart. The same trap exists for
        // "dance" inside "dancehall", and for "jazz" inside "jazz funk".
        return Whole(folded) is { } one ? [one] : InWords(tag);
    }

    /// <summary>
    /// The tag to ask the DIRECTORY for when this genre is picked, or null if the genre is not one of <see
    /// cref="All"/>.
    ///
    /// <para>The directory is asked for a tag that its own tags HOLD, not for a whole tag. So one term
    /// reaches the spellings that a listener would never think to try. "hop" finds "hiphop", "hip hop" and
    /// "hip-hop", and "sport" finds "sports" and "sportstalk". What comes back is still checked with <see
    /// cref="Is"/>. That keeps out the reggaeton stations that "reggae" brings in from the Reggae
    /// page.</para>
    /// </summary>
    public static string? Term(string? genre)
    {
        foreach (var (name, term, _) in Table)
            if (string.Equals(name, genre, StringComparison.Ordinal))
                return term;

        return null;
    }

    /// <summary>Whether any of these tags says the station is that kind of station.</summary>
    public static bool Is(IReadOnlyList<string>? tags, string genre)
    {
        foreach (var tag in tags ?? [])
            foreach (var named in Names(tag))
                if (string.Equals(named, genre, StringComparison.Ordinal))
                    return true;

        return false;
    }

    /// <summary>
    /// The genres named by the WORDS of a tag that did not match whole: "club dance electronic house
    /// trance", "local news", "música pop", "rap hiphop rnb". Many taggers write several genres into one
    /// field. A rule that read only whole tags would leave all of those stations with no genre.
    ///
    /// <para>The words are read from left to right, with the longest run of words first. So a run that is
    /// its own format counts as that format, and not as its parts. "classic hits 60s 80s" is Oldies once,
    /// not Oldies and Top 40. This is reached only after the whole tag has failed. So nothing here can take
    /// a tag away from the label that matched it whole.</para>
    /// </summary>
    private static List<string> InWords(string tag)
    {
        var words = Words(tag);
        var named = new List<string>();
        if (words.Count < 2)
            return named;

        var at = 0;
        while (at < words.Count)
        {
            var run = new StringBuilder();
            string? hit = null;
            var end = at + 1;

            for (var upto = at; upto < words.Count; upto++)
            {
                run.Append(words[upto]);
                if (Whole(run.ToString()) is { } genre)
                {
                    hit = genre;
                    end = upto + 1;
                }
            }

            if (hit is not null && !named.Contains(hit, StringComparer.Ordinal))
                named.Add(hit);

            at = hit is null ? at + 1 : end;
        }

        return named;
    }

    /// <summary>The genre that a folded tag is, taken whole, or null.</summary>
    private static string? Whole(string folded)
    {
        if (ByTag.TryGetValue(folded, out var genre))
            return genre;

        // "Pop music", "Jazz radio", "Rock FM". These are the same tag with the usual noun on the end. Many
        // stations write them this way, and none of them means anything different.
        foreach (var suffix in Suffixes)
            if (folded.Length > suffix.Length && folded.EndsWith(suffix, StringComparison.Ordinal)
                && ByTag.TryGetValue(folded[..^suffix.Length], out var trimmed))
                return trimmed;

        return null;
    }

    /// <summary>
    /// A tag as it is compared: letters and digits, in lower case, with the accents removed.
    ///
    /// <para>The directory holds "Hip-Hop", "hip hop" and "hiphop" for one thing. Only the punctuation
    /// differs. The accents are removed for the same reason. One person tags a station "música" and another
    /// tags the same station "musica". "clásicos" is written both ways on the same page.</para>
    /// </summary>
    private static string Fold(string tag)
    {
        var kept = new StringBuilder(tag.Length);
        foreach (var c in tag.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(c))
                kept.Append(char.ToLowerInvariant(c));
        }

        return kept.ToString();
    }

    /// <summary>A tag cut into its words, each folded the way <see cref="Fold"/> folds it. Every character
    /// that is not a letter or a digit is a break. A tagger separates two genres with a space, a slash, a
    /// plus or an ampersand, and which one was used is not information.</summary>
    private static List<string> Words(string tag)
    {
        var words = new List<string>();
        var word = new StringBuilder();

        foreach (var c in tag.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(c))
            {
                word.Append(char.ToLowerInvariant(c));
                continue;
            }

            if (word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
        }

        if (word.Length > 0)
            words.Add(word.ToString());

        return words;
    }

    /// <summary>The nouns that a station adds to the end of its own genre. They are read only after the
    /// whole tag has failed, so nothing here can take a tag away from a label that wanted it.</summary>
    private static readonly string[] Suffixes = ["music", "radio", "fm", "hits", "station"];

    /// <summary>
    /// Every spelling worth knowing, with the label that it means.
    ///
    /// <para>It is written as one table, not as a rule for each genre. The thing being recorded is not a
    /// pattern. It is what many people typed into a form. The decades are Oldies on purpose. A station that
    /// calls itself 70s or 80s is telling the listener when its music is from, and that is the question
    /// that Oldies answers.</para>
    ///
    /// <para>The entries are folded when they are read. So an entry can be written the way a tagger writes
    /// it, such as "hip-hop" or "поп-музыка". It does not need to be written the way <see cref="Fold"/>
    /// leaves it.</para>
    ///
    /// <para>The second column is the term that the DIRECTORY is asked for when the genre is picked. See
    /// <see cref="Term"/>. It is in the same row as the spellings on purpose. A genre that cannot be
    /// searched for is a menu entry that returns whatever happens to be on screen. Putting the term here
    /// stops a genre from being added without one.</para>
    ///
    /// <para>The rows are in the order in which the menu offers them: the music first, roughly by how much
    /// of a dial it takes up. The three spoken formats come together at the end.</para>
    /// </summary>
    private static readonly (string Genre, string Term, string[] Tags)[] Table =
    [
        ("Pop", "pop", ["pop", "popular", "poprock", "europop", "kpop", "jpop", "cpop", "mandopop",
                 "cantopop", "synthpop", "teenpop", "electropop", "indiepop", "dreampop", "britpop",
                 "popmusik", "musicapop", "popmusica", "juvenil", "chanson", "chansonsfrancaises",
                 "varietefrancaise", "schlager", "volksmusik", "italianpop", "greekpop", "поп"]),
        ("Top 40", "top 40", ["top40", "top40s", "chr", "hits", "hitradio", "tophits", "charthits", "charts",
                    "chart", "contemporaryhitradio", "contemporaryhits", "contemporaryhitsradio",
                    "currenthits", "hitmusic", "toppop", "top10", "top20", "top50", "top100",
                    "topcharts", "singlecharts", "hitparade", "hitlist", "todayshits", "newhits",
                    "musicaactual", "exitos"]),
        ("Adult Contemporary", "adult contemporary", ["adultcontemporary", "ac", "hotac", "softac", "adultcontempory",
                                "hotadultcontemporary", "softadultcontemporary", "easylistening",
                                "easy", "softrock", "soft", "adulthits", "adultstandards",
                                "middleoftheroad", "mor", "lovesongs", "ballads", "crooners",
                                "beautifulmusic", "lightorchestral", "strings", "instrumental",
                                "newage", "relax", "relaxing", "relaxation", "calm", "mellow",
                                "unwind", "smooth"]),
        ("Oldies", "oldies", ["oldies", "goldenoldies", "classichits", "gold", "goldies", "nostalgia",
                    "nostalgie", "nostalgic", "retro", "vintage", "flashback", "decades", "ретро",
                    "oldskool", "oldschool", "classics", "clasicos", "musicadelrecuerdo",
                    "recuerdo", "greatesthits", "yesterday", "yesteryear", "50s", "60s", "70s",
                    "80s", "90s", "00s", "1930", "1940", "1950", "1960", "1970", "1980", "1990",
                    "1950s", "1960s", "1970s", "1980s", "1990s", "2000s", "50er", "60er", "70er",
                    "80er", "90er", "2000er", "anni60", "anni70", "anni80", "anni90", "annees60",
                    "annees70", "annees80", "annees90", "fifties", "sixties", "seventies",
                    "eighties", "nineties"]),
        ("Rock", "rock", ["rock", "altrock", "alternative", "alternativerock", "adultalternative",
                  "adultalbumalternative", "indie", "indierock", "grunge", "punk", "punkrock",
                  "postpunk", "hardrock", "postrock", "garagerock", "rockpop", "progressiverock",
                  "artrock", "psychedelic", "psychedelicrock", "newwave", "рок", "gothic",
                  "gothrock", "emo", "modernrock", "southernrock", "folkrock", "rockalternativo",
                  "rockmusik"]),
        ("Classic Rock", "classic rock", ["classicrock", "albumrock", "aor", "classicrockhits", "oldrock",
                          "rockclassics", "60srock", "70srock", "80srock", "rockandroll",
                          "rocknroll"]),
        ("Metal", "metal", ["metal", "heavymetal", "deathmetal", "blackmetal", "thrash", "thrashmetal",
                   "metalcore", "deathcore", "grindcore", "powermetal", "numetal", "gothicmetal",
                   "symphonicmetal", "folkmetal", "speedmetal", "progressivemetal", "doom",
                   "hardcore", "металл"]),
        ("Dance", "dance", ["dance", "edm", "club", "clubmusic", "clubbing", "clubdance", "dancefloor",
                   "party", "partymix", "houseparty", "disco", "italodisco", "nudisco", "discofox",
                   "dancehits", "eurodance", "danceclassics", "dancepop", "popdance", "rave",
                   "mashup", "remix", "remixes", "remixed", "djremix", "djmix", "djmixes", "djsets",
                   "djset", "deejay", "megamix", "hardstyle", "beachclub", "ibiza", "танцевальная"]),
        ("Electronic", "electronic", ["electronic", "electronica", "electro", "electronicdancemusic", "house",
                        "deephouse", "techhouse", "electrohouse", "progressivehouse",
                        "soulfulhouse", "tribalhouse", "futurehouse", "techno", "hypertechno",
                        "hardtechno", "dubtechno", "trance", "psytrance", "progressivepsytrance",
                        "goa", "ambient", "chillout", "chill", "lounge", "downtempo", "dubstep",
                        "drumandbass", "drumbass", "dnb", "dandb", "jungle", "neurofunk",
                        "techstep", "breakbeat", "ukgarage", "ukfunky", "minimal", "idm", "triphop",
                        "synthwave", "retrowave", "vaporwave", "moombahton", "industrial",
                        "электронная", "чилаут"]),
        ("Hip-Hop", "hop", ["hiphop", "rap", "urban", "trap", "hiphoprap", "raphiphop", "oldschoolhiphop",
                     "gangsta", "grime", "drill", "boombap", "rapfrancais", "deutschrap", "рэп",
                     "хип-хоп"]),
        ("R&B / Soul", "soul", ["rb", "rnb", "randb", "rhythmandblues", "soul", "funk", "funky", "funksoul",
                        "motown", "neosoul", "rbsoul", "rnbsoul", "soulfunk", "groove",
                        "quietstorm", "urbancontemporary", "blackmusic", "gospel"]),
        ("Jazz", "jazz", ["jazz", "smoothjazz", "softjazz", "contemporaryjazz", "classicjazz", "latinjazz",
                  "bebop", "swing", "bigband", "dixieland", "ragtime", "jazzfunk", "acidjazz",
                  "fusion", "vocaljazz", "nujazz", "pianojazz", "джаз"]),
        ("Blues", "blues", ["blues", "deltablues", "chicagoblues", "electricblues", "jumpblues", "soulblues",
                   "bluesrock", "rhythmblues"]),
        ("Country", "country", ["country", "americana", "bluegrass", "countryhits", "classiccountry",
                     "hotcountry", "todayscountry", "traditionalcountry", "countryclassics",
                     "countrygold", "honkytonk", "folk", "singersongwriter", "roots", "newcountry",
                     "фолк"]),
        ("Classical", "classical", ["classical", "klassik", "klassisch", "klassiek", "klassieke", "clasica",
                       "classique", "musicaclasica", "opera", "operetta", "baroque", "symphonic",
                       "symphony", "orchestral", "choral", "chamber", "earlymusic", "romantic",
                       "piano", "классическая"]),
        ("Latin", "latin", ["latin", "latino", "salsa", "reggaeton", "regueton", "bachata", "merengue",
                   "cumbia", "tango", "latinpop", "musicalatina", "musicalatinoamericana",
                   "ranchera", "rancheras", "banda", "bandanortena", "nortena", "nortenas",
                   "norteno", "sierreno", "corrido", "corridos", "grupera", "grupero", "vallenato",
                   "tropical", "musicatropical", "tropicalmusic", "balada", "baladas", "baladapop",
                   "baladaromantica", "romantica", "romanticas", "musicaromantica", "urbano",
                   "musicaurbana", "mariachi", "bossanova", "samba", "sertanejo", "mpb", "forro",
                   "pagode", "musicamexicana", "mexicanmusic", "mexicana", "musicaregional",
                   "regionalmexicana", "regionalmexican", "musicaregionalmexicana",
                   "musicapopularmexicana", "musicatradicionalmexicana", "traditionalmexicanmusic",
                   "musicaenespanol", "popenespanol", "baladasenespanol", "clasicosenespanol",
                   "rockenespanol"]),
        ("Reggae", "reggae", ["reggae", "dancehall", "ska", "dub", "rootsreggae", "raggae", "ragga",
                    "raggamuffin", "loversrock"]),
        ("Talk", "talk", ["talk", "talkshow", "spokenword", "spoken", "chat", "discussion", "debate",
                  "politics", "politicaltalk", "conservativetalk", "podcast", "conversation",
                  "interview", "entrevistas", "comedy", "gesprek", "hablada", "radiohablada",
                  "drama", "radiodrama", "audiodrama", "oldtimeradio", "otr", "storytelling",
                  "audiobooks", "hoerspiel", "horspiel", "culture", "cultural", "cultura", "kultur",
                  "education", "educational", "educativa", "разговорное", "разговорный", "юмор"]),
        ("News", "news", ["news", "newstalk", "information", "currentaffairs", "headlines", "allnews",
                  "actualites", "actualite", "nachrichten", "noticias", "noticiero", "noticieros",
                  "informativa", "informativo", "informacion", "actualidad", "nieuws", "nyheter",
                  "notizie", "journal", "info", "weather", "wetter", "traffic", "verkehr", "npr",
                  "новости", "информационное", "информационный", "新闻"]),
        ("Sports", "sport", ["sport", "sports", "sportstalk", "sportnews", "football", "soccer", "futbol",
                    "basketball", "baseball", "hockey", "rugby", "cricket", "motorsport",
                    "formula1", "tennis", "golf", "esports", "deporte", "deportes", "deportivo",
                    "deportiva", "voetbal"]),
    ];

    /// <summary>The menu, in the table's own order. It is read from the table rather than written out a
    /// second time. So a genre cannot be on the menu without the spellings and the search term that make it
    /// work.</summary>
    public static readonly IReadOnlyList<string> All = [.. Table.Select(row => row.Genre)];

    /// <summary>The table turned round, so that it can be looked up by tag. This is the direction that
    /// every question comes from: a station holds tags and wants to know what kind of station it is. It is
    /// built once. A page of stations has a hundred entries, and each of them has about a dozen
    /// tags.</summary>
    private static readonly Dictionary<string, string> ByTag = Build();

    private static Dictionary<string, string> Build()
    {
        var byTag = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (genre, _, tags) in Table)
        {
            // The label itself, so a station tagged with exactly the menu's wording finds its own entry,
            // without the table having to repeat it.
            byTag.TryAdd(Fold(genre), genre);
            foreach (var tag in tags)
                byTag.TryAdd(Fold(tag), genre);
        }
        return byTag;
    }
}
