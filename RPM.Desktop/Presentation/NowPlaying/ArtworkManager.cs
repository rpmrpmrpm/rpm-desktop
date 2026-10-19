using RPM.Core.Art;
using RPM.Core.Library;
using RPM.Core.Metadata.Providers;
using RPM.Core.Threading;
using RPM.Desktop.Services;
using Microsoft.UI.Xaml.Media.Imaging;

namespace RPM.Desktop.Presentation.NowPlaying;

/// <summary>A track the art can be asked to show. Its <see cref="Path"/> is its identity: two requests for
/// the same file are the same request. Title, artist and album may be blank (a queue track playing from
/// disk). In that case the manager reads them from the file.</summary>
/// <param name="CoverUrl">Cover art to
/// fetch when the path has none of its own, such as a radio station's logo. It is ignored when the file
/// itself carries art, which is always the better picture.</param>
/// <param name="Movement">The movement this
/// file is, when it is one of a work. The two are separate tags, and the header prints them on separate
/// lines. Null for anything that is not part of a composition, which is most music. The header then looks
/// as it always did.</param>
public readonly record struct TrackRef(
    string? Title, string? Artist, string? Album, string? Path, string? CoverUrl = null,
    string? Work = null, string? Movement = null);

/// <summary>A decoded cover, ready to crossfade in. The colours are read from the same bytes, once, on the
/// same pass that decodes them: <paramref name="Pressing"/> is what its record is pressed in,
/// <paramref name="Tint"/> the sleeve's dominant tone and <paramref name="Glow"/> its vivid accent. Any of
/// them null means the sleeve has no colour to give. The disc is then black, and whoever wanted the palette
/// (the sleeve visualizer) falls back to the app's own. See
/// <see cref="VinylPressing.PaletteFromArt"/>.</summary>
/// <param name="Ground">A made picture to stand the
/// art on, where the art is a mark with nothing behind it, such as a radio station's logo published as a
/// transparent PNG. Null for most covers, which are opaque rectangles and are their own background. See
/// <see cref="SoftFigure.Tile"/>.</param>
/// <param name="Square">The same picture decoded for the SMALL
/// squares: the header's sleeve and the visualizer's badge, neither of which is ever more than a couple of
/// inches across.
///
/// <para>This is a second decode of bytes already in hand, because one bitmap cannot serve both ends of
/// this app. <see cref="Bitmap"/> is sized for the stage, where the sleeve is half the window high. Drawn
/// into a 156-point square, it is reduced about five times at draw time. The renderer does that with four
/// samples per pixel and no averaging, which leaves grain across every photograph and gravel through
/// lettering. Decoding small is one pass on a worker, once per track. See
/// <see cref="RemoteImage.DecodeAsync(byte[], int)"/>, which is this work.</para>
///
/// <para>Null only where the small decode failed. The square then falls back to the big picture, which is
/// still a cover.</para></param>
/// <param name="Width">The picture's own pixels, read off the bytes rather
/// than asked of the bitmap. A <see cref="BitmapImage"/>'s PixelWidth is only filled in once the platform
/// has opened it. A mark capped at a width of zero would be invisible.</param>
/// <param name="Badge">The mark
/// and its ground composed into one square picture, for the surfaces that can hold only one, such as the
/// record's label. Null for a sleeve, and for a logo that is its own background: neither has two pictures
/// to reconcile. See <see cref="TransparentMark.Badge"/>.</param>
/// <param name="Mark">Whether this is a
/// station's own logo rather than a record's sleeve. The picture cannot say this: an opaque logo and an
/// opaque sleeve are the same bytes. It decides whether a shape that is not the square's may be cropped to
/// fit. See <see cref="Fills"/>.</param>
public sealed record LoadedCover(
    BitmapImage Bitmap,
    Windows.UI.Color? Pressing = null,
    Windows.UI.Color? Tint = null,
    Windows.UI.Color? Glow = null,
    Windows.UI.Color? Shade = null,
    BitmapImage? Ground = null,
    int Width = 0,
    int Height = 0,
    bool Mark = false,
    BitmapImage? Square = null,
    BitmapImage? Badge = null)
{
    /// <summary>
    /// Whether this picture fills its square edge to edge, or is fitted whole inside it.
    ///
    /// <para><b>A sleeve fills, whatever shape it is.</b> It is a rectangle of picture. The crop takes a
    /// margin, and bars down the sides of the one square on screen that is meant to be a record look worse
    /// than the margin lost.</para>
    ///
    /// <para><b>A station's logo never fills.</b> A logo is a drawing with its own margins. A broadcaster
    /// who draws the mark out to the edges of a square file means it to be seen out to those edges. A
    /// rounded tile would take them off. For example, the R in one station's logo lost its own shoulders
    /// that way. So a mark is fitted whole and stands in the middle of the square with air around it,
    /// whatever the shape of the file. A sleeve still fills, because a sleeve is a rectangle of picture,
    /// not a drawing with a margin.</para>
    ///
    /// <para><b>A mark with nothing behind it never fills either.</b> It has a ground made for it to stand
    /// on. A transparent PNG run out to the corners of that ground would be a badge with no room.</para>
    /// </summary>
    public bool Fills => Ground is null && !Mark;
}

/// <summary>The album's disc art, or <see cref="Disc"/> null for "no disc art: draw the record". What that
/// picture is a picture of travels with it. See <see cref="ScannedDisc"/>, because a CD and a vinyl label
/// are worn differently.</summary>
public sealed record LoadedDisc(ScannedDisc? Disc);

/// <summary>
/// A backdrop decoded whole, in TWO copies of the one photograph.
///
/// <para><see cref="Wash"/> is the picture blurred down to its gradients only. It covers the whole window,
/// and it is the ground the app stands on. <see cref="Composite"/> is the framed picture for the header
/// band, with every edge dissolve baked in, and it blends into that wash. Two copies of the same photograph
/// make the background agree with the artwork by construction. No rule is needed for picking a colour out
/// of the picture. What the library stands on is the picture itself.</para>
///
/// <para>The record also carries the colours and the window the swap needs. Everything
/// <see cref="ArtDisplay.ShowBackdrop"/> puts up happens in one synchronous step. So the swap never waits,
/// and the whole backdrop fades as one.</para>
/// </summary>
/// <param name="WashWindow">The window the <see cref="Wash"/> was drawn for. It is not always the window
/// the <see cref="Composite"/> was drawn for, because a wash can outlive several bakes while it is still in
/// register (see ArtDisplay.Registers). The wash is sized on screen by this window, so it must travel with
/// the wash.</param>
/// <param name="Place">Where in the photograph this bake's picture was cut from, and how
/// big it is drawn. It travels with the artwork for the same reason as <paramref name="Baked"/>. The stage
/// compares it with the placement already on screen. This tells a re-fit of one framing (the same picture
/// at the same scale, reaching further left or less far) from a real re-framing. Only a real re-framing is
/// worth a dissolve. See <c>ArtDisplay.RefitPicture</c>.</param>
public sealed record LoadedBackdrop(
    string Url, BitmapImage Composite, BitmapImage? Wash, double Veil,
    Windows.UI.Color Ground, Windows.UI.Color Scrim, CropRect Window, BakedGeometry Baked,
    Windows.Foundation.Size WashWindow, ArtDisplay.PicturePlacement Place);

