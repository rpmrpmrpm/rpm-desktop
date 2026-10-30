using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Radio;
using RPM.Desktop.Services.Catalog;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Main.LeftPane.Models;

/// <summary>One station as a row: the station itself, plus the two things about it that change while
/// it is on screen. Whether it is a preset, and whether it is the one playing.</summary>
[RpmBindable]
public sealed partial class RadioStationItem(RadioStation station) : INotifyPropertyChanged
{
    private bool _isFavorite;
    private bool _isTuned;
    /// <summary>The picture, the ground under it where it needs one, and the shape it arrived in. It is
    /// one field, because how the picture is FITTED is read from all three, and they must never
    /// disagree.</summary>
    private FoundLogo _found;
    private bool _logoAsked;

    public RadioStation Station { get; } = station;

    public string Uuid => Station.Uuid;

    /// <summary>The name as it is printed, with the specification taken out of it. A row shows the station
    /// rather than the stream it is served as. See RadioNames.</summary>
    public string Name => Station.Display;

    /// <summary>The call sign the dial marks this station by. It is shown beside the name, so the card
    /// and the list can be read against each other: spot GLDS on the band, then find "Golden Oldies" in
    /// the rows. It is derived from the name by the same rule the card uses, so the two cannot
    /// disagree.</summary>
    public string Sign
    {
        get
        {
            // Taken from the PRINTED name, so the letters on the band match the letters of the name in
            // the row beside them. If it were taken from the raw name, a station whose strapline is
            // dropped here would be marked on the dial by words the list no longer shows.
            var sign = StationSign.For(Station.Display);
            // In brackets, so it reads as the dial's short form of the name it follows, not as another
            // word of it. An empty name stays empty, so a station with no name does not get "()".
            return sign.Length == 0 ? "" : $"({sign})";
        }
    }

    /// <summary>
    /// Whether this station's own tags say it carries advertising. See <see cref="RadioAds.CarriesAds"/>.
    ///
    /// <para>False for nearly every station. That is not a claim that there is no advertising. Hardly any
    /// stations say anything about it either way, so no badge means "not stated", not "no
    /// advertising".</para>
    /// </summary>
    public bool HasAds => RadioAds.CarriesAds(Station.Tags, Station.Name, Station.Homepage);

    /// <summary> Whether the station was on the air when the directory last checked it. This is the lamp
    /// beside its name.
    ///
    /// <para><b>The directory's answer, not ours.</b> Radio Browser runs a checker over every stream it
    /// holds and publishes what it found (<see cref="RadioStation.Reachable"/>). This shows that answer
    /// and nothing more. Asking each stream ourselves would mean one connection per row, to a hundred
    /// volunteer-run servers, every time a list is drawn, to answer a question about stations nobody has
    /// pressed.</para>
    ///
    /// <para>So an unlit lamp means "it was not answering when it was last checked". That is not the same
    /// as "it is down now", because a station can come back between checks. It is still worth showing. A
    /// dial where the dead stations are marked is one somebody can read. The same fact also orders the
    /// list (see RadioResults).</para>
    /// </summary>
    public bool OnAir => Station.Reachable;

    /// <summary>The other half of the lamp, so the row needs no converter to draw the unlit one.</summary>
    public bool OffAir => !Station.Reachable;

    /// <summary>
    /// The line under the name: where the station broadcasts from, and what it is served as.
    ///
    /// <para><b>The genres are not on this line.</b> They are a list entered by the public, and it runs
    /// to a dozen near duplicates such as "hip hop, hip-hop, hip-h...". A row has one line to spend. The
    /// genre filter still searches by them, and that goes to the directory as a query. So nothing on this
    /// line decides whether a station can be found. The bitrate is left off for the same kind of reason.
    /// See RadioStation.Format.</para>
    ///
    /// <para>It is built here, not in the template, so an empty field does not leave a stray separator
    /// behind.</para>
    /// </summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>(2);

            // Under the name a reader would use, the same as the country list (see RadioCountries). The
            // directory files a British station under "The United Kingdom Of Great Britain And Northern
            // Ireland". On a line this narrow, that becomes an ellipsis where the format should be.
            var country = RadioCountries.Short(Station.Country, Station.CountryCode);
            if (country.Length > 0) parts.Add(country);
            if (Station.Format is { Length: > 0 } format) parts.Add(format);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// The station's own logo, or null while one is being found and after they have all failed.
    ///
    /// <para>It is asked for on first read, not up front. A search returns a hundred-odd stations, and
    /// the list only realizes a dozen rows. Eager fetching would mean a hundred downloads to show twelve
    /// pictures. The read starts the search and answers null. The row draws the antenna until a picture
    /// arrives, which is also what it draws for a station with no logo.</para>
    /// </summary>
    public Microsoft.UI.Xaml.Media.ImageSource? Logo
    {
        get
        {
            if (_logoAsked)
                return _found.Picture;

            _logoAsked = true;
            _ = FindLogoAsync();
            return _found.Picture;
        }
    }

    /// <summary>
    /// Walks the station's logos until one of them turns out to be a picture.
    ///
    /// <para><b>Fetched, then decoded, rather than handed to the control as a URL.</b> A
    /// <c>BitmapImage(uri)</c> makes its own request with no user agent, and many hosts answer that with
    /// 403. alive.radio does. Its logo shows fine in the header, which goes through <see
    /// cref="RemoteImage"/>, but the row shows the antenna. Every other picture in the app goes through
    /// <see cref="RemoteImage"/>, so this row must too.</para>
    ///
    /// <para>Fetching also makes the fall-through definite. Each candidate is settled before anything is
    /// put on screen. So the walk does not depend on the Image control raising a failure, and the control
    /// does not raise one for a drawing that never loaded.</para>
    ///
    /// <para>The walk only moves forward. So a row that has fallen through to the antenna does not repeat
    /// the same failing requests on its next repaint.</para>
    ///
    /// <para><b>"Too small" is a reason to keep walking.</b> The walk must not stop at the first
    /// candidate that decodes. Decoding is not the same question as whether a candidate is worth showing.
    /// A 32-pixel browser-tab icon decodes perfectly, and then it is drawn in a hundred-pixel well at
    /// three times its size. Of the thousand American stations the directory ranks highest, a quarter
    /// have logos shorter than this well. Three in four of those have something big enough on the
    /// station's own front page. A small picture counted as a found one, so nothing looked there. So a
    /// picture that does not cover the well is kept, and the walk goes on. It is put up at the end if
    /// nothing better turns up.</para>
    ///
    /// <para>The common case costs what it always did. A station whose first logo covers the well settles
    /// on one request. The page is read only by the stations that need it.</para>
    /// </summary>
    private async Task FindLogoAsync()
    {
        var settled = await SettledFor(Station);
        if (settled.Picture is null && _found.Picture is not null)
            return;   // a re-ask that finds nothing must not remove the picture already shown

        _found = settled;
        RaiseLogo();
    }

    /// <summary>
    /// The one picture this station wears, wherever it is drawn: the winning candidate's BYTES, with nothing
    /// decoded yet.
    ///
    /// <para><b>Bytes, not a decoded logo, because the two surfaces want different sizes of the same
    /// picture.</b> They must not be allowed to pick different PICTURES. The row judged a candidate
    /// against its 102-pixel well, and the now playing square judged it against its 512. The tier was
    /// computed against whichever one asked. So a transparent 256-pixel mark scored "too small" for the
    /// square, while an opaque 512 picture scored "a picture". The opaque one ranked higher, and the
    /// square wore a flat green tile while the row two inches away wore the mark. One rule and one winner
    /// are used now. The surfaces differ only in the size they decode at.</para>
    ///
    /// <para>The bytes are fetched at <see cref="SquareLogoEdge"/>, the larger of the two sizes, so one
    /// fetch serves both. Two sizes would mean two requests for one asset, cached under two keys.</para>
    /// </summary>
    private static Task<Settlement> SettledBytesFor(RadioStation station)
    {
        if (Settled.TryGetValue(station.Uuid, out var asked) && !FinishedIncomplete(asked))
            return asked;

        if (Settled.Count >= LogosKept)
            Settled.Clear();

        var errand = WalkAsync(station);
        Settled[station.Uuid] = errand;
        return errand;
    }

    private static readonly Dictionary<string, Task<Settlement>> Settled =
        new(StringComparer.Ordinal);

    /// <summary>
    /// What a station's walk came to: the best picture it publishes, and the best raster (pixel) one.
    /// </summary>
    /// <param name="Bytes">The winner, which is what the row draws. Null where the station has nothing
    /// that loads.</param>
    /// <param name="Url">Where the winner came from. It is carried because the made ground is seeded from
    /// it, so a station's badge is the same badge every time it is drawn.</param>
    /// <param name="IsDrawing">Whether the winner is an SVG. It is decoded through a different image
    /// source and cannot be given to anything that wants pixels.</param>
    /// <param name="Raster">The best candidate that is NOT a drawing. The surfaces that read pixels need
    /// it: the now playing square and the home banner's plate. It is the same object as <paramref
    /// name="Bytes"/> whenever the winner is a raster. The walk arranges that on purpose. See <see
    /// cref="WalkAsync"/>.</param>
    /// <param name="Complete">Whether every candidate the walk looked at answered. It is False when one
    /// could not be fetched. The best of what did arrive is still shown, but the walk is not kept as the
    /// station's answer, so the next surface to ask walks again. The fetches that did answer are kept in
    /// <see cref="Logos"/>, so a second walk asks again only for the ones that failed.</param>
    private readonly record struct Settlement(
        byte[]? Bytes, string Url, bool IsDrawing, byte[]? Raster, bool Complete);

    /// <summary>
    /// The same station's picture, decoded for a ROW, with the ground made for it where it is a mark.
    /// It is cached separately from the bytes, because the decode depends on the size and the bytes do not.
    /// </summary>
    private static async Task<FoundLogo> SettledFor(RadioStation station)
        => (await RowFor(station)).Logo;

    /// <summary>The row's decode, kept per station together with whether the walk under it was complete.
    /// An incomplete one is decoded again from a fresh walk, not served. See <see
    /// cref="Settlement.Complete"/>.</summary>
    private static Task<RowLogo> RowFor(RadioStation station)
    {
        if (Rows.TryGetValue(station.Uuid, out var asked)
            && !(asked.IsCompletedSuccessfully && !asked.Result.Complete))
            return asked;

        if (Rows.Count >= LogosKept)
            Rows.Clear();

        var errand = RowLogoAsync(station);
        Rows[station.Uuid] = errand;
        return errand;
    }

    private readonly record struct RowLogo(FoundLogo Logo, bool Complete);

    private static readonly Dictionary<string, Task<RowLogo>> Rows =
        new(StringComparer.Ordinal);

    private static async Task<RowLogo> RowLogoAsync(RadioStation station)
    {
        var settled = await SettledBytesFor(station);
        return new RowLogo(
            settled.Bytes is { Length: > 0 } ? await DressAsync(settled, RowLogoEdge) : default,
            settled.Complete);
    }

