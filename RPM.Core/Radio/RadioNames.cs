using System.Globalization;
using System.Text;

namespace RPM.Core.Radio;

/// <summary>
/// The station's name, with the stream's specification removed.
///
/// <para><b>What taggers put in the name field.</b> "PureClassixx #Flac", "Intense Radio - We love
/// Dance #HQ# FLAC", "Smooth FM 91.5 - Melbourne - 91.5 FM (AAC+ 320k)". None of this is what the
/// station is called. It is the codec and the bitrate, written into the name because search matches on
/// it. The app already shows those two values on the line under every row, so keeping them in the name
/// repeats the same fact in the place with the least room.</para>
///
/// <para><b>Only clear specifications are removed.</b> A station is called Radio 538, or 100% NL, or
/// Studio Brussel. Numbers and punctuation in a name are usually part of the name. So a run is removed
/// only when it is a known quality word (a codec, a bitrate, HQ, HD). Everything else is kept exactly
/// as the broadcaster wrote it. Removing too much renames a station, which is worse than an untidy
/// line.</para>
/// </summary>
public static class RadioNames
{
    /// <summary>The name as it should be shown. Returns the original where there is nothing to remove,
    /// and never returns an empty string: a name that is only specification is kept, because an empty
    /// row is worse than an untidy one.</summary>
    public static string Clean(string? name) => Strip(Headline(name ?? ""), name);

    /// <summary>
    /// The same, with the strapline kept: "Concertzender - Hard Bop" rather than "Concertzender".
    ///
    /// <para>Used for stations that need it to tell them apart. See <see cref="Distinguish"/>. Only the
    /// specification is removed.</para>
    /// </summary>
    public static string Full(string? name) => Strip(name ?? "", name);

    /// <summary>
    /// A list of stations, with the straplines put back on the ones that need them.
    ///
    /// <para>A strapline is noise until two stations share the same head. Then it is the only thing
    /// that tells them apart. Concertzender broadcasts "De Gehoorde Stilte" and "Hard Bop" as two
    /// stations, and a list that shows "Concertzender" twice looks like a bug. A station cannot know
    /// this about itself, because it depends on the other stations in the list. So it is decided here,
    /// once, where the whole list is available.</para>
    ///
    /// <para>The name is written onto the station, not worked out by each row. So the name travels with
    /// the station: when one is tuned, the banner, the dial and the row all show the same name.</para>
    /// </summary>
    public static IReadOnlyList<RadioStation> Distinguish(IReadOnlyList<RadioStation> stations)
    {
        if (stations.Count < 2)
            return stations;

        // Counted by what each station would print: the title set by the admin if there is one,
        // otherwise the cleaned name. A station with an edited title never gets a strapline, because
        // Display reads the edit first. It still counts here, so a station that would print the same
        // name as another one gets a strapline.
        var heads = new Dictionary<string, int>(stations.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var station in stations)
        {
            var head = StationEdits.Of(station)?.Name ?? Clean(station.Name);
            heads[head] = heads.TryGetValue(head, out var seen) ? seen + 1 : 1;
        }

        // Nothing shares a head, which is the usual case: return the list that came in, not a copy.
        if (!heads.Values.Any(count => count > 1))
            return stations;

        var told = new List<RadioStation>(stations.Count);
        foreach (var station in stations)
        {
            if (StationEdits.Of(station)?.Name is not null)
            {
                told.Add(station);
                continue;
            }

            var head = Clean(station.Name);
            var whole = Full(station.Name);

            told.Add(heads[head] > 1 && !string.Equals(head, whole, StringComparison.Ordinal)
                ? station with { ShownName = whole }
                : station);
        }

        return told;
    }

