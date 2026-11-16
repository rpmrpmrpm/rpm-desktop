namespace RPM.Core.Cloud;

/// <summary>
/// The badges a person can show under their name on their profile page, and what unlocks each. The same ids,
/// order and rules as <c>ProfileBadges.kt</c> in rpm-android and <c>ProfileBadges.swift</c> in rpm-apple;
/// change all three together.
///
/// <para>Every badge is worked out from the person's own collection on their own device
/// (<see cref="Unlocked"/>), sent to the server (<c>PUT profile/badges</c>) and kept there, so a badge once
/// unlocked stays. The server only checks the ids' shape.</para>
/// </summary>
public static class RpmProfileBadges
{
    /// <summary>Every badge id, in the order the badges are listed.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        "first-spin", "crate-digger", "record-collector", "music-connoisseur", "living-archive", "museum-curator",
        "ten-thousand",
        "rock", "jazz", "classical", "hiphop", "electronic", "metal", "pop", "soul", "folk", "blues", "reggae", "latin",
        "genre-hopper", "omnivore",
    ];

    /// <summary>Albums needed for each size badge.</summary>
    private static readonly (string Id, int Least)[] Sizes =
    [
        ("first-spin", 1), ("crate-digger", 50), ("record-collector", 250),
        ("music-connoisseur", 1_000), ("living-archive", 2_500), ("museum-curator", 5_000),
    ];

    /// <summary>The genre badges, each with the shelves it counts (lower case; see <c>Genres.All</c>).</summary>
    private static readonly (string Id, string[] Shelves)[] GenreBadges =
    [
        ("rock", ["rock", "indie", "punk", "noise"]),
        ("jazz", ["jazz"]),
        ("classical", ["classical", "neo-classical", "opera"]),
        ("hiphop", ["hip-hop / rap", "rap", "trap", "old school hip hop"]),
        ("electronic", ["electronic / dance", "house", "techno", "club"]),
        ("metal", ["metal"]),
        ("pop", ["pop", "disco"]),
        ("soul", ["r&b", "soul", "funk"]),
        ("folk", ["folk", "country"]),
        ("blues", ["blues"]),
        ("reggae", ["reggae"]),
        ("latin", ["latin"]),
    ];

    /// <summary>Tracks needed for the ten-thousand badge.</summary>
    private const int TenThousand = 10_000;

    /// <summary>A genre badge needs this share of all albums ...</summary>
    private const double GenreShare = 0.2;

    /// <summary>... and at least this many albums of the genre.</summary>
    private const int GenreLeast = 25;

    /// <summary>A genre counts towards the range badges with this many albums.</summary>
    private const int RangeAlbums = 5;

    /// <summary>The shelves for music the app cannot file; they are not genres. "Other / Unknown" is the
    /// shared genre table's, "Other" the genres wall's card.</summary>
    private static readonly HashSet<string> Unfiled = new(StringComparer.Ordinal) { "other / unknown", "other" };

    /// <summary>The badges this collection has unlocked, in <see cref="All"/>'s order.</summary>
    /// <param name="genreAlbums">How many albums each genre shelf holds, by the shelf's name.</param>
    public static IReadOnlyList<string> Unlocked(int albums, int tracks, IReadOnlyDictionary<string, int> genreAlbums)
    {
        ArgumentNullException.ThrowIfNull(genreAlbums);
        var held = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, least) in Sizes)
        {
            if (albums >= least)
                held.Add(id);
        }
        if (tracks >= TenThousand)
            held.Add("ten-thousand");

        var byShelf = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (shelf, count) in genreAlbums)
        {
            var key = shelf.Trim().ToLowerInvariant();
            byShelf[key] = byShelf.GetValueOrDefault(key) + count;
        }
        foreach (var (id, shelves) in GenreBadges)
        {
            var count = shelves.Sum(s => byShelf.GetValueOrDefault(s));
            if (count >= GenreLeast && albums > 0 && count >= albums * GenreShare)
                held.Add(id);
        }
        var wide = byShelf.Count(s => !Unfiled.Contains(s.Key) && s.Value >= RangeAlbums);
        if (wide >= 10)
            held.Add("genre-hopper");
        if (wide >= 20)
            held.Add("omnivore");
        return All.Where(held.Contains).ToList();
    }
}

/// <summary>What a profile page is drawn on, as the server stores it (<c>pageGround</c>): empty for the
/// app's standard background, <c>color:#RRGGBB</c>, or <c>backdrop:{id}</c> with one of the app's
/// backgrounds (<see cref="BackdropIds"/>). A value this build cannot read is the standard one.</summary>
public readonly record struct RpmPageGround(uint? Rgb, string? Backdrop)
{
    /// <summary>The backgrounds a page can be drawn on, by the id the phones and the server use.</summary>
    public static readonly IReadOnlyList<string> BackdropIds = ["vinyl", "pegasus", "sheet-music", "gold"];

    public static RpmPageGround Standard => default;

    public bool IsStandard => Rgb is null && Backdrop is null;

    public static RpmPageGround Colour(uint rgb) => new(rgb & 0xFFFFFF, null);

    public static RpmPageGround Of(string? wire)
    {
        if (wire is { Length: 13 } && wire.StartsWith("color:#", StringComparison.Ordinal)
            && uint.TryParse(wire.AsSpan(7), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var rgb))
            return Colour(rgb);
        if (wire is { Length: > 9 } && wire.StartsWith("backdrop:", StringComparison.Ordinal))
            return new RpmPageGround(null, wire[9..]);
        return Standard;
    }

    /// <summary>The value as the server stores it.</summary>
    public string Wire => Rgb is { } rgb ? $"color:#{rgb:X6}" : Backdrop is { } id ? $"backdrop:{id}" : "";
}