    /// <summary> Walks everything a station publishes and keeps the best of it: its own logos first, then
    /// whatever its front page and manifest declare.
    ///
    /// <para><b>Fetched, then measured, rather than handed to a control as a URL.</b> A
    /// <c>BitmapImage(uri)</c> makes its own request with no user agent, and many hosts answer that with
    /// 403. alive.radio does. Fetching also makes the fall-through definite. Every candidate is settled
    /// before anything is put on screen. Nothing depends on an Image control raising a failure, which it
    /// does not do for a drawing that never loaded.</para>
    ///
    /// <para><b>Only a MARK stops the walk early, and only one already big enough for the largest
    /// surface.</b> Everything else is ranked, and the walk runs on. See <see cref="Worth"/>.</para>
    /// </summary>
    private static async Task<Settlement> WalkAsync(RadioStation station)
    {
        // The API's answer comes first, and it is usually the last one used.
        //
        // The crawl in tools/StationLogos walks all 17,376 station sites once. It settles the best logo
        // it can find for 42,750 of the 59,654 stations the directory lists. This is the same walk this
        // method would otherwise do: the same StationIcons parser and the same ranking. It runs once for
        // everybody, not once per install. The answer arrives as a 512 px mark, rather than whatever the
        // station happened to publish.
        //
        // It leads the list rather than replacing it. The directory sometimes has a logo for a station
        // the crawl could not settle, and a reader who is offline still gets whatever their own cache
        // holds.
        var candidates = new List<StationIcon>();

        // Whether the server's answer is a person's choice rather than the crawl's. If it is, it is not
        // ranked as a candidate, and it is not measured against anything. See below.
        var chosen = RadioState.IsByHand(station.Uuid);

        if (RadioLogoApi.For(station) is { } served)
            candidates.Add(new StationIcon(served, StationIconKind.Manifest));

        candidates.AddRange(LogosOf(station).Select(url => new StationIcon(url, StationIconKind.Icon)));

        byte[]? best = null;
        var bestUrl = "";
        var bestDrawing = false;
        var bestWorth = (Tier: Nothing, Side: 0);

        // Tracked alongside, because the square and the banner read PIXELS, and nothing on this head
        // rasterizes an SVG outside an XAML image source. Usually this is the same object as best.
        byte[]? raster = null;
        var rasterWorth = (Tier: Nothing, Side: 0);

        var at = -1;
        var askedTheStation = false;

        // Whether every candidate the walk looked at answered. A candidate that could not be fetched may
        // have been the best one. So a walk that met a failure is shown but not settled. See Complete.
        var complete = true;

        while (true)
        {
            while (at + 1 < candidates.Count)
            {
                at++;
                var icon = candidates[at];

                // A drawing is kept as it came. It has no raster bounds to measure and no background, so
                // it is a mark at whatever size is asked of it.
                if (!(await Fetched(icon)).Answers(out var fetched, out var why))
                {
                    complete = false;
                    Walked($"walk {station.Display}: {icon.Kind} FAILED ({why}) {icon.Url}");
                    continue;
                }

                if (fetched is not { Length: > 0 } bytes)
                {
                    Walked($"walk {station.Display}: {icon.Kind} nothing {icon.Url}");
                    continue;
                }

                // The address is used where it says so, and the bytes otherwise. The API's URLs carry no
                // extension, and the object behind one is webp or svg.
                var drawing = RadioLogo.IsVector(icon.Url) || RadioLogo.IsDrawing(bytes);

                // A drawing is a mark by construction. It has no background unless one is drawn into it,
                // and it is sharp at every size. It is ranked AT the floor, not above everything. That
                // way a real raster mark of any decent size is preferred to it. The square and the banner
                // cannot read a drawing at all. A station whose row wears a drawing while its header
                // wears something else is the disagreement this walk exists to prevent. It still wins
                // outright over any picture and over a small mark.
                var worth = drawing ? (TheMark, MarkFloor) : WorthOf(bytes);

                Walked($"walk {station.Display}: {icon.Kind} tier {worth.Item1} side {worth.Item2} {icon.Url}");

                if (worth.CompareTo(bestWorth) > 0)
                {
                    bestWorth = worth;
                    best = bytes;
                    bestUrl = icon.Url;
                    bestDrawing = drawing;
                }

                if (!drawing && worth.CompareTo(rasterWorth) > 0)
                {
                    rasterWorth = worth;
                    raster = bytes;
                }

                // A logo that a person chose wins whatever it measures.
                //
                // Ranking it was wrong, and it looked like the upload had failed. The tiers run TooSmall
                // < APicture < TheMark, and they compare on size within a tier. So an opaque 300 px logo
                // that a person picked scored below the directory's own 512 px picture, and lost to it.
                // Of 38 hand-picked logos that were measured, most were under 512 px or had no
                // transparency.
                //
                // Only the FIRST candidate, which is the server's, counts, and only once it has actually
                // been fetched. A station whose chosen logo cannot be reached falls through to the rest
                // exactly as before.
                if (chosen && at == 0)
                {
                    Walked($"walk {station.Display}: CHOSEN BY HAND, taking {icon.Url}");
                    return new Settlement(bytes, icon.Url, drawing, drawing ? null : bytes,
                                          Complete: true);
                }

                // A raster mark that already fills the largest surface. Nothing further down can improve
                // on it, and it is the same answer for every surface.
                if (!drawing && worth.Item1 == TheMark && worth.Item2 >= SquareLogoEdge)
                {
                    Walked($"walk {station.Display}: STOPPED at {icon.Url}");
                    return new Settlement(bytes, icon.Url, false, bytes, Complete: true);
                }
            }

            // Everything the API and the directory had is spent. The station's own front page is asked
            // next, and only then.
            //
            // The crawl in tools/StationLogos now does this walk once for everybody, and its answer is
            // the first candidate above. So this is the last resort. It covers 44,257 of the 59,664
            // stations the directory lists, so it is reached for about a quarter of stations, and only
            // for the rows a reader actually scrolls past.
            //
            // Do not remove this fallback. The crawl cannot reach every station, and without it the app
            // would be worse for every station the crawl missed. A server that answers for most stations
            // is a reason to ask it first, not a reason to stop looking.
            if (askedTheStation)
                break;

            askedTheStation = true;
            if ((await Published(station.Homepage)).Answers(out var published, out var unread))
            {
                candidates.AddRange(published.Icons);

                // ...and tell the API what this machine found, so the next reader does not have to look.
                // Fire and forget: a contribution that fails is one nobody was waiting for. See
                // RadioLogoApi.Propose.
                if (published.Icons.Count > 0)
                    RadioLogoApi.Propose(station, published.Icons);
            }
            else
            {
                complete = false;
                Walked($"walk {station.Display}: page FAILED ({unread}) {station.Homepage}");
            }
        }

        // ...and the encyclopedia, for a station that has come up with nothing worth drawing.
        //
        // This is the last step, and a rule keeps it affordable. It is one request per STATION, not per
        // host, so asking for every station would mean a request for every row. A station whose own logos
        // worked never reaches this line.
        //
        // It is the answer for the stations that had nothing at all. Europe 1's own site answers 403 to
        // this app, so the walk had one dead favicon, gave up, and the row drew the antenna. Yet
        // fr.wikipedia has its wordmark at 1000x368, and the Radio Kiss Kiss article carries its mark at
        // 737x584 with a fully transparent border.
        //
        // What stops it going wrong is the test already on the article. IsARadioStation reads the
        // description, so "Rafi hit songs" does not come back wearing a photograph of Mohammed Rafi
        // ("Indian playback singer"), and CCTV-13 does not come back as a television channel. Both cases
        // were tried, and both are refused.
        Asked<Article>? asked = null;
        if (bestWorth.Tier <= TooSmall && station.Display is { Length: > 0 } named)
        {
            asked = await ArticleFor(named, CancellationToken.None);
            if (asked is Asked<Article>.Unanswered)
                complete = false;
        }

        if (asked is Asked<Article>.Answered { Value.Picture: { Length: > 0 } article })
        {
            var icon = new StationIcon(article, StationIconKind.Logo);
            var fetched = await Fetched(icon);
            if (fetched is Asked<byte[]?>.Unanswered)
                complete = false;

            if (fetched is Asked<byte[]?>.Answered { Value: { Length: > 0 } bytes })
            {
                var worth = WorthOf(bytes);
                Walked($"walk {station.Display}: encyclopedia tier {worth.Tier} side {worth.Side} {article}");

                if (worth.CompareTo(bestWorth) > 0)
                {
                    bestWorth = worth;
                    best = bytes;
                    bestUrl = article;
                    bestDrawing = false;
                    raster = bytes;
                }
            }
        }

        Walked($"walk {station.Display}: SETTLED tier {bestWorth.Tier} side {bestWorth.Side} {bestUrl}");
        return new Settlement(best, bestUrl, bestDrawing, raster, complete);
    }

    /// <summary> How good a candidate is, judged on its BYTES: a tier, then its short side. The tiers are
    /// compared as a tuple, tier first. So a mark always beats a picture, and size only settles ties
    /// within a tier.
    ///
    /// <para><b>A transparent mark outranks an opaque picture, however much bigger the picture is.</b>
    /// That is the whole rule. An earlier version tiered against "does this cover the surface asking".
    /// That sounds like the same thing, and it is not. The now playing square asks at 512, so a 256-pixel
    /// transparent mark came out "too small", while an opaque 512 tile came out "a picture", and the tile
    /// ranked above the mark. The threshold below is a floor on what counts as a mark at all. It is not a
    /// measure of the surface.</para>
    ///
    /// <para><b>Why the app wants the mark and not the bigger picture.</b> Everything that draws a
    /// station (the row's well, the now playing square, the home banner's plate) draws it on a ground
    /// built from the mark's own ink (see <see cref="TransparentMark.Ground"/>). A picture with its own
    /// background cannot use any of that. It is a rectangle laid over the ground, so the ground is never
    /// seen, and the app's own shape is lost. A mark half the size of a tile is the better picture here,
    /// even though it is the smaller one.</para>
    ///
    /// <para><b>The floor stops that going wrong.</b> A 32-pixel favicon has alpha too. Without a floor
    /// it would beat a broadcaster's 512-pixel logo. <see cref="MarkFloor"/> is what a candidate must
    /// reach before its transparency counts for anything. Below it, everything competes on size alone,
    /// and the biggest picture wins. That is what a station with nothing good published should
    /// get.</para>
    /// </summary>
    private static (int Tier, int Side) WorthOf(byte[] bytes)
    {
        var (width, height) = Services.Art.ArtScaler.BoundsOf(bytes);
        var side = Math.Min(width, height);

        if (side <= 0)
            return (Nothing, 0);

        if (side < MarkFloor)
            return (TooSmall, side);

        // Taken from the PIXELS, not the header, and it asks the harder question. The question is not "is
        // anything in this see-through", but "is this a mark standing on nothing". Those differ on the
        // picture that matters most here. An apple-touch icon is usually a solid tile with its corners
        // rounded off. It has alpha, and it is not a mark. See TransparentMark.StandsAlone.
        //
        // It is paid for only above the floor, because it is a decode and a walk round the border. There
        // is no sense spending it on a candidate that cannot win anyway.
        return (TransparentMark.StandsAlone(bytes) ? TheMark : APicture, side);
    }

    /// <summary>
    /// How big a picture must be before being a mark counts in its favour.
    ///
    /// <para>The row's well is the smallest surface a station is drawn on. Under this size a picture is
    /// enlarged wherever it is shown, and a soft mark is not better than a sharp tile. Above it,
    /// transparency decides. There is one number for every surface, on purpose. If the row and the square
    /// used different floors, they could settle on different pictures of one station. That is the fault
    /// this whole rule exists to prevent.</para>
    /// </summary>
    private const int MarkFloor = RowLogoEdge;

    /// <summary> What each candidate in a logo walk turned out to be, for working out why a station wears
    /// the picture it wears.
    ///
    /// <para>Off unless <c>RPM_LOGO_DIAG=1</c> is set in the environment, like the rest of the app's
    /// diagnostics. The walk makes a request for each line it prints, so in an ordinary run this would be
    /// noise on the way past.</para>
    /// </summary>
    internal static void Walked(string line)
    {
        if (LogoDiag) Console.WriteLine("[logo] " + line);
    }

    internal static readonly bool LogoDiag =
        RPM.Core.Diagnostics.DevSwitch.Read("RPM_LOGO_DIAG") is "1" or "true";

    private const int Nothing = 0;

    /// <summary>A picture under <see cref="MarkFloor"/>. It is better than nothing, and it is drawn soft. Its
    /// transparency is not counted. See <see cref="WorthOf"/>.</summary>
    private const int TooSmall = 1;

    /// <summary>A picture big enough to draw, with a background of its own. It is perfectly drawable, but
    /// the walk keeps looking past it for a mark, however big it is.</summary>
    private const int APicture = 2;

    /// <summary>The mark itself: big enough to draw, with nothing behind it. This is what every surface
    /// here is shaped for, and it is the only thing that stops the walk early.</summary>
    private const int TheMark = 3;

    /// <summary>Whether a picture has the pixels the well is drawn at. A drawing measures nothing and covers
    /// everything. It is rasterized to whatever size is asked of it.</summary>
    private static bool Covers(FoundLogo found)
        => found.Width <= 0 || found.Height <= 0 || Side(found) >= RowLogoEdge;

    /// <summary>A picture's short side. A square well shows the whole of it. Zero for a logo that was
    /// never found, so anything at all beats it.</summary>
    private static int Side(FoundLogo found) => Math.Min(found.Width, found.Height);

    /// <summary>
    /// What the mark stands on, where it is a mark with nothing of its own behind it.
    ///
    /// <para>Null for a logo that is already a solid picture. Those are their own background, and a made
    /// ground under an opaque square would never be seen. It is also null while the walk is still
    /// running. That leaves the well its plain dark until a picture arrives.</para>
    /// </summary>
    public Microsoft.UI.Xaml.Media.ImageSource? LogoGround => _found.Ground;

