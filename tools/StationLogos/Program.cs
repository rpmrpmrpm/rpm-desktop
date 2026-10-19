using System.Diagnostics;
using System.Text.Json;
using RPM.Core.Art;
using RPM.Core.Radio;
using StationLogos;

// ── what this is ────────────────────────────────────────────────────────────────────────────────
//
// The station logo crawl. It reads the Radio Browser dump, finds the best logo it can for every
// station, and stores the results so the API can serve them.
//
// About 21,000 stations have no logo in the directory but do have a homepage, on about 11,000
// distinct sites. Doing the search once here, instead of in every app install, does it for all users:
// the first visit is as fast as the second, and those broadcasters get requests once a month instead
// of once per user per visit.
//
// It reuses RPM.Core.Radio.StationIcons and RPM.Core.Art.ImageHeader instead of its own parsing. A
// crawler with its own parser would choose different logos than the client, and the difference
// would be hard to notice and hard to trace.

var dump = Arg("--dump") ?? Path.Combine(AppContext.BaseDirectory, "stations.json");
var outDir = Arg("--out") ?? Path.Combine(Environment.CurrentDirectory, "logo-crawl");
// High on purpose. This is I/O, not computation: the CPU is almost idle at any of these values, and
// the real limits are DNS, ephemeral ports and how long dead hosts take to time out. Above a few
// hundred, the extra requests only wait in the resolver, so more is not faster.
//
// The per-host limit of one applies whatever this is set to (see Fetcher.OnePerHost). It costs
// nothing across about 11,000 distinct sites. Where it does apply (a broadcaster whose page lists
// eight icons), sending them all at once would cause a 429 and no logo for that station.
var atOnce = int.TryParse(Arg("--at-once"), out var a) ? a : 256;
var seconds = int.TryParse(Arg("--timeout"), out var t) ? t : 10;
var only = int.TryParse(Arg("--top"), out var o) ? o : 0;
var phase = Arg("--phase") ?? "all";

// Retry the sites and addresses that did not answer last time.
//
// A separate switch because most timeouts are caused by this machine, not by the sites: with 256
// requests in parallel, this machine's own resolver is overloaded, and the deadline expires on a DNS
// lookup that was never made. When retried with fewer parallel requests, about two thirds of
// "timed out" hosts answer, with a median of about 0.6 seconds. So use it with a smaller --at-once.
//
// Only failures are retried. A site that answered is never asked twice.
var retry = Environment.GetCommandLineArgs()
    .Contains("--retry-failed", StringComparer.OrdinalIgnoreCase);

// Where the pictures are uploaded, and whether they are uploaded. --pretend does everything except
// the PutObject, so the fetch, the re-encode and the sizes can be checked before anything is written
// to a bucket that the whole app reads from.
var bucketName = Arg("--bucket") ?? "rpm-station-logos";
var pretend = Environment.GetCommandLineArgs()
    .Contains("--pretend", StringComparer.OrdinalIgnoreCase);

if (!File.Exists(dump))
{
    Console.Error.WriteLine($"no station dump at {dump}. Pass --dump <path>.");
    return 1;
}

Directory.CreateDirectory(outDir);

Console.WriteLine($"reading {dump}…");
var all = JsonSerializer.Deserialize<List<Station>>(
              File.ReadAllText(dump), Station.Json) ?? [];

// Only the stations the app can show. RPM queries the directory with hidebroken=true.
var pool = all.Where(s => s.LastCheckOk == 1).ToList();
if (only > 0)
    pool = [.. pool.OrderByDescending(s => s.ClickCount).Take(only)];

Console.WriteLine($"{all.Count} stations, {pool.Count} answering{(only > 0 ? $" (top {only})" : "")}");

using var fetch = new Fetcher(atOnce, seconds);
using var measured = new Notes<Measured>(Path.Combine(outDir, "measured.jsonl"), m => m.Url);
using var walked = new Notes<Walked>(Path.Combine(outDir, "walked.jsonl"), w => w.Host);

var clock = Stopwatch.StartNew();

// Filled by the upload phase and read by the report, so both agree about which stations have a
// logo.
Dictionary<string, string>? settled = null;