    /// <summary>
    /// The name reduced to what identifies the STATION rather than the entry. Specification is removed,
    /// case, punctuation and accents are flattened, and the words that say "this is a radio station"
    /// are dropped. The result is never shown. It is only used for comparison.
    /// </summary>
    /// <remarks>
    /// <para><b>The shown name is not enough.</b> Anyone may add a stream, so one station can arrive
    /// under whatever each contributor typed. A live directory held "Qmusic", "Q-Music", "Q music
    /// Nederland" and "QMUSIC NEDERLAND"; "Heart", "Heart FM" and "Heart UK"; "ffn" and "radio ffn";
    /// "Radio Veronica" and "Radio Veronica Live". Each group is one station. Squashing the name alone
    /// does not match any of them.</para>
    ///
    /// <para><b>Word by word, and the words must then be EQUAL.</b> Not a prefix. "Radio 10" and "Radio
    /// 10 non-stop" are two channels, and so are Qmusic and Qmusic Non-Stop. A prefix rule would delete
    /// the second of each pair. A word that is not noise is content, and content is what tells a
    /// broadcaster's channels apart.</para>
    ///
    /// <para><b>A country word is removed only after the first word.</b> At the start it is part of the
    /// brand: GB News, France Inter, Radio Kiss Kiss. Removing it there merged GB News into a station
    /// called News Radio UK. After the first word it is a qualifier: Qmusic Nederland, Classic FM UK,
    /// Virgin Radio Italia.</para>
    ///
    /// <para><b>Never empty.</b> A name made only of these words keeps them. Otherwise it would be
    /// filed under "", and every other station filed under "" would be folded into it.</para>
    /// </remarks>
    /// <param name="countryCode">The station's own ISO code. It decides whether a word is this
    /// country's name. Without it, no country word is removed.</param>
    public static string Fold(string? name, string? countryCode)
    {
        var words = Split(Full(name));
        if (words.Count == 0)
            return "";

        var geo = Geo.GetValueOrDefault(countryCode?.Trim().ToUpperInvariant() ?? "");

        var kept = new List<string>(words.Count);
        for (var i = 0; i < words.Count; i++)
        {
            if (Noise.Contains(words[i]))
                continue;

            if (i > 0 && geo is not null && geo.Contains(words[i]))
                continue;

            kept.Add(words[i]);
        }

        // If removing the country word leaves nothing, as in "Radio Nederland", keep the words that are
        // not noise, country words included. Every station needs a name to be filed under.
        if (kept.Count == 0)
            kept = [.. words.Where(w => !Noise.Contains(w))];

        return string.Concat(kept.Count > 0 ? kept : words);
    }

