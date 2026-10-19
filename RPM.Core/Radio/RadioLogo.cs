using System.Globalization;
using System.Text.RegularExpressions;

namespace RPM.Core.Radio;

/// <summary>
/// Whether a station's published logo is one that can actually be drawn.
///
/// <para><b>Why this is a class and not one line in the image loader.</b> A directory entry's favicon is a
/// URL that somebody typed in, and many of them are the broadcaster's site favicon, such as
/// <c>nporadio1.nl/favicon.ico</c>. Skia has no decoder for that format, so the logo draws as nothing. Two
/// places must agree on this. The row that shows the logo needs it, so that it can show the antenna mark
/// instead of a hole. <see cref="RadioResults"/> needs it earlier, when it merges a station's several
/// entries into one. "Does this entry have a logo" must mean "one that will appear". Otherwise the merge
/// keeps a .ico from the high-bitrate stream, counts it as a logo, and throws away the real PNG from the
/// 32k twin. The station then shows the antenna even though its picture was available.</para>
/// </summary>
public static partial class RadioLogo
{
    /// <summary>
    /// Whether this is a URL worth handing to an image decoder.
    ///
    /// <para>One thing is rejected, and it is not a judgement about the picture: an address that is not
    /// http or https. A link that is simply dead cannot be told from a live one without fetching it, so
    /// that is not checked here. The row that tries it reports the failure afterwards, and moves on to the
    /// next logo the station has.</para>
    ///
    /// <para>An .ico address is accepted. No decoder in the app opens an ICO file directly, but <see
    /// cref="IcoImage"/> reads its frames into a format that a decoder can open. See <see cref="Rank"/>,
    /// where .ico is still the last thing worth trying.</para>
    /// </summary>
    public static bool IsDrawable(string? favicon)
        => !string.IsNullOrWhiteSpace(favicon)
           && Uri.TryCreate(favicon, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Whether the address is for a drawing (SVG), so it needs a different image source.
    ///
    /// <para>Broadcasters publish logos as SVG. For example, nporadio1.nl serves its logo as SVG, and
    /// nothing else on that host is the whole mark. A bitmap decoder given an SVG produces nothing, and
    /// reports only that the load failed. That is why the row went blank on a URL that answers with status
    /// 200.</para>
    /// </summary>
    public static bool IsVector(string? favicon)
        => Uri.TryCreate(favicon, UriKind.Absolute, out var uri)
           && uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether these bytes are a drawing. The check is made on the bytes, not on the address.
    ///
    /// <para><b>An address does not always have an extension to read.</b> rpm's own API serves a station's
    /// logo from <c>radio/logo/{uuid}</c>. The file behind it is WebP or SVG, depending on what the crawl
    /// found, and nothing in the URL says which. Before this check was made on the bytes, an SVG from that
    /// address was fetched, found to have no raster bounds, and discarded as "served, but not an image
    /// anything here reads". So a station whose logo is a drawing showed nothing at all.</para>
    ///
    /// <para>The check looks for the root element rather than trusting the first byte. A real SVG file
    /// usually opens with an XML declaration, often with a comment from whatever drew it, and only then
    /// with <c>&lt;svg</c>. Only the front of the file is read.</para>
    /// </summary>
    public static bool IsDrawing(ReadOnlySpan<byte> bytes)
    {
        // Enough for a declaration, a licence comment and a doctype before the root element.
        const int Front = 1024;

        var head = bytes.Length > Front ? bytes[..Front] : bytes;

        // It must open as markup. Otherwise a raster whose bytes happen to contain "<svg" would qualify.
        var at = 0;
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
            at = 3;   // a byte order mark, which plenty of editors write

        while (at < head.Length && head[at] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            at++;

        if (at >= head.Length || head[at] != (byte)'<')
            return false;

        Span<char> text = stackalloc char[Front];
        var n = 0;
        for (var i = at; i < head.Length && n < text.Length; i++)
            text[n++] = char.ToLowerInvariant((char)head[i]);

        return text[..n].IndexOf("<svg") >= 0;
    }

    /// <summary>
    /// How likely this URL is to become a picture. The one most likely to work is tried first.
    ///
    /// <para>This is a guess. Whether a host will actually serve a file cannot be known without asking it.
    /// The row that asks settles it (see <see cref="RadioStation.OtherLogos"/>). This only sets the ORDER
    /// of asking, so a wrong guess costs one failed request, not a blank row. Nothing is ever excluded by
    /// it. A station whose only logo scores badly is still tried. The alternative would be a maintained
    /// list of hosts that decides a broadcaster has no picture.</para>
    /// </summary>
    /// <param name="homepage">The station's own site. A logo served from it is the broadcaster's own copy
    /// of their own mark, which is the best signal there is. The third-party mirrors are where broken links
    /// and refusals tend to be.</param>
    public static int Rank(string? favicon, string? homepage)
    {
        if (!Uri.TryCreate(favicon, UriKind.Absolute, out var uri))
            return int.MinValue;

        var score = 0;

        if (Site(uri) is { Length: > 0 } host)
        {
            if (Uri.TryCreate(homepage, UriKind.Absolute, out var home)
                && string.Equals(host, Site(home), StringComparison.Ordinal))
                score += OwnSite;

            if (Hotlinkers.Contains(host))
                score += RefusesHotlinks;
        }

        var path = uri.AbsolutePath;
        score += Ends(path, KeepsAlpha) ? CanBeTheMark
               : path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? IsADrawing
               : Ends(path, Flattened) ? IsAPicture
               : path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) ? IsASiteIcon
               : 0;

        // A host that cuts pictures to order gives a surface whatever size it asks for. So the size in its
        // address says nothing about the picture (see Sized). Such a host is still worth a bonus over a
        // candidate that gives only the one size that somebody uploaded. The bonus is kept below OwnSite on
        // purpose: a broadcaster's own copy of their own mark is a better answer than a resizable mirror of
        // somebody's guess.
        if (CutsToOrder(favicon))
            score += AnySize;

        // This has nothing to do with the picture itself. A plain http logo is the kind that a strict host
        // or a future cleartext policy drops. When two copies of the same mark differ only in scheme, there
        // is no reason to choose the weaker one.
        if (uri.Scheme == Uri.UriSchemeHttps)
            score += OverHttps;

        return score;
    }


    // -- the size the address itself asks for --

    /// <summary>
    /// The same mark asked for at <paramref name="edge"/> pixels or more, where the host cuts a picture to
    /// order. Otherwise the address is returned exactly as it came.
    ///
    /// <para><b>What is wrong with these addresses.</b> A directory entry's logo is not the address of a
    /// picture. It is the address of a picture AT A SIZE, and the size is whatever the person who filed the
    /// entry pasted. iHeart's addresses are the plainest case. One station's streams carry the same asset,
    /// filed as <c>contain(32,32)</c> under one entry and <c>contain(360,360)</c> under another. The row
    /// drew whichever of these the fold kept. This fixes the blocky logo, where 32 pixels are stretched
    /// across a 100-point well. The picture behind it was never the problem. Of the top thousand American
    /// stations in the directory, 192 use that one host, and 42 of those ask for 32 pixels.</para>
    ///
    /// <para><b>Only up, never down.</b> One address is fetched by a row that wants 100 pixels, and by the
    /// playing square that wants several hundred. A rewrite that trimmed a generous address to the row's
    /// need would bring back the small logo on the larger surface.</para>
    ///
    /// <para><b>It cannot conjure detail.</b> These hosts resample rather than refuse, so a small asset
    /// comes back large and soft. That is not a reason to ask for less. It is the same picture either way,
    /// and most pictures on these hosts do have the pixels.</para>
    ///
    /// <para>There are three families. Each is read from the directory's own answers, and each is checked
    /// against the host, not assumed. Everything else is returned untouched, which is the usual
    /// case.</para>
    /// </summary>
    public static string? Sized(string? url, int edge)
    {
        if (url is null || edge <= 0 || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        try
        {
            return uri.Host.ToLowerInvariant() switch
            {
                "i.iheart.com" => Raise(url, IHeartBox(), edge),
                "static.wixstatic.com" => Raise(url, WixBox(), edge),
                "cdn-profiles.tunein.com" => TuneIn(url, edge),
                "upload.wikimedia.org" or "thumb.wikimedia.org" => Wikimedia(url, edge),
                _ => url,
            };
        }
        catch (RegexMatchTimeoutException)
        {
            return url;   // a pathological address is still an address, so ask for it as it came
        }
    }


    /// <summary>
    /// Where the same picture can be found AT ITS OWN SIZE, or null where the host offers no such address.
    ///
    /// <para><b>Why that is worth asking for.</b> A host that cuts to order answers every request with
    /// exactly the size that was asked for. So what comes back says nothing about how much picture there
    /// ever was. 102.7 KIIS FM is the plain case, and a bad one. Ask its directory logo for 500 pixels and
    /// 500 pixels arrive. But the asset behind them is 16 pixels, a browser tab icon, blown up to 500
    /// pixels. Nothing about the address says so, and nothing about the answer does either. Asked bare,
    /// iHeart hands over the asset as it is, and 16 is plainly 16.</para>
    ///
    /// <para>Only iHeart, and that is not an oversight. Wix cuts from a real upload, and TuneIn's letters
    /// are a real ladder. A <c>logog</c> is 600 real pixels. So what those two return is what they have.
    /// This is for the host that invents pixels rather than refusing.</para>
    ///
    /// <para>Null for an address that is already bare. There is nothing to strip, and nothing to learn from
    /// asking twice.</para>
    /// </summary>
    public static string? Native(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Host.Equals("i.iheart.com", StringComparison.OrdinalIgnoreCase)
           && uri.Query.Length > 0
            ? uri.GetLeftPart(UriPartial.Path)
            : null;

    /// <summary>Whether this host cuts a picture to whatever size is asked of it, so the size in the
    /// address says nothing about how good the picture is. <see cref="Rank"/> prefers these when two
    /// candidates are otherwise level. One of these gives a surface what it needs. The other gives it the
    /// one size that somebody uploaded.</summary>
    public static bool CutsToOrder(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && uri.Host.ToLowerInvariant() is "i.iheart.com" or "static.wixstatic.com"
                                          or "cdn-profiles.tunein.com";

    /// <summary>
    /// One address with the size taken out, so that two spellings of ONE picture can be told apart from two
    /// different pictures.
    ///
    /// <para>A station's entries often name the same asset at three sizes, and they also name three
    /// different assets. These are not the same situation. The first is one mark that its contributors
    /// agree on. The second is a disagreement about which mark it is. With the size removed, the agreement
    /// can be counted rather than guessed at. See <see cref="RadioResults"/>.</para>
    /// </summary>
    public static string SamePicture(string? url)
    {
        if (url is null)
            return "";
        if (!CutsToOrder(url))
            return url;

        try
        {
            var bare = IHeartBox().Replace(url, "contain()");
            bare = WixBox().Replace(bare, match => match.Groups["verb"].Value + "/w_,h_");
            return TuneInLogo().Replace(bare, "/images/logo.");
        }
        catch (RegexMatchTimeoutException)
        {
            return url;
        }
    }

    /// <summary>The largest size that any of these is ever asked for. The playing square is the biggest
    /// surface on which a station's mark is drawn, and it draws a mark, not a scan. Past this size, the
    /// request costs more than the screen can show.</summary>
    private const int Ceiling = 1024;

    /// <summary>
    /// A width-and-height box in an address, scaled so that its SHORT side reaches <paramref name="edge"/>.
    ///
    /// <para>Both axes are scaled by the same factor. This is not a detail. The box these hosts take is a
    /// crop as well as a size. Wix's <c>fill/</c> cuts the picture to the shape that it is given, so
    /// squaring a wordmark's box would cut the ends off the name. What is wanted is the same picture that
    /// the entry describes, only larger.</para>
    /// </summary>
    private static string Raise(string url, Regex box, int edge)
        => box.Replace(url, match =>
        {
            if (!int.TryParse(match.Groups["w"].Value, out var w)
                || !int.TryParse(match.Groups["h"].Value, out var h)
                || w <= 0 || h <= 0)
                return match.Value;

            // Never past the ceiling, measured on the LONG side, because that side decides how heavy the
            // request is.
            var factor = Math.Min((double)edge / Math.Min(w, h), (double)Ceiling / Math.Max(w, h));
            if (factor <= 1)
                return match.Value;   // the address is already asking for enough

            return Swap(match, (int)Math.Round(w * factor), (int)Math.Round(h * factor));
        });

    /// <summary>The matched box with its two numbers replaced. The later number is written first, so that
    /// writing it cannot move the earlier number's index.</summary>
    private static string Swap(Match match, int width, int height)
    {
        var w = match.Groups["w"];
        var h = match.Groups["h"];
        var (first, firstTo) = w.Index < h.Index ? (w, width) : (h, height);
        var (last, lastTo) = w.Index < h.Index ? (h, height) : (w, width);

        var text = Write(match.Value, last, lastTo);
        return Write(text, first, firstTo);

        string Write(string into, Group at, int number)
            => into.Remove(at.Index - match.Index, at.Length)
                   .Insert(at.Index - match.Index, number.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// TuneIn does not take a size. It takes one of four letters, and this picks the letter.
    ///
    /// <para>This picks the smallest letter that covers the size asked for. The address is left untouched
    /// where the letter it already has covers that size. A change to a smaller letter would be a downgrade
    /// that looks like a fit.</para>
    /// </summary>
    private static string TuneIn(string url, int edge)
    {
        var match = TuneInLogo().Match(url);
        if (!match.Success)
            return url;

        var mark = match.Groups["mark"];
        var now = TuneInLadder.FirstOrDefault(rung => rung.Mark == char.ToLowerInvariant(mark.Value[0]));
        if (now.Edge >= edge)
            return url;

        var wanted = TuneInLadder.FirstOrDefault(rung => rung.Edge >= edge, TuneInLadder[^1]);
        return url.Remove(mark.Index, 1).Insert(mark.Index, wanted.Mark.ToString());
    }

    /// <summary>
    /// A Wikimedia thumbnail, asked for at a width that Wikimedia will serve.
    ///
    /// <para><b>They do not render to order. This is why their logos never appeared.</b> A thumbnail URL
    /// carries a width, and Wikimedia answers <c>400</c> to every width outside a fixed list. Measured
    /// against three files, with the same result each time: 120, 250 and 500 are served. 80, 160, 180, 220,
    /// 320, 400, 512, 640, 800, 1024 and 1200 are all refused. The directory is full of 640 px and 1200 px
    /// addresses. Those are sizes that someone's browser asked for years ago, and every one of them is a
    /// dead link now.</para>
    ///
    /// <para><b>This is not a refusal of the app.</b> Wikimedia does not turn this app's requests away. Of
    /// the 29 Wikimedia logos among the 1,500 most-clicked stations, 15 load exactly as filed. The other 10
    /// load once their width is set to a size that Wikimedia serves. The refusal was of the SIZE.</para>
    ///
    /// <para>The largest rung that is not bigger than the size asked for. So this never enlarges a request.
    /// A request under 120 still gets 120, because nothing smaller is available.</para>
    /// </summary>
    private static string Wikimedia(string url, int edge)
    {
        var box = WikimediaWidth().Match(url);
        if (!box.Success)
            return url;   // a direct file rather than a thumbnail: served as it is

        var width = box.Groups["w"];
        var rung = WikimediaLadder.LastOrDefault(w => w <= edge, WikimediaLadder[0]);

        return url.Remove(width.Index, width.Length)
                  .Insert(width.Index, rung.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The widths at which Wikimedia renders a thumbnail, smallest first. These are read from the
    /// host, and they are the same three for every file tried.</summary>
    private static readonly int[] WikimediaLadder = [120, 250, 500];

    /// <summary>Wikimedia: <c>.../thumb/a/a0/Name.svg/500px-Name.svg.png</c>. The same applies with a
    /// language prefix on the rendered name (<c>langfr-500px-</c>), which Wikimedia's own API
    /// returns.</summary>
    [GeneratedRegex("""/(?:lang[a-z-]+-)?(?<w>\d{1,5})px-""", RegexOptions.IgnoreCase)]
    private static partial Regex WikimediaWidth();

    /// <summary>What each of TuneIn's letters is worth in pixels, smallest first. These are read from the
    /// host. The same station's <c>logot</c>, <c>logoq</c>, <c>logod</c> and <c>logog</c> come back as 75,
    /// 145, 300 and 600 pixels square.</summary>
    private static readonly (char Mark, int Edge)[] TuneInLadder =
        [('t', 75), ('q', 145), ('d', 300), ('g', 600)];

    /// <summary>iHeart: <c>?ops=...,contain(360,360),...</c>.</summary>
    [GeneratedRegex("""contain\((?<w>\d{1,5}),(?<h>\d{1,5})\)""", RegexOptions.IgnoreCase)]
    private static partial Regex IHeartBox();

    /// <summary>Wix: <c>.../v1/fill/w_455,h_413,al_c,...</c>. The regex is anchored on the verb. The
    /// segment before it has its own <c>w_</c> and <c>h_</c> (<c>/v1/crop/x_24,y_30,w_555,h_544/</c>), and
    /// those are source pixels. Rewriting them would move the crop rather than resize the
    /// picture.</summary>
    [GeneratedRegex("""(?<verb>fill|fit)/w_(?<w>\d{1,5}),h_(?<h>\d{1,5})""", RegexOptions.IgnoreCase)]
    private static partial Regex WixBox();

    /// <summary>TuneIn: <c>.../images/logod.png</c>.</summary>
    [GeneratedRegex("""/images/logo(?<mark>[a-z])\.(?:png|jpe?g|webp)""", RegexOptions.IgnoreCase)]
    private static partial Regex TuneInLogo();

    /// <summary>Served from the broadcaster's own site.</summary>
    private const int OwnSite = 4;

    /// <summary>Served from a host that turns the app away. Enough to sink a candidate below one with
    /// nothing against it, but not enough to sink it below one that cannot be read.</summary>
    private const int RefusesHotlinks = -8;

    /// <summary>Served by a host that cuts the mark to whatever size is wanted. See <see
    /// cref="CutsToOrder"/>.</summary>
    private const int AnySize = 3;

    /// <summary>
    /// A format that can hold a mark and nothing else: a PNG or a WebP with an alpha channel, or a drawing,
    /// which has no background by its nature.
    ///
    /// <para><b>Why the format is ranked at all.</b> A station's mark is drawn in a small dark well. On the
    /// now-playing square it sits on a background made from the mark's own ink. Both need the mark with
    /// nothing behind it. A JPEG cannot supply that, whatever it contains, because the format has no alpha
    /// channel. So a JPEG shows as a white or coloured rectangle in a dark round well. That looks like a
    /// badge that failed to load, not like a station's mark. When a broadcaster's entries name the same
    /// mark as a PNG and as a JPEG, and they often do, the PNG is the one to ask for first.</para>
    ///
    /// <para>A drawing ranks below a raster with transparency. A drawing needs a second image source and a
    /// rasterize step. The now-playing square cannot read one at all, and the phone head has no renderer
    /// for one. A raster that can carry the mark is the better first try on every surface.</para>
    /// </summary>
    private const int CanBeTheMark = 2;

    /// <summary>A drawing renders (see <see cref="IsVector"/>), but it needs a second image source and a
    /// rasterize step. The now-playing square cannot read one at all, and the phone head has no renderer
    /// for one. A raster copy of the same mark is the better first try.</summary>
    private const int IsADrawing = 1;

    /// <summary>A format with no alpha channel, and nothing else to say for it. It will draw. It ranks
    /// level with an address that names no format. That is the usual shape of a CDN URL, and such an
    /// address is a PNG about as often as not. So there is no reason to prefer a JPEG that the directory
    /// names over a picture that it does not name.</summary>
    private const int IsAPicture = 0;

    /// <summary>A site's own favicon. It can be read now (<see cref="IcoImage"/>), and it is still the last
    /// resort. What is in one is a browser-tab icon. That is the station's mark at 32 pixels: a picture
    /// that exists, drawn soft, and better than the antenna only because nothing else is
    /// available.</summary>
    private const int IsASiteIcon = -1;

    private const int OverHttps = 1;

    /// <summary>
    /// Hosts that will not serve an image to something that is not a browser.
    ///
    /// <para><b>Empty, and Wikimedia is deliberately not in it.</b> Its User-Agent policy does not refuse
    /// this app, although a test with no agent gets 403 and a test with a browser agent gets 400. Measured
    /// with the app's own agent, 15 of the 29 Wikimedia logos among the 1,500 most-clicked stations load
    /// exactly as filed. The rest load once the width is snapped to one that Wikimedia renders (see
    /// <see cref="Sized"/>). The 400 response was about the size. A penalty here would sort a working
    /// logo below a dead link.</para>
    ///
    /// <para>It is kept as a list rather than deleted, because the problem it describes is real, and the
    /// next host that has it needs somewhere to go. A list of hosts has to be kept up to date, which is why
    /// it only ever REORDERS candidates. A host that starts serving again is tried again after one failed
    /// request. A host that stops serving draws the next candidate instead.</para>
    /// </summary>
    private static readonly HashSet<string> Hotlinkers = new(StringComparer.Ordinal);

    /// <summary>Raster formats with an alpha channel. See <see cref="CanBeTheMark"/>.</summary>
    private static readonly string[] KeepsAlpha = [".png", ".webp"];

    /// <summary>Raster formats without an alpha channel. GIF has one bit of transparency, which is a hard
    /// edge and not a mark's antialiasing. Nothing files a station's logo as a GIF on purpose.</summary>
    private static readonly string[] Flattened = [".jpg", ".jpeg", ".gif", ".bmp"];

    private static bool Ends(string path, string[] extensions)
        => extensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    /// <summary>A host with "www." removed, so that a broadcaster's logo is recognised as theirs, whichever
    /// way the directory spelled their address that day. The entries for one station can carry
    /// <c>nporadio1.nl</c> and <c>www.nporadio1.nl</c> in the same answer.</summary>
    private static string Site(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }
}
