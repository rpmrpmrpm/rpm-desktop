using System.Text.Json.Serialization;
using RPM.Core.Net;
using RPM.Core.Radio;
using RPM.Desktop.Services.Catalog;

namespace RPM.Desktop.Presentation.Main.LeftPane.Models;

/// <summary>
/// Where rpm's own copy of a station's logo is kept.
///
/// <para><b>Why the server keeps the logos.</b> Finding a logo on the client takes work. The app
/// tries the logo that the station directory lists. If that gives nothing, it fetches the station's
/// own front page and reads the icon tags and the web app manifest that the page declares. Doing
/// that on every install means one page fetch per station, for the same sites, and a lot of traffic
/// to broadcasters. So the walk is done once, on the server. <c>tools/StationLogos</c> walks the
/// stations and files the answer. It uses the same <see cref="StationIcons"/> parser and the same
/// ranking. The logo arrives as a 512px mark, not as whatever the station happened to publish.</para>
///
/// <para><b>An address, not a fetch.</b> The logo address is handed to the walk as one more candidate.
/// So everything after it stays the same: the same fetch with the app's user agent, the same
/// measurement, the same ranking and the same caches. If the crawl could not settle a station, its
/// address answers 404. The walk then carries on to the directory's own logos, as it always does.</para>
/// </summary>
internal static partial class RadioLogoApi
{
    /// <summary>
    /// The address of this station's logo, or null where there is nothing to ask.
    ///
    /// <para>Null rather than a URL that cannot work: no uuid means the directory gave us a row
    /// without one, and no API configured means a build pointed somewhere else. Both are "ask the
    /// station's own logos instead", which is what the caller does with a null.</para>
    /// </summary>
    public static string? For(RadioStation station)
    {
        if (station is null || string.IsNullOrWhiteSpace(station.Uuid))
            return null;

        // The uuid comes from the directory and is about to become part of a URL. Checked rather
        // than trusted: a malformed one would be a request for something else entirely.
        if (!Guid.TryParse(station.Uuid, out var id))
            return null;

        // RpmApi.BaseUrl walks the same ladder every other caller does: RPM_API_URL for a dev
        // build, the shipped deployment otherwise.
        var url = RpmApi.BaseUrl() + $"radio/logo/{id:D}";

        // The stamp is added when a logo has been changed by hand. Without it the url stays the same.
        //
        // The app's own disk cache keeps an answer for thirty days, and it does not read Cache-Control
        // (see WebCachePolicy). WebCachePolicy.Vary would also defeat that cache. It is the app's own
        // mechanism for this job, and the artwork service uses it. The stamp goes in the url instead,
        // because Vary is invisible outside this process. The endpoint sends max-age=2592000, so any
        // layer that does read cache headers would keep the old picture for a month. Those layers are
        // a proxy, the url cache of the phone head, and a CloudFront behaviour for /radio/logo/* if one
        // is ever added. A stamp in the url is the one form of the change that all of them understand.
        // The API distribution's default behaviour is CachingDisabled, so today the CDN edge does not
        // keep these responses, and every logo request reaches the lambda.
        //
        // The stamp is empty for most stations, and for every reader until a logo is changed in the
        // admin. So the usual url is the same as it always was.
        return RadioState.LogoStamp is { Length: > 0 } stamp ? url + "?v=" + stamp : url;
    }

    /// <summary>
    /// Tells the API which logo addresses this machine found on a station's own site, so the next reader
    /// does not have to look.
    ///
    /// <para><b>Addresses, never pictures.</b> What is sent is a list of urls that the station's own site
    /// declared. The server fetches and judges them itself. A client that could post image bytes could put
    /// any picture on any station, for every reader. This endpoint can be reached by anything that holds
    /// an app key.</para>
    ///
    /// <para>The server checks that each address is on the station's own domain before it accepts one.
    /// What it accepts is held as PROPOSED until someone approves it on the admin page. This end cannot
    /// make anything go live, and it is not trusted to.</para>
    ///
    /// <para><b>Fire and forget, once per station per run.</b> Nothing waits on it, and no failure is
    /// reported. A reader is looking at a list of radio stations, not running an errand for us. It is sent
    /// only when the walk found something, which is the one case worth reporting.</para>
    /// </summary>
    public static void Propose(RadioStation station, IReadOnlyList<StationIcon> found)
    {
        if (station is null || found is null || found.Count == 0)
            return;

        if (!Guid.TryParse(station.Uuid, out var id))
            return;

        // One telling per station per run. The walk is already coalesced per host. A row can be realised
        // again after a re-walk, and there is no reason to say the same thing twice.
        lock (Told)
        {
            if (!Told.Add(id))
                return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var body = System.Text.Json.JsonSerializer.Serialize(
                    new Proposal([.. found.Take(6).Select(i => i.Url)]),
                    RadioLogoApiJson.Default.Proposal);

                using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                using var request = new HttpRequestMessage(
                    HttpMethod.Post, RpmApi.BaseUrl() + $"radio/logo/{id:D}")
                {
                    Content = content,
                };
                request.Headers.TryAddWithoutValidation(RpmApi.KeyHeader, RpmApi.Key);

                using var http = new HttpClient(RPM.Core.Net.Wire.GatedSockets())
                {
                    Timeout = TimeSpan.FromSeconds(15),
                };
                await http.SendAsync(request);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Nobody is waiting for this.
            }
        });
    }

    /// <summary>Stations already told about, so one run says each thing once.</summary>
    private static readonly HashSet<Guid> Told = [];

    /// <summary>The body <see cref="Propose"/> posts.</summary>
    private sealed record Proposal(
        [property: JsonPropertyName("urls")] string[] Urls);

    /// <summary>The one body this sends.
    /// A published build uses Native AOT, which serializes from this context and never by
    /// reflection.</summary>
    [JsonSerializable(typeof(Proposal))]
    private sealed partial class RadioLogoApiJson : JsonSerializerContext;
}
