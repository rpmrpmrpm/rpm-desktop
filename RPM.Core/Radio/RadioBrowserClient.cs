using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using RPM.Core.Net;

namespace RPM.Core.Radio;

/// <summary>
/// One tag to ask the directory for, and how it is to be read.
///
/// <para>Stated together, because the two read differently and the difference is the answer. Asked as a
/// whole word, "hop" finds only the stations tagged exactly that. Asked as a tag that holds the word, the
/// same word also finds "hiphop", "hip hop" and "hip-hop", which are three spellings of one thing. A caller
/// that names a tag has to say which it means.</para>
/// </summary>
public readonly record struct StationTag(string Name, bool Whole)
{
    /// <summary>Stations tagged exactly this and nothing else: "jazz" finds jazz and not jazz
    /// funk.</summary>
    public static StationTag Exactly(string name) => new(name, true);

    /// <summary>Stations with any tag holding this word: "metal" finds heavy metal, metalcore and death
    /// metal as well.</summary>
    public static StationTag Holding(string name) => new(name, false);
}

/// <summary>What to ask the directory for. Every field is optional. An empty query means "the most
/// listened-to stations there are", which is what the pane opens on.</summary>
public sealed record RadioQuery
{
    /// <summary>Substring of the station's name. The directory matches case-insensitively.</summary>
    public string? Name { get; init; }

    /// <summary>One genre tag, such as "jazz" or "drum and bass", and whether the directory is to match it
    /// whole. The tag list comes from <see cref="RadioBrowserClient.TagsAsync"/>.</summary>
    public StationTag? Tag { get; init; }

    /// <summary>ISO 3166-1 alpha-2 country code.</summary>
    public string? CountryCode { get; init; }

    /// <summary>Language name as the directory spells it ("english", "dutch").</summary>
    public string? Language { get; init; }

    public int Limit { get; init; } = 120;

    public int Offset { get; init; }
}

/// <summary>A genre tag or a country, with how many stations carry it. This is enough to build a filter
/// list that leads with the ones worth picking.</summary>
public sealed record RadioFacet(string Name, string Code, int StationCount);

/// <summary>
/// The station directory: <a href="https://api.radio-browser.info">Radio Browser</a>.
///
/// <para>It is the only open station directory that still works for this app. SHOUTcast's directory API
/// needs a registered developer id, and those are no longer issued. The Xiph Icecast directory
/// (dir.xiph.org) is no longer running. TuneIn has no public API. Radio Browser needs no key, its data is
/// CC0, and it is run by volunteers. That is also why this class never depends on one host. The service is
/// a pool of volunteer mirrors, and any of them can be down. So a call walks the pool until one answers,
/// and remembers which one did.</para>
/// </summary>
public sealed partial class RadioBrowserClient
{
    /// <summary>Where to start looking. <c>all.</c> is the pool's DNS round-robin and answers most of the
    /// time. The named mirrors are the fallback for when its record is stale. The live list replaces this
    /// the first time a call succeeds, so a mirror that is renamed or retired stops being tried without an
    /// app update.</summary>
    private static readonly string[] SeedHosts =
    [
        "all.api.radio-browser.info",
        "de1.api.radio-browser.info",
        "de2.api.radio-browser.info",
        "at1.api.radio-browser.info",
        "nl1.api.radio-browser.info",
    ];

    private readonly HttpClient _http;
    private readonly object _gate = new();
    private string[] _hosts = SeedHosts;
    private string _preferred = SeedHosts[0];