    /// <summary>
    /// The words of a name, in lower case, with accents removed and punctuation dropped.
    /// </summary>
    /// <remarks>
    /// Splits on anything that is not a letter or a digit, as <see cref="NameMatch.Squash"/> does. It
    /// uses the same Unicode test. An ASCII-only reader would turn "Вести FM" and "Ретро FM" into the
    /// one word "fm", and fold two Russian stations into one.
    /// </remarks>
    private static List<string> Split(string text)
    {
        var words = new List<string>();
        var word = new StringBuilder(12);

        foreach (var c in Deaccent(text))
        {
            if (char.IsLetterOrDigit(c) || c == '+')
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

    /// <summary>The same text with its diacritics removed, so "Rádio Comercial" and "Radio Comercial"
    /// are compared as one word.</summary>
    private static string Deaccent(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var bare = new StringBuilder(decomposed.Length);

        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                bare.Append(c);
        }

        return bare.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Words that say a thing is a radio station, or say how it sounds. They say nothing about WHICH
    /// station it is.
    /// </summary>
    /// <remarks>
    /// The codec and quality words are here as well as in <see cref="Words"/>. <see cref="Full"/>
    /// removes only a RUN of them. "QMUSIC NEDERLAND AAC HIGH" would come back as "QMUSIC NEDERLAND
    /// HIGH", because "HIGH" does not name a codec and so ends the run. Here it is removed as noise
    /// instead.
    /// </remarks>
    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "radio", "radios", "fm", "am", "hd", "sd", "dab", "live", "stream", "streaming",
        "online", "net", "web", "webradio", "digital", "channel", "station", "the", "and",
        "mp3", "aac", "aacp", "ogg", "opus", "vorbis", "flac", "wma", "hls", "high", "low",
        "hq", "sq", "lq", "hifi", "kbps", "kbit", "kb", "quality", "stereo", "official",
    };

    /// <summary>
    /// What a country is called in its own station names, by ISO code.
    /// </summary>
    /// <remarks>
    /// <para>This is a list of facts, not a rule, for the same reason as <see cref="RadioCountries"/>.
    /// Nothing turns "NL" into "Nederland". A country that is not listed keeps its qualifier. So a
    /// short list means a station is listed twice, never that two stations are folded into one.</para>
    ///
    /// <para><b>No bare two-letter codes.</b> They are ordinary words in some languages: "de" is
    /// French, and "in", "at" and "be" are English. Also "100% NL" is a station whose name the "NL" is
    /// part of. Only "uk" is listed, because British stations use it in their own names: Classic FM UK,
    /// LBC UK, Heart UK, Boom Radio UK, Gold Radio UK.</para>
    /// </remarks>
    private static readonly Dictionary<string, HashSet<string>> Geo = new(StringComparer.Ordinal)
    {
        ["NL"] = ["nederland", "nederlandse", "netherlands", "holland", "dutch"],
        ["GB"] = ["uk", "britain", "british", "england", "unitedkingdom"],
        ["US"] = ["usa", "america", "american"],
        ["DE"] = ["deutschland", "germany", "german"],
        ["FR"] = ["france", "french"],
        ["IT"] = ["italia", "italy", "italian", "italiana"],
        ["ES"] = ["espana", "spain", "spanish"],
        ["BE"] = ["belgie", "belgium", "belgique"],
        ["AT"] = ["osterreich", "austria"],
        ["CH"] = ["schweiz", "switzerland", "suisse", "svizzera"],
        ["SE"] = ["sverige", "sweden"],
        ["NO"] = ["norge", "norway"],
        ["DK"] = ["danmark", "denmark"],
        ["FI"] = ["suomi", "finland"],
        ["PT"] = ["portugal", "portuguesa"],
        ["PL"] = ["polska", "poland"],
        ["IE"] = ["ireland", "eire"],
        ["CA"] = ["canada"],
        ["AU"] = ["australia"],
        ["BR"] = ["brasil", "brazil"],
        ["MX"] = ["mexico"],
        ["IN"] = ["india"],
    };

    private static string Strip(string text, string? original)
    {
        if (text.Length == 0)
            return original ?? "";

        var cut = new System.Text.StringBuilder(text.Length);
        var at = 0;

        while (at < text.Length)
        {
            if (Marker(text, at) is { } end)
            {
                at = end;   // a specification: dropped
                continue;
            }

            cut.Append(text[at]);
            at++;
        }

        var left = Tidy(cut.ToString());
        return left.Length > 0 ? left : text;
    }

    /// <summary>
    /// The station's name up to its first subtitle. Everything after a spaced dash is a strapline, such
    /// as "Intense Radio - We love Dance" or "Smooth FM 91.5 - Melbourne - 91.5 FM". A row is not where
    /// a strapline is shown.
    /// </summary>
    /// <remarks>
    /// <para><b>Only spaced dashes count.</b> A hyphen inside a word is part of the word: Hi-Fi Radio,
    /// K-Rock, Radio X-tra, Rock-FM. Cutting at every dash would rename all of them. A renamed station
    /// is worse than a station with a strapline.</para>
    /// <para>The separators are the same ones the app reads in a station's track announcements (see
    /// <see cref="IcyTitle"/>). A tagger writing a name and a station announcing a track use the same
    /// punctuation.</para>
    /// </remarks>
    private static string Headline(string name)
    {
        var at = -1;
        foreach (var dash in Dashes)
        {
            var found = name.IndexOf(dash, StringComparison.Ordinal);
            if (found > 0 && (at < 0 || found < at))
                at = found;
        }

        // A name that starts with a dash has no head, so it keeps all of its text.
        return at > 0 ? name[..at].TrimEnd() : name;
    }

    /// <summary>The separators that start a strapline. Each one has a space on both sides.</summary>
    private static readonly string[] Dashes = [" - ", " – ", " — ", " -- ", " | "];

    /// <summary>
    /// The tags worth showing: the ones that say what a station PLAYS. Tags that say how the stream is
    /// served, or how the directory files the station, are dropped.
    /// </summary>
    /// <remarks>
    /// A tag that starts with "#" is directory housekeeping. "##browserfriendly" is a note about the
    /// stream's URL. A tag that is only specification is the codec and bitrate written a second time,
    /// next to the fields that hold them properly.
    /// </remarks>
    public static IReadOnlyList<string> Genres(IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0)
            return [];

        var kept = new List<string>(tags.Count);
        foreach (var tag in tags)
        {
            var trimmed = tag.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#' || AllQuality(trimmed))
                continue;

            kept.Add(trimmed);
        }

        return kept;
    }

    /// <summary>Where the specification starting at <paramref name="at"/> ends, or null where none
    /// starts there.</summary>
    private static int? Marker(string text, int at)
    {
        // Forms such as "#HQ", "#HQ#" and "#Flac". A directory search box treats the hash form as a
        // keyword, and a reader treats it as noise.
        if (text[at] == '#')
        {
            var end = at + 1;
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '+'))
                end++;

            if (IsQuality(text[(at + 1)..end]))
                return end < text.Length && text[end] == '#' ? end + 1 : end;

