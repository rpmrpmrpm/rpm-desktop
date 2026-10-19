namespace RPM.Core.Radio;

/// <summary>
/// One internet radio station, as the app knows it.
///
/// <para>The shape comes from the directory (Radio Browser), reduced to what a player needs: a stable
/// id to save it as a favourite, a name, a URL to open, and enough context to choose between two
/// stations with the same name. That context is the country, language, genre tags, codec and bitrate. A
/// favourite saved today still plays when the directory has changed, because the URL is saved with the
/// favourite and is not looked up again.</para>
/// </summary>
public sealed record RadioStation
{
    /// <summary>The directory's id for this station. It does not change when the name or URL changes,
    /// and favourites are saved by it.</summary>
    public required string Uuid { get; init; }

    public required string Name { get; init; }

    /// <summary>
    /// The name as it should be SHOWN: <see cref="Name"/> with the stream's specification and its
    /// strapline removed. "PureClassixx #Flac" is shown as PureClassixx, and "Intense Radio - We love
    /// Dance" is shown as Intense Radio. See <see cref="RadioNames"/>.
    ///
    /// <para>This is computed each time, not stored. <see cref="Name"/> stays exactly as the directory
    /// has it, because the raw name is what a favourite is saved under, what two entries for one
    /// station are merged by, and what search matches. Only the display changes.</para>
    ///
    /// <para>A title set by the admin comes first (<see cref="StationEdits"/>). It is read here on
    /// every draw, and is not written into the station. So a favourite saved before the edit shows the
    /// new title, and a favourite saved after it is still saved under the directory's name.</para>
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Display => StationEdits.Of(this)?.Name ?? ShownName ?? RadioNames.Clean(Name);

    /// <summary>
    /// The name to print INSTEAD, when the list this station was found in shows that the plain name
    /// would not tell it apart from another station. For example, both of Concertzender's stations
    /// reduce to "Concertzender", so both keep their straplines. Set by
    /// <see cref="RadioNames.Distinguish"/>. It is null for most stations, because nothing shares their
    /// name.
    ///
    /// <para>This is not saved with a preset. It describes a list, and the next list may be different.
    /// A favourite gets it back as soon as it is shown among its neighbours again.</para>
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ShownName { get; init; }

    /// <summary>
    /// The genre tags worth printing: <see cref="Tags"/> without the tags that are the stream's
    /// specification or the directory's own housekeeping.
    ///
    /// <para>Taggers write "flac" and "320k" into the tag list for the same reason they write it into
    /// the name: a search box matches on it. "##browserfriendly" is a note for the directory, not for a
    /// listener. The codec and the bitrate are shown next to the tags anyway, from the fields that hold
    /// them.</para>
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> Genres => RadioNames.Genres(ShownTags);

    /// <summary>
    /// The tags the station is filed under for genres: the admin's tags where the admin has set some
    /// (<see cref="StationEdits"/>), otherwise the directory's <see cref="Tags"/>.
    ///
    /// <para>Only the genre questions read this. The ads rule reads <see cref="Tags"/>, because
    /// "commercial free" is a tag written by the directory's crowd. An admin choosing a genre says
    /// nothing about adverts.</para>
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> ShownTags => StationEdits.Of(this)?.Tags ?? Tags;

    /// <summary>The URL the audio is played from. Where the directory has one, this is its
    /// <i>resolved</i> URL: the station's own URL after redirects and playlist unwrapping. That URL
    /// still works when the station's own domain has expired.</summary>
    public required string StreamUrl { get; init; }

    public string? Homepage { get; init; }

    /// <summary>The station's logo, if it publishes one. It is often missing or a dead link, so
    /// anything that shows it must handle both cases.</summary>
    public string? Favicon { get; init; }

