namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>
/// What one errand has to say for itself.
///
/// <para>There are three states, not two. The third one matters most: "nobody has a photograph of this
/// artist" is an answer, and the pane moves on from it the same way it moves on from a photograph. Only
/// <see cref="Waiting"/> holds anything up. Without this third state the sequence would need a timer,
/// and a timer is only a guess about how slow the network is.</para>
/// </summary>
internal enum Arrival
{
    Waiting,
    Landed,
    Nothing,
}

/// <summary>The six questions asked about one record. Each one has its own clock.</summary>
internal enum PaneErrand
{
    /// <summary>The row from the local mirror, then the file's own tags. The credits are built from
    /// these.</summary>
    Facts,

    /// <summary>The album cover. Read from disk, or fetched from whichever source holds the record.</summary>
    Sleeve,

    /// <summary>The artist's photograph, and the colour and the wash taken from it.</summary>
    Photo,

    /// <summary>MusicBrainz and Wikipedia: the two cards, the band members and the links out.</summary>
    Papers,

    /// <summary>The timed lyric sheet.</summary>
    Words,
}

/// <summary>
/// How much of the pane a change of record actually invalidates.
///
/// <para>Most changes of track move to the next song on the same record. Everything the pane says
/// about the ARTIST is still true then: the photograph, the colour the pane uses, the card about the
/// artist and the list of band members. Tearing those down and fetching them again costs a network
/// round trip just to get back the picture already on screen. It also makes the pane flicker through
/// an empty state.</para>
/// </summary>
internal enum PaneScope
{
    /// <summary>Same artist. The picture, the ground, the artist card and the members stay as they are;
    /// everything that names the record or the track is rebuilt.</summary>
    Record,

    /// <summary>A different artist. Everything is rebuilt.</summary>
    Artist,
}

/// <summary>
/// The pane's layers, from bottom to top. The numbering is the order. The pane empties from the
/// bottom up and fills from the bottom up.
/// </summary>
internal enum PaneLayer
{
    Facts = 0,       // the credits
    Elsewhere = 1,   // the links out
    Album = 2,
    Members = 3,
    Artist = 4,
    Lyrics = 5,
    Hero = 6,        // the album cover, the title, the way into the record
    Photo = 7,       // the artist's photograph, the tint and the wash
}

/// <summary>
/// The order in which the track info pane is taken down and put back up. It does nothing else: no
/// elements, no bitmaps, no fetching.
///
/// <para>It is a type of its own because the order is the whole fix. Six errands fill that pane, and
/// each one has its own clock. Each answer would be painted as soon as it arrived. A check that the
/// record is still on screen guards against stale data, but it says nothing about order. Without this
/// sequence, the new album's cover could show on the previous artist's photograph for a moment on every
/// track change: two records on one screen, with no single answer wrong.</para>
///
/// <para>Every question here asks about the current state, and can be asked at any moment: has the
/// cover answered, is the pane empty, which layer is due next. No answer depends on a flag that is only
/// true for one turn of the dispatcher. So the order in which the answers come back does not matter.
/// It also does not matter if one answer came from the cache, or if the reader changed record in the
/// middle. <see cref="Next"/> can be called any number of times. It returns the same layer until that
/// layer is actually put up.</para>
/// </summary>
internal sealed class PaneSequence
{
    private readonly Arrival[] _answers = new Arrival[5];
    private readonly bool[] _shown = new bool[8];

    public PaneSequence(PaneScope scope)
    {
        Scope = scope;
        if (scope == PaneScope.Artist)
            return;

        // Same artist. These layers stay on screen, so the sequence treats them as already up. The
        // photo errand is marked as answered too, because the photograph on screen is its answer.
        foreach (var layer in Enum.GetValues<PaneLayer>())
        {
            if (Keeps(layer))
                _shown[(int)layer] = true;
        }

        _answers[(int)PaneErrand.Photo] = Arrival.Landed;
    }

    /// <summary>How much of the pane this build is replacing.</summary>
    public PaneScope Scope { get; }

    /// <summary>Which layers belong to the artist rather than to the record or the track.</summary>
    private static PaneScope Owner(PaneLayer layer) => layer switch
    {
        PaneLayer.Photo or PaneLayer.Artist or PaneLayer.Members => PaneScope.Artist,
        _ => PaneScope.Record,
    };

    /// <summary>Whether this layer stays put through the whole rebuild. The one question everything else
    /// about scope is asked through: the peel skips it, the reveal skips it, and the errand behind it is
    /// never started.</summary>
    public bool Keeps(PaneLayer layer) => Scope == PaneScope.Record && Owner(layer) == PaneScope.Artist;