// ── phase one: measure every logo the directory already has ─────────────────────────────────────
//
// Two kilobytes each, not the whole picture: PNG gives its size in the first 24 bytes and the other
// formats soon after (ImageHeader.Worth). About 26,000 addresses measured this way is about 50MB of
// download instead of 800MB, and only the chosen pictures are fetched in full.
if (phase is "all" or "measure")
{
    var addresses = pool
        .Select(s => (s.Favicon ?? "").Trim())
        .Where(u => u.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(u => !measured.Has(u))
        .ToList();

    Console.WriteLine($"\nmeasuring {addresses.Count} logo addresses " +
                      $"({measured.Count} already done)…");
    await Run(addresses, atOnce, async url =>
    {
        measured.Add(await MeasureOne(fetch, url));
    }, "measured");
}

// ── phase two: walk the sites of the stations that have nothing ─────────────────────────────────
//
// Per HOST, never per station: a broadcaster's forty stations share one site and one result. The
// walk reads the page's own icon tags and the web app manifest it links to, exactly as the app
// does.
if (phase is "all" or "walk")
{
    var hosts = pool
        .Where(s => Needs(s, measured))
        .Select(s => HostOf(s.Homepage))
        .Where(h => h.Host.Length > 0)
        .DistinctBy(h => h.Host, StringComparer.OrdinalIgnoreCase)
        .Where(h => walked.Get(h.Host) is not { } had || (retry && !had.Reached))
        .ToList();

    Console.WriteLine($"\nwalking {hosts.Count} sites ({walked.Count} already done)…");
    await Run(hosts, atOnce, async site =>
    {
        var (status, html, why) = await fetch.TextAsync(site.Url, 512 * 1024, default);
        if (html is not { Length: > 0 })
        {
            walked.Add(new Walked(site.Host, Reached: false, [], why ?? $"http {status}"));
            return;
        }

        var found = StationIcons.From(html, site.Url).ToList();

        if (StationIcons.ManifestAt(html, site.Url) is { } manifest)
        {
            var (_, listed, _) = await fetch.TextAsync(manifest, 256 * 1024, default);
            if (listed is { Length: > 0 })
                found = [.. StationIcons.Best([.. found, .. StationIcons.FromManifest(listed, manifest)])];
        }

        if (StationIcons.WellKnown(site.Url) is { } guess
            && !found.Any(i => string.Equals(i.Url, guess, StringComparison.OrdinalIgnoreCase)))
            found.Add(new StationIcon(guess, StationIconKind.Icon));

        walked.Add(new Walked(site.Host, Reached: true, [.. found.Select(i => i.Url)], null));
    }, "walked");
}

// ── phase three: measure what the walk turned up ────────────────────────────────────────────────
if (phase is "all" or "walk")
{
    var fresh = walked.All()
        .Where(w => w.Reached)
        .SelectMany(w => w.Icons)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Where(u => measured.Get(u) is not { } had
                    || (retry && had.Why is "timed out"))
        .ToList();

    Console.WriteLine($"\nmeasuring {fresh.Count} addresses the walk turned up…");
    await Run(fresh, atOnce, async url =>
    {
        measured.Add(await MeasureOne(fetch, url));
    }, "measured");
}

// ── and the tally ───────────────────────────────────────────────────────────────────────────────
// ── phase three and a half: which candidates are a MARK ─────────────────────────────────────────
//
// The phases above judge a picture by its header: size, shape, format. None of that separates a
// transparent mark from an opaque home-screen tile, because both are PNGs with an alpha channel and
// only the border pixels show which one it is. The app asks the same question
// (TransparentMark.StandsAlone) to decide whether a background is drawn behind the picture, so a
// wrong answer is visible.
//
// Only where it can change the result. A station with one candidate gets that candidate whatever it
// is, so the pass is limited to stations with more than one candidate above the minimum size (about
// 14,000 distinct addresses, not every picture in the directory).
using var marked = new Notes<Marked>(Path.Combine(outDir, "marked.jsonl"), k => k.Url);

if (phase is "all" or "marks" or "upload")
{
    var contested = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var station in pool)
    {
        var over = CandidatesFor(station, measured, walked)
            .Where(m => !m.IsDrawing && m.IsPicture && m.Side >= 102)
            .Take(4)
            .ToList();

        if (over.Count > 1)
            foreach (var one in over)
                contested.Add(one.Url);
    }

    var toRead = contested.Where(u => !marked.Has(u)).ToList();
    Console.WriteLine($"\nreading the border of {toRead.Count} contested pictures " +
                      $"({marked.Count} already read)…");

    await Run(toRead, Math.Min(atOnce, 48), async url =>
    {
        var (status, body, _, why) = await fetch.WholeAsync(
            RadioLogo.Sized(url, Bucket.Edge) ?? url, default);

        marked.Add(new Marked(
            url,
            status is >= 200 and < 300 && body.Length > 0 ? Bucket.StandsAlone(body) : null));
    }, "read");
}

