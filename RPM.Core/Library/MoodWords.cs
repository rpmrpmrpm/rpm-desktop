using RPM.Core.Metadata;

namespace RPM.Core.Library;

/// <summary>
/// The words a typed sentence can name a generated list with, and the decades it can name.
///
/// <para>This is a table and nothing else. <see cref="MoodQuery"/> holds the reading rules. This class
/// holds the words, so a word can be added without changing the parser.</para>
///
/// <para>No word here is a shelf name from <see cref="Genres.All"/>. "Disco", "House", "Club" and "Trap"
/// are all genres that a collection is filed by. A table that also read them as moods would answer a
/// genre question with a mood list. "Classical" is the one deliberate exception. It is both a shelf and
/// a kind. <see cref="MoodQuery"/> decides which one it is by checking whether the sentence gives a mood
/// in some other way.</para>
/// </summary>
internal static class MoodWords
{
    /// <summary>
    /// The phrases each kind answers to. Longer phrases are included, so that "never played" is read as one
    /// thing, and not as a negated "played".
    ///
    /// <para>Every phrase is folded through <see cref="GenreText.Fold"/> on the way into the lookup. So
    /// spelling, spacing and punctuation do not matter, here or in what a user types.</para>
    /// </summary>
    private static readonly (SmartPlaylistKind Kind, string[] Phrases)[] Said =
    [
        // The five moods. These are what this table was built for, and they have the most words. "Chill
        // out" and "chilled out" must not be added here. They fold onto Chillout, which is a style on the
        // Electronic / Dance shelf. A mood and a shelf that name different things cannot both be read
        // from one phrase. "Chill" and "chilled" fold onto neither, and they cover the same sentence.
        (SmartPlaylistKind.Calm,
            ["calm", "calming", "chill", "chilled", "mellow", "relaxed",
             "relaxing", "relax", "gentle", "soft", "quiet", "quieter", "laid back", "laidback",
             "low key", "unwind", "wind down", "winding down", "soothing", "peaceful", "easy going",
             "lazy", "rainy", "rainy day", "sunday morning", "slow", "slower"]),

        (SmartPlaylistKind.Energetic,
            ["energetic", "energy", "high energy", "loud", "louder", "hype", "hyped", "pumped",
             "pump up", "pump me up", "workout", "work out", "working out", "gym", "running",
             "for a run", "adrenaline", "intense", "aggressive", "heavy", "hard", "fast", "faster",
             "upbeat", "banger", "bangers", "full tilt"]),

        (SmartPlaylistKind.Danceable,
            ["danceable", "dancing", "to dance", "dance floor", "dancefloor", "party", "for a party",
             "groove", "groovy", "rave", "night out", "get moving"]),

        (SmartPlaylistKind.Focus,
            ["focus", "focused", "concentrate", "concentration", "study", "studying", "to study",
             "to work", "working", "deep work", "reading", "to read", "coding", "writing",
             "in the background", "background music", "steady"]),

        (SmartPlaylistKind.Sleep,
            ["sleep", "to sleep", "sleeping", "asleep", "fall asleep", "falling asleep", "bedtime",
             "for bed", "nap", "lullaby", "drift off", "insomnia"]),

        (SmartPlaylistKind.Classical,
            ["classical", "orchestral", "symphony", "symphonies", "concerto", "chamber music"]),

        // The listening lists. These have fewer words each, because the phrase people use for them is close
        // to the list's name.
        (SmartPlaylistKind.FavoriteTracks,
            ["favourites", "favorites", "favourite", "favorite", "loved", "liked", "hearts",
             "my hearts", "starred"]),

        (SmartPlaylistKind.MostPlayed,
            ["most played", "most listened", "top played", "played most", "my top", "all time"]),

        (SmartPlaylistKind.OnRepeat,
            ["on repeat", "heavy rotation", "in rotation", "lately", "this month"]),

        (SmartPlaylistKind.RecentlyAdded,
            ["recently added", "newly added", "just added", "new additions", "newest",
             "new stuff", "latest additions"]),

        (SmartPlaylistKind.RecentlyPlayed,
            ["recently played", "just played", "last played"]),

        (SmartPlaylistKind.NeverPlayed,
            ["never played", "unplayed", "never heard", "not played yet", "never listened"]),

        (SmartPlaylistKind.LovedAndForgotten,
            ["loved and forgotten", "forgotten favourites", "forgotten favorites"]),

        (SmartPlaylistKind.PlayedNotLoved,
            ["played not loved", "unhearted", "not favourited", "not favorited"]),

        (SmartPlaylistKind.Rediscover,
            ["rediscover", "forgotten", "used to play", "used to listen", "used to love",
             "long time"]),

        (SmartPlaylistKind.LateNight,
            ["late night", "after midnight", "night owl", "small hours", "3am"]),

        (SmartPlaylistKind.FirstListens,
            ["first listens", "first time", "newly discovered", "just discovered", "new finds"]),

        (SmartPlaylistKind.KeepSkipping,
            ["keep skipping", "always skip", "skipped"]),
    ];

