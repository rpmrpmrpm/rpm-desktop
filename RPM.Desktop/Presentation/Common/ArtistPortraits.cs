using RPM.Core.Threading;
using RPM.Desktop.Services;
using RPM.Core.Art;
using RPM.Desktop.Services.Art;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// Everything the app knows about one artist's photograph, worked out once and kept.
///
/// <para>A picture needs a fetch, a scale, two colour reads and, where it is dark enough to need one, a
/// blur. Each of these answers is a property of the picture, not of the surface that asked for it. Before
/// this cache, paging back to an artist in the track pane, or scrolling their box back onto the artists
/// wall, repeated the whole job. The photograph was downloaded again, because nothing between here and the
/// network keeps anything.</para>
///
/// <para>The cache is shared, not owned by whichever surface asked first. The track pane and the artists
/// wall show the SAME photograph of the same person. A second cache would mean a second download, a second
/// colour read, and a risk that the two surfaces pick different pictures for one act. The choice is made
/// once for each artist and then kept.</para>
///
/// <para>What is kept is small: a cut-down copy, a drained copy, a blurred copy, and two colours. The full
/// backdrop is released as soon as those exist.</para>
/// </summary>
public static class ArtistPortraits
{
    /// <param name="Thumb">The photograph cut down to a tile's worth of pixels.</param>
    /// <param name="Grey">The same picture with its colour removed. The artists wall draws this under its
    /// tint. It is null when the picture cannot be drained of colour.</param>
    /// <param name="Wash">The whole picture, blurred. Surfaces that have text on top of the picture use it.
    /// It is null only when the picture cannot be blurred.
    ///
    /// <para>A wash is made for every photograph, including pale ones. Whether a picture is TOO pale to use
    /// is decided by the surface that asks, not here, because it depends on what is put on top. Text on a
    /// wash at one third strength needs a dark picture under it. A page with the same wash at one tenth
    /// strength is only a tint, and can use any picture. If this class made that decision, the second
    /// surface could be left with no background for half the artists in a collection, because of what the
    /// first surface does with its paragraphs. See <see cref="PrefersBlackInk"/>, which is the test that
    /// either surface can use.</para></param>
    /// <param name="Tint">The dominant colour, which is the biggest object in the picture. A surface beside
    /// the picture is painted in this colour.</param>
    /// <param name="Mean">The average colour. This answers a different question. A wash is the whole
    /// photograph blurred, so its tone is this value. Never use the dominant colour for this. The dominant
    /// colour is the colour of a jacket or a face, and it can be bright in a photograph that is dark
    /// overall.</param>
    /// <param name="Full">The photograph as it was fetched, at the size that the catalogue keeps it. Use it
    /// for a surface that draws the picture LARGE. <paramref name="Thumb"/> is cut to
    /// <see cref="ArtScaler.TileEdge"/>, which suits a tile on a wall. The home banner draws a window
    /// across most of a 760-point plate. From Thumb, that would be an upscale of an image that is already
    /// reduced.
    ///
    /// <para>It is null for a picture that came from the cache, because the full copy is not kept. See
    /// <see cref="PicturesKept"/>, which limits the cache to a few megabytes by keeping only reduced
    /// copies. A caller that needs the full picture can fall back to Thumb.</para></param>
    public sealed record Portrait(
        byte[] Thumb, byte[]? Grey, byte[]? Wash, Windows.UI.Color? Tint, Windows.UI.Color? Mean,
        byte[]? Full = null);

    private static readonly Dictionary<string, Portrait> Pictures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The number of photographs held before the whole cache is cleared. Each one is a cut-down
    /// copy, a drained copy and a blurred copy. So the cache is a few megabytes at this limit.</summary>
    private const int PicturesKept = 64;

    /// <summary>The cut-down copies made so far. This is kept in its own table, not as a half-filled
    /// <see cref="Portrait"/> in the table beside it. A record with three nulls cannot show whether they
    /// are answers or missing values. Everything that reads it treats nulls as answers.</summary>
    private static readonly Dictionary<string, byte[]> Thumbs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The number of cut-down copies held. It is much higher than <see cref="PicturesKept"/>,
    /// because each copy is one small picture and not four. Also, the surfaces that ask for these ask about
    /// a whole wall of genres at once.</summary>
    private const int ThumbsKept = 256;