// ── phase four: fetch the winners, reshape them, and put them in the bucket ─────────────────────
//
// In one pass: fetched, re-encoded to 512px webp, uploaded, released. Nothing is written to this
// machine, because about 21,000 chosen pictures at an average of 108KB is about 2.3GB.
//
// Keyed by the IMAGE, not the station: about 36,000 stations share about 21,000 pictures, so a file
// per station would store most of them twice.
if (phase is "all" or "upload")
{
    var wanted = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    var settledOn = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var station in pool)
    {
        if (BestFor(station, measured, walked, marked) is { } best)
        {
            (wanted.TryGetValue(best.Url, out var list) ? list : wanted[best.Url] = [])
                .Add(station.StationUuid);
            settledOn[station.StationUuid] = best.Url;
        }
    }

    // ── a broadcaster's other streams wear the same mark ────────────────────────────────────────
    //
    // Joe 70s & 80s is on joe.nl and Joe has a logo; KINK Distortion is on kink.nl and KINK has one.
    // The walk treats a host as one unit, and the RESULT is shared the same way, so a variant stream
    // gets its parent station's logo instead of the default antenna. About 5,000 stations with no
    // logo are on a host where another station already has a mark.
    //
    // Only when the host belongs to ONE BROADCASTER, which is what the limit is for. laut.fm has 664
    // stations, zeno.fm 527, facebook.com 382; those are platforms, and sharing a mark across them
    // would put one broadcaster's logo on an unrelated station. Eight is well above the size of a
    // broadcaster's group of streams and far below any platform.
    const int MostStationsForOneBroadcaster = 8;

    var onHost = new Dictionary<string, List<Station>>(StringComparer.OrdinalIgnoreCase);
    foreach (var station in pool)
    {
        var (h, _) = HostOf(station.Homepage);
        if (h.Length > 0)
            (onHost.TryGetValue(h, out var kin) ? kin : onHost[h] = []).Add(station);
    }

    var inherited = 0;
    foreach (var station in pool)
    {
        if (settledOn.ContainsKey(station.StationUuid))
            continue;

        var (host, _) = HostOf(station.Homepage);
        if (host.Length == 0
            || !onHost.TryGetValue(host, out var family)
            || family.Count > MostStationsForOneBroadcaster)
            continue;

        // The best logo among the other stations on the host, by the same rule used to choose it.
        var sibling = family
            .Select(k => settledOn.GetValueOrDefault(k.StationUuid))
            .Where(u => u is not null)
            .Select(u => measured.Get(u!))
            .Where(m => m is not null)
            .OrderByDescending(m => m!.IsDrawing)
            .ThenByDescending(m => m!.CanBeClear)
            .ThenByDescending(m => Squarish(m!))
            .ThenByDescending(m => m!.Side)
            .FirstOrDefault();

        if (sibling is null)
            continue;

        (wanted.TryGetValue(sibling.Url, out var list) ? list : wanted[sibling.Url] = [])
            .Add(station.StationUuid);
        settledOn[station.StationUuid] = sibling.Url;
        inherited++;
    }

    if (inherited > 0)
        Console.WriteLine($"  {inherited} stations take a mark from another stream of the same broadcaster");

    using var s3 = new Amazon.S3.AmazonS3Client(Amazon.RegionEndpoint.EUWest1);
    var bucket = new Bucket(s3, bucketName, pretend);

    // A pretend run keeps its own results file. If it shared the real one, the next real upload
    // would think everything was already in the bucket, and would upload nothing.
    var stored = new Notes<Stored>(
        Path.Combine(outDir, pretend ? "stored-pretend.jsonl" : "stored.jsonl"), r => r.Source);
    var todo = wanted.Keys.Where(u => !stored.Has(u)).ToList();

    Console.WriteLine($"\n{(pretend ? "PRETENDING to upload" : $"uploading to s3://{bucketName}")}: " +
                      $"{todo.Count} images for {wanted.Values.Sum(v => v.Count)} stations " +
                      $"({stored.Count} already there)…");

    long bytes = 0;
    await Run(todo, Math.Min(atOnce, 32), async url =>
    {
        // The same URL rewrite as the measure phase, so the stored bytes are the measured bytes.
        var (status, body, _, why) = await fetch.WholeAsync(RadioLogo.Sized(url, Bucket.Edge) ?? url, default);
        if (status is < 200 or >= 300 || body.Length == 0)
        {
            stored.Add(new Stored(url, null, 0, why ?? $"http {status}"));
            return;
        }

        var drawing = measured.Get(url)?.IsDrawing ?? false;
        var key = NameFor(url, drawing);
        var size = await bucket.PutAsync(key, body, drawing, default);
        if (size is null)
        {
            stored.Add(new Stored(url, null, 0, "skia could not read it"));
            return;
        }

        Interlocked.Add(ref bytes, size.Value);
        stored.Add(new Stored(url, key, size.Value, null));
    }, "uploaded");

    // The catalogue the API serves from: one row per station, with the object and its source. The
    // source is kept per row on purpose, so if a broadcaster asks for their mark to be removed, it
    // can be removed exactly for that station.
    var rows = wanted
        .Select(pair => (pair.Key, pair.Value, Put: stored.Get(pair.Key)))
        .Where(x => x.Put is { Key: not null })
        .SelectMany(x => x.Value.Select(uuid => new
        {
            Station = uuid,
            Object = x.Put!.Key,
            Source = x.Key,
            measured.Get(x.Key)!.Width,
            measured.Get(x.Key)!.Height,
            Fetched = DateTime.UtcNow.ToString("O"),
        }))
        .ToList();

    // Every logo a person set manually, so the fan-out does not change it.
    //
    // Without this, the next crawl would overwrite every logo that a person chose, with no warning:
    // the settle picks a result for every station and copies it to {uuid}. This tool must never
    // replace a person's choice.
    var byHand = await bucket.KeysUnderAsync("manual/", default);
    if (byHand.Count > 0)
        Console.WriteLine($"  {byHand.Count} logos were chosen by hand; leaving those alone");

    // What is actually SERVED, which is not the same as what the ranking picked: a station's chosen
    // image can fail to upload, and then nothing is at its key. This is passed to the report and to
    // the index, so the coverage tally and the station list agree about which stations have a
    // logo.
    var serving = rows.ToDictionary(r => r.Station, r => r.Source, StringComparer.Ordinal);
    foreach (var station in byHand)
        serving[station] = "by hand";

    settled = serving;

    var path = Path.Combine(outDir, "catalogue.json");
    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(rows));

    var ok = stored.All().Count(r => r.Key is not null);
    Console.WriteLine($"  {ok} images, {rows.Count} stations, " +
                      $"{stored.All().Where(r => r.Key is not null).Sum(r => r.Bytes) / 1048576.0:F0}MB stored");
    Console.WriteLine($"  catalogue: {path}");

    // The station-to-host map that the API checks a contribution against.
    //
    // Published here, not looked up live, so that accepting a logo found by a user's own walk does
    // not depend on a volunteer directory being available. This check is the only thing that
    // separates "this broadcaster's site publishes this picture" from "any picture, served to all
    // users".
    if (!pretend)
    {
        var hosts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var station in pool)
        {
            var (h, _) = HostOf(station.Homepage);
            if (h.Length > 0)
                hosts[station.StationUuid] = h;
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(hosts);
        using var into = new MemoryStream(body);
        await s3.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest
        {
            BucketName = bucketName,
            Key = "reports/hosts.json",
            InputStream = into,
            ContentType = "application/json",
        });
        Console.WriteLine($"  hosts map: {hosts.Count} stations, {body.Length / 1024}KB");

        // ── and the list the admin searches ─────────────────────────────────────────────────
        //
        // Every station, not only the missing ones. An admin does not know station uuids, so
        // setting a logo manually starts with finding the station by name. The logos that most
        // need replacing are often ones that exist and are wrong, which are not on the missing
        // list.
        //
        // The source is reduced to its host. Full addresses would make the file about 8MB, and
        // the page only shows which site a mark came from.
        // ── which rows are the same station ────────────────────────────────────────────────
        //
        // The directory stores a STREAM per row. One station can have several rows, and the app
        // merges them before it draws anything, but the admin lists rows. So hiding one row may
        // hide a row the app never shows, and the station stays visible: for example Alswin
        // Ambient Music has two rows on one stream, and only the other row is inside the 120 that
        // the app requests.
        //
        // SameStreamGroupsOf, not GroupsOf: two rows with the same address, and nothing else.
        //
        // GroupsOf is what the APP merges with, and it also matches on the name, transitively.
        // That is correct for drawing a list but wrong for a permanent decision. For example,
        // icecast.omroep.nl/radio2-bb-aac is NPO Radio 2's stream, and one row stores it under the
        // name "NPO Radio 1", so GroupsOf makes the two stations one group, and hiding NPO Radio 2
        // would also hide all seven NPO Radio 1 rows.
        //
        // The cost of this stricter key is that a station stored under two different streams is
        // two groups and must be hidden twice. That is the safer error.
        var asStations = pool
            .Select(x => new RadioStation
            {
                Uuid = x.StationUuid,
                Name = x.Name,
                StreamUrl = (x.UrlResolved ?? x.Url ?? "").Trim(),
                CountryCode = x.CountryCode,
            })
            .ToList();

        var groups = RadioResults.SameStreamGroupsOf(asStations);

        // Only when a group has more than one row: for the others it would be a useless number per
        // station, and the whole file is read.
        var shared = new HashSet<int>(
            groups.GroupBy(g => g).Where(g => g.Count() > 1).Select(g => g.Key));

        var groupOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < pool.Count; i++)
        {
            if (shared.Contains(groups[i]))
                groupOf[pool[i].StationUuid] = groups[i];
        }

        Console.WriteLine($"  {groupOf.Count} stations are one of several rows for the same station");

        var index = pool
            .OrderByDescending(x => x.ClickCount)
            .Select(x =>
            {
                var from = byHand.Contains(x.StationUuid) ? "by hand"
                         : serving.TryGetValue(x.StationUuid, out var url)
                               ? HostOf(url).Host is { Length: > 0 } h2 ? h2 : "stored"
                               : null;

                // Where to look, and why nothing was found: included only for the stations that
                // have no logo, because only those need it. On all of about 60,000 rows the
                // homepages alone would add about 2MB; on the about 15,000 without a mark it is a
                // fifth of that.
                var lost = from is null;

                return new
                {
                    u = x.StationUuid,
                    n = x.Name,
                    c = x.Country ?? "",
                    k = x.ClickCount,
                    s = from,
                    h = lost ? x.Homepage : null,
                    f = lost ? x.Favicon : null,
                    w = lost ? WhyNone(x, measured, walked) : null,

                    // The station this row belongs to. Null when it is the only row.
                    g = groupOf.TryGetValue(x.StationUuid, out var group) ? group : (int?)null,
                };
            })
            .ToList();

        // Gzipped, and the admin decompresses it. About 60,000 rows is about 7.2MB uncompressed,
        // which adds about a second of cold start on a 512MB lambda for every new container. It
        // compresses to about a fifth of that. ContentEncoding is set so an HTTP reader decompresses
        // it automatically; the SDK does not, so StationService decompresses it in code.
        var flat = JsonSerializer.SerializeToUtf8Bytes(index);
        using var zipped = new MemoryStream();
        using (var gz = new System.IO.Compression.GZipStream(
                   zipped, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            gz.Write(flat);
        zipped.Position = 0;
        var packed = zipped.Length;    // read before the put: the SDK closes the stream it sends

        await s3.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest
        {
            BucketName = bucketName,
            Key = "reports/stations.json.gz",
            InputStream = zipped,
            ContentType = "application/json",
            Headers = { ContentEncoding = "gzip" },
        });
        Console.WriteLine($"  station index: {index.Count} stations, " +
                          $"{index.Count(r => r.s is not null)} with a logo, " +
                          $"{flat.Length / 1024}KB as {packed / 1024}KB");
    }

    // ── and the same pictures again, keyed by station ───────────────────────────────────────────
    //
    // So the API can answer /radio/logo/{uuid} with one read and no lookup table. A server-side
    // copy, so nothing is fetched twice and nothing is downloaded to this machine.
    var fanned = new Notes<Fanned>(
        Path.Combine(outDir, pretend ? "fanned-pretend.jsonl" : "fanned.jsonl"), f => f.Station);

    var copies = rows.Where(r => !fanned.Has(r.Station)).ToList();
    Console.WriteLine($"\ncopying {copies.Count} objects to their station keys " +
                      $"({fanned.Count} already done)...");

    await Run(copies, Math.Min(atOnce, 32), async row =>
    {
        if (byHand.Contains(row.Station))
            return;

        // No extension. The bucket holds webp and svg, and the object's stored ContentType says
        // which; with a format in the key, the reader would have to guess which format to ask for.
        var key = row.Station;
        await bucket.CopyAsync(row.Object, key, default);
        fanned.Add(new Fanned(row.Station, key, row.Object));
    }, "copied");

    fanned.Dispose();
    stored.Dispose();
}