/// <summary>
/// The geometry a backdrop was BAKED for: the slot it was composited into, the band it was framed to, and
/// the window its wash was registered against.
///
/// <para>It travels with the artwork because a load is asynchronous. It involves a download, a saliency
/// pass, a face pass and two renders, and the header can move while it is in flight. The stage must compare
/// the live geometry with the geometry the pictures were made for, not with the geometry at the moment they
/// landed. If the latter were recorded, the picture would be stale and nothing would refresh it. It would
/// look like a picture that stops short of the band with a line across its foot, and it would never repair
/// itself, because the recrop timer would see no change.</para>
/// </summary>
public readonly record struct BakedGeometry(double SlotWidth, double Band, double WindowWidth, double WindowHeight)
{
    /// <summary>Whether <paramref name="now"/> has drifted far enough from this to be worth baking again.
    /// The comparison is relative, and it runs on every axis. The wash follows the WINDOW, and a shape-only
    /// comparison cannot see that at all. A window made taller changes no aspect, but it changes every wash
    /// pixel.</summary>
    public bool Drifted(BakedGeometry now, double tolerance)
        => Moved(SlotWidth, now.SlotWidth, tolerance)
        || Moved(Band, now.Band, tolerance)
        || Moved(WindowWidth, now.WindowWidth, tolerance)
        || Moved(WindowHeight, now.WindowHeight, tolerance);

    private static bool Moved(double was, double now, double tolerance)
        => was > 0 && Math.Abs(now - was) / was >= tolerance;
}

/// <summary>
/// The photograph the placement window is about to work on: which picture, whose artist it is filed under,
/// and how it is framed at this moment. The framing is the one somebody placed by hand if there is one.
/// Otherwise it is the chooser's own answer, or nothing, and the window then opens on the whole picture.
/// </summary>
/// <param name="Placed">Whether <paramref name="Framing"/> is a framing somebody made, rather than the
/// chooser's. It decides whether the window offers to put the app's placement back.</param>
public readonly record struct ArtFramingSubject(string Url, string Artist, ArtFraming? Framing, bool Placed);

/// <summary>What the flip and refresh buttons and the recrop timer forward to: the manager. The stage takes
/// input and does not decide.</summary>
public interface IArtController
{
    void Step(int delta);
    void Refresh();
    void NotifyReshaped();

    /// <summary>The listener turned the header's artist photograph on or off (Settings, Display). The track
    /// that is up is decided again from the setting as it now stands. There is no refetch when it comes
    /// back on, because the photographs that were dropped are still in hand.</summary>
    void NotifyArtistPhotosChanged();

    /// <summary>A picture the person chose from their own computer, to be this artist's artwork from now
    /// on. The stage opens the file picker, since that is UI. What the choice means (copy it in, put it at
    /// the front, keep it there) is the manager's decision, like every other decision about the
    /// art.</summary>
    void UseImage(string path);

    /// <summary>
    /// Takes the picture on the header off this artist: the wrong image, chosen by hand or offered by a
    /// source. It is removed from the carousel and not offered again.
    ///
    /// <para>This is the picture that is up now. No argument says which one. The person is looking at the
    /// one they mean. A URL passed in from the menu could be the one that was up when the menu opened, and
    /// a track change may have replaced it since.</para>
    /// </summary>
    void RemoveImage();

    /// <summary>
    /// What the placement window needs to open. It is null when there is nothing on the header to place:
    /// nothing playing, no artist to file it under, or no photograph up.
    /// </summary>
    ArtFramingSubject? FramingSubject();

    /// <summary>
    /// Puts a hand-made framing on a photograph and remembers it against its artist.
    /// <paramref name="framing"/> null hands the picture back to the chooser. The framing is forgotten
    /// rather than stored as a special value, because "the app places it" is not a placement.
    ///
    /// <para><paramref name="subject"/> names the picture the framing was made for, and this matters. The
    /// placement window can stay open across a track change. So the photograph on the header when the
    /// person presses save is not always the one they were placing.</para>
    /// </summary>
    void Frame(ArtFramingSubject subject, ArtFraming? framing);
}

/// <summary>
/// The stage the manager drives: the layers and the primitives, and nothing about what to show. It is split
/// in two on purpose. The <c>Load</c> and <c>Decode</c> half is awaitable and cancellable, and touches
/// nothing visible. The <c>Show</c> half is synchronous. It must be called only under the manager's current
/// generation, so two swaps can never interleave.
/// </summary>
internal interface IArtStage
{
    /// <summary>The live obstruction geometry, for the chooser and the crop.</summary>
    ArtScene Scene { get; }

    void SetText(string? title, string? artist, string? album, string? work = null, string? movement = null);
    void SetLoading(bool on);
    void SetCarousel(int index, int count);
    void EnterTrack();
    void EnterIdle();

    Task<byte[]?> LoadLocalCoverAsync(string filePath, CancellationToken ct);
    Task<Asked<byte[]?>> LoadRemoteCoverAsync(string url, CancellationToken ct);

    /// <summary>
    /// The tuned station's mark, found the long way. It is null when no station is on.
    ///
    /// <para>This is here, and not left to <c>LoadRemoteCoverAsync</c>, because the address that the source
    /// names is only one of the addresses a station has. <c>RadioStreamSource</c> reports
    /// <c>station.Favicon</c>, which is the single logo the fold ranked highest. That is often the
    /// apple-touch icon: flattened onto an opaque tile by the platform it was drawn for, and 180 pixels at
    /// most. The walk behind this reads everything the directory filed, and the station's own manifest, and
    /// prefers a mark with nothing behind it.</para>
    /// </summary>
    Task<Asked<byte[]?>> TunedStationMarkAsync(CancellationToken ct);
    /// <param name="mark">Whether these bytes are a station's LOGO rather than a record's sleeve. The
    /// caller states it, and it is never guessed. An opaque logo and an opaque sleeve are the same picture,
    /// and only the caller knows which one it wants. The answer decides whether a ground is made to stand
    /// the picture on, and whether the two are composed into a badge (see <see cref="LoadedCover.Badge"/>).
    /// It must be known before those decisions are made, so it is passed in and not written onto the answer
    /// afterwards.</param>
    Task<LoadedCover?> DecodeCoverBytesAsync(byte[] bytes, bool mark, CancellationToken ct);
    Task<LoadedDisc?> LoadLocalDiscAsync(string filePath, CancellationToken ct);

