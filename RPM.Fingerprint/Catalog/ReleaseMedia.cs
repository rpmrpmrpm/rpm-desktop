namespace RPM.Fingerprint.Catalog;

/// <summary>
/// What a release was pressed, cut, taped or uploaded on. This is the one facet that decides whether the
/// pressing in the user's hands can surface at all.
///
/// <para>It has to <i>filter</i> rather than merely rank: a famous album is mostly CDs in any
/// catalogue (thirteen to one for "Rumours"), so a record, a cassette or a reel never reaches the
/// first page of an unfiltered search. Every value here is a facet both catalogues can answer:
/// MusicBrainz as a quoted <c>format:</c> phrase, and Discogs as its own <c>format</c> facet.</para>
///
/// <para>Part of the wire contract, so it lives beside the neutral <c>Catalog*</c> records. The
/// desktop app and the API must agree on the token, and the surest way to agree is to share the
/// enum.</para>
/// </summary>
public enum ReleaseMedia
{
    /// <summary>No filter. Every pressing of the album is included, whatever it was released on.</summary>
    Any = 0,

    /// <summary>Records, every size, shellac 78s included.</summary>
    Vinyl,

    Cd,
    Cassette,

    /// <summary>Download and streaming releases: MusicBrainz "Digital Media", Discogs "File".</summary>
    Digital,

    Sacd,
    Dvd,
    BluRay,
    ReelToReel,
    EightTrack,
}

/// <summary>
/// The media filter as it travels and as it reads back: one token on the wire, and one test against
/// the format string a catalogue hands back.
/// </summary>
public static class ReleaseMediaInfo
{
    /// <summary>Every value the lookup offers, in the order it offers them: the filter off, then the
    /// media a record collector actually holds, commonest first.</summary>
    public static IReadOnlyList<ReleaseMedia> All { get; } =
    [
        ReleaseMedia.Any, ReleaseMedia.Vinyl, ReleaseMedia.Cd, ReleaseMedia.Cassette,
        ReleaseMedia.Digital, ReleaseMedia.Sacd, ReleaseMedia.Dvd, ReleaseMedia.BluRay,
        ReleaseMedia.ReelToReel, ReleaseMedia.EightTrack,
    ];

    /// <summary>What goes on the query string. Null for <see cref="ReleaseMedia.Any"/>, which is the
    /// absence of a filter rather than a filter for everything.</summary>
    public static string? Token(this ReleaseMedia media) => media switch
    {
        ReleaseMedia.Vinyl => "vinyl",
        ReleaseMedia.Cd => "cd",
        ReleaseMedia.Cassette => "cassette",
        ReleaseMedia.Digital => "digital",
        ReleaseMedia.Sacd => "sacd",
        ReleaseMedia.Dvd => "dvd",
        ReleaseMedia.BluRay => "bluray",
        ReleaseMedia.ReelToReel => "reel",
        ReleaseMedia.EightTrack => "8track",
        _ => null,
    };

    /// <summary>Reads a token back. Anything unknown, such as an older or newer client or a hand-typed URL,
    /// is <see cref="ReleaseMedia.Any"/>: a filter nobody understands must not silently become a
    /// different one.</summary>
    public static ReleaseMedia Parse(string? token) => (token ?? "").Trim().ToLowerInvariant() switch
    {
        "vinyl" => ReleaseMedia.Vinyl,
        "cd" => ReleaseMedia.Cd,
        "cassette" => ReleaseMedia.Cassette,
        "digital" or "file" => ReleaseMedia.Digital,
        "sacd" => ReleaseMedia.Sacd,
        "dvd" => ReleaseMedia.Dvd,
        "bluray" or "blu-ray" => ReleaseMedia.BluRay,
        "reel" => ReleaseMedia.ReelToReel,
        "8track" => ReleaseMedia.EightTrack,
        _ => ReleaseMedia.Any,
    };

    /// <summary>
    /// Whether a catalogue's format string ("12&quot; Vinyl", "Hybrid SACD", "File") is this medium.
    ///
    /// <para>Used for ordering, not for the search itself. The providers filter on the server, so this
    /// only has to be right about what came back. The one trap is that "SACD" contains "CD", which is
    /// why the narrower media are checked before the broader ones.</para>
    /// </summary>
    public static bool Matches(this ReleaseMedia media, string? format)
    {
        if (media == ReleaseMedia.Any)
            return true;
        if (format is not { Length: > 0 } f)
            return false;

        bool Has(string s) => f.Contains(s, StringComparison.OrdinalIgnoreCase);

        return media switch
        {
            ReleaseMedia.Vinyl => Has("Vinyl") || Has("LP") || Has("Shellac"),
            ReleaseMedia.Sacd => Has("SACD"),
            ReleaseMedia.Dvd => Has("DVD"),
            ReleaseMedia.BluRay => Has("Blu-ray") || Has("Blu Ray"),
            // Everything a CD player reads, and nothing whose name merely ends in those two letters.
            ReleaseMedia.Cd => Has("CD") && !Has("SACD") && !Has("DVD"),
            ReleaseMedia.Cassette => Has("Cassette"),
            ReleaseMedia.Digital => Has("Digital") || Has("File") || Has("Download"),
            ReleaseMedia.ReelToReel => Has("Reel"),
            ReleaseMedia.EightTrack => Has("8-Track") || Has("8 Track"),
            _ => false,
        };
    }
}