await ReportAsync(pool, measured, walked, outDir, settled, bucketName, pretend);
Console.WriteLine($"\n{clock.Elapsed:hh\\:mm\\:ss} elapsed. Notes in {outDir}");
return 0;


// One address, measured from as few bytes as possible.
//
// Two kilobytes first. PNG, GIF, WebP and ICO all give their size within the first thirty bytes,
// and so do most JPEGs. Only a JPEG with a large colour profile or an embedded thumbnail has its SOF
// marker after that, and for those the 64KB that the app itself reads (ImageHeader.ProbeBytes) is
// requested in a second request. So the crawl downloads about 2KB per address instead of 64KB, and
// gives exactly the same result as the app.
static async Task<Measured> MeasureOne(Fetcher fetch, string url)
{
    const int Cheap = 2 * 1024;

    // An .ico has a directory of frames, and the largest is usually after the first two kilobytes,
    // so a short read reports "not a picture" for most valid icos. They are small files, so fetch
    // the whole file.
    var ico = url.Split('?')[0].EndsWith(".ico", StringComparison.OrdinalIgnoreCase);
    var want = ico ? 256 * 1024 : Cheap;

    // Requested the same way as the APP requests it, not with the address stored in the directory.
    //
    // A directory entry's logo is not only the address of a picture, it is the address of a picture
    // AT A SIZE, and RadioLogo.Sized rewrites the addresses of hosts that resize on request. The raw
    // address can fail: for example a Wikimedia thumbnail URL at a width that Wikimedia refuses gives
    // http 400. The app never gets that 400 because it never sends that URL.
    var asked = RadioLogo.Sized(url, Bucket.Edge) ?? url;

    var (status, body, type, declared, why) = await fetch.HeadBytesAsync(asked, want, default);
    var size = Read(asked, body);

    if (size is null && why is null && status < 400 && body.Length >= want)
    {
        // The short read did not decide, and the file is longer. Request what the app requests.
        (status, body, type, declared, why) = await fetch.HeadBytesAsync(asked, ImageHeader.ProbeBytes, default);
        size = Read(asked, body);
    }

    return new Measured(
        url, status, size?.Width ?? 0, size?.Height ?? 0, type, declared,
        why ?? (status >= 400 ? $"http {status}" : size is null ? "not a picture" : null));

    // An .ico has several frames at several sizes; the app draws the largest, so that frame's size
    // is the one recorded.
    static (int Width, int Height)? Read(string url, byte[] body)
    {
        if (body.Length == 0)
            return null;
        if (url.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)
            && IcoImage.LargestFrame(body) is { Length: > 0 } frame)
            return ImageHeader.Measure(frame);
        return ImageHeader.Measure(body);
    }
}