    /// <summary>The decades, by the words people write them with. A bare four-digit year is read by
    /// <see cref="MoodQuery"/> as arithmetic, so 1975 needs no line here.</summary>
    private static readonly (int Decade, string[] Phrases)[] Eras =
    [
        (1960, ["60s", "1960s", "sixties"]),
        (1970, ["70s", "1970s", "seventies"]),
        (1980, ["80s", "1980s", "eighties"]),
        (1990, ["90s", "1990s", "nineties"]),
        (2000, ["00s", "2000s", "noughties", "aughts"]),
        (2010, ["10s", "2010s", "twenty tens"]),
    ];

    /// <summary>
    /// The words that turn the next thing said into something the listener does not want.
    ///
    /// <para>These are read only from tokens that no phrase above has claimed. That stops the "never" in
    /// "never played" from negating the "played" beside it. See <see cref="MoodQuery"/>, where this order
    /// is the rule.</para>
    /// </summary>
    private static readonly string[] Negators =
    [
        "not", "no", "nothing", "none", "without", "except", "avoid", "hate", "dont", "doesnt",
        "isnt", "less",
    ];

    private static readonly Dictionary<string, SmartPlaylistKind> KindByPhrase = BuildKinds();
    private static readonly Dictionary<string, int> DecadeByPhrase = BuildEras();
    private static readonly HashSet<string> NegatorKeys = [.. Negators.Select(GenreText.Fold)];

    private static Dictionary<string, SmartPlaylistKind> BuildKinds()
    {
        var map = new Dictionary<string, SmartPlaylistKind>(StringComparer.Ordinal);
        foreach (var (kind, phrases) in Said)
            foreach (var phrase in phrases)
                map[GenreText.Fold(phrase)] = kind;
        return map;
    }

    private static Dictionary<string, int> BuildEras()
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (decade, phrases) in Eras)
            foreach (var phrase in phrases)
                map[GenreText.Fold(phrase)] = decade;
        return map;
    }

    /// <summary>How many words the longest phrase in the table runs to. The window that the parser slides
    /// over a sentence has this width. It is read from the table, not written down beside it.</summary>
    internal static readonly int LongestPhrase =
        Said.SelectMany(s => s.Phrases)
            .Concat(Eras.SelectMany(e => e.Phrases))
            .Max(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);

    /// <summary>The kind a folded phrase names, or null.</summary>
    internal static SmartPlaylistKind? Kind(string folded) =>
        KindByPhrase.TryGetValue(folded, out var kind) ? kind : null;

    /// <summary>The decade a folded phrase names, or zero.</summary>
    internal static int Decade(string folded) =>
        DecadeByPhrase.TryGetValue(folded, out var decade) ? decade : 0;

    /// <summary>Whether a folded phrase negates what follows it.</summary>
    internal static bool Negates(string folded) => NegatorKeys.Contains(folded);

    /// <summary>Every phrase in the kind table. A test uses it to check the table against the kinds that
    /// exist, so no reader has to hold both in their head.</summary>
    internal static IEnumerable<(SmartPlaylistKind Kind, string Phrase)> EveryPhrase =>
        Said.SelectMany(s => s.Phrases.Select(p => (s.Kind, p)));
}
