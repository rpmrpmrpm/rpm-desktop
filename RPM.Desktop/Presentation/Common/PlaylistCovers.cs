using RPM.Core.Art;
using RPM.Core.Library;
using RPM.Core.Threading;
using RPM.Desktop.Services;
using RPM.Desktop.Services.Art;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// Whose list is being drawn: this library's own, or one on a shelf borrowed from another computer.
///
/// <para>It is stated at every call and never defaulted. Both halves of drawing a cover depend on it, and
/// neither one fails loudly when it is wrong. The records are looked up by NODE ID. A borrowed tree and the
/// user's own both number their playlists from one. So a wrong answer here gives a friend's playlist one of
/// the listener's own covers. That looks like a real picture, not like a fault.</para>
/// </summary>
public enum PlaylistShelf
{
    /// <summary>The listener's own tree.</summary>
    Own,

    /// <summary>The tree on the shelf that is currently open, which belongs to another computer.</summary>
    Borrowed,
}

/// <summary>
/// The one place a stored playlist's cover is drawn, and the one copy of it.
///
/// <para><b>Why it is shared.</b> Two surfaces show a playlist as a picture. They are the head of its own
/// tracklist, and the shelf of playlists on an artist's page. A playlist that came out as two different
/// pictures in two places would read as two different playlists. So the gathering lives here, not in either
/// surface. The same records, in the same order, drawn the same way, give one answer.</para>
///
/// <para><b>And why it is remembered.</b> Drawing one is a flatten of the whole list, up to four cover
/// reads from the disk, and a Skia composite. An artist's page asks for several at once. The tracklist asks
/// again every time it is opened. Without a memo, opening the same playlist twice would do all of that
/// twice, for a picture that cannot have changed. The memo is dropped whole whenever the playlist tree
/// changes. See <see cref="Forget"/>.</para>
///
/// <para><b>Nothing here is drawn for a generated list.</b> Those have no records to show. They are a
/// query, and what is in one depends on when it is asked. So their cover is made from what the list is. It
/// needs neither a gather nor a memo. See <see cref="GeneratedCover"/>, which the surfaces call directly.
/// The one stored playlist that is a generated list, the owner's on a borrowed shelf (<see
/// cref="PlaylistCover.Generated"/>), is handed straight to it.</para>
/// </summary>
public static class PlaylistCovers
{
    /// <summary>How far down a list the gather looks for its four DISTINCT records. A playlist is often an
    /// album dropped in whole, followed by a few singles. So the first four songs are usually one record.
    /// The search is bounded anyway. A thousand-song list whose records all have to be fetched from another
    /// computer would otherwise cost a thousand reads for one small square.</summary>
    private const int Tries = 60;

    /// <summary> What has been drawn so far, by playlist node AND by whose tree that node is in. Empty
    /// answers are kept as well as pictures. "This list has no artwork anywhere" is an answer. Reaching it
    /// costs a flatten and sixty reads, so it should not be paid for twice.
    ///
    /// <para>The computer is part of the key, because the node id is not unique across two trees. Every
    /// library numbers its playlists from one. So a friend's third list and the listener's third list are
    /// both node 3. If the key were the id alone, whichever was drawn first would be the picture that both
    /// of them wore for the rest of the session.</para>
    /// </summary>
    private static readonly Dictionary<string, PlaylistArt.Drawn> Drawn = [];