// The name of one image in the bucket: a hash of its source address, so the same picture chosen
// for forty stations is one object, and two stations with different copies of one logo are still
// two objects, so one does not overwrite the other.
//
// Always .webp, because every raster in the bucket IS webp: Bucket.Reshape re-encodes whatever was
// downloaded. Do not use the source address's extension: the name would then say .jpg, .png or
// .ico for a webp file, which misleads anyone debugging the bucket.
static string NameFor(string url, bool drawing)
    => Convert.ToHexStringLower(
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(url)))[..24] + (drawing ? ".svg" : ".webp");

// Whether this station still has no usable logo, after the measuring pass.
static bool Needs(Station s, Notes<Measured> measured)
{
    var url = (s.Favicon ?? "").Trim();
    if (url.Length == 0)
        return true;
    var m = measured.Get(url);
    // Not measured yet, or measured and not good enough: no picture at all, or smaller than the row
    // well needs at 2x (RadioModel.RowLogoEdge is 102).
    return m is null || !m.IsPicture || m.Side < 102;
}

static (string Host, string Url) HostOf(string? homepage)
{
    if (!Uri.TryCreate((homepage ?? "").Trim(), UriKind.Absolute, out var uri)
        || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        return ("", "");
    return (uri.Host.ToLowerInvariant(), uri.GetLeftPart(UriPartial.Path));
}

// A fixed pool of workers over a list, with a progress line. Not Parallel.ForEachAsync, so the
// count and the rate can be printed while it runs: this can run for an hour, and without output
// nobody can tell whether it is still working.
static async Task Run<T>(IReadOnlyList<T> items, int atOnce, Func<T, Task> each, string verb)
{
    if (items.Count == 0)
        return;

    var next = -1;
    var done = 0;
    var clock = Stopwatch.StartNew();

    var workers = Enumerable.Range(0, Math.Min(atOnce, items.Count)).Select(async _ =>
    {
        while (true)
        {
            var i = Interlocked.Increment(ref next);
            if (i >= items.Count)
                return;

            try
            {
                await each(items[i]);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"\n  {items[i]}: {ex.Message}");
            }

            var n = Interlocked.Increment(ref done);
            if (n % 25 == 0 || n == items.Count)
            {
                var rate = n / Math.Max(clock.Elapsed.TotalSeconds, 0.001);
                var left = TimeSpan.FromSeconds((items.Count - n) / Math.Max(rate, 0.001));
                Console.Write($"\r  {n}/{items.Count} {verb}  {rate:F0}/s  {left:hh\\:mm\\:ss} left   ");
            }
        }
    });

    await Task.WhenAll(workers);
    Console.WriteLine();
}

