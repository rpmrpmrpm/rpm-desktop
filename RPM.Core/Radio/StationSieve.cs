namespace RPM.Core.Radio;

/// <summary>Whether the dial should be carrying stations that advertise. <see cref="Either"/> is the
/// answer that asks nothing, and it is the one a sieve starts on.</summary>
public enum AdsWanted
{
    Either,
    Without,
    With,
}

/// <summary>
/// What the listener has narrowed the dial to: a format, a genre, and whether the station sells
/// anything. One value, carried whole, so that "no filter" is a stated answer rather than three fields
/// nobody set.
///
/// <para><b>It sieves what is listed.</b> Radio Browser answers a search with a page of stations and
/// this narrows that page, which is what makes the same three questions work over the saved presets
/// with no network at all, and what makes the count under the heading true.</para>
///
/// <para><b>The genre is also asked of the directory, by whoever lists the stations.</b> This type
/// cannot ask, because it only reads stations it is given. The first page is the most listened-to
/// stations, and it holds almost no metal stations at all. So a genre that only narrowed that page came
/// back empty, however well the tags were read. See <see cref="RadioGenres.Term"/> for the tag to ask
/// for. The sieve still runs over the answer. What it removes there is what the tag brought in. For
/// example, "reggae" as a tag that a station holds also finds reggaeton stations, and those are
/// Latin.</para>
///
/// <para><b>Nothing here is a claim about a station that did not say.</b> A station files its own codec
/// and its own tags, and a station that filed neither is not kept by a sieve that asks about them. See
/// <see cref="RadioAds.CarriesAds(IReadOnlyList{string}?, string?, string?)"/>, where the same rule is
/// written out for advertising.</para>
/// </summary>
public sealed record StationSieve
{
    /// <summary>Asking nothing of anybody: every station is kept. What the pane opens on.</summary>
    public static readonly StationSieve Off = new();

    /// <summary>The codec wanted, exactly as <see cref="Formats"/> spells it, or null for
    /// any.</summary>
    public string? Format { get; init; }

    /// <summary>The genre wanted, one of <see cref="RadioGenres.All"/>, or null for any.</summary>
    public string? Genre { get; init; }

    public AdsWanted Ads { get; init; } = AdsWanted.Either;

    /// <summary>Whether this sieve narrows anything at all. The button on the pane shows as active when
    /// it does, and a listing can skip the walk entirely when it does not.</summary>
    public bool IsOn => Format is not null || Genre is not null || Ads != AdsWanted.Either;

    /// <summary>
    /// Whether this station is one of the ones asked for. Each question is asked only when it was
    /// asked: a sieve with a format and nothing else says nothing about genre.
    /// </summary>
    public bool Keeps(RadioStation station)
    {
        ArgumentNullException.ThrowIfNull(station);

        if (Format is not null && !IsFormat(station.Codec, Format))
            return false;

        // The admin's tags where they set some (RadioStation.ShownTags). The ads rule below keeps the
        // directory's own.
        if (Genre is not null && !RadioGenres.Is(station.Genres, Genre))
            return false;

        return Ads switch
        {
            AdsWanted.With => RadioAds.CarriesAds(station.Tags, station.Name, station.Homepage),
            AdsWanted.Without => !RadioAds.CarriesAds(station.Tags, station.Name, station.Homepage),
            _ => true,
        };
    }

    /// <summary>The stations this sieve keeps, in the order they came. The list itself where nothing is
    /// being asked, which is the usual case and is not worth a copy.</summary>
    public IReadOnlyList<RadioStation> Through(IReadOnlyList<RadioStation> stations)
    {
        ArgumentNullException.ThrowIfNull(stations);
        return IsOn ? [.. stations.Where(Keeps)] : stations;
    }

    /// <summary>
    /// The formats a station can be asked for, in the order the menu offers them.
    ///
    /// <para>A stated list, rather than whatever codecs happen to be in the page on screen. A filter
    /// whose entries come and go as the search changes is a filter that nobody can learn.</para>
    ///
    /// <para>Four: the four codecs that a stream is actually served as. FLAC and DAB+ are not in the
    /// list. Broadcast DAB is not something an internet directory files at all. The few stations that
    /// stream lossless are not worth a menu entry, since it would come back empty for almost every
    /// dial.</para>
    /// </summary>
    public static IReadOnlyList<string> Formats { get; } = ["MP3", "AAC", "AAC+", "OGG"];

    /// <summary>The kinds of station that can be asked for: radio's own list, with talk, news and sport
    /// among them. It is not the record collection's shelves. See <see cref="RadioGenres"/>.</summary>
    public static IReadOnlyList<string> Genres => RadioGenres.All;

    /// <summary>
    /// Whether a station's codec is the one asked for.
    ///
    /// <para>A station files one string, and it is not always one codec. For example, "AAC+,H.264" is a
    /// video stream, and "MP3, OGG" is a station that serves both. So the field is cut on its
    /// separators, and each part answers for itself, EXACTLY. "AAC" and "AAC+" are two entries on the
    /// menu. A listener who picked one did not pick the other.</para>
    /// </summary>
    private static bool IsFormat(string? codec, string wanted)
    {
        if (string.IsNullOrWhiteSpace(codec))
            return false;

        foreach (var part in codec.Split([',', '/', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
            if (string.Equals(Fold(part), wanted, StringComparison.Ordinal))
                return true;

        return false;
    }

    /// <summary>One codec as the menu spells it. Directories spell the same codec in different ways.
    /// For example, "AACP" is AAC+, and "VORBIS" is OGG. Those spellings are mapped here, so a listener
    /// does not have to try each one.</summary>
    private static string Fold(string codec) => codec.Trim().ToUpperInvariant() switch
    {
        "AACP" or "AAC PLUS" or "HE-AAC" or "HEAAC" => "AAC+",
        "VORBIS" or "OGG VORBIS" => "OGG",
        "MPEG" or "MPEG3" => "MP3",
        var other => other,
    };
}