    /// <summary>
    /// How the logo is fitted to the well. This is not the same question for a picture and for a mark.
    ///
    /// <para><b>A solid SQUARE logo fills it.</b> It is a square of picture with its own background and
    /// its own corners. Anything less leaves it floating inside the well, with a ring of the app's dark
    /// between it and the rounded edge. That reads as a badge that failed to load, not as the station's
    /// mark. Filled, the well's own corner radius rounds it, and the two become one shape.</para>
    ///
    /// <para><b>A wide one is fitted whole.</b> Filling is a CROP, and it is only free where the picture
    /// is about as square as the well. 538 Non-Stop publishes a wide magenta lockup. Filled into a square
    /// well, it lost both ends of itself. The shape decides, not the fact that it is a picture. See <see
    /// cref="FoundLogo.Fills"/>.</para>
    ///
    /// <para><b>A mark stands on its ground, held off the edges.</b> It has nothing behind it of its own,
    /// so what it needs is room. Run to the corners, it would sit on the very edge of the ground with
    /// nothing to separate it from the row. It uses the same pair of rules that the now playing square
    /// draws by. See ArtDisplay.Cover's Frame, where the argument is written out at length.</para>
    /// </summary>
    public Microsoft.UI.Xaml.Media.Stretch LogoStretch => _found.Fills
        ? Microsoft.UI.Xaml.Media.Stretch.UniformToFill
        : Microsoft.UI.Xaml.Media.Stretch.Uniform;

    /// <summary>The room a logo is given inside the well. None for the one that fills it, which is the
    /// well's own shape. See <see cref="LogoStretch"/>.</summary>
    public Microsoft.UI.Xaml.Thickness LogoPad =>
        new(_found.Fills ? 0 : MarkInset);

    /// <summary>How far a mark is held off the well's edges, at the well's own size.</summary>
    private const double MarkInset = 4;

    private void RaiseLogo()
    {
        Raise(nameof(Logo));
        Raise(nameof(LogoGround));
        Raise(nameof(LogoStretch));
        Raise(nameof(LogoPad));
        Raise(nameof(HasLogo));
        Raise(nameof(NoLogoVisibility));
    }

    /// <summary>
    /// One candidate's bytes, fetched at most once, however many stations and surfaces ask for it.
    ///
    /// <para>The TASK is what is kept. Two walks that reach the same address therefore share one request
    /// instead of racing. An address that turned out to be nothing is remembered as nothing.</para>
    ///
    /// <para><b>Bytes, not a decoded logo, and at the LARGER of the two sizes.</b> A decode made for the
    /// row's 102-pixel well could not be used by the now playing square, which then fetched everything a
    /// second time at 512. That is two requests and two cache entries for one picture, and two chances to
    /// settle on different pictures of one station. One fetch serves both now. Each surface decodes the
    /// bytes at its own size.</para>
    ///
    /// <para>No lock: every caller is on the UI thread.</para>
    ///
    /// <para><b>A failure is not kept.</b> An entry whose fetch finished without an answer is started
    /// again the next time it is asked for. Keeping it would make one failed request count as "this
    /// address has no logo" for the rest of the session.</para>
    /// </summary>
    private static Task<Asked<byte[]?>> Fetched(StationIcon icon)
    {
        // The KIND is part of the key. A social image has to be roughly square before it is worn as a
        // logo (see MarkBytesAsync), so the same address asked about in two ways gives two answers.
        //
        // The key is the address as it will be FETCHED, not as the directory filed it. A station's
        // entries name one asset at three sizes (see RadioLogo.Sized). Once a size has been asked for,
        // all three become the same request. Keyed on the raw field, this would hold three copies of one
        // picture and fetch it three times to fill them.
        var key = $"{(int)icon.Kind}|{RadioLogo.Sized(icon.Url, SquareLogoEdge)}";
        if (Logos.TryGetValue(key, out var asked) && !FinishedUnanswered(asked))
            return asked;

        // Bounded roughly, like every other picture cache here. Each entry can be rebuilt from one fetch,
        // so what this needs is a ceiling, not a careful eviction rule.
        if (Logos.Count >= LogosKept)
            Logos.Clear();

        var errand = MarkBytesAsync(icon, SquareLogoEdge, CancellationToken.None);
        Logos[key] = errand;
        return errand;
    }

    /// <summary> What a station's own front page declares, for the stations the directory has no picture
    /// for. That is 164 of the 400 stations it lists for the Netherlands, while 389 of them give a
    /// homepage.
    ///
    /// <para>It is kept per HOST, not per station. A broadcaster's several stations share a site, and a
    /// page is a far heavier thing to fetch than an icon. The task is kept, so stations realizing
    /// together wait on one fetch, rather than each asking for the same page.</para>
    /// </summary>
    /// <remarks>A page that could not be read is not kept, so the next ask for the host reads it again (see
    /// <see cref="FinishedUnanswered{T}"/>).</remarks>
    private static Task<Asked<StationPage>> Published(string? homepage)
    {
        if (!Uri.TryCreate(homepage, UriKind.Absolute, out var page)
            || (page.Scheme != Uri.UriSchemeHttp && page.Scheme != Uri.UriSchemeHttps))
            return Task.FromResult(Asked.Answer(default(StationPage)));

        var host = page.Host.ToLowerInvariant();
        if (Pages.TryGetValue(host, out var asked) && !FinishedUnanswered(asked))
            return asked;

        if (Pages.Count >= PagesKept)
            Pages.Clear();

        // What this host's site said last time, read from disk before anything is fetched. A site is read
        // once a month, not once a visit. A site that was read and declared nothing is remembered as
        // having declared nothing, so it is not read again every time the pane is opened.
        //
        // Only an ANSWER is ever filed (see ReadPageAsync). So a hit here can never be a past failure
        // served as "this site says nothing". That is the fault Asked<T> exists to stop. It is also why
        // this check sits under the FinishedUnanswered check, not above it.
        //
        // Answered synchronously and filed as a completed task, so the coalescing above still works.
        // Whatever asks second still finds a task, rather than starting a second read.
        if (Services.Catalog.StationSites.Read(host) is { } remembered)
        {
            Walked($"site {host}: remembered, {remembered.Icons.Count} icons");
            var known = Task.FromResult(Asked.Answer(new StationPage(remembered.Icons, remembered.Says)));
            Pages[host] = known;
            return known;
        }

        var errand = ReadPageAsync(page, host);
        Pages[host] = errand;
        return errand;
    }

    /// <returns>Unanswered where the page, or the manifest it names, could not be fetched. A page read
    /// without its manifest would miss the big transparent mark, and would be kept that way.</returns>
    private static async Task<Asked<StationPage>> ReadPageAsync(Uri page, string host)
    {
        // This uses RemoteImage.PageAsync, not BytesAsync. BytesAsync returns null for any body the host
        // declared as text, deliberately, and a front page is text/html. So the homepage walk never found
        // anything. It read nothing, for every station, and the only symptom was a station wearing the
        // antenna while its own site had its logo. Same client, and only that one check differs.
        //
        // PageKeep.Nothing: the body is fetched and let go. A front page averages 177 KB and can run to
        // 5.4 MB. Filing every page this walk read under the artwork cache filled about 74 MB of a 256 MB
        // store. That pushed out the album covers and the station logos, and each page was walked again
        // on every visit to the pane. Instead, the few addresses below are kept, through StationSites.
        if (!(await RemoteImage.PageAsync(page.ToString(), PageKeep.Nothing)).Answers(out var html, out var why))
            return Asked.Failed<StationPage>(why);
        if (html is not { Length: > 0 })
            return Filed(host, default);

        var declared = StationIcons.From(html, page.ToString());

        // ...and the site's web app manifest. It is a second file, and it is where the big transparent
        // copy of a station's mark actually is. A page's own tags top out at the apple-touch icon's 180
        // pixels, and iOS composites that one onto a tile, so sites flatten it. The manifest lists the
        // same mark at 512 with its alpha. That is what the now playing square draws, and what the row's
        // made ground needs in order to stand off something. See StationIcons.FromManifest for what it is
        // worth across the directory's most-clicked stations.
        //
        // One extra request, made once per HOST rather than per station, on a connection that is already
        // open and behind the same disk cache as the page. It happens only for the stations that reach
        // this walk at all, which are the ones the directory had no usable picture for.
        if (StationIcons.ManifestAt(html, page.ToString()) is { } manifest)
        {
            if (!(await RemoteImage.PageAsync(manifest, PageKeep.Nothing)).Answers(out var listed, out why))
                return Asked.Failed<StationPage>(why);
            if (listed is { Length: > 0 })
                declared = StationIcons.Best([.. declared, .. StationIcons.FromManifest(listed, manifest)]);
        }

        // What the broadcaster says it is, from the same head and the same fetch. It is read here, not by
        // a second walk of the page, so a station's mark and its line cost one request between them. See
        // StationBlurb, which is why this cache holds the answer rather than the icons.
        var says = StationBlurb.FromPage(html);

        // ...and the address every site keeps this at, for a page that declared none. It comes last,
        // after everything the page actually said, and it is skipped where the page already said the same
        // thing.
        var icons = StationIcons.WellKnown(page.ToString()) is { } guess
                    && !declared.Any(i => string.Equals(i.Url, guess, StringComparison.OrdinalIgnoreCase))
            ? (IReadOnlyList<StationIcon>)[.. declared, new StationIcon(guess, StationIconKind.Icon)]
            : declared;

        return Filed(host, new StationPage(icons, says));
    }

    /// <summary>
    /// Keeps what a site said, and hands it back.
    ///
    /// <para>This is only reached with an ANSWER. The two ways this read can fail, the page and the
    /// manifest it names, both return <c>Asked.Failed</c> above and never arrive here. So nothing a host
    /// did not say can be filed as something it said. The store depends on this rule, and it is held by
    /// the returns, not by a flag. See StationSites, and Asked&lt;T&gt; for why the distinction is a
    /// type.</para>
    ///
    /// <para>An answer with NO icons is filed too, and it is the one most worth filing. Those are the
    /// sites the walk would otherwise read again on every visit for nothing. Among the stations that get
    /// this far, they are the common case.</para>
    /// </summary>
    private static Asked<StationPage> Filed(string host, StationPage found)
    {
        Services.Catalog.StationSites.Write(
            host, new Services.Catalog.StationSites.Found(found.Icons, found.Says));
        Walked($"site {host}: read and filed, {found.Icons.Count} icons");
        return Asked.Answer(found);
    }

    /// <summary>What one reading of a station's front page found: the pictures it declares, and the line
    /// it describes itself with. It is one record, not two caches, because it is one fetch. The two
    /// answers cannot be asked for separately without asking for the page twice.</summary>
    /// <param name="Icons">Best first, in the order <see cref="StationIcons.From"/> gives them.</param>
    /// <param name="Says">The broadcaster's own description, already cut to one line. Null where the page
    /// declares none.</param>
    private readonly record struct StationPage(IReadOnlyList<StationIcon> Icons, string? Says)
    {
        /// <summary>What was passed in. It may be null, and for <c>default</c> it always is.</summary>
        private readonly IReadOnlyList<StationIcon>? _icons = Icons;

        /// <summary>
        /// The pictures. This is never null.
        ///
        /// <para><b>Computed, not an auto-property with an initializer.</b> An initializer runs in the
        /// CONSTRUCTOR, and <c>default(StationPage)</c> does not call a constructor. It is zeroed memory,
        /// so the list would be null. The default of this struct is a page that could not be read, and it
        /// has neither a list nor a line. Both paths that produce a page that could not be read return
        /// <c>default</c>: a homepage that is not an http address, and a fetch that came back with
        /// nothing.</para>
        ///
        /// <para>It did not throw until the walk started reaching this more often (see <see
        /// cref="Worth"/>). Before that, only a station with no usable logo asked its page. A station
        /// with no usable logo and no readable homepage is rare, so the crash stayed hidden. Coalescing
        /// on READ has no such window.</para>
        /// </summary>
        public IReadOnlyList<StationIcon> Icons => _icons ?? [];
    }

    private static readonly Dictionary<string, Task<Asked<StationPage>>> Pages =
        new(StringComparer.Ordinal);

    /// <summary>Hosts whose page has been read. It is smaller than the logo ceiling. A page is only asked
    /// for by the stations the directory failed, and several of those share one host.</summary>
    private const int PagesKept = 64;

    private static readonly Dictionary<string, Task<Asked<byte[]?>>> Logos =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether a kept fetch finished without an answer, so it must be asked again rather than
    /// served. It is read off the task when it is looked up. So it holds whenever it is asked, and it
    /// needs nothing to run when the fetch ends.</summary>
    private static bool FinishedUnanswered<T>(Task<Asked<T>> fetch)
        => fetch.IsCompletedSuccessfully && fetch.Result is Asked<T>.Unanswered;