/// <param name="settled">The final logo of each station, including logos shared from another
/// station on the same host. Passed in, not computed again here: the sibling rule runs in the upload
/// phase, and a Report that computed it again would list stations as having no logo while the
/// bucket has one for them, and the admin page would ask someone to fix them.</param>
static async Task ReportAsync(
    List<Station> pool, Notes<Measured> measured, Notes<Walked> walked, string outDir,
    IReadOnlyDictionary<string, string>? settled, string bucketName, bool pretend)
{
    // Four tiers, using the app's own sizes, not round numbers. RadioModel draws a logo in a
    // 51-point row well, which is 102 pixels at 2x (RowLogoEdge), and in the now playing square at
    // 512 (SquareLogoEdge). So: large enough for the square; large enough for the row but blurred on
    // the square; smaller than the row well and blurred everywhere; or nothing at all.
    int square = 0, row = 0, soft = 0, none = 0;
    var missing = new List<object>();

    foreach (var s in pool)
    {
        // HAS A LOGO has one definition, used here and in the station index: is this station in
        // the served map? Not "did the ranking pick something": a pick whose upload failed would
        // count as covered here and as missing in the index.
        //
        // When no upload phase ran there is no map, so it is computed instead.
        string? chose = null;
        var served = settled is null
            ? BestFor(s, measured, walked, null) is { } worked && (chose = worked.Url) is not null
            : settled.TryGetValue(s.StationUuid, out chose);

        // Its SIZE is a separate question, which the map cannot answer for a logo chosen
        // manually or measured under another address. Those are counted in the top tier: they are
        // served, and that is what this tally counts.
        var best = chose is { Length: > 0 } ? measured.Get(chose) : null;

        // A drawing counts in the best tier, not as missing. It has no measured size (that is what
        // makes it a drawing), so a tally that reads only Side would put the best kind of logo in
        // the same tier as no logo at all.
        if (!served)
        {
            none++;
            missing.Add(new
            {
                s.StationUuid,
                s.Name,
                s.Country,
                s.Homepage,
                s.Favicon,
                s.ClickCount,
                Why = WhyNone(s, measured, walked),
            });
        }
        else if (best is null or { IsDrawing: true } || best.Side >= 512)
            square++;
        else if (best.Side >= 102)
            row++;
        else
            soft++;
    }

    var total = Math.Max(pool.Count, 1);
    Console.WriteLine();
    Console.WriteLine($"  512+, good for the square   {square,7}  {100.0 * square / total,5:F1}%");
    Console.WriteLine($"  102-511, good for the row   {row,7}  {100.0 * row / total,5:F1}%");
    Console.WriteLine($"  under 102, soft everywhere  {soft,7}  {100.0 * soft / total,5:F1}%");
    Console.WriteLine($"  nothing at all              {none,7}  {100.0 * none / total,5:F1}%");

    // Everything a person may want to fix manually: no logo, and logos too small for the row well.
    // A station with a 256px mark is fine in the list and blurred on the square, which is useful to
    // know but is not "missing".
    var path = Path.Combine(outDir, "missing.json");
    File.WriteAllText(path, JsonSerializer.Serialize(
        missing.OrderByDescending(m => ((dynamic)m).ClickCount),
        new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"\n  the ones with nothing, for the admin page: {path}");

    // ── and the totals the admin page draws ─────────────────────────────────────────────────────
    //
    // Written here, not by a separate script, so that every crawl updates the totals and the admin
    // page never shows numbers that no repeatable process produced.
    var clicks = missing.Select(m => (int)((dynamic)m).ClickCount).ToList();

    var summary = new
    {
        takenAt = DateTime.UtcNow.ToString("O"),
        stations = pool.Count,
        covered = settled?.Count ?? 0,
        missing = missing.Count,
        missingWithClicks = clicks.Count(k => k > 0),
        missingOver10 = clicks.Count(k => k > 10),
        missingOver100 = clicks.Count(k => k > 100),
        objects = settled?.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() ?? 0,

        // Grouped by the HEAD of the reason, before the colon: "site did not answer", not one row
        // per DNS message. The admin links each group back to its stations by matching the same
        // head, so both must cut the reason the same way. See Head.
        reasons = missing
            .GroupBy(m => Head((string)((dynamic)m).Why))
            .OrderByDescending(g => g.Count())
            .Select(g => new { why = g.Key, n = g.Count() })
            .ToList(),

        countries = missing
            .GroupBy(m => (string)((dynamic)m).Country is { Length: > 0 } c ? c : "(none)")
            .OrderByDescending(g => g.Count())
            .Take(25)
            .Select(g => new { country = g.Key, n = g.Count() })
            .ToList(),
    };

    await File.WriteAllTextAsync(
        Path.Combine(outDir, "summary.json"), JsonSerializer.Serialize(summary));

    if (pretend)
        return;

    using var s3 = new Amazon.S3.AmazonS3Client(Amazon.RegionEndpoint.EUWest1);

    foreach (var (key, payload) in new (string, object)[]
             {
                 ("reports/summary.json", summary),
                 ("reports/missing.json",
                  missing.OrderByDescending(m => (int)((dynamic)m).ClickCount).ToList()),
             })
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        using var into = new MemoryStream(body);
        await s3.PutObjectAsync(new Amazon.S3.Model.PutObjectRequest
        {
            BucketName = bucketName,
            Key = key,
            InputStream = into,
            ContentType = "application/json",
            Headers = { CacheControl = "public, max-age=300" },
        });
        Console.WriteLine($"  {key}: {body.Length / 1024}KB");
    }
}

