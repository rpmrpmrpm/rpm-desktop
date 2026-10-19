using System.Text.Json;
using System.Text.Json.Serialization;

namespace StationLogos;

/// <summary>The result of fetching and measuring one address.</summary>
/// <param name="Url">The requested address.</param>
/// <param name="Status">The HTTP status, 0 when the request did not complete.</param>
/// <param name="Width">Width in pixels, 0 when it could not be measured.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="Type">The content type the host gave.</param>
/// <param name="Bytes">Content length if the host gave one, otherwise the number of bytes read.</param>
/// <param name="Why">Why it could not be measured, when it could not.</param>
public sealed record Measured(
    string Url,
    int Status,
    int Width,
    int Height,
    string? Type,
    long Bytes,
    string? Why)
{
    /// <summary>
    /// A drawing, which has no pixel size to measure and does not need one.
    ///
    /// <para>The app draws an SVG at any size and prefers one for that reason (RadioLogo.IsDrawable,
    /// and the "drawing is kept as fetched" case of the walk). Do not measure SVGs with ImageHeader,
    /// which cannot read them: almost every SVG would be rejected, and they are the best logos
    /// available, because a drawing has a transparent background and never blurs.</para>
    /// </summary>
    public bool IsDrawing => (Type ?? "").Contains("svg", StringComparison.OrdinalIgnoreCase)
                             || Url.Split('?')[0].EndsWith(".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether this can be drawn: a measured raster, or a drawing.</summary>
    public bool IsPicture => IsDrawing || (Width > 0 && Height > 0);

    /// <summary>
    /// Whether the format can hold transparency at all.
    ///
    /// <para>A JPEG never can. This is useful before anything is fetched: ranking on size alone
    /// would choose a 600x600 JPEG over a station's smaller transparent PNG, and the way the app
    /// draws a logo depends on whether it is a mark on a transparent background
    /// (TransparentMark.StandsAlone).</para>
    /// </summary>
    public bool CanBeClear
    {
        get
        {
            var t = (Type ?? "").ToLowerInvariant();
            if (t.Contains("jpeg") || t.Contains("jpg"))
                return false;
            var path = Url.Split('?')[0].ToLowerInvariant();
            return !path.EndsWith(".jpg") && !path.EndsWith(".jpeg");
        }
    }

    /// <summary>The short side, which decides the size at which the app can draw the logo.</summary>
    public int Side => Math.Min(Width, Height);
}

/// <summary>The icons one site declares, after its page was read.</summary>
/// <param name="Host">The site, which is the key: several stations of one broadcaster share it.</param>
/// <param name="Reached">Whether the site answered at all. False means "try again another day";
/// true with an empty list means "this site declares no icons", which is a result and is kept.</param>
/// <param name="Icons">Every address it declared, best first, in <c>StationIcons</c> order.</param>
/// <param name="Why">Why it could not be reached, when it could not.</param>
public sealed record Walked(
    string Host,
    bool Reached,
    IReadOnlyList<string> Icons,
    string? Why);

/// <summary>
/// The crawl's own results file, one JSON object per line, appended as each result arrives.
///
/// <para>Append-only and flushed per line on purpose: this crawl makes tens of thousands of requests
/// to about eleven thousand third-party sites and will be stopped, rate-limited and restarted. If a
/// run had to start again from zero it might never finish, and other people's servers would get the
/// same requests twice.</para>
///
/// <para>Keyed by what was requested (an address, or a host), not by the station, because the work
/// is done per address and per host. About 37,000 stations use about 26,000 distinct logos, and
/// about 21,000 stations share about 11,000 sites.</para>
/// </summary>
public sealed class Notes<T> : IDisposable where T : class
{
    private readonly StreamWriter _out;
    private readonly Dictionary<string, T> _done = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<T, string> _keyOf;
    private readonly Lock _gate = new();

    public Notes(string path, Func<T, string> keyOf)
    {
        _keyOf = keyOf;

        if (File.Exists(path))
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length == 0)
                    continue;
                try
                {
                    if (JsonSerializer.Deserialize<T>(line, Json) is { } had)
                        _done[_keyOf(had)] = had;
                }
                catch (JsonException)
                {
                    // A line cut in half when the process was killed. That work is done again, which
                    // costs one request.
                }
            }
        }

        _out = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };
    }

    /// <summary>How many results are already stored.</summary>
    public int Count
    {
        get { lock (_gate) return _done.Count; }
    }

    public bool Has(string key)
    {
        lock (_gate) return _done.ContainsKey(key);
    }

    public T? Get(string key)
    {
        lock (_gate) return _done.GetValueOrDefault(key);
    }

    public IReadOnlyList<T> All()
    {
        lock (_gate) return [.. _done.Values];
    }

    public void Add(T note)
    {
        var line = JsonSerializer.Serialize(note, Json);
        lock (_gate)
        {
            _done[_keyOf(note)] = note;
            _out.WriteLine(line);
        }
    }

    public void Dispose() => _out.Dispose();

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>The result for one chosen picture: the object it was stored as, or why it was not stored.</summary>
/// <param name="Source">The address it came from, which is the key: the same picture can be chosen for
/// several stations and is fetched once.</param>
/// <param name="Key">The object in the bucket, or null when nothing was stored.</param>
/// <param name="Bytes">The size after re-encoding.</param>
/// <param name="Why">Why nothing was stored, when nothing was.</param>
public sealed record Stored(string Source, string? Key, long Bytes, string? Why);

/// <summary>One station's own copy of a picture: what the API reads.</summary>
/// <param name="Station">The station's uuid, which is the key and the object's name.</param>
/// <param name="Key">The object in the bucket, always <c>{uuid}.webp</c>.</param>
/// <param name="From">The picture it was copied from, which several stations may share.</param>
public sealed record Fanned(string Station, string Key, string From);

/// <summary>
/// Whether one picture is a mark on a TRANSPARENT background, not a tile that only has rounded
/// corners.
/// </summary>
/// <param name="Url">The address tested.</param>
/// <param name="IsMark">True when the border is mostly transparent. Null when it could not be
/// decoded, which is not the same as false: a candidate that cannot be read keeps its rank.</param>
public sealed record Marked(string Url, bool? IsMark);