    /// <summary>
    /// One photograph, cut down to a tile's worth of pixels. Nothing else is made from it.
    ///
    /// <para><b>Why this is not <see cref="PictureAsync"/> with a field read.</b> That method drains the
    /// picture, blurs the full backdrop and reads two colours from it. It does this because the artists
    /// wall and the track pane draw all four. A genre card draws one photograph in its own colours. The
    /// drain, the blur and both colours are work whose results the genre card never uses. The blur is a
    /// decode and a Gaussian over a 1920-wide press shot. A screenful of cards would mean a lot of unused
    /// work on the thread pool. That slows the wall, because the thread that draws the wall competes with
    /// it.</para>
    ///
    /// <para>A photograph that is asked for as a thumb and LATER asked for whole is fetched twice. The full
    /// backdrop is what a wash is made of, and it is not kept. This trade-off is deliberate, and it is the
    /// right choice. One extra fetch for the rare artist who is on both a genre card and the artists wall
    /// costs less than four unused derivations for every artist on the wall.</para>
    /// </summary>
    /// <returns>Answered with the thumb, or with null where there is no picture at the address;
    /// unanswered where it could not be fetched. See <see cref="RemoteImage.BytesAsync"/>.</returns>
    public static async Task<Asked<byte[]?>> ThumbAsync(string url, CancellationToken ct = default)
    {
        lock (Pictures)
        {
            if (Pictures.TryGetValue(url, out var whole))
                return Asked.Answer<byte[]?>(whole.Thumb);
        }

        Task<Asked<byte[]?>> shared;
        lock (Thumbs)
        {
            if (Thumbs.TryGetValue(url, out var kept))
                return Asked.Answer<byte[]?>(kept);

            // The same photograph is asked for twice at once. This happens on every wall, because one act
            // can be filed under more than one genre. When two cards start together, each would download,
            // decode and cut the same picture, for one answer. So the TASK is shared, not only the answer.
            // GenreFaces does the same thing. The second asker waits for the first asker's work, and does
            // not do it again.
            if (!Cutting.TryGetValue(url, out shared!))
                Cutting[url] = shared = CutAsync(url);
        }

        // The caller's token cancels the wait, not the work. The picture is worth keeping whether or not
        // this card is still on screen. The next asker is then answered from the table, not from the
        // network.
        return await shared.WaitAsync(ct);
    }