/// <summary>A reason without its detail: "site did not answer", not the DNS message after the
/// colon. The summary groups by this and the admin filters by it, so it is defined in one
/// place.</summary>
static string Head(string why) => why.Split(':')[0].Trim();

static Measured? BestFor(Station s, Notes<Measured> measured, Notes<Walked> walked,
                        Notes<Marked>? marked = null)
{
    // The DIRECTORY's own logo first, and the site's icons only when it is not usable.
    //
    // This is what the app does. Do not rank the two together: the person who submitted the
    // station to Radio Browser chose that picture, but a page's <link> tags are whatever the CMS
    // writes, and the walk also finds hero backgrounds and og:image teasers. Ranked together, a
    // 2400x1349 teaser image or a hero-bg.png would win over the directory's proper mark.
    var filed = (s.Favicon ?? "").Trim();
    if (filed.Length > 0 && measured.Get(filed) is { } named && Usable(named))
    {
        // Except when the site has a real MARK and the directory's logo is a tile.
        //
        // Both are PNGs with an alpha channel and only the border shows which, so this needs the
        // results of the marks pass, which reads the pixels. The app prefers a mark: it can draw a
        // background behind a mark but not behind a tile. Only when the mark is also large
        // enough, because a 32px transparent favicon is worse than a 180px tile.
        if (marked is not null && marked.Get(filed)?.IsMark is false
            && BestMark(s, measured, walked, marked, named.Side) is { } better)
            return better;

        return named;
    }

    var (host, _) = HostOf(s.Homepage);
    var candidates = new List<string>();
    if (host.Length > 0 && walked.Get(host) is { Reached: true } site)
        candidates.AddRange(site.Icons);

    // Also the directory's logo, even if it was too small, so there is at least something.
    if (filed.Length > 0)
        candidates.Add(filed);

    return candidates
        .Select(measured.Get)
        .Where(m => m is { IsPicture: true })
        // In the order of what matters to the app, which is NOT "biggest first".
        //
        // 1. A DRAWING, always. It has a transparent background, never blurs, and the app draws it
        //    at any size.
        // 2. A true MARK, from the marks pass, when that pass ran for this station.
        // 3. A format that CAN be transparent. A JPEG never can, and the way the app draws a logo
        //    depends on whether it is a mark on a transparent background. Without this, a 600x600
        //    JPEG would win over a station's own smaller transparent PNG.
        // 4. Squarish. An og:image social banner is 1200x630 and has more pixels than any real mark.
        // 5. Only then, biggest.
        //
        // Without marks pass results, this cannot tell whether a PNG is a mark or an opaque tile:
        // an android-chrome icon has an alpha channel and is still opaque, and only the pixels show
        // the difference (TransparentMark.StandsAlone reads the border).
        .OrderByDescending(m => m!.IsDrawing)
        .ThenByDescending(m => marked?.Get(m!.Url)?.IsMark == true)
        .ThenByDescending(m => m!.CanBeClear)
        .ThenByDescending(m => Squarish(m!))
        .ThenByDescending(m => m!.Side)
        .FirstOrDefault();
}

