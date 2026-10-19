using System.Text.Json;
using System.Text.Json.Serialization;

namespace StationLogos;

/// <summary>
/// One row of the Radio Browser dump, cut down to what the crawl reads.
///
/// <para>Not <c>RPM.Core.Radio.RadioStation</c>, on purpose: that is the app's type, merged and
/// cleaned by <c>RadioResults</c>, and this needs the directory's own fields exactly as stored,
/// including the ones the app removes.</para>
/// </summary>
public sealed record Station
{
    [JsonPropertyName("stationuuid")] public string StationUuid { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("favicon")] public string? Favicon { get; init; }
    [JsonPropertyName("homepage")] public string? Homepage { get; init; }
    [JsonPropertyName("country")] public string? Country { get; init; }
    [JsonPropertyName("countrycode")] public string? CountryCode { get; init; }

    /// <summary>The stream URL. Together with the name, it shows that two rows are one station. The
    /// crawl does not fetch it; it is here so the index can group rows, and the admin can hide a
    /// whole station, not only one of its rows.</summary>
    [JsonPropertyName("url_resolved")] public string? UrlResolved { get; init; }

    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("clickcount")] public int ClickCount { get; init; }

    /// <summary>Whether the directory's own checker reached the stream on its last check. The app
    /// queries with <c>hidebroken=true</c>, so other stations are never shown, and are not
    /// requested here.</summary>
    [JsonPropertyName("lastcheckok")] public int LastCheckOk { get; init; }

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}