            return null;
        }

        // A bracketed group that is nothing but specification: "(AAC+ 320k)", "[MP3 128]".
        var close = text[at] switch { '(' => ')', '[' => ']', _ => '\0' };
        if (close != '\0')
        {
            var shut = text.IndexOf(close, at + 1);
            return shut > at && AllQuality(text[(at + 1)..shut]) ? shut + 1 : null;
        }

        // And a bare run of it, wherever it sits: "- MP3 128k", "| 320 kbps", "FLAC".
        if (!char.IsLetterOrDigit(text[at]) || (at > 0 && char.IsLetterOrDigit(text[at - 1])))
            return null;   // mid-word, so not the start of anything

        var run = at;
        var last = -1;
        var named = false;

        while (run < text.Length)
        {
            var word = run;
            while (word < text.Length && (char.IsLetterOrDigit(text[word]) || text[word] == '+'))
                word++;

            if (word == run)
                break;

            var it = text[run..word];
            if (IsQuality(it))
                named = true;
            else if (!IsBitrate(it))
                break;   // a word that is neither ends the run

            last = word;
            run = word;
            while (run < text.Length && (text[run] == ' ' || text[run] == '/'))
                run++;
        }

        // A run of bare numbers is not a specification. "538 TOP 50" and "Kiss 108" are names. The run
        // needs a word that NAMES one: a codec, a unit, or HQ.
        return named && last > at ? last : null;
    }

    /// <summary>Whether everything between a pair of brackets is specification, and at least one of it
    /// says so by name.</summary>
    private static bool AllQuality(string inside)
    {
        var named = false;

        foreach (var word in inside.Split([' ', '/', ',', '-'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsQuality(word))
                named = true;
            else if (!IsBitrate(word))
                return false;
        }

        return named;
    }

    /// <summary>
    /// Whether one word NAMES part of a stream's specification: a codec, a rate with its unit on it, or
    /// one of the words taggers write for "this one sounds good".
    /// </summary>
    private static bool IsQuality(string word)
    {
        var folded = word.Trim().TrimEnd('+').ToLowerInvariant();
        if (folded.Length == 0)
            return false;

        if (Words.Contains(folded))
            return true;

        // "128k", "320kbps", "1411k": a number with its unit attached.
        foreach (var unit in Units)
        {
            if (!folded.EndsWith(unit, StringComparison.Ordinal))
                continue;

            var rate = folded[..^unit.Length];
            if (rate.Length > 0 && IsBitrate(rate))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a word is a bare number in the band a stream is served at.
    /// </summary>
    /// <remarks>
    /// Never enough on its own. "538", "100" and "108" are all in the band, and all are station names.
    /// It counts only beside a word that names a specification. That is what makes "320 kbps" a rate
    /// and "Kiss 108" a station. It accepts DIGITS only. "90s" is a decade. Trimming the "s" to find a
    /// number would read a genre as a bitrate.
    /// </remarks>
    private static bool IsBitrate(string word)
    {
        foreach (var c in word)
            if (!char.IsAsciiDigit(c))
                return false;

        return word.Length > 0 && int.TryParse(word, out var rate) && rate is >= Slowest and <= Fastest;
    }

    /// <summary>The units a bitrate can end with. Longest first, so "kbps" is not read as a bare "k"
    /// with "bps" left over.</summary>
    private static readonly string[] Units = ["kbps", "kbit", "kbs", "kb", "k"];

    /// <summary>Closes up what a removal leaves: the double space where a specification stood, and the
    /// dash or pipe written in front of it.</summary>
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

        return run.ToString().Trim(Danglers);
    }

    /// <summary>The characters a name can end or start with once the specification attached to it is
    /// removed.</summary>
    private static readonly char[] Danglers = [' ', '-', '–', '|', ',', ':', '/', '(', ')', '[', ']'];

    /// <summary>Codecs, and the words for "this stream is a good one".</summary>
    private static readonly HashSet<string> Words = new(StringComparer.Ordinal)
    {
        "mp3", "aac", "aacp", "aac+", "he-aac", "heaac", "ogg", "opus", "vorbis", "flac", "wma", "wav",
        "alac", "mp2", "m4a", "hls",
        "hq", "hd", "sq", "lq", "hifi", "hi-fi", "highquality", "lowquality", "lofi", "lo-fi",
        "kbps", "kbit", "kb", "bitrate", "stereo", "mono", "cd", "cdq", "quality",
    };

    /// <summary>The range a stream's bitrate can be in. Numbers below it are frequencies or parts of a
    /// name, and numbers above it are not bitrates either.</summary>
    private const int Slowest = 8;

    private const int Fastest = 2000;
}