    /// <summary>The same question about a station's walk: finished, and whether some candidate in it
    /// could not be fetched. See <see cref="Settlement.Complete"/>.</summary>
    private static bool FinishedIncomplete(Task<Settlement> walk)
        => walk.IsCompletedSuccessfully && !walk.Result.Complete;

    /// <summary>More than a screenful of rows, so scrolling a search up and down does not fetch again
    /// what it has just loaded.</summary>
    private const int LogosKept = 256;

    /// <summary>The pixels a row's 51-point well wants, near enough for any display: the well at twice
    /// its size. It follows the well. If this number is not kept in step with the well, every logo is
    /// enlarged on a 2x screen.</summary>
    private const int RowLogoEdge = 102;

    /// <summary> The size the playing square wants. It is the other surface a station's mark is drawn on,
    /// and it is a far larger one.
    ///
    /// <para>This is not the cover pane's own DisplayEdge. A mark is drawn inside that square with room
    /// around it, rather than filling it. Asking a resizing host for a thousand pixels of a badge costs a
    /// quarter of a megabyte, to draw a logo the size of a sleeve's corner.</para>
    /// </summary>
    private const int SquareLogoEdge = 512;

    /// <summary> One candidate's bytes, or null where it is not a picture worth showing. That is the case
    /// when the address is not worth trying, the host would not serve it, what came back is not an image,
    /// or it is a social picture the wrong shape to be a mark.
    ///
    /// <para>It is separated from the decode, because two surfaces want different ends of it. A row wants
    /// an image source. The now playing square wants the BYTES, so that it can read the mark's ink, draw
    /// a ground away from it, and bake a halo in. See ArtDisplay.Cover.</para>
    /// </summary>
    /// <param name="edge">How many pixels of mark the surface asking for this one draws. It is not a
    /// hint. A directory logo is an address for a picture AT A SIZE, and on the hosts that cut to order,
    /// this is the size they are asked for. There is no default, because a caller that forgot would
    /// silently get whichever size a stranger typed into a form. That is the whole fault. See
    /// RadioLogo.Sized.</param>
    /// <returns>Answered with the mark's bytes, or with null where the address holds nothing usable as
    /// one. Unanswered where it could not be fetched. See <see cref="RemoteImage.BytesAsync"/>.</returns>
    private static async Task<Asked<byte[]?>> MarkBytesAsync(StationIcon icon, int edge, CancellationToken ct)
    {
        if (!RadioLogo.IsDrawable(icon.Url))
            return Asked.Answer<byte[]?>(null);

        var address = RadioLogo.Sized(icon.Url, edge);

        // A host that cuts to order is asked how much picture it actually HAS before it is asked for a
        // size. Otherwise it cannot be asked at all. Request five hundred pixels, and five hundred
        // arrive, whether the asset behind them is five hundred or sixteen. A kilobyte off the front of
        // the bare asset is its header, and the header is the honest answer. See RadioLogo.Native, and
        // RemoteImage.HeaderAsync for why one kilobyte is the whole cost.
        //
        // Under the size wanted, the bare asset IS the picture. There is no more of it anywhere, so it is
        // taken as it stands, rather than blown up to a size it cannot fill. The walk that asked for it
        // then sees a small picture, and knows to keep looking (see FindLogoAsync).
        if (!RadioLogo.IsVector(icon.Url)
            && RadioLogo.Native(icon.Url) is { } bare
            && await RemoteImage.HeaderAsync(bare, ct) is { } front)
        {
            var (has, tall) = Services.Art.ArtScaler.BoundsOf(front);
            if (has > 0 && tall > 0 && Math.Min(has, tall) < edge)
                address = bare;
        }

        if (!(await RemoteImage.BytesAsync(address, ct)).Answers(out var fetched, out var why))
            return Asked.Failed<byte[]?>(why);
        if (fetched is not { } bytes)
            return Asked.Answer<byte[]?>(null);

        // A drawing has no raster bounds to measure, and no shape to judge. It is either read by an
        // SvgImageSource, or it is nothing.
        //
        // This is asked of the bytes as well as the address. rpm's own API serves from radio/logo/{uuid}
        // with no extension. A drawing from there would fall past this check, fail to measure, and be
        // thrown away as "not an image anything here reads".
        if (RadioLogo.IsVector(icon.Url) || RadioLogo.IsDrawing(bytes))
            return Asked.Answer<byte[]?>(bytes);

        // A browser-tab icon is a container of frames, not a picture, and no decoder in the app opens
        // one. It is unwrapped here, so the rest of this does not have to know.
        if (icon.Url.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
        {
            if (IcoImage.LargestFrame(bytes) is not { } frame)
                return Asked.Answer<byte[]?>(null);
            bytes = frame;
        }

        var (width, height) = Services.Art.ArtScaler.BoundsOf(bytes);
        if (width <= 0 || height <= 0)
            return Asked.Answer<byte[]?>(null);   // served, but not an image anything here can read

        // A social image is what a site wants to show when the page is SHARED. That is often a photograph
        // as much as a mark. Radio 538's is 1200x630 of its presenters. The shape of what arrived is the
        // only thing that tells them apart. It is checked here, not on the page, because the page's claim
        // is the unreliable part.
        return Asked.Answer<byte[]?>(
            icon.Kind != StationIconKind.Social || StationIcons.LooksLikeAMark(width, height)
                ? bytes
                : null);
    }

    /// <summary>
    /// What a station is playing right now, without tuning it in.
    /// </summary>
    /// <remarks> For the home screen's radio card. It shows what is on the station, so a reader can see
    /// whether they want it before pressing. How the title is found, and what that costs, is on <see
    /// cref="IcyPeek"/>. The only way to know is to open the stream. So this opens it, reads to the first
    /// metadata block, and hangs up.
    ///
    /// <para><b>The cache here is not the usual blunt ceiling. It has a clock.</b> Every other cache in
    /// this file holds an answer that does not change. A logo is a logo. This one holds an answer that is
    /// wrong in three minutes. Asking again every time a card came round would mean a connection to
    /// somebody's server every few seconds. So an answer is kept for <see cref="OnAirFor"/>, and the
    /// station is asked again after that.</para>
    ///
    /// <para>The TASK is kept, not the string, like the rest of this file. Two cards asking together
    /// share one peek. A station that answered with nothing is remembered as nothing for the same
    /// stretch. A station that sends no metadata sends none a second later either.</para>
    ///
    /// <para><b>Never for a station that is tuned.</b> The tuner already has the title, live and free. A
    /// peek would be a second connection to a station this app is already listening to. The caller
    /// decides this, because only it knows what is tuned. See <c>HomeView.ShowWhatIsOn</c>.</para>
    /// </remarks>
    public static Task<string?> OnAirAsync(RadioStation station, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(station);

        if (string.IsNullOrWhiteSpace(station.Uuid) || station.IsHls)
            return Task.FromResult<string?>(null);

        var now = Environment.TickCount64;

        if (Peeked.TryGetValue(station.Uuid, out var asked) && now - asked.AtMs < OnAirFor.TotalMilliseconds)
            return asked.Answer;

        if (Peeked.Count >= PeeksKept)
            Peeked.Clear();

        // Runs on a worker thread: IcyPeek blocks on a socket, like everything under IcyConnection.
        var errand = Task.Run(() => IcyPeek.Title(station, ct), ct);
        Peeked[station.Uuid] = (errand, now);
        return errand;
    }

    /// <summary>How long a peeked title is believed. A song is about three minutes, and this is well
    /// inside that. So the line is either current or one song behind. A shorter time would mean a
    /// connection to a station every time its card came round, which is every forty-nine seconds on a
    /// full strip.</summary>
    private static readonly TimeSpan OnAirFor = TimeSpan.FromSeconds(90);

    /// <summary>What each station was last heard to be playing, and when it was asked. It is bounded like
    /// every other cache here. The clock is what usually evicts an entry, not the ceiling.</summary>
    private static readonly Dictionary<string, (Task<string?> Answer, long AtMs)> Peeked =
        new(StringComparer.OrdinalIgnoreCase);

    private const int PeeksKept = 64;

    /// <summary> One short line saying what a station IS: what its own front page says, and failing that,
    /// what Wikipedia says about a station of that name.
    /// </summary>
    /// <remarks> For the home screen's radio card, which has a line under the station's name and nothing
    /// to put in it. Why these two sources, and in this order, is on <see cref="StationBlurb"/>.
    ///
    /// <para><b>The station's own page costs nothing here.</b> It is the same fetch that <see
    /// cref="LogoBytesAsync"/> already makes, and it uses the same per-host cache. So a station whose
    /// page has been read for its pictures answers this from memory. A station that the directory gave a
    /// working logo has NOT had its page read, so for that station this is the first request. It is one
    /// request, for a card that is on screen.</para>
    ///
    /// <para>Wikipedia is a second request. It is made only when the page said nothing. The result is
    /// kept per station name, so stepping to and fro across the banner asks once. That includes names
    /// that came back with nothing. An article that does not exist is an answer. Asking again for it
    /// every time the card came round would be the same 404 forever.</para>
    ///
    /// <para>Null is the ordinary answer for many stations. The card is finished without it, and the line
    /// is simply not drawn.</para>
    /// </remarks>
    public static async Task<string?> BlurbAsync(RadioStation station, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(station);

        // A description the admin wrote comes first, exactly as written. It does not go through
        // StationBlurb.Shorten. The admin has already held it to the same 80 characters, and Shorten
        // drops anything under 16 characters, which a short line such as "Dutch pop radio" is.
        if (StationEdits.Of(station)?.Description is { Length: > 0 } written)
            return written;

        // A page or an article that could not be read gives no line this time. Neither is kept, so the
        // next time the card is drawn it asks again.
        if ((await Published(station.Homepage)).Match(page => page.Says, _ => null) is { Length: > 0 } says)
            return says;

        if (ct.IsCancellationRequested || station.Display is not { Length: > 0 } name)
            return null;

        return (await ArticleFor(name, ct)).Match(article => article.Line, _ => null);
    }

    /// <summary> What Wikipedia has for a station of this name: its line, and the picture its article
    /// leads with. One request and one cache entry serve both, because they come from one body.
    ///
    /// <para>It is kept per name, including the names that came back with nothing. An article that does
    /// not exist is an answer. Asking again for it every time a row comes round would be the same 404
    /// forever.</para>
    /// </summary>
    private static Task<Asked<Article>> ArticleFor(string name, CancellationToken ct)
    {
        // An article that could not be fetched is not kept. See FinishedUnanswered.
        if (Blurbs.TryGetValue(name, out var asked) && !FinishedUnanswered(asked))
            return asked;

        if (Blurbs.Count >= BlurbsKept)
            Blurbs.Clear();

        var errand = AskTheEncyclopediaAsync(name, ct);
        Blurbs[name] = errand;
        return errand;
    }

    /// <summary>One article, as far as this app cares about it.</summary>
    /// <param name="Line">The station's one line, or null where the article is not about a radio station
    /// or does not exist.</param>
    /// <param name="Picture">The picture the article leads with, under the same test. It is a lead image,
    /// not a logo (see <see cref="StationBlurb.LogoFromWikipedia"/>), so whatever uses it must judge it
    /// like any other candidate.</param>
    private readonly record struct Article(string? Line, string? Picture);

    /// <summary>Wikipedia's one-paragraph summary for a station of this name, in the reader's own
    /// language and then in English. Whether the article is the RIGHT one is decided by <see
    /// cref="StationBlurb.FromWikipedia"/>, which refuses anything that does not read as a radio
    /// station.</summary>
    /// <returns>The article. An answered empty article where no language has one. Unanswered where none
    /// was found and at least one language could not be asked, because that language may have had
    /// it.</returns>
    private static async Task<Asked<Article>> AskTheEncyclopediaAsync(string name, CancellationToken ct)
    {
        string? failed = null;

        foreach (var language in Languages())
        {
            if (ct.IsCancellationRequested)
                return Asked.Failed<Article>("cancelled");

            try
            {
                // Fetched with PageAsync, for the reason ReadPageAsync gives. A summary is
                // application/json, and the picture fetcher refuses a body that the host declared as
                // text. Kept on disk. A summary is a few hundred bytes, and it is asked for again every
                // time the station comes round. That is the case the cache is for.
                if (!(await RemoteImage.PageAsync(
                        StationBlurb.SummaryUrl(language, name), PageKeep.OnDisk, ct))
                        .Answers(out var body, out var why))
                {
                    failed = why;
                    continue;
                }

                if (body is { Length: > 0 } && StationBlurb.FromWikipedia(body) is { Length: > 0 } said)
                    // The picture comes from the SAME body. The test that decides whether this article is
                    // about a radio station also decides whether its picture is this station's mark. Both
                    // halves, or neither.
                    return Asked.Answer(new Article(said, StationBlurb.LogoFromWikipedia(body)));
            }
            catch (OperationCanceledException)
            {
                return Asked.Failed<Article>("cancelled");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed = $"{ex.GetType().Name}: {ex.Message}";
            }
        }

        return failed is null ? Asked.Answer(default(Article)) : Asked.Failed<Article>(failed);
    }

    /// <summary>Which Wikipedia to read, best first: the reader's own language, then English. This is the
    /// same answer <c>TrackInfoService.Languages</c> gives, for the same reason. English is where the
    /// article usually is. It is a fallback, not a preference.</summary>
    private static IReadOnlyList<string> Languages()
    {
        var own = RPM.Core.Metadata.Providers.WikipediaJson.BaseLanguage(
            System.Globalization.CultureInfo.CurrentUICulture.Name);
        return own is "" or "en" ? ["en"] : [own, "en"];
    }

    /// <summary>What the encyclopedia said about each station name, including the names it said nothing
    /// about. The TASK is kept, so two cards asking together share one request.</summary>
    private static readonly Dictionary<string, Task<Asked<Article>>> Blurbs =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>More than the banner can hold. It is bounded roughly, like every other cache here. Each
    /// entry can be rebuilt from one request, so this needs a ceiling, not a careful eviction
    /// rule.</summary>
    private const int BlurbsKept = 128;

    /// <summary> A station's logo as BYTES. It walks everything the station publishes, exactly as a row
    /// does: its own logos first, then whatever its front page declares.
    ///
    /// <para>This is for the now playing square. The directory's single address for a station is not
    /// always enough. NPO 3FM publishes none at all, so the square asked for nothing and drew the
    /// antenna, while the row beside it showed the logo from the station's own front page. The walk is
    /// the same walk. Only the end of it differs.</para>
    ///
    /// <para>Drawings are skipped. What wants these bytes decodes them with Skia, and Skia does not open
    /// a drawing. The row, which can give one to an SvgImageSource, still uses them.</para>
    ///
    /// <para>The bar is the same one the row walks by, set higher because this square is larger. A mark
    /// that would do for a fifty-point row is a soft badge across a cover pane. The walk takes the
    /// biggest thing the station publishes anywhere. The first one that already covers the square stops
    /// the walk there.</para>
    /// </summary>
    /// <returns>Answered with the station's raster mark, or with null where the station publishes none.
    /// Unanswered where the walk found nothing and one of its candidates could not be fetched.</returns>
    public static async Task<Asked<byte[]?>> LogoBytesAsync(RadioStation station, CancellationToken ct = default)
    {
        // There is one answer per station. The row's walk has one for the same reason: the now playing
        // square, the home banner's card and the header all ask for this, and three walks of one station
        // are three chances to disagree. SettledBytesFor keeps it per uuid, and walks again when the kept
        // walk was incomplete. Do not add a second table here. It would keep the first answer for good,
        // including an incomplete one.
        //
        // The cancellation token belongs to the CALLER, not to the answer. A card that goes away in the
        // middle of a walk leaves the walk running for whoever asked next. That is the cheaper of the two
        // wrong answers.
        _ = ct;
        var settled = await SettledBytesFor(station);

        // A drawing is the one thing this cannot hand over, because nothing on this head rasterizes one
        // outside a XAML image source. The walk prefers a raster mark for exactly that reason (see
        // WalkAsync), so this is rare. When it happens, the square falls back to the antenna, as it does
        // for a station with no picture.
        return settled.Raster is null && !settled.Complete
            ? Asked.Failed<byte[]?>("no mark found, and a candidate could not be fetched")
            : Asked.Answer<byte[]?>(settled.Raster);
    }

    /// <summary>
    /// The settled picture, decoded for one surface, with the ground made under it where it is a mark.
    ///
    /// <para>This is split from the walk, because the walk settles ONE picture and the surfaces draw it
    /// at different sizes: the row's well at 102, and the dial's at the same size. What must not differ
    /// is which picture. That is decided before this is called.</para>
    /// </summary>
    private static async Task<FoundLogo> DressAsync(Settlement settled, int edge)
    {
        if (settled.Bytes is not { Length: > 0 } fetched)
            return default;

        try
        {
            // A drawing is drawn into a PNG first, where this head can do it. It then goes through the
            // same steps as a raster logo below, and a transparent one gets its ground. On the Skia head,
            // or for an SVG that cannot be drawn, this is null. That keeps the drawing path at the end.
            var bytes = settled.IsDrawing
                ? await Task.Run(() => Presentation.Common.SvgRaster.Png(fetched, SquareLogoEdge))
                : fetched;

            if (bytes is not null)
            {
                // Measured from the BYTES, and from the ones that came back, not the ones asked for. An
                // .ico was unwrapped to a frame on the way here, and the frame is the picture. A bitmap
                // does not report its own pixels until the platform has opened it. That is later than the
                // row needs to know what shape it is. See FoundLogo.Fills.
                var (width, height) = Services.Art.ArtScaler.BoundsOf(bytes);

                // A well is a small dark square. A mark with nothing behind it sits in it as a shape
                // floating on the pane, not as a badge. So the shape is read off the fetched bytes on a
                // worker thread. It is a decode, three passes over the pixels and a blur, for a row that
                // may be scrolled past. It is the same question the walk asked when it chose this
                // picture, and it must be the same one. A rounded tile has alpha and is not a mark. A
                // ground made under one would be work nobody ever sees. See TransparentMark.StandsAlone.
                var alone = TransparentMark.StandsAlone(bytes);
                var made = alone
                    ? await Task.Run(() => TransparentMark.Ground(bytes, settled.Url))
                    : null;

                // What decided the ground. A null ink means the palette was used instead of the mark's
                // own colours. The ink is read again only when the diagnostic is on.
                if (LogoDiag)
                {
                    var ink = await Task.Run(() => TransparentMark.Ink(bytes));
                    Walked($"dressed {settled.Url}: {(settled.IsDrawing ? "drawing drawn" : "raster")} " +
                           $"{width}x{height}, mark {alone}, ink {(ink is { } c ? c.ToString() : "none")}, " +
                           $"light {(ink is { } l && TransparentMark.IsLight(l))}, ground {(made is null ? "none" : "made")}");
                }

                // Cut here, rather than at the decoder, and cut on the side the WELL measures.
                //
                // The mark arrives at SquareLogoEdge, and the well is 51 points. So this is a five-fold
                // reduction. Handed to the bitmap decoder, it would read sixteen source pixels for each
                // output pixel, with the rest dropped. On lettering, that leaves gravel down every
                // stroke. See RemoteImage.DecodeMarkAsync.
                //
                // Which side is capped is the same question LogoStretch answers, asked before the decode
                // instead of after it. A logo that fills the well is cropped by it, so it needs its SHORT
                // side to reach the edge. A logo fitted whole needs its long side and nothing more.
                // Capping the short side of a wide lockup that is then fitted would return four times the
                // pixels the row draws. That hands the reduction straight back to the renderer, which is
                // the fault this line exists to avoid.
                var fills = made is null && FoundLogo.MayBeCropped(width, height);
                var side = fills
                    ? Services.Art.ArtScaler.MarkSide.Short
                    : Services.Art.ArtScaler.MarkSide.Long;

                var picture = edge > 0
                    ? await RemoteImage.DecodeMarkAsync(bytes, edge, side)
                    : await RemoteImage.DecodeAsync(bytes);

                if (made is null)
                    return new FoundLogo(picture, null, width, height, IsMark: alone);

                return new FoundLogo(
                    picture,
                    // The ground fills the well and is cropped by it, whatever shape the mark was.
                    await RemoteImage.DecodeMarkAsync(made, edge, Services.Art.ArtScaler.MarkSide.Short),
                    width,
                    height,
                    IsMark: true);
            }

            // A drawing that could not be drawn into a PNG. A bitmap decoder handed one of these loads
            // nothing and reports only that it failed. That is a station whose logo answers 200 and still
            // shows an antenna. It gets no ground, because the ground is made from pixels.
            var stream = Presentation.Common.BitmapBytes.Stream(fetched);
            var drawing = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource();
            if (edge > 0) drawing.RasterizePixelWidth = edge;

            // No bounds. A drawing has no pixels of its own, so it is fitted whole rather than filled.
            return await drawing.SetSourceAsync(stream)
                       == Microsoft.UI.Xaml.Media.Imaging.SvgImageSourceLoadStatus.Success
                ? new FoundLogo(drawing, null, 0, 0, IsMark: true)
                : default;
        }
        catch
        {
            return default;   // the bytes are not a picture that anything here can read
        }
    }

    /// <summary> What a surface draws for a station: the mark, and the made ground under it where the
    /// mark has nothing of its own. Both are null where nothing could be found.
    /// </summary>
    /// <remarks>
    /// <para>The pair travels together, because a picture and a mark are FITTED differently. A picture
    /// fills its well and is rounded by it. A mark is held off the edges, on its ground. Which one this
    /// is cannot be read off an ImageSource. See RadioStationItem.LogoStretch.</para>
    ///
    /// <para>The SHAPE travels with them for the same reason. A decoded bitmap reports its pixels only
    /// once the platform has opened it. That is after the row has already been asked how to draw it. So
    /// the answer is read off the bytes at load time and carried here.</para>
    /// </remarks>
    /// <param name="Width">The picture's own width in pixels. It is 0 where nothing measured it, as for a
    /// drawing or a logo that was never found.</param>
    /// <param name="Height">The same, for its height.</param>
    /// <param name="IsMark">Whether the picture is the station's mark with nothing behind it, rather than
    /// a square with the mark printed on it. It is read off the fetched bytes by
    /// <c>DiscStamp.HasAlpha</c>, which reads the PIXELS and not the header. So a PNG that has an alpha
    /// channel but is opaque in every pixel counts as a picture. It is always true for a drawing, which
    /// has no background it could have. This is what the walk stops for. See <see cref="Worth"/>.</param>
    public readonly record struct FoundLogo(
        Microsoft.UI.Xaml.Media.ImageSource? Picture,
        Microsoft.UI.Xaml.Media.ImageSource? Ground,
        int Width,
        int Height,
        bool IsMark = false)
    {
        /// <summary>
        /// Whether this is a picture that should fill its well edge to edge and be rounded by it.
        ///
        /// <para>Two things have to hold. It must have a background of its own. A mark with nothing
        /// behind it gets the made ground and room to stand on it instead. And it must be near enough
        /// square, because filling CROPS. 538 Non-Stop publishes a wide magenta lockup. Filled into a
        /// square well, it lost both of its ends. A logo shown wrong is worse than a logo shown
        /// small.</para>
        ///
        /// <para>This bar is TIGHTER than <see cref="StationIcons.LooksLikeAMark"/>. That one asks a
        /// different question: "is this a logo at all, or a share card". It lets four-to-three through,
        /// and that cuts a quarter of the width off a wordmark. This one decides what may be cropped
        /// without anyone noticing. Only a picture that is already the well's shape qualifies.</para>
        ///
        /// <para>A drawing measures 0 by 0, so it is fitted whole. It can be rasterized to any size, and
        /// nothing here has read its viewBox. So the answer that never crops is the one to give.</para>
        /// </summary>
        public bool Fills => Ground is null && MayBeCropped(Width, Height);

        /// <summary> Whether a picture of this shape may be filled to a square well without anybody
        /// noticing what the crop took.
        ///
        /// <para>This is asked here, rather than worked out again wherever a logo is shown. The now
        /// playing square asks it too (see ArtDisplay.Cover's Frame). A station whose mark is fitted
        /// whole in the row and cropped in the header would be one station shown two ways.</para>
        ///
        /// <para>Zero on either side means a drawing. A drawing has no shape until something rasterizes
        /// it, so the answer that never crops is the one to give.</para>
        /// </summary>
        public static bool MayBeCropped(int width, int height) =>
            width > 0 && height > 0
            && (double)Math.Max(width, height) / Math.Min(width, height) <= FillsSquare;

        /// <summary>How far from square a picture may be and still be filled to the well. The crop takes
        /// about a twentieth off either end. Anything further than this is fitted whole.</summary>
        private const double FillsSquare = 1.1;
    }

    /// <summary>Every logo the directory had for this station, best guess first. The extras are what <see
    /// cref="RadioResults"/> gathered from the station's other streams.</summary>
    private static IReadOnlyList<string> LogosOf(RadioStation station)
    {
        if (station.OtherLogos.Count == 0)
            return string.IsNullOrWhiteSpace(station.Favicon) ? [] : [station.Favicon];

        var all = new List<string>(station.OtherLogos.Count + 1);
        if (!string.IsNullOrWhiteSpace(station.Favicon))
            all.Add(station.Favicon);
        all.AddRange(station.OtherLogos);
        return all;
    }

    /// <summary>Whether to draw the logo or the fallback tile. It asks <see cref="Logo"/>, so the two can
    /// never disagree.</summary>
    public bool HasLogo => Logo is not null;

    /// <summary>The antenna mark, shown only where there is no logo to show instead. It is here, not
    /// behind a converter, because the phone's template binds it directly.</summary>
    public Microsoft.UI.Xaml.Visibility NoLogoVisibility =>
        HasLogo ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    /// <summary> The logo's URL did not resolve to a picture. The host is gone, the file moved, the
    /// certificate expired, or the host will not serve it to us at all. Directory entries are years old,
    /// and this is common, so it is not an error.
    ///
    /// <para><b>Rare. It is not the walk's normal fall-through.</b> The walk fetches and decodes every
    /// candidate itself. So a picture that reaches the control has already been proved to be one. This
    /// fires only for what is left, which is a decoded bitmap that the renderer then refuses. The
    /// station's settled answer is thrown away and walked again from the top, once.</para>
    ///
    /// <para><b>Once</b> is load-bearing. The walk is shared and cached by uuid, so without the guard, a
    /// picture that fails for every row showing it would clear and re-run the same walk for each row, on
    /// every repaint, forever. <see cref="_reWalked"/> is what stops that. It is per ROW, not per
    /// station, on purpose. A second row of the same station that asks again costs one walk, which the
    /// cache then serves to both.</para>
    /// </summary>
    public void NoteLogoFailed()
    {
        if (_found.Picture is null || _reWalked)
            return;

        _reWalked = true;

        // The ground goes down with the mark. It was made for that picture. A ground with no mark on it
        // is just a coloured square.
        _found = default;
        RaiseLogo();

        // Both tables. The row's decode is kept in Rows, on top of the walk in Settled. Both must be
        // dropped here. If only the walk were dropped, FindLogoAsync would be served the same failed
        // picture from Rows, and the re-walk would never run.
        Settled.Remove(Station.Uuid);
        Rows.Remove(Station.Uuid);
        _ = FindLogoAsync();
    }

    /// <summary>Whether this row has already thrown away the station's settled answer once. See
    /// <see cref="NoteLogoFailed"/>.</summary>
    private bool _reWalked;

    /// <summary>
    /// Drops the picture this row is showing, and lets it ask again.
    ///
    /// <para>This is for when a logo has been changed on the server. The row latches its first read of
    /// <see cref="Logo"/> and never asks twice. So clearing the tables behind it is not enough. A row
    /// already on screen would keep drawing what it had until it was recycled.</para>
    ///
    /// <para><see cref="_reWalked"/> is reset too. That latch stops one row re-walking forever on a
    /// picture that will not decode. This is a different picture.</para>
    /// </summary>
    public void ForgetLogo()
    {
        _found = default;
        _logoAsked = false;
        _reWalked = false;
        RaiseLogo();
    }

    /// <summary>
    /// Throws away every station's settled walk and decoded row picture.
    ///
    /// <para>This is for when the server says a logo has changed. Both tables are keyed by STATION, not
    /// by address. So a new url from <see cref="RadioLogoApi.For"/> is never reached: a row asking for
    /// its logo is served <c>Settled[uuid]</c> first. The caller follows this with <see
    /// cref="ForgetLogo"/> on the rows it has, because those have already latched.</para>
    /// </summary>
    public static void ForgetEveryLogo()
    {
        Settled.Clear();
        Rows.Clear();
    }

    /// <summary> A station's logo without a row to hang it on. This is for the dial, which shows the
    /// station playing whether or not it is one of the ones listed.
    ///
    /// <para>It gets the same answer a row gets, because it is literally the same one (see <see
    /// cref="SettledFor"/>). A separate walk with its own rule could show two different pictures of one
    /// station. The row stops at the first candidate that is big enough. A walk that stopped at the first
    /// candidate that decodes would pick a different picture for a station whose first logo is a 32-pixel
    /// favicon.</para>
    /// </summary>
    public static Task<FoundLogo> LogoForAsync(RadioStation station) => SettledFor(station);

    /// <summary> The same station's picture, decoded at <paramref name="edge"/> pixels, for a surface
    /// drawn larger than a row's well: Pegasus's station cards and its tuner's player.
    ///
    /// <para>It uses the same walk and the same winner as the row (see <see cref="SettledBytesFor"/>). So
    /// a card and a row cannot show two different pictures of one station. Only the decode differs. It is
    /// not kept. A card that asked holds what it was given, and a card built again decodes again.</para>
    /// </summary>
    public static async Task<FoundLogo> LogoForAsync(RadioStation station, int edge)
    {
        var settled = await SettledBytesFor(station);
        return settled.Bytes is { Length: > 0 } ? await DressAsync(settled, edge) : default;
    }

    /// <summary> The same, from the URL alone. This is for places that have the station's logo but not
    /// the station: the player's cover tile and the mini player, which are handed what the audio source
    /// says it is playing and nothing more.
    /// </summary>
    /// <remarks>
    /// <para><b>This is still the URL given straight to a BitmapImage, and it still has the fault
    /// described above.</b> The control fetches with no user agent on the request. Hosts that refuse
    /// those, such as alive.radio, which answers 403, give a blank tile. The phone's two callers
    /// (MiniPlayer and NowPlayingArt) assign the result synchronously. So putting them right means making
    /// both of them await, and the mobile head does not build on this machine. It is left as it is,
    /// because it cannot be checked here. Use <see cref="LogoForAsync"/> for anything new.</para>
    /// </remarks>
    /// <param name="decodePixelWidth">The width it is drawn at. Zero decodes the logo as published. That
    /// is what a full-width cover tile wants. A station's logo is small to begin with, and forcing it
    /// wider than it is only costs memory.</param>
    public static Microsoft.UI.Xaml.Media.ImageSource? LogoFrom(string? favicon, int decodePixelWidth = 68) =>
        LoadLogo(favicon, decodePixelWidth);

    /// <summary> A station logo is a URL that somebody typed into a directory years ago, so nearly
    /// everything about it is suspect. What is worth trying at all is <see cref="RadioLogo.IsDrawable"/>.
    /// It is shared, because the list has to ask the same question a step earlier, when it folds a
    /// station's several entries into one. Whatever survives is decoded small. A station serving a 2000
    /// px banner should not cost more than the 34 px well it lands in.
    /// </summary>
    private static Microsoft.UI.Xaml.Media.ImageSource? LoadLogo(string? favicon, int decodePixelWidth = 68)
    {
        if (!RadioLogo.IsDrawable(favicon) || !Uri.TryCreate(favicon, UriKind.Absolute, out var uri))
            return null;

        // A drawing, not a picture. A BitmapImage given one of these loads nothing and reports only that
        // it failed. That is a station whose logo answers 200 and still shows an antenna.
        if (RadioLogo.IsVector(favicon))
        {
            var drawing = new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(uri);
            if (decodePixelWidth > 0) drawing.RasterizePixelWidth = decodePixelWidth;
            return drawing;
        }

        var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
        if (decodePixelWidth > 0) image.DecodePixelWidth = decodePixelWidth;
        return image;
    }

    public bool IsFavorite
    {
        get => _isFavorite;
        set { if (_isFavorite != value) { _isFavorite = value; Raise(); Raise(nameof(FavoriteBrush)); } }
    }

    /// <summary>The heart, lit when the station is a preset. The bundled Font Awesome is Solid only, with
    /// no hollow heart in it. So "saved" is shown by colour, in the app's own cyan.</summary>
    public Microsoft.UI.Xaml.Media.Brush FavoriteBrush => _isFavorite ? Saved : Unsaved;

    private static readonly Microsoft.UI.Xaml.Media.Brush Saved =
        new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF));

    private static readonly Microsoft.UI.Xaml.Media.Brush Unsaved =
        new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));

    public bool IsTuned
    {
        get => _isTuned;
        set
        {
            if (_isTuned == value) return;
            _isTuned = value;
            Raise();
            Raise(nameof(PlayGlyph));
            Raise(nameof(PlayName));
            Raise(nameof(PlayingVisibility));
        }
    }

    /// <summary>The mark in the gutter beside the station that is playing. It is the same 3-point white
    /// bar that a playing track wears in the library. On the phone the row IS the button, so this is the
    /// only thing that says which one is on. The desktop keeps a key and uses <see cref="PlayGlyph"/>
    /// instead.</summary>
    public Microsoft.UI.Xaml.Visibility PlayingVisibility =>
        _isTuned ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>The star. It is the same glyph whether saved or not. The bundled Font Awesome is Solid
    /// only, so the hollow star does not exist in it. "Not saved" is shown by colour instead.</summary>
    public const string FavoriteGlyph = "\uf005";

    /// <summary>Stop square on the station that is playing, play triangle on the rest. It is one button
    /// doing both jobs, the way the transport works everywhere else in the app.</summary>
    public string PlayGlyph => _isTuned ? "\uf04d" : "\uf04b";

    /// <summary>The same key's accessible name, which says what the glyph shows.</summary>
    public string PlayName => RPM.Desktop.Strings.Loc.Get(_isTuned ? "Radio_StopStation" : "Radio_PlayStation");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary> One entry of the genre and country boxes: a facet of the directory, or the "anything" row
