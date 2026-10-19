using Microsoft.UI.Xaml.Media.Imaging;

using SkiaSharp;

using RPM.Core.Net;

namespace RPM.Desktop.Presentation.Common;

/// <summary>The app's one way of turning a picture's address into bytes, and bytes into a bitmap.
///
/// <para>It does not use <c>new BitmapImage(uri)</c>. Every surface that shows a fetched picture needs
/// the raw bytes first. The artwork band crops and blurs them, and the panes cut them down to the size
/// they are drawn at (see <see cref="Services.Art.ArtScaler"/>). A BitmapImage that fetched its own URL
/// has already discarded the bytes. It also cannot be cancelled. A photo requested for a track that the
/// user has since left would keep downloading.</para>
///
/// <para>One HttpClient, so every picture in the app shares one connection pool and one timeout.</para>
/// </summary>
/// <summary>Whether the body of a page fetch is worth keeping on this disk.
///
/// <para>The caller states this at every call. It is never defaulted, because a wrong answer is
/// invisible and expensive. A Wikipedia summary is a few hundred bytes, and it is worth keeping for a
/// month. A radio station's front page averages 177 KB and can reach 5.4 MB, and the walk needs only a
/// handful of addresses from it. Kept, these pages filled a 256 MB store with 431 rows and 74.4 MB. The
/// store then evicted its entries within twelve hours. That pushed out album artwork, the station logos
/// the walk had just found, and each other.</para>
/// </summary>
public enum PageKeep
{
    /// <summary>File the body under the ordinary thirty days. For a small answer asked for again.</summary>
    OnDisk,

    /// <summary>Read it, use it, and let it go. This is for a page read only to reach a small
    /// conclusion. The conclusion is stored instead. See <c>StationSites</c>.</summary>
    Nothing,
}

public static class RemoteImage
{
    private static readonly System.Net.Http.HttpClient Http = Client();

    /// <summary>The same route out, without the cache under it. It is gated and budgeted exactly as
    /// <see cref="Http"/> is, and it identifies itself the same way. But nothing it brings back is
    /// written to disk.
    ///
    /// <para>This is a second CLIENT, not a header on a request. The cache is a handler in the
    /// pipeline, and a request cannot opt out of a handler it is already inside.</para>
    /// </summary>
    private static readonly System.Net.Http.HttpClient Passing = Uncached();

    private static System.Net.Http.HttpClient Uncached()
    {
        var http = new System.Net.Http.HttpClient(RPM.Core.Net.Wire.GatedSockets())
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        RPM.Core.Net.Destination.Unaddressed.Aim(http);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("rpm/1.0 ( https://rpmrpmrpm.com )");
        return http;
    }

    /// <summary>One client, and it identifies the app. Most picture hosts do not need this. Wikimedia
    /// does. It refuses anonymous requests, and the track sheet fetches artist portraits directly from
    /// upload.wikimedia.org. The same user agent string is used by the app's other outside
    /// calls.</summary>
    private static System.Net.Http.HttpClient Client()
    {
        // Through the cache, like everything else that fetches from outside. The cache is the only
        // place a picture is kept. Without it, the lookups that find a picture's address would be
        // cached, but the picture itself would be downloaded again every time it was asked for. A wall
        // that was scrolled down and back up would download every photograph on it again. The cache
        // handler also merges requests for the same address, so one URL is never sent twice at once.
        //
        // The cache keeps an answer for thirty days, and a 404 for seven. It revalidates with the
        // server's own ETag or Last-Modified, so a renewal is a 304 and carries no bytes. The store on
        // disk is 256 MB, and it evicts the least recently used entry first. The gate sits UNDER the
        // cache, not over it. So a picture already on this disk can be drawn on a machine with no
        // connection at all.
        //
        // The bytes matter as much as the requests. Every re-fetch makes a fresh array. Many of those
        // arrays make the heap grow, and the collector pauses that follow make the wall stutter.
        var http = Services.Platform.AppCache.Client("art", RPM.Core.Net.Destination.Unaddressed, TimeSpan.FromSeconds(20));
        http.DefaultRequestHeaders.UserAgent.ParseAdd("rpm/1.0 ( https://rpmrpmrpm.com )");
        return http;
    }

    /// <summary>How long to wait before asking a second time, when the host reports a temporary
    /// problem. The wait is long enough to be a different moment, and short enough that a list of
    /// thumbnails does not stall visibly.</summary>
    private static readonly TimeSpan Backoff = TimeSpan.FromMilliseconds(600);