    /// <summary>The same picture from anywhere else it can come from. Today that is the computer that holds
    /// a borrowed record. It is cut and decoded exactly as the folder's own is, so a borrowed disc is the
    /// same artefact as an owned one.</summary>
    Task<LoadedDisc?> DecodeDiscBytesAsync(byte[]? bytes, CancellationToken ct);
    /// <summary>The framing is always stated, never defaulted. A load that forgot it would put the
    /// chooser's window back over a picture that somebody placed by hand. A null framing is the statement
    /// that the chooser frames it. That is what nearly every picture wants.</summary>
    Task<Asked<LoadedBackdrop?>> LoadBackdropAsync(string? url, ArtFraming? framing, CancellationToken ct);

    /// <summary>How the chooser framed a photograph the last time it was baked, for the placement window to
    /// open on and to reset back to. Null when it has not been baked this session.</summary>
    ArtFraming? AutomaticFraming(string? url);

    /// <summary>A picture the person has just chosen from their own computer is up. The stage opens the
    /// placement window on it. A chosen photograph was framed by a chooser that has never seen it in this
    /// header, and the moment somebody picks one is the moment they mean to place it. This is only called
    /// for a chosen picture. The photographs from the sources open framed and stay that way until somebody
    /// asks.</summary>
    void OfferPlacement();

    void ShowCover(LoadedCover? cover);
    void ShowDisc(LoadedDisc disc);
    void ShowBackdrop(LoadedBackdrop art);
    void ClearBackdrop();
}

/// <summary>
/// The one thing in charge of the displayed artwork.
///
/// Everything that could change what the strip shows comes here first: a track starting to play, the flip
/// arrows, the refresh button, and the panel being resized. The manager decides which request wins, runs
/// the fitting algorithms (the chooser and the crop), and drives the stage's crossfades. All of this runs
/// on one serialized path, so nothing races.
///
/// <b>Playback is the only cue.</b> The strip shows the track loaded in the player: its cover, its disc,
/// and its artist's backdrop. It goes idle when playback stops. Library selection never touches the art.
/// Browsing a large collection is not listening.
///
/// <b>No stale art.</b> Each request bumps a generation and cancels the one in flight, including its
/// download. The pipeline checks the generation immediately before every synchronous swap, with no await in
/// between. So a slow load for a track the user has left can never land after a newer one and fade in a
/// previous artist's photo.
/// </summary>
internal sealed class ArtworkManager : IArtController
{
    private readonly IArtStage _stage;
    private readonly ArtworkService? _artwork;
    private readonly LibraryService? _library;
    private readonly BackdropPreselector? _preselector;
    private readonly Func<bool> _headerArtistPhoto;

    /// <param name="headerArtistPhoto">Whether the header wears a photograph of the artist. Asked rather
    /// than handed over, and asked again on every show: it is a standing preference the listener can change
    /// mid-track, so a value taken at wiring time would be the answer as it was when the app started. A
    /// question rather than the settings object because that is the whole of what this needs from it, and
    /// it is what lets a test say "off" without a store to say it in.</param>
    public ArtworkManager(IArtStage stage, ArtworkService? artwork, LibraryService? library,
        BackdropPreselector? preselector, Func<bool> headerArtistPhoto)
    {
        _stage = stage;
        _artwork = artwork;
        _library = library;
        _preselector = preselector;
        _headerArtistPhoto = headerArtistPhoto;
    }

    // What wants to be shown.

    private TrackRef? _playing;
    private TrackRef? _displayed;

    /// <summary>The track loaded in the player, or null when nothing is. This is the strip's one
    /// cue.</summary>
    public void SetPlaying(TrackRef? track)
    {
        _playing = track;
        Reconcile();
    }

    private void Reconcile()
    {
        var next = _playing;
        if (Same(next, _displayed))
            return;

        _displayed = next;
        StartShow(next, forceRefresh: false);
    }

    /// <summary>
    /// Whether two requests are asking for the same picture. Path alone is not enough. A radio station
    /// keeps one URL from the moment it is tuned in until it is switched off. But the song it is playing
    /// changes every few minutes, and so does the artist whose backdrop belongs behind it.
    /// </summary>
    private static bool Same(TrackRef? a, TrackRef? b) =>
        a?.Path == b?.Path && a?.Title == b?.Title && a?.Artist == b?.Artist;

    // The serialized, generation-guarded pump.

    private int _gen;
    private CancellationTokenSource? _cts;
    private Task _pump = Task.CompletedTask;