/// that stands for not filtering by it at all.
///
/// <para>This is a type of the pane's own, not the directory's <see cref="RadioFacet"/> taken straight
/// off the wire. The directory client is a plain library with no XAML in it, so nothing there can carry
/// the binding engine's metadata attribute. Eighty rows of it would have every label read by reflection.
/// Mapping once on arrival costs a hundred small objects, and it buys the generated path.</para>
/// </summary>
[RpmBindable]
public sealed partial class RadioFacetItem
{
    private RadioFacetItem(string name, string code)
    {
        Name = name;
        Code = code;
    }

    public RadioFacetItem(RadioFacet facet) : this(facet.Name, facet.Code) { }

    /// <summary>The row that narrows by nothing. Its empty <see cref="Code"/> is what the query reads as
    /// "do not filter by this". So choosing it is a stated answer, not a missing one.</summary>
    public static RadioFacetItem Any(string label) => new(label, "");

    /// <summary>What the box shows.</summary>
    public string Name { get; }

    /// <summary>What the query sends: the tag itself, or a country's ISO code. It is empty on <see
    /// cref="Any"/>.</summary>
    public string Code { get; }
}

/// <summary> The Internet Radio pane's model: what the directory was asked for, what it answered, which
/// stations are presets, and which one is playing.
///
/// <para>Searching is deliberately lazy. The pane opens on the presets, which need no network at all. It
/// reaches the directory only once, for the most-played stations, when it is first shown. Typing
/// re-queries after a pause, not on every keystroke. The directory is run by volunteers, and a search on
/// every keystroke would be a small denial of service.</para>
/// </summary>
[RpmBindable]
public sealed partial class RadioModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>How long the typing has to stop before the directory is asked.</summary>
    private static readonly TimeSpan TypingPause = TimeSpan.FromMilliseconds(450);

    /// <summary>
    /// How long the directory's last answer stands.
    ///
    /// <para>A station list is not news. It lists which volunteer-run streams existed half an hour ago.
    /// Asking again on every visit to the pane costs the directory a request, costs the user a wait, and
    /// loses their scroll position for an answer that is the same. So a revisit shows what was listed.
    /// The directory is only asked again once this time has passed, or when the last attempt failed. A
    /// failed attempt is the one case where asking again is the point.</para>
    /// </summary>
    private static readonly TimeSpan ResultsGoodFor = TimeSpan.FromMinutes(30);

    // The tuner is not one particular class. The desktop's decodes the ICY socket itself, through the
    // audio engine. The phone's hands the URL to AVPlayer. Everything in this class is the same either
    // way. That is what lets this file be the model on both.
    private readonly IRadioTuner _radio;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _dispatcher;

    private CancellationTokenSource? _query;
    private Timer? _typing;
    private bool _loaded;

    /// <summary>When the directory last answered, on the monotonic clock. Zero until it has.</summary>
    private long _answeredAt;

    /// <summary>What the dial's badge wears for the station now tuned. See <c>CurrentBadge</c>, which is
    /// the only thing that reads or clears it.</summary>
    private RadioStationItem.FoundLogo _badge;
    private string? _logoFor;

    private string _searchText = "";
    private RadioFacetItem? _country;
    private bool _favoritesOnly;
    private StationSieve _sieve = StationSieve.Off;
    private StationOrdering _order = StationOrdering.Listed;

    /// <summary>What was last listed, before the filter had anything to say about it. It is kept because
    /// the filter is answered here, not by the directory. Narrowing is then a re-read of a list already
    /// in hand. Widening again puts the stations back without a second search.</summary>
    private IReadOnlyList<RadioStation> _listed = [];

    /// <summary>Why THAT list is empty, and where it is. Examples: "the directory found nothing", or
    /// "nothing is saved". It is held here, not written straight to <see cref="EmptyNote"/>, because the
    /// filter can empty a listing that is not empty. The two say different things.</summary>
    private string? _listedNote;
    private bool _isBusy;
    private string? _error;
    private string? _emptyNote;

    public RadioModel(IRadioTuner radio, Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
    {
        _radio = radio;
        _dispatcher = dispatcher;
        _radio.Changed += OnRadioChanged;
        _radio.Favorites.Changed += OnFavoritesChanged;
        RadioState.Changed += OnStateChanged;
    }

    /// <summary>The server's answer about hidden stations and changed logos has arrived. It usually
    /// arrives after the pane is already up, so the list is drawn again rather than waited for.</summary>
    private void OnStateChanged() => Post(() =>
    {
        // The walk and the decode are kept PER STATION, not per address.
        //
        // A moved stamp is not enough on its own. RadioLogoApi.For hands out a new url. That is a new key
        // in Logos and a new request. But a row asking for its logo is served Settled[uuid] and
        // Rows[uuid] first, and never gets that far. So both are dropped here. That is the same pair that
        // NoteLogoFailed drops for one station, for the same reason.
        if (RadioState.LogoStamp != _drawnFor)
        {
            _drawnFor = RadioState.LogoStamp;
            RadioStationItem.ForgetEveryLogo();
            foreach (var item in Stations)
                item.ForgetLogo();
        }

        // A genre listing asks the directory again when the edits now file a different set of stations
        // under that genre. Those are added to the directory's answer at search time (see Search). Show
        // only re-reads what was found. Everything else is drawn again from what is listed. That picks up
        // an edited title or tags through Display and ShownTags.
        if (!_favoritesOnly && _sieve.Genre is { } genre && RetaggedKey(genre) != _searchedRetagged)
        {
            Search();
            return;
        }

        Show();
    });

    /// <summary>The stations the edits file under a genre, as one string to compare.</summary>
    private static string RetaggedKey(string genre) => string.Join(',', StationEdits.Now.RetaggedInto(genre));

    /// <summary>What <see cref="RetaggedKey"/> said when the listing on screen was asked for. It is set
    /// on the drawing thread by <see cref="Search"/>, and read there by <see
    /// cref="OnStateChanged"/>.</summary>
    private string _searchedRetagged = "";

    /// <summary>The logo stamp the rows on screen were drawn against. A state answer that says the same
    /// thing does not throw away every picture in the pane.</summary>
    private string _drawnFor = "";

    /// <summary>The rows on screen: search results, or the presets when those are what is shown.</summary>
    /// <remarks>It has its own collection type, so a {Binding} can hand it to the list under Native AOT.
    /// See <see cref="Common.RadioStationList"/>.</remarks>
    public Common.RadioStationList Stations { get; } = [];

    public ObservableCollection<RadioFacetItem> Countries { get; } = [];

    /// <summary>The station list the dial should be able to reach. It is always exactly what is
    /// listed.</summary>
    public IReadOnlyList<RadioStation> DialStations => [.. Stations.Select(s => s.Station)];

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value ?? "";
            Raise();
            RestartTypingTimer();
        }
    }

    public RadioFacetItem? SelectedCountry
    {
        get => _country;
        set
        {
            if (ReferenceEquals(_country, value)) return;
            _country = value;
            Raise();
            Raise(nameof(CountryLabel));
            Search();
        }
    }

    /// <summary>What the country button reads: the country in force, or the words for none of them. The
    /// button carries no list of its own. It drops one, so this is the whole of what it shows.</summary>
    public string CountryLabel =>
        _country is { Code.Length: > 0 } chosen ? chosen.Name : Loc.Get("Radio_AllCountries");

    /// <summary>The star switch: shows the saved stations instead of the directory. It works offline,
    /// which is the point of keeping the whole station in the preset.</summary>
    public bool FavoritesOnly
    {
        get => _favoritesOnly;
        set
        {
            if (_favoritesOnly == value) return;
            _favoritesOnly = value;
            Raise();
            if (value) ShowFavorites();
            else Search();
        }
    }

    public string FavoritesGlyph => "\uf005";

    /// <summary>
    /// The saved stations this list shows with the star switch and marks with the heart, where they are not
    /// the listener's own: a shared collection's owner's, read only (Pegasus's tuner hands it
    /// RadioService.Saved). Null, or an answer that is the listener's own, lists and marks the listener's
    /// own as before.
    /// </summary>
    public Func<Services.Audio.SavedStations>? Saved { get; set; }

    /// <summary>Somebody else's saved stations, while <see cref="Saved"/> gives them; null for the
    /// listener's own.</summary>
    private Services.Audio.SavedStations? Borrowed => Saved?.Invoke() is { Mine: false } theirs ? theirs : null;

    /// <summary>Whether the heart may be pressed: false while the saved stations are somebody else's.</summary>
    public bool CanSave => Borrowed is null;

    private IReadOnlyList<RadioStation> SavedList => Borrowed?.Stations ?? _radio.Favorites.Stations;

    private bool IsSaved(string uuid) => Borrowed is { } theirs ? theirs.Contains(uuid) : _radio.Favorites.Contains(uuid);

    /// <summary>The saved stations that <see cref="Saved"/> gives have changed (a shared collection's
    /// owner's were read): the hearts and the star's list are read again.</summary>
    public void SavedChanged() => OnFavoritesChanged();

    /// <summary> What the dial has been narrowed to: a format, a genre, and whether the station
    /// advertises. It is one value, set whole. See <see cref="StationSieve"/>, which is where the
    /// questions are asked.
    ///
    /// <para><b>The genre asks the directory. The other two do not.</b> Format and advertising are
    /// questions about stations that have been listed. A station already on screen does not stop existing
    /// because the network has gone. A genre is different. The page in hand holds the most-listened-to
    /// stations, and it has no metal stations at all. So a Metal filter that only narrowed what was
    /// listed came back empty, however the tags were read. Picking a genre runs the search again with
    /// that genre's tag on it (see <see cref="RadioGenres.Term"/>). What comes back is still read through
    /// the sieve. That keeps out the reggaeton stations that "reggae" drags in off the Reggae
    /// page.</para>
    ///
    /// <para>The presets are not affected. They are a local list, and they stay one. A listener with
    /// twelve saved stations is narrowing those twelve. Going to the network for more would answer a
    /// different question.</para>
    /// </summary>
    public StationSieve Sieve
    {
        get => _sieve;
        set
        {
            value ??= StationSieve.Off;
            if (_sieve == value) return;

            var genre = !string.Equals(_sieve.Genre, value.Genre, StringComparison.Ordinal);
            _sieve = value;
            Raise();
            Raise(nameof(FiltersOn));

            if (genre && !_favoritesOnly) Search();
            else Show();
        }
    }

    /// <summary>Whether the filter is narrowing anything. The pane's button shows its mark for
    /// this.</summary>
    public bool FiltersOn => _sieve.IsOn;

    /// <summary>
    /// What order the stations are listed in. See <see cref="StationOrdering"/>.
    ///
    /// <para>It sits beside the filter, not as part of it. One says which stations, and the other says in
    /// what order. A listener changing one is not changing the other. Both are answered here, over the
    /// listing in hand, so neither costs the directory a request.</para>
    /// </summary>
    public StationOrdering Order
    {
        get => _order;
        set
        {
            if (_order == value) return;
            _order = value;
            Raise();
            Raise(nameof(OrderChosen));
            Show();
        }
    }

    /// <summary>Whether the list is in an order somebody asked for, rather than the one the directory
    /// answered in. The pane's sort button shows its mark for this.</summary>
    public bool OrderChosen => _order != StationOrdering.Listed;

    public bool IsBusy
    {
        get => _isBusy;
        private set { if (_isBusy != value) { _isBusy = value; Raise(); } }
    }

    /// <summary> Something the user should be told about, such as the directory being unreachable so far.
    /// This is NOT the empty-list explanation (see <see cref="EmptyNote"/>). It stands even with a full
    /// list under it, which is why the pane shows it in its own band, not over the rows.
    /// </summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (_error == value) return;
            _error = value;
            Raise();
            Raise(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_error);

    /// <summary>The user closed the band. The message has been read, so it is dropped. What made it, a
    /// stale or never-answered directory, is remembered by <c>_answeredAt</c>, so a later showing of the
    /// pane still retries.</summary>
    public void Dismiss() => Error = null;

    /// <summary>Why the list is empty, when it is empty for a reason worth saying. It is only set when
    /// nothing is listed, so it has nothing to overlap.</summary>
    public string? EmptyNote
    {
        get => _emptyNote;
        private set
        {
            if (_emptyNote == value) return;
            _emptyNote = value;
            Raise();
            Raise(nameof(HasEmptyNote));
        }
    }

    public bool HasEmptyNote => !string.IsNullOrEmpty(_emptyNote);

    /// <summary>The station playing, or null.</summary>
    public RadioStation? Current => _radio.Current;

    /// <summary> The tuned station's mark for the dial's badge. See <see cref="CurrentBadge"/>, which is
    /// where it is asked for and cached.
    ///
    /// <para>It is filled by an errand, like a row's (see <see cref="RadioStationItem.Logo"/>), for the
    /// same reason. The picture is fetched with the app's name on the request. A good number of hosts
    /// turn away a request with no user agent. That is what left a station's badge blank, while the
    /// header above it showed the very same logo.</para>
    /// </summary>
    public Microsoft.UI.Xaml.Media.ImageSource? CurrentLogo => CurrentBadge.Picture;

    /// <summary>What the badge's mark stands on, where it is a mark with nothing of its own behind it. It
    /// is the same made figure the rows use for the same mark, because it is the same <c>FoundLogo</c>.
    /// So the badge on the glass and the well two inches below it are one badge. It is null for a logo
    /// that is already a solid picture, and null while the walk is still running.</summary>
    public Microsoft.UI.Xaml.Media.ImageSource? CurrentLogoGround => CurrentBadge.Ground;

    /// <summary>Whether the badge's logo is a square picture with its own background. Such a picture
    /// should fill the badge and be rounded by it. It is not a mark that needs room, and not a wide
    /// lockup that filling would crop. See RadioStationItem.LogoStretch, which applies the same rule to a
    /// row.</summary>
    public bool CurrentLogoFills => CurrentBadge.Fills;

    /// <summary>
    /// The whole of what the badge wears, and the one place the station is checked.
    ///
    /// <para><b>All three public reads come through here, so none of them depends on the order they are
    /// read in.</b> If the check sat in <see cref="CurrentLogo"/> alone, the dial would be correct only
    /// when its caller read the picture first. Read the ground first, and it would be the LAST station's
    /// ground, under the mark of the one now tuned.</para>
    ///
    /// <para>It is fetched on read, not pushed. So the badge asks whenever it needs to know, and nothing
    /// has to remember to tell it. It is cached against the station it was built for. The dial re-reads
    /// this every time the station announces another track. Fetching afresh each time would re-download
    /// the logo once a song.</para>
    /// </summary>
    private RadioStationItem.FoundLogo CurrentBadge
    {
        get
        {
            var current = _radio.Current;
            if (!string.Equals(current?.Uuid, _logoFor, StringComparison.Ordinal))
            {
                _logoFor = current?.Uuid;
                _badge = default;
                if (current is not null)
                    _ = FetchCurrentLogoAsync(current);
            }
            return _badge;
        }
    }

    /// <summary>
    /// The badge's picture, once it has been fetched.
    ///
    /// <para>It is dropped when the dial has moved on while the fetch was in the air. The station's uuid
    /// is the test, so an answer about the last station cannot land on the badge of the one now
    /// tuned.</para>
    ///
    /// <para>The mark, the ground and the shape land as ONE record. So the badge can never be seen with a
    /// new station's mark on the last one's ground.</para>
    /// </summary>
    private async Task FetchCurrentLogoAsync(RadioStation station)
    {
        var found = await RadioStationItem.LogoForAsync(station);

        if (!string.Equals(station.Uuid, _logoFor, StringComparison.Ordinal))
            return;

        _badge = found;
        Raise(nameof(CurrentLogo));
        Raise(nameof(CurrentLogoGround));
        Raise(nameof(CurrentLogoFills));
    }

    /// <summary>The dial's second line: the live track title the station is announcing. When it is not
    /// playing one, it shows what the connection is doing.</summary>
    public string StatusText
    {
        get
        {
            if (_radio.Current is null) return Loc.Get("Radio_NothingTuned");
            if (_radio.IsReconnecting) return Loc.Get("Radio_Reconnecting");

            // The station said so itself. Printing its marker as if it were a song (for example "Reclame"
            // by nobody, for four minutes) makes the line look broken. Saying what it is gives the same
            // fact in words the listener asked for. The whole announcement has to BE the marker, so a
            // record whose title merely mentions one still prints as the record. See RadioAds.
            if (RadioAds.IsAdBreak(_radio.NowPlayingTitle))
                return Loc.Get("Radio_AdBreak");

            // Nothing was announced. That is not the same as still connecting. It is not rare either: a
            // break's tail runs for a minute on some stations, and a station that sends no metadata at
            // all stays in this state for as long as it plays. The line says the one thing that is true
            // of all of them.
            return _radio.NowPlayingTitle ?? Loc.Get("Radio_NoTrack");
        }
    }

    public bool IsReconnecting => _radio.IsReconnecting;

    /// <summary>Raised when the tuned station or its readout changes, so the pane can move the needle
    /// without re-reading the whole model.</summary>
    public event Action? TunedChanged;

    /// <summary>
    /// What a showing of the pane asks for.
    ///
    /// <para>The first time, the presets are already in hand. They go up at once, and the directory's
    /// most-played list is fetched behind them.</para>
    ///
    /// <para>Every time after that, as little as possible. The pane is looked at, left and looked at
    /// again dozens of times in a session. What is listed (the query, the filters, the results and the
    /// place in them) is still true. So a revisit fetches only what is actually missing. The filter lists
    /// are fetched if they never arrived, and the stations are fetched if the last answer has gone stale
    /// or never came at all. See <see cref="ResultsGoodFor"/>.</para>
    /// </summary>
    public void EnsureLoaded()
    {
        // Asked at most once an hour, and nothing waits on it. What it answers (the hidden stations, and
        // whether any logo changed) is read off the last answer wherever it is needed. So a pane that
        // opens before it arrives behaves the same as one that opens after.
        RadioState.Refresh();

        if (!_loaded)
        {
            _loaded = true;
            ShowFavorites();
            LoadCountries();
            Search();
            return;
        }

        // Only the "any" entry in it, or nothing at all: the directory never answered for the list.
        if (Countries.Count <= 1) LoadCountries();

        // The presets need no network, and they are already correct. The directory's answer is asked for
        // again only if it is old, or if what is on screen is the reason it failed.
        if (!_favoritesOnly && (HasError || IsStale)) Search();
    }

    /// <summary>Whether the listed stations are old enough to be worth asking about again.</summary>
    private bool IsStale =>
        _answeredAt == 0 || Environment.TickCount64 - _answeredAt > (long)ResultsGoodFor.TotalMilliseconds;

    /// <summary>Runs the current query against the directory and replaces whatever is listed. A query
    /// already in flight is dropped. The last thing asked for is the only answer anyone wants.</summary>
    public void Search()
    {
        if (_favoritesOnly)
        {
            ShowFavorites();
            return;
        }

        _query?.Cancel();
        _query?.Dispose();
        var cts = new CancellationTokenSource();
        _query = cts;

        IsBusy = true;
        Error = null;
        EmptyNote = null;

        var query = new RadioQuery
        {
            Name = string.IsNullOrWhiteSpace(_searchText) ? null : _searchText,
            CountryCode = _country?.Code,

            // The genre goes to the directory, so that a genre nothing on the page happens to show comes
            // back as a page of its own. It is asked for as a tag that the directory's tags HOLD, not as
            // a whole tag. Otherwise the spellings would be the listener's problem.
            Tag = RadioGenres.Term(_sieve.Genre) is { } term ? StationTag.Holding(term) : null,
        };

        // The stations the admin filed under this genre. The directory is asked by its own tags and never
        // returns these, so they are fetched by uuid and added (RadioResults.WithRetagged). They are read
        // here, on the drawing thread, and remembered. So a state answer that changes the set asks again
        // (OnStateChanged), and one that does not only redraws.
        IReadOnlyList<string> retagged = _sieve.Genre is { } genre ? StationEdits.Now.RetaggedInto(genre) : [];
        _searchedRetagged = string.Join(',', retagged);

        _ = Task.Run(async () =>
        {
            try
            {
                var stations = await _radio.Directory.SearchAsync(query, cts.Token);
                if (retagged.Count > 0)
                    stations = RadioResults.WithRetagged(stations, await RetaggedAsync(retagged, cts.Token), query);

                Post(() =>
                {
                    if (cts.IsCancellationRequested) return;

                    // One row per station, not one per stream (see RadioResults). Only the directory's
                    // answers are used here. The presets below go up exactly as they were saved, because
                    // a preset is a stream somebody chose. Picking a different one for them would
                    // silently retune a favourite.
                    Replace(RadioResults.OnePerStation(stations), Loc.Get("Radio_NoResults"));
                    IsBusy = false;

                    // Answered. This is what a later showing of the pane reads to decide it has nothing
                    // to ask for (see EnsureLoaded).
                    _answeredAt = Environment.TickCount64;
                });
            }
            catch (OperationCanceledException) { /* superseded */ }
            catch (Exception)
            {
                Post(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    IsBusy = false;
                    Error = Loc.Get("Radio_DirectoryDown");
                });
            }
        });
    }

    /// <summary>The retagged stations from the directory, or none where it did not answer. The genre's
    /// own answer is still worth showing without them.</summary>
    private async Task<IReadOnlyList<RadioStation>> RetaggedAsync(IReadOnlyList<string> uuids, CancellationToken ct)
    {
        try
        {
            return await _radio.Directory.ByUuidAsync(uuids, ct);
        }
        catch (RadioDirectoryUnavailableException)
        {
            return [];
        }
    }

    /// <summary>Lists the presets. No network is needed, so there is no waiting.</summary>
    private void ShowFavorites()
    {
        _query?.Cancel();
        IsBusy = false;
        var saved = SavedList;
        Replace(saved, _favoritesOnly ? Loc.Get("Radio_NoFavorites") : null);
    }

    /// <summary>The country list, with an "all countries" entry first. Clearing the filter is then a
    /// selection, not a right-click on a combo box.</summary>
    private void LoadCountries() => _ = Task.Run(async () =>
    {
        try
        {
            var countries = await _radio.Directory.CountriesAsync();
            Post(() =>
            {
                Countries.Clear();
                Countries.Add(RadioFacetItem.Any(Loc.Get("Radio_AllCountries")));

                // Which countries, under which names, in which order: see RadioCountries.ForPicker. The
                // phone builds its list with a port of the same function. The listener's own country
                // leads (AppCountry).
                var home = App.Services.GetService<Services.Shell.SettingsService>() is { } settings
                    ? Services.Shell.AppCountry.Code(settings)
                    : null;
                foreach (var country in RadioCountries.ForPicker(countries, home))
                    Countries.Add(new RadioFacetItem(country));
            });
        }
        catch { /* the filter simply stays empty; searching by name still works */ }
    });

    /// <summary>
    /// Takes a new listing, with the reason it would be worth explaining if it turned out to be empty.
    ///
    /// <para>The note is asked for, not defaulted. "The directory found nothing" and "you have saved
    /// nothing" are different sentences. Only the caller knows which listing this is. A listing that
    /// arrived with no reason at all would go up under whatever the last one left behind.</para>
    /// </summary>
    private void Replace(IReadOnlyList<RadioStation> stations, string? whenEmpty)
    {
        _listed = stations;
        _listedNote = whenEmpty;
        Show();
    }

    /// <summary>
    /// Puts the listing on screen, less whatever the filter takes out.
    ///
    /// <para>Every route to the rows goes through here: a directory answer, the presets, and a change to
    /// the filter itself. So what is listed, what the dial can reach, and what the heading counts are one
    /// answer, not three that have to be kept in step.</para>
    /// </summary>
    private void Show()
    {
        // Stations taken out of the app are removed first, before anything else looks at the list.
        //
        // This is done here, not where the directory answers. This is the one place every route to the
        // rows passes through: a search, the presets, the saved stations, and a change to the filter.
        // Done at the directory, a hidden station would still be reachable through somebody's favourites.
        //
        // It is asked of the ROW, not of its Uuid. By the time a listing reaches here, duplicate rows for
        // one station have been folded into one, and the fold kept a single uuid. Reading Uuid alone
        // would miss every station hidden under one of the others.
        //
        // Nothing is stopped mid-song. A station hidden while it is playing leaves the list and keeps
        // playing, which is better than cutting somebody off.
        var listed = RadioState.Hidden.Count == 0
            ? _listed
            : _listed.Where(s => !RadioState.IsHidden(s)).ToList();

        // Straplines go back on wherever two stations would otherwise print the same. This is a fact
        // about the LIST, so it is settled here, once, not by each row asking about itself. It is about
        // the list AS SHOWN. Two stations that print alike are not worth telling apart if the filter has
        // taken one of them out. See RadioNames.Distinguish. It runs last: narrowed, then ordered, then
        // told apart. The straplines are a fact about the list as shown, so they are put back last, over
        // the stations that survived the filter, in the order they will be read in.
        var kept = RadioNames.Distinguish(StationSort.By(_order, _sieve.Through(listed)));

        Stations.Clear();
        foreach (var station in kept)
        {
            Stations.Add(new RadioStationItem(station)
            {
                IsFavorite = IsSaved(station.Uuid),
                IsTuned = _radio.IsTuned(station.Uuid),
            });
        }

        // Nothing listed is the caller's sentence. Something listed but nothing shown is the filter's.
        // "No stations found" there would send somebody searching again for stations they already have in
        // front of them.
        EmptyNote = _listed.Count == 0 ? _listedNote
                  : kept.Count == 0 ? Loc.Get("Radio_NoFilterMatch")
                  : null;

        Raise(nameof(DialStations));
        Raise(nameof(Subheading));
        TunedChanged?.Invoke();
    }

    /// <summary>How many stations are on the dial. This is the line under the pane's heading. It counts
    /// what is LISTED, not what the directory holds. The list is a search, and a count of everything on
    /// the internet would say nothing about the stations in front of the user.</summary>
    public string Subheading => Presentation.Common.PaneStats.Stations(Stations.Count);

    /// <summary>The row's play button. It tunes the station in, or switches it off when it is already the
    /// one on.</summary>
    public void Toggle(RadioStationItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (_radio.IsTuned(item.Uuid)) _radio.Stop();
        else _radio.Tune(item.Station);
    }

    public void Tune(RadioStation station)
    {
        ArgumentNullException.ThrowIfNull(station);
        _radio.Tune(station);
    }

    /// <summary>The row's star. A station is saved whole, so it keeps playing after the directory has
    /// forgotten it.</summary>
    public void ToggleFavorite(RadioStationItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!CanSave)
            return;   // somebody else's saved stations: read only
        item.IsFavorite = _radio.Favorites.Toggle(item.Station);
    }

    private void RestartTypingTimer()
    {
        _typing?.Dispose();
        _typing = new Timer(_ => Post(Search), null, TypingPause, Timeout.InfiniteTimeSpan);
    }

    private void OnRadioChanged() => Post(() =>
    {
        foreach (var item in Stations)
            item.IsTuned = _radio.IsTuned(item.Uuid);
        Raise(nameof(Current));
        Raise(nameof(CurrentLogo));
        Raise(nameof(CurrentLogoGround));
        Raise(nameof(CurrentLogoFills));
        Raise(nameof(StatusText));
        Raise(nameof(IsReconnecting));
        TunedChanged?.Invoke();
    });

    private void OnFavoritesChanged() => Post(() =>
    {
        foreach (var item in Stations)
            item.IsFavorite = IsSaved(item.Uuid);
        if (_favoritesOnly)
            ShowFavorites();
    });

    /// <summary>Everything here is bound, and the radio's events arrive off the decode thread.</summary>
    private void Post(Action action) => _dispatcher.TryEnqueue(() => action());

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _radio.Changed -= OnRadioChanged;
        _radio.Favorites.Changed -= OnFavoritesChanged;
        RadioState.Changed -= OnStateChanged;
        _typing?.Dispose();
        _query?.Cancel();
        _query?.Dispose();
    }
}