    /// <summary>
    /// How far the peel has got, counting UP from the bottom layer. Past the top means the pane is empty
    /// and the build may start putting things back.
    ///
    /// <para>The peel goes bottom first: the credits, the links, the album, the members, the artist, the
    /// lyric sheet, the hero, and last the photograph. It looks like the pane being rolled up from
    /// underneath. It keeps the two things that say WHICH RECORD this is, the title and the picture, on
    /// screen for as long as anything else is left to remove.</para>
    /// </summary>
    public int PeeledTo { get; private set; }

    /// <summary>Takes the next layer off the bottom, and says which it was. Null once the pane is
    /// empty. Layers the artist still owns are stepped over rather than taken.</summary>
    public PaneLayer? Peel()
    {
        while (!Peeled)
        {
            var layer = (PaneLayer)PeeledTo++;
            if (!Keeps(layer))
                return layer;
        }

        return null;
    }

    /// <summary>Whether the pane has been emptied. Everything below waits for this.</summary>
    public bool Peeled => PeeledTo > (int)PaneLayer.Photo;

    /// <summary>
    /// Says the pane is empty already, so this build starts by putting things up.
    ///
    /// <para>This is for the one case where nothing has to be taken away. The music stopped, and the pane
    /// was taken down behind the empty state at that moment. The next thing that happens is a listener
    /// pressing play. Peeling an empty pane is not free: the whole peel time goes on taking away nothing,
    /// while the record they just started waits behind a spinner for its title.</para>
    /// </summary>
    public void AlreadyEmpty() => PeeledTo = (int)PaneLayer.Photo + 1;

    public Arrival Of(PaneErrand errand) => _answers[(int)errand];

    /// <summary>Records what an errand came back with. <paramref name="found"/> false is "there is
    /// none", which is an answer and moves the sequence on.</summary>
    public void Answered(PaneErrand errand, bool found)
        => _answers[(int)errand] = found ? Arrival.Landed : Arrival.Nothing;

    public bool Up(PaneLayer layer) => _shown[(int)layer];

    public void Put(PaneLayer layer) => _shown[(int)layer] = true;

    /// <summary>Whether anything is being fetched for the two picture layers at all. False on a change
    /// of track by the same artist, where the photograph is already up: nothing is out, so nothing
    /// should be spinning for it.</summary>
    public bool FetchingPictures => !Keeps(PaneLayer.Photo) || Of(PaneErrand.Sleeve) == Arrival.Waiting;

    /// <summary>Both pictures have answered. The first half waits for this. It also makes sure a cover
    /// is never shown on the wrong artist's photograph.</summary>
    public bool PicturesSettled
        => Of(PaneErrand.Sleeve) != Arrival.Waiting && Of(PaneErrand.Photo) != Arrival.Waiting;

    /// <summary>The file tags, the encyclopedia pages and the lyrics have all answered.</summary>
    public bool WordsSettled
        => Of(PaneErrand.Facts) != Arrival.Waiting
        && Of(PaneErrand.Papers) != Arrival.Waiting
        && Of(PaneErrand.Words) != Arrival.Waiting;

    /// <summary>
    /// The layer due next, or null while no layer may go up yet.
    ///
    /// <para>Each half runs bottom to top. The two halves are gated separately. The second half waits for
    /// ALL of its answers and does not put each block up as it lands. The credits are the bottom block,
    /// and the spinner stands directly over them. Showing them early would put a loader on top of text
    /// the reader has already started to read. Either the words above the credits are coming, or they
    /// are not. The pane decides which before it shows anything down there.</para>
    ///
    /// <para><see cref="PaneLayer.Elsewhere"/> covers the links AND the three cards. They come from one
    /// errand, so they land together.</para>
    /// </summary>
    public PaneLayer? Next()
    {
        if (!Peeled || !PicturesSettled)
            return null;

        if (!Up(PaneLayer.Hero))
            return PaneLayer.Hero;

        if (!Up(PaneLayer.Photo))
            return PaneLayer.Photo;

        if (!WordsSettled)
            return null;

        if (!Up(PaneLayer.Facts))
            return PaneLayer.Facts;

        if (!Up(PaneLayer.Elsewhere))
            return PaneLayer.Elsewhere;

        return Up(PaneLayer.Lyrics) ? null : PaneLayer.Lyrics;
    }

    /// <summary>Whether every layer that will ever go up is up. This is what removes the last
    /// spinner.</summary>
    public bool Done => Peeled && PicturesSettled && WordsSettled && Up(PaneLayer.Lyrics);

    /// <summary>Every layer, bottom to top. For the callers that have to walk them all.</summary>
    public static IEnumerable<PaneLayer> Layers => Enum.GetValues<PaneLayer>();
}
