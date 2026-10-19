namespace RPM.Desktop.Services.Shell;

/// <summary>
/// The commands that the keyboard's media keys, the headphones' buttons and the Now Playing tile in
/// Control Center can send to the player. One list, so a platform that adds a button has one place to
/// send it, and the service has one switch that answers it.
/// </summary>
public enum RemoteCommand
{
    /// <summary>Start what is on the deck, or resume it.</summary>
    Play,
    /// <summary>Pause it, keeping the track loaded.</summary>
    Pause,
    /// <summary>The one key on the keyboard row: pause if it is playing, start it if it is not.</summary>
    Toggle,
    Next,
    Previous,
    /// <summary>Scrubbed from the tile. The target position in seconds is passed with the
    /// command.</summary>
    SeekTo,
}

/// <summary>
/// What is playing, as one complete statement.
///
/// <para>The system's Now Playing tile does not take small changes. Each update replaces the whole card,
/// and a field that is left out is shown as missing, not as unchanged. So every update sends this one
/// shape: title, artist, album, length, position, and whether it is playing. Every path that tells the
/// system anything builds one of these first.</para>
///
/// <para>A card for an empty deck is null, not an empty card. A player with no record loaded is different
/// from a player with a record whose tags are blank. The tile shows the first as nothing at all.</para>
/// </summary>
public readonly record struct NowPlayingCard(
    string Title,
    string? Artist,
    string? Album,
    double Duration,
    double Elapsed,
    bool Playing)
{
    /// <summary>
    /// A card for what is on the deck. The fields are put into the form the tile reads them in: blank tags
    /// become missing lines rather than empty ones, and the position never goes past the end of the record.
    /// </summary>
    public static NowPlayingCard For(
        string? title, string? artist, string? album, double duration, double elapsed, bool playing)
    {
        var length = duration > 0 && double.IsFinite(duration) ? duration : 0;
        var needle = double.IsFinite(elapsed) && elapsed > 0 ? elapsed : 0;

        // A position past the end would put the scrubber in the wrong place. The tile keeps drawing from
        // these two numbers between updates, so they must agree with each other.
        if (length > 0 && needle > length)
            needle = length;

        return new(
            Text(title) ?? "",
            Text(artist),
            Text(album),
            length,
            length > 0 ? needle : 0,
            playing);
    }

    /// <summary>A stream, not a record. A radio station has no end to count towards. A tile given a length
    /// of zero draws the station as live, not as a scrubber stuck at zero.</summary>
    public bool IsLive => Duration <= 0;

    /// <summary>
    /// Whether the system must be told again.
    ///
    /// <para>The tile is not repainted by us. Given a position and a rate, it runs its own clock between
    /// our updates, so restating the card on every tick would be wasted work. The tile cannot know about
    /// anything else that changed: a new track, a pause, a new length. It also cannot know about a position
    /// that its own counting would not have reached. That is what a seek looks like from here.</para>
    ///
    /// <para><paramref name="toldSecondsAgo"/> is how many seconds the system has been counting on its own
    /// since the last update. It is passed in, not read from a clock, so the rule can be tested.</para>
    /// </summary>
    public static bool NeedsRestating(NowPlayingCard told, double toldSecondsAgo, NowPlayingCard now)
    {
        if (told.Title != now.Title || told.Artist != now.Artist || told.Album != now.Album
            || told.Duration != now.Duration || told.Playing != now.Playing)
            return true;

        // Where its own clock has counted to. A paused tile counts nowhere.
        var counted = told.Elapsed + (told.Playing ? Math.Max(0, toldSecondsAgo) : 0);
        return Math.Abs(now.Elapsed - counted) > DriftAllowed;
    }

    /// <summary>How far the tile's own clock may drift before it is corrected. It is wider than the normal
    /// gap between a 400ms tick and the system's counting, so ordinary drift is never restated. It is
    /// narrower than a seek, which always moves the position by more than this.</summary>
    private const double DriftAllowed = 2.5;

    private static string? Text(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>What made the service look at what the system is showing.</summary>
public enum StatementCause
{
    /// <summary>The player changed: position, pause, a load finishing. Most of these need nothing
    /// said.</summary>
    Tick,

    /// <summary>A different record went on the deck. The old record's panel is cleared, even if the new
    /// record is not loaded yet.</summary>
    DeckChanged,

    /// <summary>The cover for the record on the deck was handed to the system.</summary>
    CoverLanded,
}

/// <summary>What the service tells the system.</summary>
public enum Statement
{
    Nothing,
    Show,
    Clear,
}

/// <summary>
/// What to tell the system for each cause, given whether there is a card to show. One table rather than a
/// flag per call site, because one wrong cell is a bug that nobody sees until a record plays.
///
/// <para>A cover can arrive while the deck is still opening its file, so there is no card to go with it
/// yet. Restating then would clear the panel, which drops the cover, and nothing loads it again for that
/// record. So the cover waits in the system transport, which keeps it until the next <c>Show</c>. The show
/// that follows the load finishing takes it.</para>
/// </summary>
public static class NowPlayingStatement
{
    /// <param name="cause">Why the service is looking.</param>
    /// <param name="hasCard">Whether the deck has a loaded record or station to show.</param>
    /// <param name="toldSomething">Whether the system was last shown a card (and not cleared
    /// since).</param>
    /// <param name="needsRestating">For a tick with a card: <see cref="NowPlayingCard.NeedsRestating"/>
    /// against what the system was last told.</param>
    public static Statement Next(StatementCause cause, bool hasCard, bool toldSomething, bool needsRestating) =>
        (cause, hasCard) switch
        {
            (StatementCause.Tick, true) => !toldSomething || needsRestating ? Statement.Show : Statement.Nothing,
            (StatementCause.Tick, false) => toldSomething ? Statement.Clear : Statement.Nothing,
            (StatementCause.DeckChanged, true) => Statement.Show,
            (StatementCause.DeckChanged, false) => Statement.Clear,
            (StatementCause.CoverLanded, true) => Statement.Show,
            (StatementCause.CoverLanded, false) => Statement.Nothing,
            _ => Statement.Nothing,
        };
}

/// <summary>
/// Tracks which record's cover the system's media controls should show, and whether that cover has been
/// handed over.
///
/// <para>Three things change this, each on its own schedule, all on the UI thread: the deck changing
/// (<see cref="Want"/>), a cover load finishing (<see cref="Land"/>), and the service clearing the system
/// panel (<see cref="Cleared"/>). A clear can come between a deck change and its cover landing, because a
/// new deck has no card for a moment. That clear must not cancel the load. The cover is for the record on
/// the deck, and it is still wanted.</para>
/// </summary>
public sealed class NowPlayingSleeve
{
    private string? _for;
    private bool _landed;

    /// <summary>The deck now holds <paramref name="path"/>. Returns true when a cover must be loaded for
    /// it: it is a different record, or its cover was dropped by a clear since it last landed.</summary>
    public bool Want(string? path)
    {
        if (string.Equals(path, _for, StringComparison.Ordinal))
            return false;

        _for = path;
        _landed = false;
        return true;
    }

    /// <summary>A cover load for <paramref name="path"/> finished. Returns true when it should be shown: it
    /// is for the record on the deck now. A load for a record that has since been replaced returns
    /// false.</summary>
    public bool Land(string? path)
    {
        if (path is null || !string.Equals(path, _for, StringComparison.Ordinal))
            return false;

        _landed = true;
        return true;
    }

    /// <summary>The system panel was cleared, which drops the cover. A cover that had landed is forgotten,
    /// so the next <see cref="Want"/> for the same record loads it again. A load still running is
    /// kept.</summary>
    public void Cleared()
    {
        if (!_landed)
            return;

        _for = null;
        _landed = false;
    }
}