    /// <summary>The cuts that are being made right now. Two askers share one cut. An entry is removed for
    /// each url when it finishes. What is kept is the answer, in <see cref="Thumbs"/>.</summary>
    private static readonly Dictionary<string, Task<Asked<byte[]?>>> Cutting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One photograph is fetched and cut down. It uses no caller's token. This uses Task.Run and
    /// not a bare async method. It is started from whichever thread asked, which on a wall is the thread
    /// that draws, and the code below decodes an image. Only a thumb is kept in <see cref="Thumbs"/>. "No
    /// picture" is kept by the web cache under the fetch. A failure is not kept.</summary>
    private static Task<Asked<byte[]?>> CutAsync(string url)
        => Task.Run(async () =>
        {
            try
            {
                if (!(await RemoteImage.BytesAsync(url).ConfigureAwait(false)).Answers(out var bytes, out var why))
                    return Asked.Failed<byte[]?>(why);
                if (bytes is null)
                    return Asked.Answer<byte[]?>(null);

                var thumb = await ThumbCache.GetAsync(bytes, ArtScaler.TileEdge).ConfigureAwait(false);

                lock (Thumbs)
                {
                    if (Thumbs.Count >= ThumbsKept)
                        Thumbs.Clear();
                    Thumbs[url] = thumb;
                }

                return Asked.Answer<byte[]?>(thumb);
            }
            catch (Exception ex)
            {
                // The cut failed. Nothing is kept, so the next request for this photograph tries again.
                return Asked.Failed<byte[]?>($"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                lock (Thumbs)
                    Cutting.Remove(url);
            }
        });

    /// <summary>
    /// One photograph and everything the app makes of it, from the cache or from the network.
    ///
    /// <para>The photograph is fetched and cut down before it is decoded. A fanart.tv backdrop is a
    /// 1920-wide photograph, and nothing here shows one at that size. Decoded whole, it would take
    /// megabytes of texture for as long as the surface is up, for a picture whose detail nobody can
    /// see.</para>
    ///
    /// <para>Use <see cref="ThumbAsync"/> instead where the cut-down copy is all that is drawn. Every line
    /// below it is paid for whether or not it is read.</para>
    /// </summary>
    /// <returns>Answered with the portrait, or with null where there is no picture at the address (a
    /// 404, a page instead of a picture); unanswered where it could not be fetched, and where
    /// <paramref name="ct"/> fired before it was finished. See
    /// <see cref="RemoteImage.BytesAsync"/>.</returns>
    public static async Task<Asked<Portrait?>> PictureAsync(string url, CancellationToken ct = default)
    {
        lock (Pictures)
        {
            if (Pictures.TryGetValue(url, out var kept))
                return Asked.Answer<Portrait?>(kept);
        }

        if (!(await RemoteImage.BytesAsync(url, ct)).Answers(out var bytes, out var why))
            return Asked.Failed<Portrait?>(why);
        if (bytes is null)
            return Asked.Answer<Portrait?>(null);

        var thumb = await ThumbCache.GetAsync(bytes, ArtScaler.TileEdge, ct);

        // The dominant colour and the average brightness. Both are read from the cut-down copy, not from
        // the full picture. Neither value changes when a picture is scaled, and this decode is already
        // done.
        Windows.UI.Color? tint = null;
        Windows.UI.Color? mean = null;
        try
        {
            var (read, colours) = await Cancellable.RunAsync(
                () => ((Windows.UI.Color?)(CoverColor.Dominant(thumb) ?? CoverColor.Highlight(thumb)),
                       CoverColor.Average(thumb)), ct);
            if (read)
                (tint, mean) = colours;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not a picture the reader knows. The surfaces still get it; they keep their own dark.
        }

        // Both derived copies are made together, in one hop to a worker thread. The drained copy is made
        // from the THUMB, because a tile is the only thing that draws it. The blur is made from the FULL
        // picture. Blurring a picture that has already been reduced gives a wash made of the artefacts of
        // the reduction, not of the photograph.
        byte[]? grey = null;
        byte[]? wash = null;
        try
        {
            var (drawn, copies) = await Cancellable.RunAsync(
                () => (ArtScaler.Grey(thumb), ArtScaler.Wash(bytes)), ct);
            if (drawn)
                (grey, wash) = copies;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Not a picture Skia can read twice. Both surfaces have a fallback for a missing copy.
        }

        // The caller has stopped waiting. The two reads above then stop answering, without throwing. A
        // picture that is missing its colours or its blur must not be kept as this url's answer. Everything
        // that asks for it later would get the gaps. The result is also not "no picture".
        if (ct.IsCancellationRequested)
            return Asked.Failed<Portrait?>("cancelled");

        var picture = new Portrait(thumb, grey, wash, tint, mean, bytes);
        lock (Pictures)
        {
            // The limit is set bluntly. The surfaces need the record that is playing, whoever is on the
            // pager, and the boxes on screen. Each entry can be rebuilt with one fetch. So the cache needs
            // a ceiling, not a careful eviction rule.
            if (Pictures.Count >= PicturesKept)
                Pictures.Clear();

            // WITHOUT the full copy. The ceiling above assumes that every held copy is a reduced one. A
            // press shot is several times the size of the three reduced copies together. The caller that
            // needs the full picture is the one that just asked for the fetch.
            Pictures[url] = picture with { Full = null };
        }

        return Asked.Answer<Portrait?>(picture);
    }

    /// <summary>Whether BLACK text reads better than white text on this ground. See
    /// <see cref="CoverColor.PrefersBlackInk"/>, which holds the rule. The rule was moved there for the
    /// phone head. A transparent logo's ground is decided by the same sum. The phone head needs the colour
    /// reading, but not the portraits, the cache and the artwork service around it.</summary>
    public static bool PrefersBlackInk(Windows.UI.Color ground) => CoverColor.PrefersBlackInk(ground);

    // ── which photograph an artist gets ──

    private static readonly Dictionary<string, string> Settled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Which of an artist's photographs the app shows for them. The choice is made once, and never again.
    ///
    /// <para><b>Shape first.</b> The track pane's strip is 260 points tall and 320 points wide, which is
    /// nearly upright. The pictures it is given are fanart.tv backdrops, at 1920 by 1080. That shape is
    /// chosen for the back of a television. Filling an upright box from one of them crops away four fifths
    /// of it, and leaves a band across someone's chest. So the candidates are measured first. The
    /// measurement reads only the header, not the pixels (see <see cref="ArtScaler.BoundsOf"/>). The
    /// candidate nearest to three by four wins.</para>
    ///
    /// <para><b>The window's picture loses a tie.</b> The carousel behind the pane opens on the first saved
    /// photograph. So if the pane also took the first one, one picture would appear on screen twice. The
    /// penalty is small enough that a truly upright first photograph still beats a landscape second one.
    /// The aim is to differ where there is a choice, not to show a worse picture for no reason.</para>
    ///
    /// <para><b>Then it is remembered.</b> This is not for speed. The carousel rewrites the saved order
    /// whenever the user stops on a picture (see ArtistArtChoice). A surface that decided again would
    /// change the artist's photograph while a reader only paged back and forth. So the choice is made once
    /// per artist per session, and everything that shows the artist stays as steady as the name.</para>
    /// </summary>
    /// <returns>Answered with the chosen address, or with null where the artist has no photograph;
    /// unanswered where the artwork lookup did not answer. Only an address is settled.</returns>
    public static async Task<Asked<string?>> ChooseAsync(string artist, string? album, CancellationToken ct = default)
    {
        Task<Asked<string?>> shared;
        lock (Settled)
        {
            if (Settled.TryGetValue(artist, out var settled))
                return Asked.Answer<string?>(settled);

            // One artist can appear on more than one shelf, and the wall asks about all of them at once.
            // Deciding takes a catalogue lookup and up to four ranged reads. Two cards that start on the
            // same artist together would do all of it twice, for one answer. That is also two chances to
            // settle on different pictures of one person, if the sources return in a different order. So
            // the task is shared. There is one walk and one answer.
            if (!Choosing.TryGetValue(artist, out shared!))
                Choosing[artist] = shared = DecideAsync(artist, album);
        }

        return await shared.WaitAsync(ct);
    }

    /// <summary>The choices that are being made right now. Two askers share one choice. What is kept is the
    /// answer, in <see cref="Settled"/>. An entry is removed for each artist when it finishes.</summary>
    private static readonly Dictionary<string, Task<Asked<string?>>> Choosing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The walk itself. It uses no caller's token, and it runs off the calling thread. A card asks
    /// from the thread that draws it, and this reads headers from the network.</summary>
    private static Task<Asked<string?>> DecideAsync(string artist, string? album)
        => Task.Run(async () =>
        {
            try
            {
                return await ChooseCoreAsync(artist, album).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Nothing settled, so the next asker walks it again.
                return Asked.Failed<string?>($"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                lock (Settled)
                    Choosing.Remove(artist);
            }
        });

    private static async Task<Asked<string?>> ChooseCoreAsync(string artist, string? album)
    {
        var ct = CancellationToken.None;
        if (!(await CandidatesAsync(artist, album, ct)).Answers(out var candidates, out var why))
            return Asked.Failed<string?>(why);

        if (candidates.Count == 0)
            return Asked.Answer<string?>(null);

        // With one photograph, that photograph is the answer, whatever its shape. There is nothing to
        // choose between. A fetch made only to measure a picture that will be shown anyway is wasted.
        if (candidates.Count == 1)
            return Asked.Answer<string?>(Settle(artist, candidates[0]));

        var considered = candidates.Take(PhotosConsidered).ToArray();

        // The size is read from the first kilobyte, not by fetching the whole picture. See
        // RemoteImage.BoundsAsync. Without this, the walk would use more bandwidth than anything else in
        // the app, on pictures that are then thrown away.
        //
        // All four are asked at once, not one after another. None of them depends on the one before it, and
        // they come from different hosts. Four in a row would take four round trips to answer one question.
        // The answer is the same either way, because every candidate is measured, whatever the earlier ones
        // scored. A playlist header on a shared library often needed this. No artist from someone else's
        // library has a picture saved on this computer, so every one of them came through here.
        var shapes = await Task.WhenAll(considered.Select(
            url => Measurable(url) ? RemoteImage.BoundsAsync(url, ct) : Task.FromResult((0, 0))));

        string? best = null;
        var bestScore = double.MaxValue;

        for (var i = 0; i < considered.Length; i++)
        {
            var (width, height) = shapes[i];
            if (width <= 0 || height <= 0)
                continue;

            var score = Math.Abs(((double)width / height) - PortraitShape) + (i == 0 ? FirstPhotoPenalty : 0);
            if (score >= bestScore)
                continue;

            bestScore = score;
            best = considered[i];
        }

        // Nothing could be measured. This happens when the app is offline, or when every candidate refuses
        // to be read. Then the fallback rule applies: use the second photograph, and use the first only
        // when there is no second one.
        best ??= candidates.Count > 1 ? candidates[1] : candidates[0];

        return Asked.Answer<string?>(Settle(artist, best));
    }

    // ── the square one, for the surfaces about one artist ──

    /// <summary>The square chosen for each artist, and the choices that are being made right now, so that
    /// two askers share one. These are separate tables, not entries in <see cref="Settled"/>. That is
    /// needed.
    /// <see cref="Settled"/> holds the answer from <see cref="ChooseAsync"/>, which the wall's boxes, the
    /// genre cards and the home banner all show. <see cref="ShownFirst"/> puts that answer at the front of
    /// the artist page's pager. A square stored there would become the picture on every one of those
    /// surfaces. The artist page puts the square first on purpose. It does that by calling
    /// <see cref="SquareAsync"/> by name (see MainPage.LoadArtistPicturesAsync), not through this
    /// table.</summary>
    private static readonly Dictionary<string, string> SquareSettled = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task<Asked<string?>>> SquareChoosing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary> Which picture an artist gets on a surface about that one artist: the track pane's upright
    /// strip and the artist page's band. It is fanart.tv's square artist thumb where there is one, and
    /// otherwise the picture that everything else in the app shows. The 16:9 boxes on the artists wall and
    /// on a genre page use <see cref="ChooseAsync"/>'s backdrop instead.
    ///
    /// <para><b>Why a different picture and not a crop.</b> fanart.tv has two kinds of picture. The
    /// backdrops are 1920 by 1080 press shots, a shape chosen for the back of a television. A box or an
    /// upright strip filled from one of them keeps a band across the middle of a person. The second kind,
    /// <c>artistthumb</c>, is 1000 by 1000 and framed for a square. It is a separate picture, not a cut of
    /// the backdrop. So nothing here measures or ranks the squares. fanart.tv's own likes order already
    /// shows which square of an act is the best one, and they all have the same shape.</para>
    ///
    /// <para><b>The fallback is written here, not at each call site.</b> Most artists have no square. A
    /// square needs a resolved MBID and someone to have uploaded one to fanart.tv. So "no square" is the
    /// common answer, not an edge case. A caller that falls back itself might forget, and then a box would
    /// draw nothing at all.</para>
    ///
    /// <para><b>The choice is settled once for each artist</b>, like the backdrop choice, for the same
    /// reason. Two surfaces that draw one act must not disagree about which picture is theirs. A surface
    /// that decided again would change the picture while the reader does nothing.</para>
    ///
    /// <para>Known limit: a named square that will not fetch leaves the artist with the picture a surface
    /// draws when there is no photograph. It does not fall back to the backdrop. A backdrop that will not
    /// fetch has the same problem. Checking would cost one round trip for each artist, just to catch the
    /// case where the picture host is down.</para>
    /// </summary>
    /// <returns>Answered with the square, or the backdrop standing in for one, or with null where the
    /// artist has no photograph; unanswered where a lookup did not answer. Only an address is
    /// settled.</returns>
    public static async Task<Asked<string?>> SquareAsync(string artist, string? album, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return Asked.Answer<string?>(null);

        Task<Asked<string?>> shared;
        lock (SquareSettled)
        {
            if (SquareSettled.TryGetValue(artist, out var settled))
                return Asked.Answer<string?>(settled);

            if (!SquareChoosing.TryGetValue(artist, out shared!))
                SquareChoosing[artist] = shared = DecideSquareAsync(artist, album);
        }

        return await shared.WaitAsync(ct);
    }

    /// <summary>The walk itself. It uses no caller's token, and it runs off the calling thread. A box asks
    /// from the thread that draws it, and this makes a catalogue lookup.</summary>
    private static Task<Asked<string?>> DecideSquareAsync(string artist, string? album)
        => Task.Run(async () =>
        {
            try
            {
                // This uses the artwork service, not the saved order. The saved order holds BACKDROPS only.
                // Those are reconciled and ranked as backdrops, and a square was never in that order. The
                // service answers from its own table first, then from the web cache. So this is one round
                // trip per artist per install.
                //
                // A lookup that gives no answer settles nothing. If it were settled as "no square", the
                // artist would wear a backdrop for the whole session, even though they have a square.
                if (!(await App.Services.GetRequiredService<ArtworkService>()
                                        .GetAsync(artist, album).ConfigureAwait(false))
                        .Answers(out var art, out var why))
                    return Asked.Failed<string?>(why);

                var square = art.Thumbs.FirstOrDefault(u => !string.IsNullOrWhiteSpace(u));

                // ChooseAsync is called outside the lock above, because it takes its own lock. If it were
                // called while SquareSettled is held, and one artist were being decided for a box and a
                // band at once, two threads would take the two locks in opposite orders.
                var answer = square is not null
                    ? Asked.Answer<string?>(square)
                    : await ChooseAsync(artist, album).ConfigureAwait(false);

                if (answer is Asked<string?>.Answered { Value: { } address })
                {
                    lock (SquareSettled)
                        SquareSettled[artist] = address;
                }

                return answer;
            }
            catch (Exception ex)
            {
                // Nothing settled, so the next asker walks it again.
                return Asked.Failed<string?>($"{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                lock (SquareSettled)
                    SquareChoosing.Remove(artist);
            }
        });

    /// <summary>
    /// Every square photo of an artist (fanart.tv's <c>artistthumb</c>), best-liked first, one entry per
    /// picture. Empty for an artist with none, which is most of them.
    ///
    /// <para>Asked with the same artist and album as <see cref="DecideSquareAsync"/>, so both read the
    /// same answer from the artwork service's memory.</para>
    /// </summary>
    public static async Task<Asked<IReadOnlyList<string>>> SquaresAsync(
        string artist, string? album, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return Asked.Answer<IReadOnlyList<string>>([]);

        return (await App.Services.GetRequiredService<ArtworkService>().GetAsync(artist, album, ct: ct))
            .Then(art => OnePerPicture(art.Thumbs.Where(u => !string.IsNullOrWhiteSpace(u)).ToList()));
    }

    /// <summary> Whether the artist page's pager turns through the artist's square photos rather than their
    /// wide ones. This is true when the band opened on one of the squares. If an artist has square photos,
    /// the pager shows only those. Wide photos are shown only for an artist with no square.
    ///
    /// <para>This is decided from the picture that the band opened on, not from whether squares exist. Page
    /// one of the pager must be the picture on screen. <see cref="SquareAsync"/> is settled once for each
    /// artist per session. So if an artist's first artwork answer had no squares (for example, because
    /// fanart.tv was down, or the answer was partial), that artist keeps a wide photo in the band and in
    /// the pager until the app is restarted. The track pane shows the same wide photo, so the two surfaces
    /// agree.</para>
    /// </summary>
    public static bool PagesSquares(string? opened, IReadOnlyList<string> squares)
        => !string.IsNullOrWhiteSpace(opened)
           && squares.Contains(opened, StringComparer.OrdinalIgnoreCase);

    /// <summary>The artist's photographs to choose between: the order the app has saved for them, or the
    /// catalogues' if nothing is saved, with one entry per picture.</summary>
    private static async Task<Asked<IReadOnlyList<string>>> CandidatesAsync(
        string artist, string? album, CancellationToken ct)
    {
        var candidates = SavedBackdrops(artist);
        if (candidates.Count == 0)
        {
            if (!(await App.Services.GetRequiredService<ArtworkService>().GetAsync(artist, album, ct: ct))
                    .Answers(out var art, out var why))
                return Asked.Failed<IReadOnlyList<string>>(why);
            candidates = art.Backgrounds;
        }

        // The same picture can reach this list twice. The catalogues mirror each other, so one photograph
        // can come from fanart.tv and again from theaudiodb, with the same file name on a different host.
        // If it is left in, it uses a place in the list, and only the first few places are ever looked at.
        // For one real artist, two of the first four candidates were the same picture.
        return Asked.Answer(OnePerPicture(candidates));
    }

    /// <summary>
    /// Whether this picture can be MEASURED. That is, whether a fresh request for it would leave this
    /// process at all.
    ///
    /// <para>Only the API may send a request to a catalogue host (see <c>CatalogueReach</c>). Some of the
    /// pictures that the art sources return are on such a host. A probe of such an address is refused
    /// before a socket is opened. That causes a first-chance exception, and no measurement is made.</para>
    ///
    /// <para><b>Measuring a picture is not the same question as showing it.</b> The cache sits over the
    /// gate. So a picture that is already on this disk is served without opening any socket, even if its
    /// host is refused. If these pictures were dropped from the candidates, an artist whose only
    /// photographs come from a catalogue host would lose the picture that the app already draws. So they
    /// stay in the list and are not measured. The ranking below already handles a candidate with no shape:
    /// it keeps the order it arrived in.</para>
    /// </summary>
    private static bool Measurable(string url)
        => !Uri.TryCreate(url, UriKind.Absolute, out var address)
           || !RPM.Fingerprint.External.CatalogueReach.Refuses(address);

    /// <summary>An artist's photographs, in the order that suits one surface's shape. It also says whether
    /// that order is a ranking, or just the order the photographs arrived in.</summary>
    public sealed record Framing(IReadOnlyList<string> Urls, bool Ranked);

    /// <summary>
    /// An artist's photographs in the order that suits a given shape, best first. The home screen's banner
    /// asks for this. The order is not the one that <see cref="ChooseAsync"/> settles on.
    ///
    /// <para><b>Why the two differ.</b> <see cref="ChooseAsync"/> scores candidates against one ideal shape
    /// only. Its answer is "which photograph IS this artist's", and every surface shares that question. A
    /// banner four times as wide as it is tall, with a cover and a title down its left side, needs the
    /// widest photograph whose subject is clear of them. That can be the fourth photograph in the artist's
    /// list.
    /// <see cref="BackdropRanker"/> already asks that question for the now playing header. This is the same
    /// machinery, used for another scene.</para>
    ///
    /// <para><b>This returns a list, not one answer, and that matters.</b> If it returned one URL, the
    /// first picture could be one that this process may not fetch and does not have cached. The card would
    /// then get nothing, while other photographs of the artist went unused. The caller walks the list and
    /// keeps the first picture that actually arrives.</para>
    ///
    /// <para><b>When nothing is measurable, there is no ranking.</b> The ranker's first question is shape,
    /// and the shape is read from the file. If all of an artist's photographs are on a host this process
    /// may not reach (see <see cref="Measurable"/>), they cannot be ranked here. Asking anyway is a refusal
    /// for each one. The order is then whatever the saved order or the sources offered.
    /// <see cref="Framing.Ranked"/> says so, so the caller can record that and stop asking.</para>
    ///
    /// <para><b>The number is capped, because ranking costs downloads.</b> The preselector measures every
    /// candidate it is given. Each one costs a fetch, and the fetch is kept afterwards. It also runs the
    /// face detector over the widest few. A decorative banner is not worth an artist's whole gallery, so it
    /// is handed at most <paramref name="most"/> photographs, and no more.</para>
    /// </summary>
    /// <returns>Answered with the order, or unanswered where the artwork lookup did not answer. The
    /// caller writes an answered order to disk (BannerOrders), so a failure must not come back as an
    /// empty one.</returns>
    public static async Task<Asked<Framing>> BestForAsync(
        string artist, string? album, ArtScene scene, int most, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return Asked.Answer(new Framing([], false));

        // The saved order is used as the shortlist to rank, not as the answer. It holds this artist's
        // photographs, already matched with what the sources still offer and with what the user has hidden.
        // Ranking this list costs no extra candidates.
        IReadOnlyList<string> candidates;
        if (SavedBackdrops(artist) is { Count: > 0 } saved)
            candidates = saved;
        else if (!(await CandidatesAsync(artist, album, ct)).Answers(out var asked, out var why))
            return Asked.Failed<Framing>(why);
        else
            candidates = asked;

        if (candidates.Count < 2 || ct.IsCancellationRequested)
            return Asked.Answer(new Framing(candidates, false));

        var askable = candidates.Take(Math.Max(2, most)).Where(Measurable).ToList();
        if (askable.Count < 2)
            return Asked.Answer(new Framing(candidates, false));

        try
        {
            var ranked = await App.Services.GetRequiredService<BackdropPreselector>()
                                  .RankAsync(askable, scene, ct);

            if (ranked is { Count: > 0 })
            {
                // The ranked ones first, then whatever could not be measured, in the order it arrived.
                var order = ranked.Select(r => r.Url).ToList();
                order.AddRange(candidates.Where(url => !order.Contains(url, StringComparer.Ordinal)));
                return Asked.Answer(new Framing(order, true));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed ranking does not mean the artist has no photographs. The source's own order is used.
            // That is the order that the surfaces use when there is no ranking.
        }

        return Asked.Answer(new Framing(candidates, false));
    }

    private static string Settle(string artist, string url)
    {
        lock (Settled)
        {
            Settled[artist] = url;
        }
        return url;
    }

    /// <summary>
    /// The artist's photographs in the order the app has saved for them, or nothing where none has been
    /// saved yet. Hidden ones are the ones the user has taken out of the carousel, and they stay out of
    /// this list too.
    /// </summary>
    public static IReadOnlyList<string> SavedBackdrops(string artist)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen)
            return [];

        // If the read gives no answer, the result is treated as nothing saved, and the service answers
        // instead. A refusal has already shown the problem screen over the app. A closed mirror is followed
        // by a Changed event, which reads the new one.
        return library.Read(library.Db, "an artist's saved photos", db => db.GetArtistArt(artist))
            .Match<IReadOnlyList<string>>(
                saved => saved?.Backdrops
                    .Where(b => !b.Hidden)
                    .OrderBy(b => b.Position)
                    .Select(b => b.Url)
                    .ToList() ?? [],
                _ => []);
    }

    /// <summary>
    /// The same photographs with the one the app has SETTLED on at the front.
    ///
    /// <para>For a surface that pages through an artist's pictures. It opens on the picture that everything
    /// else in the app shows for that artist. So the first picture shown is the one already on screen, and
    /// page one is not a jump. The chosen picture is put at the front, even when the list passed in does
    /// not contain it. A saved order and a fresh answer from the sources can disagree. A carousel whose
    /// first page is not the picture it opened on can never be paged back to that picture.</para>
    /// </summary>
    public static IReadOnlyList<string> ShownFirst(string artist, IReadOnlyList<string> urls)
    {
        lock (Settled)
        {
            return Settled.TryGetValue(artist, out var shown)
                ? WithFirst(urls, shown)
                : urls;   // Nothing is chosen yet, so the list keeps its own order.
        }
    }

    /// <summary>The same move, with the picture to lead on given as a parameter, instead of looked up. This
    /// is where the rule is easiest to read and test. Null, or a blank value, leaves the list exactly as it
    /// came.</summary>
    public static IReadOnlyList<string> WithFirst(IReadOnlyList<string> urls, string? shown)
    {
        if (string.IsNullOrWhiteSpace(shown))
            return urls;

        var at = -1;
        for (var i = 0; i < urls.Count; i++)
        {
            if (string.Equals(urls[i], shown, StringComparison.OrdinalIgnoreCase))
            {
                at = i;
                break;
            }
        }

        if (at == 0)
            return urls;

        var moved = urls.ToList();
        if (at > 0)
            moved.RemoveAt(at);
        moved.Insert(0, shown);
        return moved;
    }

    /// <summary>Three wide by four tall, as a width over a height. This is the shape an upright strip
    /// needs.</summary>
    private const double PortraitShape = 3.0 / 4.0;

    /// <summary>The number of an artist's photographs that are measured before one is chosen. Each one
    /// costs a fetch. The fetch is what the measurement is for, and the bytes are kept afterwards, so
    /// nothing is downloaded twice. This counts four DISTINCT pictures, because duplicates are dropped
    /// before the count is taken.</summary>
    private const int PhotosConsidered = 4;

    /// <summary>The penalty applied to the window's own picture. It is roughly the difference between a
    /// squarish photograph and a slightly wide one. It is enough to break a tie, but not enough to prefer a
    /// television-shaped picture over an upright one.</summary>
    private const double FirstPhotoPenalty = 0.12;

    /// <summary>One entry per picture, however many catalogues carry it. Pictures are compared by file
    /// name. The mirrors keep the file name, and the host is what they change.</summary>
    public static IReadOnlyList<string> OnePerPicture(IReadOnlyList<string> urls)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<string>(urls.Count);

        foreach (var url in urls)
        {
            var slash = url.LastIndexOf('/');
            var name = slash >= 0 && slash < url.Length - 1 ? url[(slash + 1)..] : url;

            if (seen.Add(name))
                kept.Add(url);
        }

        return kept;
    }
}