    /// <summary> What one stored playlist is drawn as: the square at its head, and the wash that the page
    /// it opens stands on. Both are empty where none of its records can supply artwork. The caller then
    /// falls back to what it shows for a list with no picture.
    /// </summary>
    /// <param name="nodeId">The playlist's node. A nested reference asks under the node it points AT. So a
    /// list and a reference to it show the same picture.</param>
    /// <param name="name">The list's name. The ground's arrangement is drawn from it, so a playlist is the
    /// same cover every time, on every machine.</param>
    /// <param name="cover">How this playlist's picture was chosen. It is stated at every call and never
    /// defaulted, for the same reason as the shelf. A caller that left it out would quietly redraw the
    /// automatic cover over a picture that the user chose. The result would look like a real picture, not
    /// like a fault.</param>
    /// <param name="shelf">Whose tree the node is in. No default. See <see cref="PlaylistShelf"/>.</param>
    public static async Task<PlaylistArt.Drawn> MadeAsync(
        long nodeId, string name, PlaylistCover cover, PlaylistShelf shelf, CancellationToken ct)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen)
            return PlaylistArt.Drawn.None;   // Not remembered. A closed library is a temporary state.

        var own = shelf is PlaylistShelf.Own;

        // Asked for a borrowed list while nothing is borrowed. The shelf closed between the surface
        // deciding what it was drawing and this line. Nothing is a better answer than the user's own
        // playlist number three.
        if (!own && library.ViewingPeer is not { Length: > 0 })
            return PlaylistArt.Drawn.None;

        // One of the owner's generated lists, on their shelf. It wears that list's own picture, the same
        // one that GeneratedCover draws for it everywhere else. This is checked BEFORE the master, and
        // nothing is written back. Files left on the disk for these rows by older builds (quilts) could sit
        // under node ids that the shelf reuses. If they were read first, one of those quilts would win over
        // the list's own picture.
        if (cover is PlaylistCover.Generated generated)
            return await GeneratedCover.PageAsync(generated.Id, ct);

        // A picture the owner chose, from their computer. If it cannot be had (they are not on the network,
        // or the file is lost), the list falls through to the quilt below. That is the picture this side
        // can draw for itself. It is asked again on the next draw.
        if (!own && cover is PlaylistCover.ChosenByOwner
            && await ChosenAsync(nodeId, cover, shelf, ct) is { Length: > 0 } theirs)
            return new PlaylistArt.Drawn(theirs, await Task.Run(() => ArtScaler.Wash(theirs), ct));

        var segment = PlaylistCoverStore.SegmentOf(shelf, library.Root, library.ViewingPeer);

        // The master wins outright. It is asked for before the memo. It is a file, so it may have been
        // replaced by the picker since this session last drew anything. A memo checked first would keep the
        // old picture until the next tree change.
        if (PlaylistCoverStore.Read(nodeId, segment) is { Cover: { Length: > 0 } kept } master)
            return new PlaylistArt.Drawn(kept, master.Wash);

        var key = own ? $":{nodeId}" : $"{library.ViewingPeer}:{nodeId}";

        lock (Drawn)
            if (Drawn.TryGetValue(key, out var keptDrawn))
                return keptDrawn;

        // A chosen list of records skips the flatten entirely. Sixty rows are walked to GUESS which four to
        // show. When somebody has said which four, there is nothing left to guess.
        List<string> folders;
        if (cover is PlaylistCover.Records { Folders.Count: > 0 } chosen)
        {
            folders = [.. chosen.Folders];
        }
        else
        {
            // The records on the list, in its own order, each named once. This runs off the thread, because a
            // flatten walks every entry and looks up each one in the tracks table.
            var mirror = own ? library.Db : library.Shelf;
            if (await Cancellable.RunAsync(
                    () => library.Read(mirror, "a playlist's records for its cover",
                        m => m.FlattenPlaylistTracks(nodeId)), ct) is not { Ran: true, Value: { } asked })
                return PlaylistArt.Drawn.None;   // nobody is waiting on this list any more

            // Unanswered is no picture. A refusal has put the problem screen up over the app. A closed
            // mirror is followed by a Changed that draws the list again.
            if (!asked.Answers(out var tracks, out _))
                return PlaylistArt.Drawn.None;

            folders = [.. tracks
                .Select(track => track.Folder)
                .Where(folder => folder is { Length: > 0 })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(Tries)];
        }

        var sleeves = new List<byte[]>(PlaylistArt.Sleeves);
        foreach (var folder in folders)
        {
            if (sleeves.Count == PlaylistArt.Sleeves)
                break;

            // The mirror that the folders came from, stated rather than left to the default. The two
            // mirrors are not interchangeable, in either direction. A folder from the listener's own list
            // does not exist on the borrowed shelf that the window is browsing. A folder from a borrowed
            // list does not exist in the listener's own collection.
            if (await AlbumCovers.LoadAsync(folder, ct, ArtScaler.TileEdge, ownCollection: own)
                    is { Length: > 0 } sleeve)
                sleeves.Add(sleeve);
        }

        // This is not gated on the number of sleeves. PlaylistArt.Made already gives Drawn.None for an
        // empty gather. A Figure choice needs no sleeves at all (see Render). A gate here would stop a
        // chosen figure from ever being drawn for an empty or unreachable list. Those are exactly the lists
        // that a figure is chosen for.
        if (await Cancellable.RunAsync(() => Render(cover, sleeves, name), ct)
                is not { Ran: true, Value: { } made })
            return PlaylistArt.Drawn.None;

        if (!ct.IsCancellationRequested)
        {
            lock (Drawn)
                Drawn[key] = made;

            // The automatic cover leaves its answer on the disk as it goes. This is not for this machine,
            // which has just drawn it and is holding it. It is for the phone, and for a borrower, neither
            // of which can flatten this list or run Skia over the result. The disk copy is a copy of the
            // last derivation, not an authority. So nothing invalidates it. The next open overwrites it.
            if (made.Cover is { Length: > 0 } drawnCover)
                PlaylistCoverStore.Write(nodeId, segment, drawnCover, made.Wash);
        }

        return made;
    }

    /// <summary>
    /// The picture that somebody chose for a node, or null where nobody did, or where it cannot be had.
    ///
    /// <para>On the user's own tree, and for a borrowed node with no choice, that is the master kept on
    /// this disk. A borrowed node whose owner chose its picture (<see cref="PlaylistCover.ChosenByOwner"/>)
    /// is requested from the owner's computer. That computer keeps the only copy. The copy that is fetched
    /// is kept here, under the owner's node id (see <c>SharingService.PlaylistPictureAsync</c>).</para>
    /// </summary>
    /// <param name="cover">How the node's picture was chosen, as the tree records it. It is stated at every
    /// call. It is what says whose computer the picture is on.</param>
    /// <param name="shelf">Whose tree the node is in. No default. See <see cref="PlaylistShelf"/>.</param>
    public static async Task<byte[]?> ChosenAsync(
        long nodeId, PlaylistCover cover, PlaylistShelf shelf, CancellationToken ct)
    {
        var library = App.Services.GetRequiredService<LibraryService>();

        if (shelf is PlaylistShelf.Borrowed && cover is PlaylistCover.ChosenByOwner theirs)
            return library.ViewingPeer is { Length: > 0 } peer
                ? await App.Services.GetRequiredService<Services.Sharing.SharingService>()
                    .PlaylistPictureAsync(peer, theirs.NodeId, theirs.Stamp, ct)
                : null;

        var segment = PlaylistCoverStore.SegmentOf(shelf, library.Root, library.ViewingPeer);
        return (await Task.Run(() => PlaylistCoverStore.Read(nodeId, segment), ct)).Cover;
    }

    /// <summary>
    /// The picture, from the choice and whatever sleeves were gathered for it.
    ///
    /// <para>This is lifted out of <see cref="MadeAsync"/>. It is given no library, no disk and no
    /// cancellation. Those are the three things that stop the decision here from being checked on its own.
    /// What is left is the whole rule: which kind draws from what, and what each one falls back to.</para>
    ///
    /// <para><see cref="PlaylistCover.Picture"/> is not in the switch, on purpose. Its bytes are the
    /// master, and there is nothing to compose. A caller that reaches this point with one has already
    /// failed to find the file. The honest answer then is the picture that the list would have had.</para>
    /// </summary>
    /// <param name="sleeves">The covers gathered for this list, best first. For a <see
    /// cref="PlaylistCover.Records"/> choice these are the chosen four, in the chosen order. Otherwise they
    /// are the first distinct records on the list.</param>
    public static PlaylistArt.Drawn Render(
        PlaylistCover cover, IReadOnlyList<byte[]> sleeves, string name)
        => cover switch
        {
            // No mood, and therefore no mark. This is a figure that somebody CHOSE for a playlist they made
            // and named. The app's mark says "the app made this", which would not be true here.
            PlaylistCover.Figure f => PlaylistArt.Figure(f.Style, f.Seed, f.Mark, mood: null),
            _ => PlaylistArt.Made(sleeves, name),
        };

    /// <summary>What the framing decided, and on what. It writes one line for each photograph considered,
    /// and one for each framing, under RPM.Faces. A picture that comes out wrong is a chain of judgements
    /// about what is in it. None of those judgements can be seen from the screen that it produced.
    ///
    /// <para>It is off unless <c>RPM_FACES_DIAG=1</c> is in the environment. That is the same as the app's
    /// other diagnostics. These pictures are decoration, so in an ordinary run the log would be
    /// noise.</para></summary>
    private static ILogger Faces =>
        Diag ? App.Services.GetRequiredService<ILoggerFactory>().CreateLogger("RPM.Faces")
             : NullLogger.Instance;

    private static readonly bool Diag =
        RPM.Core.Diagnostics.DevSwitch.Read("RPM_FACES_DIAG") is "1" or "true";

    /// <summary>The strips drawn so far, by the people on them AND the list they were drawn for.
    ///
    /// <para>Both halves matter. The people are what the fetching costs. So two lists built round the same
    /// artists should not photograph them twice. The list is in the key because the COLOUR is not theirs.
    /// It is the page's ground laid over their faces, and that is a different colour on every playlist. If
    /// the key were the names alone, the second list to hold the same five artists would wear the first
    /// list's colours.</para></summary>
    private static readonly Dictionary<string, byte[]?> Strips = [];

    /// <summary>
    /// One photograph, and the part of it worth showing on a band a fifth as wide as it is long.
    ///
    /// <para><b>The models decide it, not a fixed rule.</b> U²-Netp says where the subject is. UltraFace
    /// says where the faces are. <see cref="SaliencyCrop"/> then puts a window of the panel's shape around
    /// them. This is the same three steps that the now playing header uses to frame its backdrop, for the
    /// same reason. A guess from the silhouette fails on ordinary photographs of musicians. A hooded jacket
    /// has no shoulders to find. A seated singer has a silhouette twice as tall as her head. A four-piece
    /// band has four heads at four heights. See FaceService, which explains this at length.</para>
    ///
    /// <para><b>A photograph with nobody in it is not used at all.</b> The result is null, and the caller
    /// takes the next act instead. The panels are meant to show the people on the list. The catalogues hold
    /// many pictures that are filed under an act without showing the act: a sleeve, a logo, a stage lit
    /// from behind, or the back of somebody's head. Cropped to a panel a sixth as wide as the strip, one of
    /// those is an ear or a shoulder. That is what this check exists to stop.</para>
    ///
    /// <para><b>A face too small to be what the picture is OF is not a face this cares about.</b> A
    /// detector answers "there is a face here". It does not answer "this is a photograph of somebody". A
    /// bystander in a crowd, a poster on a wall behind the band, and the occasional confident mistake all
    /// come back as small boxes. Framed, a small box is a huge zoom into a few pixels: the ear, the mouth,
    /// or a wristwatch. See <see cref="MinFaceHeight"/>.</para>
    ///
    /// <para><b>Best effort at every step.</b> The window is null wherever a step could not answer. That
    /// happens for a model that is not installed, a runtime that will not start, or a picture that will not
    /// decode. The strip then uses its own guess, which is what it did before any of this existed. Note
    /// which way that cuts. A machine with no face model looked at nothing, so it refuses nothing. See <see
    /// cref="FaceScan"/>. It exists so that "nobody is in this picture" and "nobody looked" are no longer
    /// the same answer. Refusing on the second would empty every strip in the app on a machine where the
    /// model failed to install.</para>
    ///
    /// <para>It is internal rather than private, because the genres wall asks the same question of the same
    /// models. Its card is one artist's photograph in a slot a little taller than it is wide. A centre crop
    /// of a press shot in that slot cuts the tops off heads. There is one framing, used on two surfaces. A
    /// second copy would mean a second set of judgements about the same photographs.</para>
    /// </summary>
    /// <returns>The framed portrait, or null where the detector looked at this picture and found nobody in
    /// it.</returns>
    internal static async Task<PlaylistArt.Framed?> FramedAsync(
        byte[] picture, double aspect, CancellationToken ct)
    {
        try
        {
            using var source = SKBitmap.Decode(picture);
            if (source is null || source.Width < 8 || source.Height < 8)
                return new PlaylistArt.Framed(picture, null);

            // The faces are checked BEFORE the saliency map, because the map is the more expensive of the
            // two. A picture that will be passed over should not have a subject found in it first.
            var scan = await FacesAsync(source, SaliencyService.Size, ct);

            // Said out loud, because everything below is a judgement made on a picture that nobody watching
            // the screen can see in full. The judgements are which of the five panels came from which act,
            // and what the detector made of their photograph. The band is decoration, so it is never a
            // warning. It is the one thread of the app whose output IS a taste. Taste is argued about
            // against evidence.
            Faces.LogInformation("[faces] looked={Looked} found={Found} biggest={Biggest:P0} of the height",
                scan.Looked, scan.Faces.Count,
                scan.Faces.Count == 0 ? 0 : scan.Faces.Max(f => f.Height) / (double)SaliencyService.Size);

            if (scan.FoundNobody || TooSmall(scan))
                return null;

            if (await SaliencyAsync(source, ct) is not { } map)
                return new PlaylistArt.Framed(picture, null);

            // The panel's shape, as the MAP sees it.
            //
            // The map is the photograph SQUASHED INTO A SQUARE (see SaliencyAsync, which resizes to Size ×
            // Size whatever shape the picture is). So shapes do not survive the trip. A window that comes
            // out 1.45:1 in the photograph is 1.45 × height / width : 1 in the map. On a 640 × 360 press
            // shot, which is nearly everything a catalogue hands over, that is 0.82. That is an UPRIGHT
            // window. The panel's own 1.45 asked for one nearly twice as wide as it should be.
            //
            // That is the cause of the faulty crops. Choose could not fit a window that wide around a head.
            // So it took the pillarbox arm and handed back a band 2.8 times as wide as it was tall, showing
            // eyes and no more. Coming back out of the map, the band stretched to five times as wide as it
            // was tall. The panel then crops what it is given to its own shape, and it cut nearly half the
            // width off from the middle. A head at the left of the frame is not in the middle. That is how
            // a photograph of somebody standing with their head turned came out as a panel of their ear.
            var inMap = aspect * source.Height / source.Width;

            var chosen = SaliencyCrop.Choose(
                map, SaliencyService.Size, SaliencyService.Size, inMap, scan.Faces);
            if (chosen.Rect.Width <= 0 || chosen.Rect.Height <= 0)
                return new PlaylistArt.Framed(picture, null);

            // Squared up to the panel, ALWAYS, not only where the crop says it did not fill the slot. That
            // verdict is about the map's own square. This is a question about the photograph. A window that
            // the crop is content with can still be the wrong shape once it is stretched back out. Then the
            // panel takes a band out of the middle of it. See Panelled, which does nothing to a window that
            // is already the right shape.
            var window = Panelled(
                chosen.Rect, inMap, SaliencyService.Size, SaliencyCrop.EyeLineOf(scan.Faces));

            Faces.LogInformation("[faces] chose {Chosen} fills={Fills}, panelled to {Window} of {Size}",
                $"{chosen.Rect.Width}x{chosen.Rect.Height} at {chosen.Rect.X},{chosen.Rect.Y}",
                chosen.FillsTarget,
                $"{window.Width}x{window.Height} at {window.X},{window.Y}", SaliencyService.Size);

            // Back up to the photograph's own pixels. The map is a SQUARE resize of the photograph, so the
            // two axes scale by different amounts. This is the same arithmetic that ArtDisplay uses when it
            // comes back from its own map. Getting it wrong gives a window that slides sideways on every
            // portrait that is not square to begin with.
            var across = (double)source.Width / SaliencyService.Size;
            var down = (double)source.Height / SaliencyService.Size;

            return new PlaylistArt.Framed(picture, new CropRect(
                (int)Math.Round(window.X * across),
                (int)Math.Round(window.Y * down),
                (int)Math.Round(window.Width * across),
                (int)Math.Round(window.Height * down)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new PlaylistArt.Framed(picture, null);   // the strip frames it the old way
        }
    }

    /// <summary> How tall the biggest face has to be, as a share of the photograph's height, before the
    /// strip takes the picture as a picture of somebody.
    ///
    /// <para>A tenth. A press shot's face is a fifth of the frame or more. A band of four is nearer a tenth
    /// each. Below that, whatever was found is not what the photograph is about. It could be somebody in
    /// the crowd, a face on a poster behind the stage, or the detector being confidently wrong about a
    /// wristwatch. The crop frames what it is given, so it would zoom a sixth of the strip's width into
    /// that.</para>
    ///
    /// <para>The boxes are in the saliency map's square, which is the whole picture squashed. So a box's
    /// height divided by the map's size IS its share of the picture's height, whatever shape the picture
    /// is.</para>
    /// </summary>
    private const double MinFaceHeight = 0.10;

    /// <summary>Whether the detector looked, found somebody, and everybody it found is too small to be who
    /// the photograph is of. This is not the same as finding nobody. The answer is the same either way:
    /// this picture is not a portrait, so take the next act.</summary>
    private static bool TooSmall(FaceScan scan)
        => scan.Looked
           && scan.Faces.Count > 0
           && scan.Faces.Max(f => f.Height) < MinFaceHeight * SaliencyService.Size;

    /// <summary>
    /// A window squared up to the panel's own shape, so that the panel has nothing left to crop.
    ///
    /// <para><b>It grows whichever side is short, and never cuts the other.</b> <see
    /// cref="SaliencyCrop.Choose"/> follows the same rule one step earlier, for the same reason. Growing
    /// shows MORE of the photograph, and the person stays whole. Cutting is what the panel would do on its
    /// own. What it cuts out of a portrait is a band of eyes, nose and teeth.</para>
    ///
    /// <para>Two windows arrive here needing work. One is taller than the slot. That is a close portrait in
    /// an upright frame. <see cref="SaliencyCrop.Choose"/> hands it back on the understanding that the
    /// caller will letterbox it over a blurred copy of itself. The strip cannot do that, because its panels
    /// are flush, and a letterboxed one would look like a hole in the row. The other is the band that the
    /// crop falls back to when it cannot fit a head at the shape it was asked for. That band is five times
    /// as wide as it is tall, and it is nobody's portrait.</para>
    ///
    /// <para>Only where the picture has run out of room in both directions does anything get lost. Then the
    /// loss is height. It is taken from the hair and the chin, with the eyes left where a camera operator
    /// would put them (<see cref="SaliencyCrop.EyesInBand"/>).</para>
    ///
    /// <para>A window that is already the panel's shape comes back untouched. That is the ordinary
    /// case.</para>
    /// </summary>
    /// <param name="rect">The chosen window, in the map's pixels.</param>
    /// <param name="aspect">The panel's width over its height AS THE MAP SEES IT. The panel's own number is
    /// the wrong one here, for the reason that the caller gives at length.</param>
    /// <param name="size">The map's edge. It is square, so this is both of its sides.</param>
    /// <param name="eyeLine">Where the eyes are down the map, from <see
    /// cref="SaliencyCrop.EyeLineOf"/>.</param>
    private static CropRect Panelled(CropRect rect, double aspect, int size, double eyeLine)
    {
        // The smallest window of the panel's shape that holds everything this one holds. Grow the short
        // side. Then, if that has grown past the picture, come back inside it. That is the only step that
        // gives anything up, and it gives up height.
        var height = Math.Min(size, Math.Max(rect.Height, rect.Width / aspect));
        var width = height * aspect;
        if (width > size)
        {
            width = size;
            height = width / aspect;
        }

        if (Math.Abs(width - rect.Width) < 1 && Math.Abs(height - rect.Height) < 1)
            return rect;

        // Across: on what was framed. Down: on the eyes where there are any, and on what was framed where
        // there are not. Both are slid back inside the picture. A window half outside the map scales up to
        // a window half outside the photograph.
        var left = Math.Clamp(rect.X + rect.Width / 2.0 - width / 2, 0, size - width);
        var middle = eyeLine > 0 ? eyeLine - height * SaliencyCrop.EyesInBand : rect.Y + rect.Height / 2.0 - height / 2;
        var top = Math.Clamp(middle, 0, size - height);

        return new CropRect(
            (int)Math.Round(left), (int)Math.Round(top),
            (int)Math.Round(width), (int)Math.Round(height));
    }

    /// <summary>Where the subject is: U²-Netp's answer where the model is present, and local contrast where
    /// it is not.
    ///
    /// <para>Internal, for the same reason that <see cref="FramedAsync"/> is. The home screen's banner asks
    /// the same two models about the same photographs. This method does NOT go through the framing above.
    /// That framing turns a picture away when its faces are small or absent. That is right for a strip
    /// choosing between twenty candidates, and wrong for a banner that holds one.</para>
    ///
    /// <para>The fallback finds DETAIL rather than the subject. So it is a degradation, not an equal
    /// alternative. But a crop chosen on contrast still beats a crop chosen on nothing.</para>
    /// </summary>
    internal static async Task<double[]?> SaliencyAsync(SKBitmap source, CancellationToken ct)
        => (await SaliencyReadAsync(source, ct)).Map;

    /// <summary>A saliency map, and whether the model drew it or the local-contrast fallback did. <paramref
    /// name="Map"/> is null only where the picture could not be resized at all.</summary>
    internal readonly record struct SaliencyRead(double[]? Map, bool FromModel);

    /// <summary>The same reading as <see cref="SaliencyAsync"/>, which also says which of the two drew it.
    /// The home banner keeps a cut between launches only when the model answered. So a fallback is never
    /// written down as the answer.</summary>
    internal static async Task<SaliencyRead> SaliencyReadAsync(SKBitmap source, CancellationToken ct)
    {
        const int size = SaliencyService.Size;
        var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Unpremul);

        using var square = source.Resize(
            info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (square is null)
            return new(null, FromModel: false);

        var rgba = square.GetPixelSpan().ToArray();

        if (App.Services.GetService<SaliencyService>() is { } saliency
            && await saliency.ComputeAsync(rgba, ct) is { } model)
            return new(model, FromModel: true);

        return new(SaliencyCrop.ClassicalSaliency(rgba, size, size), FromModel: false);
    }

    /// <summary>The faces in a photograph, in the saliency map's coordinates. That is the space that the
    /// crop is chosen in, and it is why the detector is told the map's size and not the picture's.
    ///
    /// <para>It returns a <see cref="FaceScan"/> rather than a list. The caller turns a picture away on
    /// this answer, and the two ways of coming back empty mean opposite things to it. One is nobody in the
    /// picture. The other is nobody to look. Everything that could not get as far as the model (no service,
    /// or a resize that failed) gives the second answer.</para></summary>
    internal static async Task<FaceScan> FacesAsync(
        SKBitmap source, int mapSize, CancellationToken ct)
    {
        if (App.Services.GetService<FaceService>() is not { } detector)
            return FaceScan.Unread;

        var (width, height) = FaceService.InputSize;
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);

        using var small = source.Resize(
            info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

        return small is null
            ? FaceScan.Unread
            : await detector.DetectAsync(small.GetPixelSpan().ToArray(), mapSize, ct);
    }

    /// <summary>
    /// Forgets what has been drawn, because the tree has changed underneath it.
    ///
    /// <para>Everything is dropped, not one node. That is deliberate. A song added to a list changes that
    /// list's cover. But so does a list being dragged into another one, a nested reference being added, or
    /// a record being retagged into a different folder. The report that arrives says only that something
    /// moved. Redrawing a handful of covers is cheaper than working out which of them the change could have
    /// reached. It is far cheaper than being wrong about it.</para>
    /// </summary>
    public static void Forget()
    {
        lock (Drawn)
            Drawn.Clear();

        // The faces go too. A song added to a list can change who it is mostly by, and the strip is keyed
        // by exactly that answer.
        lock (Strips)
            Strips.Clear();
    }
}