    /// <summary>
    /// The other logos the directory has for this station, best first. Whatever draws a logo can fall
    /// back through them in order.
    ///
    /// <para><b>Why there is more than one.</b> The directory lists a stream, not a station. The
    /// several entries for one broadcaster can carry different pictures. For example, NPO Radio 1's
    /// 192k stream points at a Wikimedia file that refuses requests without browser credentials. Its
    /// 64k stream points at a plain PNG on the station's own site, which always works. A URL cannot be
    /// checked without requesting it. So <see cref="RadioResults"/> keeps all of them, and a row that
    /// fails on the first one tries the next.</para>
    ///
    /// <para>This is not saved with a preset. It is left over from one directory answer, and a
    /// favourite is one stream that somebody chose. It is rebuilt each time the directory is
    /// asked.</para>
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> OtherLogos { get; init; } = [];

    /// <summary>
    /// The uuids of the directory rows merged into this one, not counting its own.
    ///
    /// <para><b>What this is for.</b> A station reaches the list as several rows, and
    /// <see cref="RadioResults.OnePerStation"/> keeps one of them. The uuids of the others are not kept
    /// elsewhere. So anything that asks a question ABOUT THE STATION by uuid gives the wrong answer for
    /// each row that was merged away. For example, "NPO 2" and "NPO Radio 2" are one icecast mount
    /// filed twice. Hiding the first would say nothing about the second, so the station would stay in
    /// the list.</para>
    ///
    /// <para>Not saved with a preset, for the same reason as <see cref="OtherLogos"/>. It is left over
    /// from one directory answer and is rebuilt each time the directory is asked.</para>
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> AlsoFiledAs { get; init; } = [];

    /// <summary>Whether this row is the station with that uuid, under any of the ids the directory
    /// files it under.</summary>
    public bool Is(string uuid)
        => string.Equals(Uuid, uuid, StringComparison.OrdinalIgnoreCase)
           || AlsoFiledAs.Contains(uuid, StringComparer.OrdinalIgnoreCase);

    public string? Country { get; init; }

    /// <summary>ISO 3166-1 alpha-2, which is what the directory filters on.</summary>
    public string? CountryCode { get; init; }

    public string? Language { get; init; }

    /// <summary>Genre tags, lowercase, as the directory's crowd entered them.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    public string? Codec { get; init; }

    /// <summary>Advertised bitrate in kbps. Zero when the station does not say.</summary>
    public int BitrateKbps { get; init; }

    /// <summary>Votes given to the station by directory users.</summary>
    public int Votes { get; init; }

    /// <summary>How often the directory has handed this station out. This is the default sort order. It
    /// is the closest thing to "popular" that does not need an account.</summary>
    public int ClickCount { get; init; }

    /// <summary>True when the stream is HLS rather than a plain ICY/HTTP stream. It plays, but it
    /// carries no ICY metadata, so there is no live track title from it.</summary>
    public bool IsHls { get; init; }

    /// <summary>The result of the directory's most recent check. False is not fatal, because a station
    /// can be down between checks. But it is worth not offering first.</summary>
    public bool Reachable { get; init; } = true;

    /// <summary>
    /// "MP3", "AAC+", "FLAC": the codec alone.
    ///
    /// <para>This is what a row shows. The bitrate is left out on purpose. It varies between a
    /// station's several streams and says nothing a listener chooses by. The codec is the one word that
    /// does. <see cref="Quality"/> still holds both, for anything that needs both.</para>
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Format => string.IsNullOrWhiteSpace(Codec) ? "" : Codec.ToUpperInvariant();

    /// <summary>"MP3 128k": the format line, with the codec name and the bitrate. Empty when the
    /// station declares neither. It is derived, so it is not part of a saved preset.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Quality =>
        (string.IsNullOrWhiteSpace(Codec), BitrateKbps > 0) switch
        {
            (false, true) => $"{Codec!.ToUpperInvariant()} {BitrateKbps}k",
            (false, false) => Codec!.ToUpperInvariant(),
            (true, true) => $"{BitrateKbps}k",
            _ => "",
        };
}