    /// <summary>The bytes of a picture, as an <see cref="Asked{T}"/>. Cancellation is not a failure,
    /// and it is passed on.
    ///
    /// <para><b>Answered with bytes:</b> the picture. <b>Answered with null:</b> there is no picture
    /// here, and asking again will not change that. Examples are: no address, a 404 or another 4xx, a
    /// host that answered with a page or an empty body instead of a picture, or a local file that does
    /// not exist. The caller may keep this and show "no picture". <b>Unanswered:</b> the picture could
    /// not be asked for. Examples are: no connection, the app is offline, a 5xx, or a 408 or 429 twice.
    /// This says nothing about the picture, so it must not be kept or shown as "no picture".</para>
    ///
    /// <para>A null answer alone cannot say "gone" apart from "failed". The type makes each caller
    /// decide which one it has.</para>
    /// </summary>
    /// <remarks>Asked twice when the first answer was that the host was unwell, rather than that the
    /// picture is absent. The distinction matters. A 404 is an answer: that release has no cover, and
    /// asking again would only add a request to be told so. A 502 says nothing about the picture at
    /// all. The Cover Art Archive is the main case. A cover there is a 307 to archive.org, and then a
    /// second hop to a storage node. Those nodes return 5xx often enough that a single attempt would
    /// miss covers that are there.
    /// </remarks>
    public static async Task<Asked<byte[]?>> BytesAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Asked.Answer<byte[]?>(null);

        // A picture on this computer, such as artwork that the user chose for an artist. It travels as
        // a file:// address, so everything downstream (the caches, the saved order and the crop) can
        // treat a picture as a string. Only the fetch needs to know the difference. HttpClient does not
        // handle file:// addresses.
        if (uri.IsFile)
            return await FileBytesAsync(uri.LocalPath, ct);

        var first = await FetchAsync(uri, ct);
        if (!first.AskAgain)
            return first.Answer;