    /// <param name="http">Not disposed here. The caller owns it. Its <c>User-Agent</c> matters. The
    /// directory is run by volunteers and asks callers to identify themselves, so it can tell a busy app
    /// from a runaway script.</param>
    public RadioBrowserClient(HttpClient http)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("RPM/1.0");
    }

    /// <summary>Stations matching <paramref name="query"/>, most-listened first, with broken ones left
    /// out.</summary>
    public async Task<IReadOnlyList<RadioStation>> SearchAsync(RadioQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var path = new StringBuilder("json/stations/search?hidebroken=true&order=clickcount&reverse=true");
        Append(path, "name", query.Name);
        if (query.Tag is { Name.Length: > 0 } tag)
        {
            Append(path, "tagExact", tag.Whole ? "true" : "false");
            Append(path, "tag", tag.Name);
        }
        Append(path, "countrycode", query.CountryCode);
        Append(path, "language", query.Language);
        Append(path, "limit", Math.Clamp(query.Limit, 1, 1000).ToString());
        if (query.Offset > 0) Append(path, "offset", query.Offset.ToString());

        var dtos = await GetAsync(path.ToString(), RadioBrowserJson.Default.StationDtoArray, Reading.Stored, ct) ?? [];
        var stations = new List<RadioStation>(dtos.Length);
        foreach (var dto in dtos)
        {
            if (ToStation(dto) is { } station)
                stations.Add(station);
        }
        return stations;
    }

    /// <summary>
    /// The stations with these uuids, in the directory's order, with broken ones included.
    ///
    /// <para>This is for the stations that an admin filed under a genre that their directory tags do not
    /// name (<see cref="StationEdits.Set.Retagged"/>). A genre search never returns them, so they are asked
    /// for by uuid and added to its answer. Broken ones are not left out, because an admin chose these by
    /// name. The fold still prefers a reachable stream where there is one.</para>
    ///
    /// <para>The uuids are asked for a hundred at a time, which keeps the address under 4 KB.</para>
    /// </summary>
    public async Task<IReadOnlyList<RadioStation>> ByUuidAsync(
        IReadOnlyList<string> uuids, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(uuids);

        var stations = new List<RadioStation>(uuids.Count);
        foreach (var chunk in uuids.Where(u => !string.IsNullOrWhiteSpace(u)).Chunk(100))
        {
            var path = new StringBuilder("json/stations/byuuid?");
            path.Append("uuids=").Append(string.Join(',', chunk.Select(Uri.EscapeDataString)));

            var dtos = await GetAsync(path.ToString(), RadioBrowserJson.Default.StationDtoArray, Reading.Stored, ct) ?? [];
            foreach (var dto in dtos)
            {
                if (ToStation(dto) is { } station)
                    stations.Add(station);
            }
        }

        return stations;
    }

    /// <summary>The genre tags worth showing, biggest first. The long tail is cut. The directory holds tens
    /// of thousands of tags, and almost all of them are typos with one station behind them.</summary>
    public async Task<IReadOnlyList<RadioFacet>> TagsAsync(int minStations = 200, CancellationToken ct = default)
    {
        var dtos = await GetAsync("json/tags", RadioBrowserJson.Default.FacetDtoArray, Reading.Stored, ct) ?? [];
        return [.. dtos
            .Where(d => !string.IsNullOrWhiteSpace(d.Name) && d.StationCount >= minStations)
            .OrderByDescending(d => d.StationCount)
            .Select(d => new RadioFacet(d.Name!, d.Name!, d.StationCount))];
    }

    /// <summary>The countries that have stations, most first.</summary>
    public async Task<IReadOnlyList<RadioFacet>> CountriesAsync(CancellationToken ct = default)
    {
        var dtos = await GetAsync("json/countries", RadioBrowserJson.Default.CountryDtoArray, Reading.Stored, ct) ?? [];
        return [.. dtos
            .Where(d => !string.IsNullOrWhiteSpace(d.Name) && !string.IsNullOrWhiteSpace(d.Code))
            .OrderByDescending(d => d.StationCount)
            .Select(d => new RadioFacet(d.Name!, d.Code!.ToUpperInvariant(), d.StationCount))];
    }

    /// <summary>
    /// Tells the directory that a station was actually played. This is how its ranking is fed. The click
    /// counts that every app orders by come from these calls, so a reader that never reports is only using
    /// other people's votes. This is best-effort. It never throws and never blocks tuning.
    /// </summary>
    public async Task ReportPlayedAsync(string stationUuid, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(stationUuid))
            return;
        try { await GetAsync($"json/url/{Uri.EscapeDataString(stationUuid)}", RadioBrowserJson.Default.ClickDto, Reading.PastTheStore, ct); }
        catch { /* the directory's bookkeeping is not the listener's problem */ }
    }

    private static void Append(StringBuilder path, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;
        path.Append('&').Append(key).Append('=').Append(Uri.EscapeDataString(value.Trim()));
    }

    /// <summary>
    /// One GET, against whichever mirror answers. The mirror that worked last time is tried first, and the
    /// rest are tried in order. The first success also refreshes the mirror list. So the list in use is the
    /// one that exists now, and not the one that existed when the app was released. Only when every mirror
    /// has failed does the last error reach the caller.
    /// </summary>
    /// <param name="shape">What comes back. It is named at the call, because a published build is Native
    /// AOT and cannot describe a type argument on its own.</param>
    /// <param name="reading">Whether a cached answer may stand in, where the caller's client has a cache
    /// (the desktop's does). It is stated at every call, with no default.</param>
    private async Task<T?> GetAsync<T>(string path, JsonTypeInfo<T> shape, Reading reading, CancellationToken ct)
    {
        string[] hosts;
        string first;
        lock (_gate) { hosts = _hosts; first = _preferred; }

        Exception? last = null;
        foreach (var host in Ordered(hosts, first))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"https://{host}/{path}");
                if (reading == Reading.PastTheStore)
                    request.Refresh();

                using var response = await _http.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadFromJsonAsync(shape, ct);
                lock (_gate) _preferred = host;
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;   // this mirror is down, so try the next
            }
        }

        throw new RadioDirectoryUnavailableException(last);
    }

    /// <summary>
    /// Whether a directory call may be answered from the caller's HTTP cache.
    ///
    /// <para>Searches, tags, countries and the mirror list are questions, and a recent answer is still an
    /// answer. A play report is not a question. If the cache answered it, the report would never reach the
    /// directory. So play reports always bypass the cache.</para>
    /// </summary>
    private enum Reading
    {
        Stored,
        PastTheStore,
    }

    /// <summary>The mirror that worked last, then everything else, without repeating it.</summary>
    private static IEnumerable<string> Ordered(string[] hosts, string first)
    {
        yield return first;
        foreach (var host in hosts)
        {
            if (!string.Equals(host, first, StringComparison.OrdinalIgnoreCase))
                yield return host;
        }
    }

    /// <summary>Replaces the mirror pool with the live one. It is called after a successful call, so a
    /// failure here is only ever a missed refresh.</summary>
    public async Task RefreshMirrorsAsync(CancellationToken ct = default)
    {
        try
        {
            var servers = await GetAsync("json/servers", RadioBrowserJson.Default.ServerDtoArray, Reading.Stored, ct);
            var names = servers?
                .Select(s => s.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (names is { Length: > 0 })
                lock (_gate) _hosts = names;
        }
        catch { /* keep the seed list */ }
    }

    /// <summary>Rejects what cannot be played or cannot be told apart: no URL, no name, no id.</summary>
    private static RadioStation? ToStation(StationDto dto)
    {
        var url = !string.IsNullOrWhiteSpace(dto.UrlResolved) ? dto.UrlResolved : dto.Url;
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(dto.Name) || string.IsNullOrWhiteSpace(dto.Uuid))
            return null;

        return new RadioStation
        {
            Uuid = dto.Uuid!,
            Name = dto.Name!.Trim(),
            StreamUrl = url!.Trim(),
            Homepage = Blank(dto.Homepage),
            Favicon = Blank(dto.Favicon),
            Country = Blank(dto.Country),
            CountryCode = Blank(dto.CountryCode)?.ToUpperInvariant(),
            Language = Blank(dto.Language),
            Tags = SplitTags(dto.Tags),
            Codec = Blank(dto.Codec),
            BitrateKbps = Math.Max(0, dto.Bitrate),
            Votes = Math.Max(0, dto.Votes),
            ClickCount = Math.Max(0, dto.ClickCount),
            IsHls = dto.Hls != 0,
            Reachable = dto.LastCheckOk != 0,
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>The directory returns tags as one comma-separated string, with blanks and duplicates
    /// included.</summary>
    public static IReadOnlyList<string> SplitTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
            return [];
        var seen = new List<string>();
        foreach (var raw in tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tag = raw.ToLowerInvariant();
            if (!seen.Contains(tag, StringComparer.Ordinal))
                seen.Add(tag);
        }
        return seen;
    }

    private sealed record StationDto
    {
        [JsonPropertyName("stationuuid")] public string? Uuid { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("url")] public string? Url { get; init; }
        [JsonPropertyName("url_resolved")] public string? UrlResolved { get; init; }
        [JsonPropertyName("homepage")] public string? Homepage { get; init; }
        [JsonPropertyName("favicon")] public string? Favicon { get; init; }
        [JsonPropertyName("tags")] public string? Tags { get; init; }
        [JsonPropertyName("country")] public string? Country { get; init; }
        [JsonPropertyName("countrycode")] public string? CountryCode { get; init; }
        [JsonPropertyName("language")] public string? Language { get; init; }
        [JsonPropertyName("codec")] public string? Codec { get; init; }
        [JsonPropertyName("bitrate")] public int Bitrate { get; init; }
        [JsonPropertyName("votes")] public int Votes { get; init; }
        [JsonPropertyName("clickcount")] public int ClickCount { get; init; }
        [JsonPropertyName("hls")] public int Hls { get; init; }
        [JsonPropertyName("lastcheckok")] public int LastCheckOk { get; init; } = 1;
    }

    private sealed record FacetDto
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("stationcount")] public int StationCount { get; init; }
    }

    private sealed record CountryDto
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("iso_3166_1")] public string? Code { get; init; }
        [JsonPropertyName("stationcount")] public int StationCount { get; init; }
    }

    private sealed record ServerDto
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
    }

    private sealed record ClickDto
    {
        [JsonPropertyName("ok")] public bool Ok { get; init; }
    }
    /// <summary>Everything the directory answers with, as the source generator describes it.</summary>
    [JsonSerializable(typeof(StationDto[]))]
    [JsonSerializable(typeof(FacetDto[]))]
    [JsonSerializable(typeof(CountryDto[]))]
    [JsonSerializable(typeof(ServerDto[]))]
    [JsonSerializable(typeof(ClickDto))]
    private sealed partial class RadioBrowserJson : JsonSerializerContext;

}

/// <summary>Every mirror refused. Either the directory is down, or this computer is not on the internet.
/// Either way there is no station list to show, and the favourites are all the user has.</summary>
public sealed class RadioDirectoryUnavailableException(Exception? inner)
    : Exception("No Radio Browser mirror answered.", inner);