/// <summary>
/// The biggest true mark this station has, if it has one that is good enough to use instead.
/// </summary>
/// <param name="atLeast">The size of the picture it would replace. A mark much smaller than that
/// tile is not better: the app would draw a large background behind a small picture. The mark must
/// be at least two thirds of this size.</param>
static Measured? BestMark(Station s, Notes<Measured> measured, Notes<Walked> walked,
                          Notes<Marked> marked, int atLeast)
    => CandidatesFor(s, measured, walked)
        .Where(m => !m.IsDrawing && m.IsPicture && m.Side >= 102)
        .Where(m => m.Side >= atLeast * 2 / 3)
        .Where(m => marked.Get(m.Url)?.IsMark == true)
        .OrderByDescending(m => m.Side)
        .FirstOrDefault();

/// <summary>Every measured candidate logo for this station; the caller sorts them best first.
/// Kept separate so the marks pass and the settle use the same list of candidates.</summary>
static IEnumerable<Measured> CandidatesFor(Station s, Notes<Measured> measured, Notes<Walked> walked)
{
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    if ((s.Favicon ?? "").Trim() is { Length: > 0 } filed && seen.Add(filed)
        && measured.Get(filed) is { } named)
        yield return named;

    var (host, _) = HostOf(s.Homepage);
    if (host.Length > 0 && walked.Get(host) is { Reached: true } site)
    {
        foreach (var url in site.Icons)
        {
            if (seen.Add(url) && measured.Get(url) is { } one)
                yield return one;
        }
    }
}

/// <summary>Whether a picture is good enough to stop searching. A drawing always is; a raster must
/// fill the row well, which is RadioModel.RowLogoEdge at 102.</summary>
static bool Usable(Measured m) => m.IsDrawing || (m.IsPicture && m.Side >= 102);

/// <summary>Whether a picture has the shape of a mark, not of a banner.</summary>
static bool Squarish(Measured m)
{
    if (m.IsDrawing)
        return true;
    if (m.Width <= 0 || m.Height <= 0)
        return false;
    var ratio = (double)Math.Max(m.Width, m.Height) / Math.Min(m.Width, m.Height);
    return ratio <= 1.4;
}

static string WhyNone(Station s, Notes<Measured> measured, Notes<Walked> walked)
{
    var (host, _) = HostOf(s.Homepage);
    if ((s.Favicon ?? "").Trim().Length == 0 && host.Length == 0)
        return "no logo and no homepage";
    if (host.Length > 0 && walked.Get(host) is { Reached: false } down)
        return $"site did not answer: {down.Why}";
    if (host.Length > 0 && walked.Get(host) is { Reached: true, Icons.Count: 0 })
        return "site declares no icons";

    // The site answered and listed some icons, and none of them was a picture. This is the most
    // common remaining reason, so give useful detail for the admin list: how many were tried and
    // what happened to the first one.
    if (host.Length > 0 && walked.Get(host) is { Reached: true } site && site.Icons.Count > 0)
    {
        var tried = site.Icons.Select(measured.Get).Where(m => m is not null).ToList();
        var first = tried.FirstOrDefault(m => m!.Why is not null);
        return first is null
            ? $"site named {site.Icons.Count} icons, none of them fetched"
            : $"site named {site.Icons.Count} icons, none is a picture: {first.Why}";
    }

    if ((s.Favicon ?? "").Trim() is { Length: > 0 } fav && measured.Get(fav) is { } m && !m.IsPicture)
        return $"the directory's logo is not a picture: {m.Why}";
    return "no homepage, and the logo it names is not a picture";
}

static string? Arg(string name)
{
    var args = Environment.GetCommandLineArgs();
    for (var i = 0; i < args.Length - 1; i++)
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];
    return null;
}