        await Task.Delay(Backoff, ct);
        return (await FetchAsync(uri, ct)).Answer;
    }

    /// <summary>A local picture: its bytes, null for a file that is not there, and unanswered for one
    /// that is there and could not be read.</summary>
    private static async Task<Asked<byte[]?>> FileBytesAsync(string path, CancellationToken ct)
    {
        try
        {
            return Asked.Answer<byte[]?>(await File.ReadAllBytesAsync(path, ct));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Asked.Answer<byte[]?>(null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Asked.Failed<byte[]?>($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>A page, as text. This is the one thing <see cref="BytesAsync"/> cannot fetch.
    /// </summary>
    /// <remarks>
    /// <b>This is why PageAsync exists.</b> <see cref="BytesAsync"/> answers "no picture" for any body
    /// that the host declares as text, on purpose. A picture host that answers "no such picture" with a
    /// web page is the case that check guards against. A page fetch needs the text, so it cannot use
    /// BytesAsync. Two callers need pages: a radio station's front page, for the mark and the line it
    /// declares in its head, and Wikipedia's summary, which is JSON.
    ///
    /// <para>It uses the same user agent and the same connection pool. The user agent decides between
    /// an answer and a 403 on many hosts. The caller decides whether the answer is also written to
    /// disk. See
    /// <paramref name="keep"/>.</para>
    ///
    /// <para>The read is bounded, unlike a picture. A picture is read in full, because the caller needs
    /// all of it. A front page can be a megabyte of script, and the readers here use only its head. See
    /// <see cref="PageBytes"/>.</para>
    ///
    /// <para>No backoff and one attempt. Answered with the text, or with null where there is no page (a
    /// 404 or other 4xx, an empty body); unanswered where it could not be fetched, by the same rule as
    /// a picture (see <see cref="Unsuccessful"/>). The radio walk keeps what a page said per host for
    /// the session, so a failure must not come back as "this page says nothing".</para>
    /// </remarks>
    /// <param name="keep">Whether this body is worth a row on disk. It is required, not defaulted. See
    /// <see cref="PageKeep"/> for why the answer matters.</param>
    public static async Task<Asked<string?>> PageAsync(
        string? url, PageKeep keep, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return Asked.Answer<string?>(null);

        try
        {
            // Read the response headers first, then at most PageBytes from the stream. An uncached
            // fetch of a five megabyte front page therefore stops after half a megabyte. Through the
            // cached client this limit does nothing. The cache buffers the whole body in order to store
            // it, so a large page would be kept in full on disk, although the reader uses only the
            // first 512 KB.
            var client = keep is PageKeep.OnDisk ? Http : Passing;
            using var response = await client.GetAsync(uri, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return Unsuccessful((int)response.StatusCode,
                                    RPM.Core.Net.OnlineGateHandler.WasRefused(response),
                                    RpmApi.Owns(uri.ToString()))
                    .Answer.Then(_ => (string?)null);

            using var stream = await response.Content.ReadAsStreamAsync(ct);

            var buffer = new byte[PageBytes];
            var filled = 0;
            while (filled < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(filled), ct);
                if (read == 0)
                    break;
                filled += read;
            }

            // The host's own charset is used if it declared one. Otherwise the page's own meta tag is
            // used. Otherwise UTF-8 is used where the bytes are UTF-8, and Windows-1252 where they are
            // not (see ForeignText). A page read up to the limit may end part way through a character.
            // The result says so.
            return Asked.Answer<string?>(filled == 0
                ? null
                : RPM.Core.Files.ForeignText.DecodeWebPage(
                    buffer.AsSpan(0, filled),
                    response.Content.Headers.ContentType?.CharSet,
                    filled == buffer.Length
                        ? RPM.Core.Files.ForeignText.Ending.MayBeCut
                        : RPM.Core.Files.ForeignText.Ending.Whole));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Asked.Failed<string?>($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>How much of a page is read. The readers here use only its head, the link and meta tags.
    /// Those are in the first few thousand characters of every page, and the readers themselves stop at
    /// 200,000 characters. This limit is above that with room to spare, and well under what a front
    /// page can weigh.</summary>
    private const int PageBytes = 512 * 1024;

    /// <summary>Whether the server said the body is text.
    ///
    /// <para>The check is named the way round it is on purpose. It rejects what a host DECLARED as
    /// prose. It does not require every body to be declared as a picture. A picture served as
    /// <c>application/octet-stream</c> is common, and it decodes perfectly. An allowlist of image
    /// types, or a check against the four formats that
    /// <c>RPM.Core.Art.ImageHeader</c> can measure, would throw away pictures that work. This rejects
    /// only bodies that cannot be pictures at all.</para>
    ///
    /// <para>It cannot catch a broken picture that is served AS a picture. Nothing here can. That is
    /// the job of the decoders, and they already fail safely.</para>
    /// </summary>
    private static bool LooksTextual(string? mediaType) =>
        mediaType is { Length: > 0 } type
        && (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || type.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || type.Equals("application/xml", StringComparison.OrdinalIgnoreCase)
            || type.Equals("application/xhtml+xml", StringComparison.OrdinalIgnoreCase));

    /// <summary>One attempt's answer, and whether asking once more could change it.
    ///
    /// <para>It is returned as a value, not kept in a thread-static field. A field written after an
    /// await and read after the caller's own await could be wrong. The two continuations can run on
    /// different pool threads, so the caller could read another request's flag.</para></summary>
    internal readonly record struct Attempt(Asked<byte[]?> Answer, bool AskAgain);

    /// <summary>What a response that was not a success means for the picture.
    ///
    /// <para>The gate's own refusal is a 503 that no service sent. It means the app is offline. It is
    /// not asked again within six hundred milliseconds, because the gate has not changed its mind. It
    /// is not an answer about the picture either. 5xx is a problem at the host. 408 and 429 mean the
    /// host is asking for a moment. Those are asked once more. Everything else, 404 above all, is a
    /// settled answer: there is no picture here.</para>
    ///
    /// <para><b>Being refused says nothing about the picture.</b> A 401 is never the host's opinion of
    /// a file. It means this app failed to identify itself. A 403 from our own API has the same
    /// meaning. If these were filed as "there is no picture", they would be invisible, because "no
    /// picture" is an ordinary answer that nothing reports. So they are unanswered, nothing keeps them,
    /// the next request tries again, and they are reported in the diagnostics.</para>
    ///
    /// <para>A 403 from anywhere else stays an answer. That is a host refusing to be hotlinked.
    /// Wikimedia does this, see <c>RadioLogo.Hotlinkers</c>. Such a host will refuse again, so treating
    /// the 403 as unanswered would mean asking forever.</para>
    /// </summary>
    /// <param name="ourOwnApi">Whether the address belongs to this app's API (<see
    /// cref="RpmApi.Owns"/>). A refusal from there is our fault and never the picture's.</param>
    internal static Attempt Unsuccessful(int code, bool refusedByGate, bool ourOwnApi)
    {
        if (refusedByGate)
            return new Attempt(Asked.Failed<byte[]?>("offline: the gate refused it"), AskAgain: false);

        if (code == 401 || (code == 403 && ourOwnApi))
        {
            var why = $"HTTP {code}: refused" + (ourOwnApi ? " by our own API" : "");

            // This is logged loudly, because the failure would otherwise be silent.
            RPM.Core.Diagnostics.Scope.Said("net", $"picture {why}");

            // Not asked again at once: whatever is missing from the request will still be missing.
            return new Attempt(Asked.Failed<byte[]?>(why), AskAgain: false);
        }

        return code >= 500 || code == 408 || code == 429
            ? new Attempt(Asked.Failed<byte[]?>($"HTTP {code}"), AskAgain: true)
            : new Attempt(Asked.Answer<byte[]?>(null), AskAgain: false);
    }

    /// <summary>The app key, on the requests that go to this app's own API, and on no others.
    ///
    /// <para>This client is <see cref="RPM.Core.Net.Destination.Unaddressed"/>. It has no base address,
    /// because every caller passes it a whole URL, so it carries no key by default. That is right for
    /// the other hosts, which belong to somebody else. Our own API needs the key. Station logos are
    /// fetched from <c>radio/logo/{uuid}</c>. Without the key the API answers 401. A 4xx is read here
    /// as "there is no picture", so every station fell through to the directory and the homepage walk,
    /// and nothing reported it.</para>
    ///
    /// <para><see cref="RpmApi.Owns"/> is the check for exactly this. It matches the host only for the
    /// deployed API, and the host and port for a dev override. So the key never goes to any other
    /// host.</para>
    /// </summary>
    private static void FitKey(System.Net.Http.HttpRequestMessage ask, Uri uri)
    {
        if (RpmApi.Owns(uri.ToString()))
            ask.Headers.TryAddWithoutValidation(RpmApi.KeyHeader, RpmApi.Key);
    }

    /// <summary>One attempt.</summary>
    private static async Task<Attempt> FetchAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var ask = new System.Net.Http.HttpRequestMessage(
                System.Net.Http.HttpMethod.Get, uri);
            FitKey(ask, uri);

            using var response = await Http.SendAsync(ask, ct);
            if (response.IsSuccessStatusCode)
            {
                // A 200 response is not a promise of a picture. A host with no photograph may answer
                // with a page that says so, or with a placeholder, and send it with a success code.
                // Without the check below, the body would reach the decoders, and they would fail on it
                // each time. The host's answer in that case is "no picture".
                if (LooksTextual(response.Content.Headers.ContentType?.MediaType))
                    return new Attempt(Asked.Answer<byte[]?>(null), AskAgain: false);

                var bytes = await response.Content.ReadAsByteArrayAsync(ct);

                // An empty body is also a settled answer. SKBitmap.Decode would throw an
                // ArgumentNullException for it.
                return new Attempt(Asked.Answer<byte[]?>(bytes.Length > 0 ? bytes : null), AskAgain: false);
            }

            return Unsuccessful((int)response.StatusCode,
                                RPM.Core.Net.OnlineGateHandler.WasRefused(response),
                                RpmApi.Owns(uri.ToString()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A connection that never got an answer is the same kind of failure as a 502.
            return new Attempt(Asked.Failed<byte[]?>($"{ex.GetType().Name}: {ex.Message}"), AskAgain: true);
        }
    }


    /// <summary>How much of a picture has to arrive before its size can be read. Every raster format
    /// the app reads writes its dimensions in the first few dozen bytes. A kilobyte gives room to
    /// spare, and it fits in one packet.</summary>
    private const int HeaderBytes = 1024;

    /// <summary>The FRONT of a picture: enough of it to read the header that says how big it is, and
    /// no more.
    ///
    /// <para>This answers a question that a resizing host cannot be asked any other way: how much
    /// picture is actually behind an address? See <see cref="Services.Art.ArtScaler.BoundsOf"/>, and
    /// <c>RadioLogo.Native</c>, which is where the address to ask comes from. Fetching the whole
    /// picture to read the first 24 bytes would cost a download for every candidate on a list that is
    /// checking several.</para>
    ///
    /// <para>This uses a ranged request, which the hosts that matter honour. For example, iHeart
    /// answers 206 with the kilobyte asked for, whether the file is three hundred bytes or seventy
    /// thousand. A host that IGNORES the range answers 200 with the whole picture. That is not a
    /// failure. It is the same bytes, and more of them, and the header is still at the front. So the
    /// status is not checked here. Only a kilobyte is ever read from the stream, either way.</para>
    ///
    /// <para>Returns null where there is nothing to fetch, where the host refused, or where nothing
    /// arrived. There is one attempt and no backoff, unlike <see cref="BytesAsync"/>. This is a
    /// question asked to save work. A caller that gets no answer simply goes on without one.</para>
    /// </summary>
    public static async Task<byte[]?> HeaderAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        try
        {
            if (uri.IsFile)
                return null;   // a file on this disk is already free to read whole

            using var ask = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, uri);
            ask.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, HeaderBytes - 1);
            FitKey(ask, uri);

            using var response = await Http.SendAsync(
                ask, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var front = new byte[HeaderBytes];
            var got = await body.ReadAtLeastAsync(front, HeaderBytes, throwOnEndOfStream: false, ct);
            return got > 0 ? front[..got] : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;   // As above. Cancellation passes through the filter and is not caught here.
        }
    }

    /// <summary>How big the picture at an address is, WITHOUT fetching it.
    ///
    /// <para>This is <see cref="HeaderAsync"/> put to the use it was written for, and it exists because
    /// the alternative was being paid for on the hottest path in the app. Choosing which photograph an
    /// artist wears means comparing the shapes of several, and shape came from
    /// <see cref="BytesAsync"/>, the whole picture down the wire so that twenty-four bytes at the front
    /// of it could be read. Four candidates an artist, a couple of artists a genre card, thirty-six
    /// cards: several hundred press shots downloaded in full to be measured and thrown away, which is
    /// where the wall's stall and the console's socket errors both came from.</para>
    ///
    /// <para>A kilobyte is read instead. Every raster format the app reads writes its dimensions inside
    /// that first kilobyte. A host that ignores the range sends the whole file, and the header is still
    /// at the front. So the answer is the same either way. Only the cost differs.</para>
    ///
    /// <para>Zero by zero where the address is not a picture, where the host refused, or where nothing
    /// arrived. That is the same result that <see cref="Services.Art.ArtScaler.BoundsOf"/> gives for
    /// bytes it cannot read, and callers already handle it. Cancellation is passed on rather than
    /// turned into zero. A walk that was cut short must settle nothing. A caller cannot tell a
    /// cancelled walk from a pair of zeroes, so cancellation must not look like one.</para>
    /// </summary>
    public static async Task<(int Width, int Height)> BoundsAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return (0, 0);

        // A picture on this disk is measured from the front of the file. It is not read whole, for the
        // same reason that a remote picture is not fetched whole. HeaderAsync is not used either. It
        // answers null for file:// addresses, on the grounds that a local picture is free to read. Free
        // to open is not the same as free to read several megabytes.
        if (uri.IsFile)
        {
            try
            {
                await using var file = File.OpenRead(uri.LocalPath);
                var front = new byte[HeaderBytes];
                var got = await file.ReadAtLeastAsync(front, HeaderBytes, throwOnEndOfStream: false, ct);
                return got > 0 ? Services.Art.ArtScaler.BoundsOf(front[..got]) : (0, 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return (0, 0);
            }
        }

        return await HeaderAsync(url, ct) is { } header
            ? Services.Art.ArtScaler.BoundsOf(header)
            : (0, 0);
    }

    /// <summary>
    /// Turns bytes into a bitmap, ready to be given to something that draws. It uses a MemoryStream with
    /// AsRandomAccessStream, which is the reliable path on Uno Skia.
    /// </summary>
    /// <remarks>
    /// <b>SetSource is used, not SetSourceAsync. This is the most important line in the file.</b>
    ///
    /// <para>A BitmapImage opens its stream when it needs the pixels, and the element it is given to
    /// opens the stream too. If this code awaited <c>SetSourceAsync</c>, it would add an open, not save
    /// one. The picture would be decoded twice. The added open is the dangerous one, because it starts
    /// on the thread that draws. The
    /// <c>await Task.Run(…, ct)</c> inside Uno's TryOpenSourceAsync captures that thread's context, and
    /// it rethrows a cancellation ON that thread.</para>
    ///
    /// <para>On the drawing thread under a debugger, a cancellation is not just an exception. It is a
    /// stop. The runtime blocks the throwing thread until the debugger answers. The debugger's own
    /// thread needs the thread-store lock to answer. Meanwhile a collection, triggered by an allocation
    /// anywhere, holds that lock in a suspend-retry loop that cannot finish while a thread is at an
    /// unsafe place. All three wait on each other. In one test, this stopped the drawing thread for
    /// about 13.5 seconds.</para>
    ///
    /// <para><b>The trade-off.</b> This returns as soon as the bytes are handed over. The element
    /// decodes them a frame or two later, so a fade can start a little before its picture is on screen.
    /// That small loss of brightness is accepted, because the alternative is a hang of several
    /// seconds.</para>
    /// </remarks>
    public static Task<BitmapImage> DecodeAsync(byte[] bytes)
    {
        // The stream is NOT disposed here. A BitmapImage does not read the stream once and let go of
        // it. It keeps the stream, and clones it on every open (`_stream.CloneStream()` at the top of
        // Uno's TryOpenSourceAsync). There is always more than one open. This method awaits the first
        // open, so it knows the picture is decoded before anything fades it in. The element the bitmap
        // is then given opens it a second time. If the stream were disposed here, the second open would
        // read a closed stream. The read would be cancelled, and each picture would throw first-chance
        // exceptions while nobody was scrolling. Under a debugger, those exceptions are what a
        // collection cannot suspend through (see the remarks above).
        //
        // Leaving the stream open does no harm. It is a MemoryStream over bytes that the caller already
        // holds. It owns no handle, and it is collected together with the bitmap that keeps it.
#if RPM_WINUI
        return WinUiDecodeAsync(bytes, edge: 0);
#else
        var stream = Presentation.Common.BitmapBytes.Stream(bytes);
        var bitmap = new BitmapImage();
        bitmap.SetSource(stream);
        return Task.FromResult(bitmap);
#endif
    }

#if RPM_WINUI
    /// <summary>The same picture, decoded the way the WinUI head needs it.
    ///
    /// <para><b>This head does not decode the way the one above does.</b> The comment above is about
    /// Uno. SetSource rather than SetSourceAsync avoids a cancellation that is rethrown on the drawing
    /// thread inside Uno's TryOpenSourceAsync. Leaving the stream undisposed is needed because Uno
    /// clones it on every open. Neither applies here, and the same shape costs a lot on this head. A
    /// MemoryStream behind AsRandomAccessStream is managed, and it is not thread-safe. WinUI decodes
    /// through WIC on its own threads, and it opens the stream more than once. Two decodes that share
    /// one MemoryStream race on its Position.</para>
    ///
    /// <para>InMemoryRandomAccessStream is a real WinRT stream. It is a native buffer that clones
    /// correctly, and it can be opened from several threads at once. It is awaited, so the picture is
    /// decoded before this method returns. That is what the caller wants, and it is what the Uno path
    /// had to give up.</para>
    /// </summary>
    /// <param name="edge">The longest side to decode to, or 0 for the picture's own size.</param>
    private static async Task<BitmapImage> WinUiDecodeAsync(byte[] bytes, int edge)
    {
        var stream = BitmapBytes.Stream(bytes);
        var bitmap = new BitmapImage();

        if (edge > 0)
        {
            // Only ever scale DOWN, as on the other head.
            var (w, h) = Services.Art.ArtScaler.BoundsOf(bytes);
            if (w <= 0 || h <= 0 || Math.Min(w, h) > edge)
            {
                if (w > h)
                    bitmap.DecodePixelHeight = edge;
                else
                    bitmap.DecodePixelWidth = edge;
            }
        }

        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }
#endif

    /// <summary>The same again, but with the reduction done HERE, in Skia, before anything is decoded.
    ///
    /// <para><b>Why not just <see cref="DecodeAsync(byte[], int)"/>.</b> That hands the reduction to
    /// the framework's decoder. What a decoder does at a scale factor is up to the decoder. It is fast
    /// and roughly a box filter. That is fine for a photograph, but not for a picture with hard black
    /// and white lettering. The parental advisory mark shows the problem. A lockup a third of the width
    /// of a 640 px cover, cut to a tile, came back as grey gravel, with a black smear where the words
    /// EXPLICIT CONTENT are.</para>
    ///
    /// <para>Here the cut goes through <see cref="Services.Art.ArtScaler.SamplingFor(double)"/>. That
    /// is the one place in this app that decides how a reduction is sampled. Mitchell's cubic is used
    /// where the picture barely shrinks. True mipmapped averages are used past half size, which is the
    /// usual case for a tile. The result is then re-encoded through <c>ArtScaler.Jpeg</c>. That keeps
    /// the colour at full resolution rather than subsampling it. This matters here for the same reason.
    /// The mark is black on white on a coloured ground, and chroma at half the edge turns its white
    /// band muddy.</para>
    ///
    /// <para>The cost is a decode, a resize and an encode for each picture. This runs off the UI
    /// thread. It is used on a grid of tiles, which is a few dozen at most, and never on a wall of two
    /// thousand.</para>
    /// </summary>
    /// <param name="edge">The edge in PHYSICAL pixels, the same as <see cref="DecodeAsync(byte[],
    /// int)"/> takes, see <see cref="Services.Art.ArtScaler.DecodeEdgeFor"/>.</param>
    /// <param name="kind">What is being cut. The caller states it at every call, and it has no default.
    /// The two kinds need opposite filters, and neither looks wrong on its own. See <see
    /// cref="Cut"/>.</param>
    public static async Task<BitmapImage> DecodeSmoothAsync(byte[] bytes, int edge, Cut kind)
    {
        var how = kind is Cut.Drawing ? new SKSamplingOptions(SKCubicResampler.Mitchell) : (SKSamplingOptions?)null;

        // Downscale hands the same bytes back where the picture already fits, so this is a decode of
        // whatever it was given either way.
        var cut = await Task.Run(() => Services.Art.ArtScaler.Downscale(bytes, edge, how));
        return await DecodeAsync(cut);
    }

    /// <summary>A logo, cut to the size the surface draws it at before anything decodes it.
    ///
    /// <para><b>This is different from <see cref="DecodeAsync(byte[], int)"/> and from
    /// <see cref="DecodeSmoothAsync"/>.</b> The first hands the reduction to the bitmap decoder. The
    /// decoder reads sixteen source pixels per output pixel and drops the rest. On a photograph that
    /// shows as grain. On lettering it shows as gravel down every stroke. The second samples with <see
    /// cref="Services.Art.ArtScaler.SamplingFor(double)"/>. That is the rule for photographs, and it
    /// gives a logo the wrong kind of result. A mark goes through
    /// <see cref="Services.Art.ArtScaler.FitMark"/> instead. It is halved until it is within a factor
    /// of two of the target, then finished with the B-spline cubic, which is the app's one cut for
    /// type.</para>
    ///
    /// <para>It is used for station logos. A logo arrives 512 pixels square and is drawn in a 51-point
    /// well. So the five-fold reduction is done here, not by the decoder.</para>
    /// </summary>
    /// <param name="edge">The cap in PHYSICAL pixels, points times the display scale.</param>
    /// <param name="side">Which side that cap is measured on. Stated at every call: a mark fitted whole
    /// and a mark cropped to fill do not want the same side capped. See
    /// <see cref="Services.Art.ArtScaler.MarkSide"/>.</param>
    public static async Task<BitmapImage> DecodeMarkAsync(
        byte[] bytes, int edge, Services.Art.ArtScaler.MarkSide side)
    {
        var cut = await Task.Run(() => Services.Art.ArtScaler.FitMark(bytes, edge, side));

        // Still capped on the way in. FitMark returns the same bytes when the picture already fits, or
        // when it cannot read the picture. In the second case the mark would otherwise decode at
        // whatever size its broadcaster published it at.
        return await DecodeAsync(cut, edge);
    }

    /// <summary>A sleeve or a record, cut to exactly the square it fills on screen. Nothing scales it
    /// when it is drawn.
    ///
    /// <para><b>A smooth bicubic cut, at the size shown.</b> A picture decoded once at the widest size,
    /// and left for the renderer to shrink, looks soft. The renderer's shrink is not a cubic. This is
    /// the cut the app already makes for marks (<see cref="Services.Art.ArtScaler.FitMark"/>). It is
    /// halved while more than a factor of two is left to go. It is then finished with <see
    /// cref="Services.Art.ArtScaler.SmoothCubic"/>, on the SHORT side, because the surface crops with
    /// UniformToFill.</para>
    ///
    /// <para>The cut is exact, not bucketed. So a box that changes size needs a new cut. The caller
    /// keeps the bytes and asks again once the size has settled (see
    /// <c>AlbumDetailView.RecutStage</c>).</para>
    /// </summary>
    /// <param name="edge">The square's edge in PHYSICAL pixels: points times the display scale.</param>
    public static async Task<BitmapImage> DecodeArtAsync(byte[] bytes, int edge)
    {
        var cut = await Task.Run(
            () => Services.Art.ArtScaler.FitMark(bytes, edge, Services.Art.ArtScaler.MarkSide.Short));

        // The picture is capped again here, for a picture that FitMark could not read and returned
        // unchanged. A cut whose short side is exactly the edge is not changed by this.
        return await DecodeAsync(cut, edge);
    }

    /// <summary>What kind of picture is being cut down. This is the one thing that the reduction
    /// FACTOR cannot say.
    ///
    /// <para><b>A photograph.</b> Sampled by the app's own rule. See
    /// <see cref="Services.Art.ArtScaler.SamplingFor(double)"/>, where the reasons are given. Past half
    /// size a cubic resampler stops filtering and starts sampling. A photographed sleeve with fine
    /// detail would then show moire. Mipmapped averages are the right answer for a photograph. This is
    /// the answer for anything that came from a camera or a scanner.</para>
    ///
    /// <para><b>A drawing.</b> Mitchell's cubic, whatever the factor. What the app draws for itself is
    /// mostly wide, smooth gradients, with at most one piece of hard black and white type in a corner.
    /// There is no fine texture to alias. The mipmap chain that suits a photograph turns that type to
    /// mush, because it is an average of averages. A letter three pixels tall, averaged twice, becomes
    /// a grey smear. Cut from the same cover at the same size, the three methods come out as noisy
    /// (plain linear), soft (mipmapped), and readable (this one).</para>
    /// </summary>
    public enum Cut
    {
        /// <summary>Something photographed: a sleeve, a portrait, a station's logo.</summary>
        Photograph,

        /// <summary>Something this app drew: a made cover, a figure, a wash.</summary>
        Drawing,
    }

    /// <summary>The same, decoded AT the size it is going to be drawn.
    ///
    /// <para>A bitmap that is told nothing decodes all of itself, and the renderer then scales what
    /// comes out. This has two costs, and one of them is visible. Memory is width × height × 4 bytes. A
    /// 640 px thumbnail behind a 128-point sleeve takes 1.6 MB for 147 KB of pixels that are shown. The
    /// other cost: the scaling happens at draw time and not at decode time, so a picture reduced by a
    /// large factor looks sampled down rather than resized. That is the softness. A large picture is
    /// being shrunk in the wrong place, and a smaller copy is not being loaded.</para>
    ///
    /// <para>Only ONE dimension is set, never both. Setting both decodes to exactly that box, which
    /// stretches a picture that is not square. WHICH dimension is set depends on the picture. A surface
    /// that crops with UniformToFill needs its SHORT side to reach the box. Constraining the width of a
    /// landscape sleeve would leave its height short, and the whole picture would then be scaled up
    /// again to cover. The album wall uses the same rule.</para>
    /// </summary>
    /// <param name="edge">The edge in PHYSICAL pixels: points times the display scale. See
    /// <see cref="Services.Art.ArtScaler.DecodeEdgeFor"/>, which is where that sum belongs.</param>
    /// <inheritdoc cref="DecodeAsync(byte[])" path="/summary"/>
    public static Task<BitmapImage> DecodeAsync(byte[] bytes, int edge)
    {
        // The stream is NOT disposed here. A BitmapImage keeps the stream, and clones it on every open
        // (`_stream.CloneStream()` at the top of Uno's TryOpenSourceAsync). There is always more than
        // one open. This method awaits the first, so it knows the picture is decoded before anything
        // fades it in. The element that the bitmap is given to opens the stream a second time. If the
        // stream were disposed here, the second open would read a closed stream and be cancelled. Each
        // picture would then throw first-chance exceptions, even with nobody scrolling. Under a
        // debugger, those are the exceptions that a collection cannot suspend through (see the remarks
        // of DecodeAsync(byte[])).
        //
        // Leaving it open does no harm. It is a MemoryStream over bytes that the caller already holds.
        // It owns no handle, and it is collected with the bitmap that keeps it.
#if RPM_WINUI
        return WinUiDecodeAsync(bytes, edge);
#else
        var stream = Presentation.Common.BitmapBytes.Stream(bytes);
        var bitmap = new BitmapImage();

        var (w, h) = Services.Art.ArtScaler.BoundsOf(bytes);

        // Only ever scale DOWN. A picture already smaller than the box, such as a small station logo or
        // a cover from a tag, is decoded as it is. If it were told to reach the edge, it would be
        // enlarged on the way in and shrunk again on the way out. That costs the memory of a bigger
        // picture and gives a softer result than drawing the picture as it is.
        //
        // If the bounds cannot be read, the picture is treated as a big one. Constraining a decode that
        // would be constrained anyway is the safe way to be wrong here.
        if (w <= 0 || h <= 0 || Math.Min(w, h) > edge)
        {
            if (w > h)
                bitmap.DecodePixelHeight = edge;
            else
                bitmap.DecodePixelWidth = edge;
        }

        bitmap.SetSource(stream);
        return Task.FromResult(bitmap);
#endif
    }

    /// <summary>Fetch and decode in one step, off any visible layer and under the cancellation token.
    /// Answered with null where there is no picture, which includes bytes that will not decode.
    /// Unanswered where the fetch failed. See <see cref="BytesAsync"/>.</summary>
    public static async Task<Asked<BitmapImage?>> LoadAsync(string? url, CancellationToken ct = default)
    {
        if (!(await BytesAsync(url, ct)).Answers(out var bytes, out var why))
            return Asked.Failed<BitmapImage?>(why);

        if (bytes is null)
            return Asked.Answer<BitmapImage?>(null);

        try
        {
            return Asked.Answer<BitmapImage?>(await DecodeAsync(bytes));
        }
        catch
        {
            return Asked.Answer<BitmapImage?>(null);   // bytes that are not a picture we can read
        }
    }
}