    private int Begin()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        return ++_gen;
    }

    /// <summary>Chains an operation behind whatever is running, then runs it only if it is still the newest
    /// request. Serializing means the previous pipeline, which is already cancelled, stops at its next
    /// generation check before this one begins. So the swap order equals the request order.</summary>
    private void Enqueue(Func<int, CancellationToken, Task> op)
    {
        var gen = Begin();
        var ct = _cts!.Token;
        var prev = _pump;
        _pump = RunAsync();

        async Task RunAsync()
        {
            try { await prev; } catch { /* the previous op's failure is its own */ }
            if (gen != _gen)
                return;   // superseded while queued
            try { await op(gen, ct); }
            catch (OperationCanceledException) { /* newer request took over */ }
            catch (Exception ex) { RPM.Core.Diagnostics.Faults.Note("art", "showing the artwork", ex); }
        }
    }

    private void StartShow(TrackRef? track, bool forceRefresh)
        => Enqueue((gen, ct) => track is { } t ? RunShowAsync(t, forceRefresh, gen, ct) : ShowIdleAsync(gen));

    /// <summary>Bypasses the caches for the current track and runs the chooser again. A photo added
    /// upstream appears without waiting for the 30-day cache to expire.</summary>
    public void Refresh()
    {
        if (_displayed is { } d)
            StartShow(d, forceRefresh: true);
    }

    /// <summary>Steps the carousel: a different backdrop for the same artist, no refetch, no
    /// re-rank.</summary>
    public void Step(int delta) => Enqueue((gen, ct) => RunStepAsync(delta, gen, ct));

    /// <inheritdoc/>
    public void UseImage(string path) => Enqueue((gen, ct) => RunUseImageAsync(path, gen, ct));

    /// <inheritdoc/>
    public void RemoveImage() => Enqueue((gen, ct) => RunRemoveImageAsync(gen, ct));

    /// <inheritdoc/>
    public void Frame(ArtFramingSubject subject, ArtFraming? framing)
        => Enqueue((gen, ct) => RunFrameAsync(subject, framing, gen, ct));

    /// <inheritdoc/>
    public ArtFramingSubject? FramingSubject()
    {
        if (CurrentBackdrop() is not { } url || _backdropArtist is not { Length: > 0 } artist)
            return null;   // nothing playing, no artist to file it under, or no photograph up

        // The framing the window opens on, best first: the framing somebody placed, else what the chooser
        // made of this picture when it was last baked, else nothing. With nothing, the window opens on the
        // whole photograph, which is the only honest answer when neither is known.
        var placed = FramingFor(url);
        return new ArtFramingSubject(url, artist, placed ?? _stage.AutomaticFraming(url), placed is not null);
    }

    /// <summary>The strip changed shape enough that the current backdrop must be cut again for the new
    /// aspect.</summary>
    public void NotifyReshaped() => Enqueue((gen, ct) => RunReshapeAsync(gen, ct));

    /// <inheritdoc/>
    public void NotifyArtistPhotosChanged() => StartShow(_displayed, forceRefresh: false);

    // The pipeline.

    private IReadOnlyList<string> _backdrops = [];
    private int _backdropIndex;

    private string? CurrentBackdrop()
        => _backdrops.Count == 0 ? null : _backdrops[Math.Clamp(_backdropIndex, 0, _backdrops.Count - 1)];

    private async Task RunShowAsync(TrackRef track, bool forceRefresh, int gen, CancellationToken ct)
    {
        // The cloud library row for a track with no local file. It is read once and used twice: for the
        // tags that the disk cannot give, and later for the album key and the ID that its stored sleeve is
        // filed under.
        LibraryTrack? cloudRow = null;
        if (!string.IsNullOrEmpty(track.Path) && !File.Exists(track.Path))
        {
            cloudRow = await Task.Run(() => CloudRowFor(track.Path!));
            if (gen != _gen) return;
        }

        // A borrowed record's disc art is on the computer that holds it. There is no folder on this disk to
        // read it from. The request starts as soon as the row names that computer. It is awaited below,
        // next to the folder's own read, so it runs together with the cover's request.
        //
        // It starts before the record is dressed. This is required, not an optimisation. The disc is part
        // of the record's face. A face goes on once, and it must go on where nobody can see it change (see
        // RecordChange). Fetching the disc after the record is down would mean taking the record off again
        // to put its own disc on, in front of the listener.
        var borrowedDisc = cloudRow is { PeerDeviceId.Length: > 0 } discRow
            ? BorrowedDiscAsync(discRow, ct)
            : null;

        // A queue track plays straight off disk with no library row, so its tags are the only thing to go
        // on. Read them once, off the UI thread, when they were not handed in. File.Exists is checked, not
        // just whether there is a path. A radio station's path is its stream URL. A tag reader pointed at
        // one either throws or downloads the broadcast looking for an ID3 header.
        if (string.IsNullOrWhiteSpace(track.Artist) && !string.IsNullOrEmpty(track.Path))
        {
            if (File.Exists(track.Path))
            {
                var info = await Task.Run(() => RPM.Core.Metadata.TagReader.Read(track.Path!));
                if (gen != _gen) return;
                track = track with
                {
                    Title = string.IsNullOrWhiteSpace(track.Title) ? info.Title : track.Title,
                    Artist = info.Artist,
                    Album = info.Album,
                    Work = info.Work,
                    Movement = info.MovementName,
                };
            }
            // No file to read, but a path this library knows: a track that lives only in the cloud. Its row
            // carries the same tags the file would have. Without this, nothing that follows has anything to
            // work with. No artist means no backdrops, and no album means the stored sleeve below is never
            // requested. A streamed record would then play under the placeholder on an empty stage.
            else if (cloudRow is { } row)
            {
                track = track with
                {
                    Title = string.IsNullOrWhiteSpace(track.Title) ? row.Title : track.Title,
                    Artist = row.Artist,
                    Album = row.Album,
                    Work = row.Work,
                    Movement = row.MovementName,
                };
            }
        }

        _stage.EnterTrack();
        _stage.SetText(track.Title, track.Artist, track.Album, track.Work, track.Movement);
        _stage.SetLoading(true);
        try
        {
            // The cover is local-first: the file's own embedded art (or its folder image) is the exact
            // album art and needs no network or sign-in.
            var localBytes = string.IsNullOrEmpty(track.Path)
                ? null
                : await _stage.LoadLocalCoverAsync(track.Path!, ct);
            if (gen != _gen) return;

            // Nothing is on disk because there is no file. The record is only in the cloud. Its sleeve is
            // stored there too, per album. It is the user's own cover. That suits a bootleg or a home
            // recording, where a lookup by artist and album finds nothing.
            if (localBytes is null && !string.IsNullOrWhiteSpace(track.Album)
                && !string.IsNullOrEmpty(track.Path) && !File.Exists(track.Path))
            {
                // The row read at the top says which computer holds the record, and carries the album
                // artist its cover is keyed under. The stream itself only knows what the tags on the deck
                // say. The two fallbacks cover that.
                localBytes = cloudRow is { } sleeveRow
                    ? await RemoteSleeve.ForAsync(sleeveRow, track.Artist, track.Album, ct)
                    : await App.Services.GetRequiredService<CloudArtService>()
                        .CoverAsync(null, track.Artist, track.Album, null, ct);
                if (gen != _gen) return;
            }

            // Nothing on disk, but the source named a picture: a radio station's logo. It is the closest
            // thing to a sleeve that a broadcast has. Without it, the header stays on the empty placeholder
            // for the whole time the radio is on.
            //
            // The bytes cannot say that they are a logo, but everything downstream needs to know. It
            // decides whether a ground is made to stand the picture on, whether it may be cropped to the
            // square, and whether the record's label gets the two composed into a badge. It is recorded
            // where the bytes are taken, because this branch is the source of that fact. Anywhere further
            // down, it would be a guess about where the bytes came from.
            //
            // A station played through the deck gets this flag, and so does a station tuned in with its
            // details already known. Both paths must agree, so the same logo is never framed two ways in
            // one app.
            var mark = false;
            if (localBytes is null && track.CoverUrl is { Length: > 0 } coverUrl)
            {
                // The station's own walk comes first. The address the source named is used only when there
                // is no station on, which is the case for anything else that sets a cover URL.
                //
                // The named address is one of several the station has. The source reports station.Favicon,
                // the highest-ranked single logo, which is usually the apple-touch icon. The platform
                // flattens that onto an opaque background. So the header showed a white tile with a small
                // mark in the middle, while the list beside it showed the mark on a proper ground. The walk
                // makes both show the same picture.
                //
                // The branch in ArtDisplay (ShowEmptyMark) does not cover this case. It only runs when the
                // square is empty, and the named address had already filled it. One picture was enough to
                // stop the app looking for the better one.
                //
                // If either lookup fails, it is the same as having nothing here. The square keeps its
                // placeholder for this track, nothing is kept, and the next track asks again. The station
                // walk itself does not keep a walk that met a failure (see
                // RadioStationItem.LogoBytesAsync).
                localBytes = (await _stage.TunedStationMarkAsync(ct)).Match(found => found, _ => null)
                             ?? (await _stage.LoadRemoteCoverAsync(coverUrl, ct)).Match(found => found, _ => null);
                mark = localBytes is { Length: > 0 };
                if (gen != _gen) return;
            }

            var localCover = localBytes is null
                ? null
                : await _stage.DecodeCoverBytesAsync(localBytes, mark, ct);
            if (gen != _gen) return;

            // The album's own record, read BEFORE the sleeve goes up and before any lookup. It comes from
            // the folder (disc.png), so it never waits on the chooser.
            //
            // It is read first because of what the sleeve does when it lands. A new album takes the
            // header's record off the deck, changes its face and puts it back. The disc is part of that
            // face. If it were read after the artist lookup, it would arrive after the record was back
            // down. A CD cover turning into a vinyl label would then be a two-second dissolve on screen.
            var disc = string.IsNullOrEmpty(track.Path) ? null : await _stage.LoadLocalDiscAsync(track.Path!, ct);
            if (gen != _gen) return;

            // The borrowed disc, started above. It is used when the local folder had no disc art, which is
            // always the case for a borrowed record. Null is the usual answer: most albums have no disc
            // art, and then the drawn record keeps turning, as it always does.
            if (disc is not { Disc: not null } && borrowedDisc is not null)
            {
                if (await borrowedDisc is { Length: > 0 } borrowedBytes)
                    disc = await _stage.DecodeDiscBytesAsync(borrowedBytes, ct);
                if (gen != _gen) return;
            }

            // Sleeve first, then the disc, and never the other way round. The sleeve tells the header that
            // the record has changed, so the disc must already be in hand by then.
            _stage.ShowCover(localCover);
            _stage.ShowDisc(disc ?? new LoadedDisc(null));

            // The photograph has been switched off in Settings. Nothing is asked of the catalogues, nothing
            // is ranked, and nothing is downloaded. The header stands on the record's own colours, which is
            // what it does for an artist with no photograph. The artist is not remembered either. With no
            // picture wanted, there is nothing to file a choice under, and the menu that would offer to
            // make one is greyed out.
            if (!_headerArtistPhoto())
            {
                SetBackdrops(null, []);
                _stage.ClearBackdrop();
                return;
            }

            if (_artwork is null || string.IsNullOrWhiteSpace(track.Artist))
            {
                // The artist is still remembered when there is one, even with nothing to show. A header
                // with no photograph is exactly where somebody wants to choose their own, and the artist is
                // the key that choice is filed under.
                SetBackdrops(string.IsNullOrWhiteSpace(track.Artist) ? null : track.Artist, []);
                _stage.ClearBackdrop();
                return;
            }

            // Who the header's photograph is OF. This is the credit as typed. That is the whole answer for
            // all but a few records.
            var artist = track.Artist!;

            // The lookup did not answer: offline, or the service failed. That says nothing about the
            // artist's photographs. So the saved order is shown as it is, and nothing is reconciled, ranked
            // or saved. A failure must not be read as "no photographs". Reconciling the saved order against
            // an empty list would write the order back with only the user's own pictures in it. A curated
            // order and its hidden photos would be lost to one failed lookup.
            if (!(await _artwork.GetAsync(artist, track.Album, forceRefresh: forceRefresh, ct: ct))
                    .Answers(out var art, out _))
            {
                if (gen != _gen) return;
                await ShowSavedOrderAsync(gen, artist, ct);
                return;
            }
            if (gen != _gen) return;

            // Nobody has photographed the credit as it is WRITTEN. It may still name somebody who has been
            // photographed. "Aretha Franklin, The Royal Philharmonic Orchestra" is a pairing that no
            // catalogue has, while the first name alone is in every catalogue. Before this step, a record
            // credited that way opened on an empty header, even though the app held pictures of the artist.
            //
            // The credit is asked first, and this is only ever the second guess. ArtistCredit is built for
            // that order. If it went first, the same rule would turn "Earth, Wind & Fire" into "Earth" and
            // put a stranger's photograph across the top of the window. Reaching this line is the evidence
            // that the catalogues do not know THIS one.
            //
            // From here, the lead name is the artist, not just the search term. The ranking, the saved
            // order and the user's own choice all file under it. So the picture for a guest appearance is
            // the same picture, in the same order, as when that artist plays alone.
            if (art.Backgrounds.Count == 0 && ArtistCredit.First(artist) is { } lead)
            {
                // The lead name's lookup did not answer. The credit's own answer stands, which is "no
                // photographs of the credit as written". That is the same as before this second guess.
                if ((await _artwork.GetAsync(lead, track.Album, forceRefresh: forceRefresh, ct: ct))
                        .Answers(out var leads, out _)
                    && leads.Backgrounds.Count > 0)
                {
                    art = leads;
                    artist = lead;
                }
                if (gen != _gen) return;
            }

            // The chooser decides which backdrop shows first. A saved order applies at once. When there is
            // none, or the photo set has changed, the photos are ranked before anything is shown. So the
            // first backdrop the user sees is already the best one.
            var (plan, stored) = PlanBackdrops(artist, art.Backgrounds);
            var order = plan.DisplayUrls;

            if (plan.NeedsRank)
            {
                // RankUrls, not DisplayUrls: the ranker judges the sources' order, with fanart.tv's photos
                // at the front where the app put them. It does not judge yesterday's saved order with
                // today's arrivals added to the end. That would bury the new photos past the ranker's
                // cutoff.
                var ranked = await RankAndSaveAsync(artist, plan.RankUrls, stored, ct);
                if (gen != _gen) return;
                if (ranked is { Count: > 0 })
                    order = ranked;
            }

            SetBackdrops(artist, order);

            var current = CurrentBackdrop();
            var backdrop = await _stage.LoadBackdropAsync(current, FramingFor(current), ct);
            if (gen != _gen) return;
            // No picture at the address clears the header; a picture that could not be fetched leaves
            // whatever is on it, because that is not an answer about this photograph.
            if (!backdrop.Answers(out var loaded, out _))
                return;
            if (loaded is null)
                _stage.ClearBackdrop();
            else
                _stage.ShowBackdrop(loaded);
        }
        finally
        {
            // Only the newest request owns the spinner. An older request that finishes or is cancelled must
            // not turn it off for the one that replaced it.
            if (gen == _gen)
                _stage.SetLoading(false);
        }
    }

    private async Task RunStepAsync(int delta, int gen, CancellationToken ct)
    {
        if (_backdrops.Count < 2)
            return;

        // Wraps: the arrows are a carousel, not a scrollbar with ends to get stuck against.
        _backdropIndex = (_backdropIndex + delta + _backdrops.Count) % _backdrops.Count;
        _stage.SetCarousel(_backdropIndex, _backdrops.Count);

        var url = CurrentBackdrop();
        var backdrop = await _stage.LoadBackdropAsync(url, FramingFor(url), ct);
        if (gen != _gen) return;
        if (backdrop.Match(found => found, _ => null) is not { } loaded || url is null)
            return;   // no picture, or not fetched: the header keeps what it has either way

        _stage.ShowBackdrop(loaded);
        // Stopping on a photograph is the choice (see ArtistArtChoice). Nothing needs to be clicked and
        // nothing needs remembering. The next time this artist plays, this is the picture that opens.
        RememberChoice(url);
    }

    /// <summary>
    /// Takes a picture from this computer as the artist's artwork: copies it into the app's own store, puts
    /// it at the front of the carousel, shows it, and writes the order down as the person's.
    ///
    /// <para>It works with an EMPTY carousel, which is the case that most wants it. An artist the sources
    /// have no photograph of shows a blank header. Picking one is the only way there will ever be a picture
    /// there. So nothing here depends on there being artwork already. The only thing it needs is to know
    /// whose artist this is.</para>
    /// </summary>
    private async Task RunUseImageAsync(string path, int gen, CancellationToken ct)
    {
        if (_backdropArtist is not { Length: > 0 } artist)
            return;   // nothing playing, or a track with no artist to file the picture under

        // Copying is disk work, and the picture may be a 20 megapixel photograph.
        var (stored, url) = await Cancellable.RunAsync(() => OwnArt.Store(path), ct);
        if (!stored || gen != _gen) return;
        if (url is null)
            return;   // unreadable, or not a picture, the header keeps whatever it had

        // In front of everything the sources offered, and never twice if it was chosen before.
        var carousel = _backdrops.Where(u => !string.Equals(u, url, StringComparison.OrdinalIgnoreCase)).ToList();
        carousel.Insert(0, url);
        SetBackdrops(artist, carousel);   // …and the index goes back to 0, which is the new picture

        var backdrop = await _stage.LoadBackdropAsync(url, FramingFor(url), ct);
        if (gen != _gen) return;
        if (backdrop.Match(found => found, _ => null) is not { } loaded)
            return;   // not shown; the order is not written for a picture that cannot be shown

        _stage.ShowBackdrop(loaded);

        // The same gesture the arrows make: what is on screen is the choice, and it is saved as curated. So
        // the chooser never re-ranks this artist out from under it.
        RememberChoice(url);

        // ...and then the placement window, unasked. A photograph from somebody's own computer is the one
        // case where the chooser works blind. It was picked because of what is in it, and where in it that
        // thing sits is the reason for picking it. So the window opens on the chooser's answer, already
        // applied, and the person either agrees or moves it. Nothing is imposed. Closing the window leaves
        // exactly the picture that is already on screen.
        _stage.OfferPlacement();
    }

    /// <summary>
    /// Takes the photograph on the header off this artist: out of the carousel here, hidden in the saved
    /// order there, and the next picture up in its place.
    ///
    /// <para>The carousel does not go back to the first picture. If picture 3 of 8 is taken away, the
    /// reader is looking at picture 3 of 7, which moved up into the place they were already looking at.
    /// Being sent back to the front would be a second change that nobody asked for.</para>
    ///
    /// <para>Nothing is deleted off the disk. A chosen picture is stored under what is IN it, so five
    /// artists can be sharing the one file (see OwnArt), and this artist not wanting it is not the other
    /// four saying so.</para>
    /// </summary>
    private async Task RunRemoveImageAsync(int gen, CancellationToken ct)
    {
        if (_backdropArtist is not { Length: > 0 } artist || CurrentBackdrop() is not { } url)
            return;   // nothing playing, no artist to file it under, or no photograph up

        // Written down first, while the picture is still in the carousel. The save needs the list the
        // picture is still in. A picture chosen a moment ago may have no stored row of its own yet, so this
        // is how it gets hidden. See ArtistArtChoice.Hide.
        RememberRemoval(url);

        var standing = _backdropIndex;
        var carousel = _backdrops.Where(u => !string.Equals(u, url, StringComparison.OrdinalIgnoreCase)).ToList();
        SetBackdrops(artist, carousel);

        if (carousel.Count == 0)
        {
            _stage.ClearBackdrop();   // the artist has one picture, and it is the wrong one
            return;
        }

        _backdropIndex = Math.Clamp(standing, 0, carousel.Count - 1);
        _stage.SetCarousel(_backdropIndex, carousel.Count);

        var next = CurrentBackdrop();
        var backdrop = await _stage.LoadBackdropAsync(next, FramingFor(next), ct);
        if (gen != _gen) return;
        // As above: a failed fetch leaves the header as it is, and only "no picture" clears it.
        if (!backdrop.Answers(out var loaded, out _))
            return;
        if (loaded is null)
            _stage.ClearBackdrop();
        else
            _stage.ShowBackdrop(loaded);
    }

    /// <summary>
    /// Puts a hand-made framing on the photograph that is up, and remembers it.
    ///
    /// <para>The swap it ends in is a re-cut, not a dissolve. It is the same photograph in a different
    /// window, so the header defocuses into its own wash and comes back sharp somewhere else (see
    /// ShowBackdrop). This is why applying a placement looks like the picture moving, and not like the
    /// header blinking. The two copies are registered, so a crossfade has nothing to double.</para>
    /// </summary>
    private async Task RunFrameAsync(ArtFramingSubject subject, ArtFraming? framing, int gen, CancellationToken ct)
    {
        if (subject.Url is not { Length: > 0 } url || subject.Artist is not { Length: > 0 } artist)
            return;

        // Saved whatever else is true, and saved against the photograph it was made for. The window can
        // stay open across a track change. It is not tied to the music. The placement is still a true
        // statement about that picture and that artist, whether or not either is on screen. It is also
        // written down before the bake. The bake reads the framing back out (FramingFor), and so does every
        // recrop and every flip back to this photograph.
        RememberFraming(artist, url, framing);

        // ...but it is only drawn again while it is still the picture up there. Applying it to whatever is
        // on the header now would put one photograph's placement on another.
        if (!string.Equals(CurrentBackdrop(), url, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(_backdropArtist, artist, StringComparison.OrdinalIgnoreCase))
            return;

        var backdrop = await _stage.LoadBackdropAsync(url, framing, ct);
        if (gen != _gen) return;
        if (backdrop.Match(found => found, _ => null) is { } loaded)
            _stage.ShowBackdrop(loaded);
    }

    private async Task RunReshapeAsync(int gen, CancellationToken ct)
    {
        if (CurrentBackdrop() is not { } url)
            return;

        var backdrop = await _stage.LoadBackdropAsync(url, FramingFor(url), ct);
        if (gen != _gen) return;
        if (backdrop.Match(found => found, _ => null) is { } loaded)
            _stage.ShowBackdrop(loaded);   // same-window reshapes re-place without a dissolve
    }

    private Task ShowIdleAsync(int gen)
    {
        if (gen != _gen)
            return Task.CompletedTask;

        SetBackdrops(null, []);
        _stage.SetText(null, null, null);
        _stage.ShowCover(null);
        _stage.ShowDisc(new LoadedDisc(null));
        _stage.ClearBackdrop();
        _stage.SetLoading(false);
        _stage.EnterIdle();
        return Task.CompletedTask;
    }

    /// <summary>Whose photographs are in the carousel. This is the key the choice is saved under. Null
    /// while nothing is playing, or for a track whose artist is not known.</summary>
    private string? _backdropArtist;

    /// <summary>
    /// This artist's hand-made framings, by photograph. They are read once when the carousel is set. They
    /// are not read from the store on every flip and every recrop, because those run on the pump, which is
    /// the thread the header is drawn on.
    ///
    /// <para>A photograph with no entry is one the chooser frames. That is the ordinary case. There is no
    /// "automatic" value. See the store's own note.</para>
    /// </summary>
    private IReadOnlyDictionary<string, ArtFraming> _framings =
        new Dictionary<string, ArtFraming>(StringComparer.OrdinalIgnoreCase);

    /// <summary>How this photograph is placed, or null for "the chooser frames it".</summary>
    private ArtFraming? FramingFor(string? url)
        => url is not null && _framings.TryGetValue(url, out var framing) ? framing : null;

    private void SetBackdrops(string? artist, IReadOnlyList<string> backdrops)
    {
        _backdropArtist = artist;
        _backdrops = backdrops;
        _backdropIndex = 0;
        _framings = ReadFramings(artist);
        _stage.SetCarousel(0, backdrops.Count);
    }

    private IReadOnlyDictionary<string, ArtFraming> ReadFramings(string? artist)
    {
        // If the library cannot answer, the chooser frames everything. A refusal has put the problem screen
        // up over the app. A closed library is followed by a Changed event, which reads the new one.
        if (artist is { Length: > 0 } && _library is { IsOpen: true } lib)
            return lib.Read(lib.Db, "an artist's photo framings", db => db.GetArtistFramings(artist))
                .Match(framings => framings, _ => NoFramings());

        return NoFramings();

        static IReadOnlyDictionary<string, ArtFraming> NoFramings()
            => new Dictionary<string, ArtFraming>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Files a framing against a photograph, in the copy the bake reads first, then on disk.
    ///
    /// <para>Null forgets it. The framing is not stored as the chooser's automatic answer. That answer
    /// depends on the picture and the header, and it is recomputed each time. Writing it down would store a
    /// value that goes stale as soon as either changes.</para>
    /// </summary>
    private void RememberFraming(string artist, string url, ArtFraming? framing)
    {
        // The in-memory map belongs to the current artist, so it is only changed when this framing is
        // theirs. A placement saved for the artist who was playing a minute ago is saved on disk and
        // nowhere else. Writing it into this dictionary would file it under whoever is playing now.
        if (string.Equals(_backdropArtist, artist, StringComparison.OrdinalIgnoreCase))
        {
            var next = new Dictionary<string, ArtFraming>(_framings, StringComparer.OrdinalIgnoreCase);
            if (framing is { } placed)
                next[url] = placed;
            else
                next.Remove(url);
            _framings = next;
        }

        _ = Task.Run(() =>
        {
            try
            {
                if (_library is { IsOpen: true } lib)
                    lib.Db.SaveArtistFraming(artist, url, framing);
            }
            catch { /* a save that fails is a placement lost at the next launch, not a broken header */ }
        });
    }

    /// <summary>
    /// The photograph the user has just paged to becomes this artist's first one, from now on.
    ///
    /// <para>It is saved only once the picture is actually up. A photo that failed to download is not a
    /// choice. Making it the default would leave the artist opening on a picture that cannot be shown. The
    /// write runs off the UI thread. The person is looking at the result, not waiting for it.</para>
    /// </summary>
    private void RememberChoice(string url)
    {
        if (_backdropArtist is not { Length: > 0 } artist)
            return;

        var showing = _backdrops;
        QueueOrderWrite(() =>
        {
            // A saved order that could not be read is not "no saved order": merged against nothing, the
            // write would replace the artist's order with what happens to be showing. Not saved.
            if (!ReadStoredOrder(artist).Answers(out var stored, out _))
                return;

            var merged = ArtistArtChoice.Prefer(stored?.Backdrops, showing, url);
            SaveOrder(artist, merged, curated: true);
        });
    }

    /// <summary>
    /// The photograph the user has just taken off is hidden in this artist's saved order, so no source and
    /// no chooser offers it again.
    ///
    /// <para>Curated is left as it was found. Arranging the order is a statement about which picture leads.
    /// Taking one off is a statement about that picture only. An artist whose order the app may still rank
    /// stays that way, minus one photograph.</para></summary>
    private void RememberRemoval(string url)
    {
        if (_backdropArtist is not { Length: > 0 } artist)
            return;

        var showing = _backdrops;   // the removed picture included, the row to hide may only exist here
        QueueOrderWrite(() =>
        {
            // As in RememberChoice: an order that could not be read is not overwritten.
            if (!ReadStoredOrder(artist).Answers(out var stored, out _))
                return;

            var merged = ArtistArtChoice.Hide(stored?.Backdrops, showing, url);
            SaveOrder(artist, merged, curated: stored?.Curated ?? false);
        });
    }

    /// <summary>The tail of the order writes, so they land in the order they were asked for.</summary>
    /// <remarks>
    /// Read and written on the UI thread only. Every caller is inside the manager's own queue, so the
    /// chaining needs no lock of its own.
    ///
    /// <para>The writes are chained rather than each on its own thread, because two of them often make one
    /// gesture. A picture chosen off the computer and taken straight back off again is a save and then a
    /// save, seconds apart. If they ran loose, they could land in either order. The removal could land
    /// first, and then the choice would write the picture back onto the artist, after the person had just
    /// taken it off.</para>
    /// </remarks>
    private Task _orderWrites = Task.CompletedTask;

    private void QueueOrderWrite(Action write)
        => _orderWrites = _orderWrites.ContinueWith(
            _ =>
            {
                try { write(); }
                catch { /* one order that will not save is not the next one's problem */ }
            },
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    // The art chooser: which backdrop comes first, and the order of the rest.

    /// <summary>
    /// The header when the artwork lookup did not answer: the artist's saved order, shown as it was saved,
    /// with nothing written. An artist with no saved order keeps whatever the header shows.
    /// </summary>
    private async Task ShowSavedOrderAsync(int gen, string artist, CancellationToken ct)
    {
        var saved = ReadStoredOrder(artist).Match(stored => stored, _ => null)?.Backdrops
            .Where(b => !b.Hidden)
            .OrderBy(b => b.Position)
            .Select(b => b.Url)
            .ToList();

        if (saved is not { Count: > 0 })
            return;

        SetBackdrops(artist, saved);

        var current = CurrentBackdrop();
        var backdrop = await _stage.LoadBackdropAsync(current, FramingFor(current), ct);
        if (gen != _gen) return;
        if (backdrop.Match(found => found, _ => null) is { } loaded)
            _stage.ShowBackdrop(loaded);
    }

    /// <summary>Reconciles the source's candidates against the saved order, and saves the cheap changes.
    /// Returns the plan to display and the stored order, so the re-ranker can keep hidden photos.</summary>
    /// <remarks>A saved order that could not be read is reconciled as if there were none, for what is
    /// SHOWN. Nothing is saved or ranked in that case. Reconciled against nothing, the source's order would
    /// be saved over the artist's curated one, including its own pictures and hidden photos.</remarks>
    private (ArtistArtPlan Plan, ArtistArtOrder? Stored) PlanBackdrops(string artist, IReadOnlyList<string> candidates)
    {
        if (!ReadStoredOrder(artist).Answers(out var stored, out _))
        {
            var shown = ArtistArtReconciler.Reconcile(null, candidates, BackdropRanker.RankVersion);
            return (shown with { NeedsRank = false, Changed = false }, null);
        }

        var plan = ArtistArtReconciler.Reconcile(stored, candidates, BackdropRanker.RankVersion);

        if (plan.Changed && !plan.NeedsRank)
            SaveOrder(artist, plan.Merged, stored?.Curated ?? false);

        return (plan, stored);
    }

    /// <summary>
    /// The disc art of a borrowed record, from the computer that holds it. A failure gives null.
    ///
    /// <para>It is started before it is known to be wanted, because the folder here may answer first. So it
    /// must be safe to abandon. A task that nobody awaits and that faults leaves an unobserved exception.
    /// This one reaches a machine that can drop off the network in the middle of a request.</para>
    /// </summary>
    private static async Task<byte[]?> BorrowedDiscAsync(LibraryTrack row, CancellationToken ct)
    {
        try
        {
            return await RemoteSleeve.DiscForAsync(row, ct);
        }
        catch
        {
            return null;   // a computer that went away is a record with no disc art, not a failure
        }
    }

    /// <summary>The cloud library row for a track whose file is not on this disk. It is what the cloud
    /// needs in order to supply the sleeve: the album artist the cover was keyed under, and the ID of a
    /// track whose sidecar still carries the picture.</summary>
    private LibraryTrack? CloudRowFor(string path)
    {
        if (_library is not { IsOpen: true } lib || lib.Root is not { } root) return null;

        // The user's own collection, and then a borrowed one if there is one on screen. A record put on
        // from a friend's shelf goes on playing after the user has switched back to their own, so neither
        // on its own is the whole answer.
        return Row(lib.Db, root) ?? (lib.ShelfRoot is { } shelfRoot ? Row(lib.Shelf, shelfRoot) : null);

        LibraryTrack? Row(LibraryDatabase db, string root)
        {
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                return null;   // playing from outside the library, a radio stream, a dragged-in file

            var cut = relative.LastIndexOf('/');
            var folder = cut < 0 ? "" : relative[..cut];
            var fileName = cut < 0 ? relative : relative[(cut + 1)..];

            // A failed read gives no row, and the tags on the deck still name the record. A refusal has put
            // the problem screen up over the app.
            return lib.Read(db, "the playing track's row", mirror => mirror.FolderTracks(folder))
                .Match(rows => rows.FirstOrDefault(
                        t => string.Equals(t.FileName, fileName, StringComparison.OrdinalIgnoreCase)),
                    _ => null);
        }
    }

    /// <summary>The artist's saved order: answered with it, or with null where none is saved (or no library
    /// is open); unanswered where the read failed. Every caller that saves afterwards must not save on a
    /// failed read, or it writes over the order it could not see.</summary>
    private Asked<ArtistArtOrder?> ReadStoredOrder(string artist)
        => _library is { IsOpen: true } lib
            ? lib.Read<ArtistArtOrder?>(lib.Db, "an artist's saved photo order", db => db.GetArtistArt(artist))
            : Asked.Answer<ArtistArtOrder?>(null);

    private void SaveOrder(string artist, IReadOnlyList<ArtistBackdrop> backdrops, bool curated)
    {
        try
        {
            if (_library is { IsOpen: true } lib)
                lib.Db.SaveArtistArt(artist, backdrops, curated, BackdropRanker.RankVersion);
        }
        catch { /* a save that fails is a slower next visit, not a failure */ }
    }

    /// <summary>Runs the preselect against the real scene, saves the winning order, and returns it
    /// (best-first URLs). Null when the rank could not run; the caller keeps the order it had. Hidden
    /// photos are carried across rather than lost.</summary>
    private async Task<IReadOnlyList<string>?> RankAndSaveAsync(
        string artist, IReadOnlyList<string> candidates, ArtistArtOrder? stored, CancellationToken ct)
    {
        if (_preselector is null)
            return null;

        IReadOnlyList<RankedBackdrop>? ranked;
        try
        {
            ranked = await _preselector.RankAsync(candidates, _stage.Scene, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;   // a rank that fails leaves the source's order in place
        }

        if (ranked is null)
            return null;

        var toSave = new List<ArtistBackdrop>();
        foreach (var r in ranked)
            toSave.Add(new ArtistBackdrop(r.Url, toSave.Count, r.Score));
        if (stored is not null)
            foreach (var hidden in stored.Backdrops.Where(b => b.Hidden))
                toSave.Add(new ArtistBackdrop(hidden.Url, toSave.Count, hidden.Score, Hidden: true));

        await Task.Run(() => SaveOrder(artist, toSave, curated: false), CancellationToken.None);

        return ranked.Select(r => r.Url).ToList();
    }
}
