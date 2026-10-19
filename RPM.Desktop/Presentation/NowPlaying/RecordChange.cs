using Microsoft.UI.Xaml.Media.Imaging;

namespace RPM.Desktop.Presentation.NowPlaying;

/// <summary>
/// The album's own picture of its record, and what that picture is a picture OF.
///
/// <para>Two kinds arrive, and they are worn differently. A photographed CD is the whole face of the record
/// and replaces the pressing under it. A vinyl label is transparent everywhere except the middle third. The
/// Cover Art Archive has one for most LPs, and the cover search sizes it to the label. The pressing has to
/// show around it, or there is no record there at all: the label would float over the disc's own shadow.
/// This pair exists to prevent that.</para>
///
/// <para>The two travel together because the picture alone cannot be asked. By the time a decoded bitmap
/// reaches the layer that draws it, the alpha it was read from is gone, see
/// <see cref="RPM.Desktop.Services.Art.DiscStamp.Covers"/>, which answers it once where the bytes
/// are.</para>
/// </summary>
/// <param name="CoversRecord">The picture is the whole record. False for a label, which is worn ON the
/// pressing rather than instead of it.</param>
public readonly record struct ScannedDisc(BitmapImage Picture, bool CoversRecord);

/// <summary>
/// Everything a record wears: the colour it is pressed in, the album art on its label, and the photographed
/// cd that replaces the drawn disc entirely when the folder carries one.
///
/// <para>One value rather than three, because they are one thing. They arrive separately, since the sleeve
/// decodes in one pass and the cd is read in another. They are applied together, or the record wears half
/// of one album and half of another until the gap is filled.</para>
/// </summary>
/// <param name="Album">Which record this is. It is the identity: the next track of an album is the same
/// record, and nothing about the disc on the deck changes for it.</param>
public readonly record struct RecordFace(
    string Album, Windows.UI.Color? Hue, BitmapImage? Label, ScannedDisc? Cd)
{
    /// <summary>An empty deck: no album, no colour, no pictures. This is what the record wears when nothing
    /// is playing: the house pressing and the rpm stamp.</summary>
    public static readonly RecordFace None = new("", null, null, null);

    /// <summary>
    /// Whether this is the same record as that one. This is not the same question as whether the values are
    /// equal. Each track of an album gets a freshly decoded copy of the sleeve. Comparing the bitmaps would
    /// call each one a new record, and the disc would come off the deck only to put the same picture back.
    ///
    /// <para>The presence of a photographed cd counts, though. Which one it is does not, since each track
    /// decodes it again. Without this check, a cd that arrived just after the record was dressed for its
    /// album was treated as the same record and ignored. The album then played with the drawn disc, and its
    /// own photographed cd was never used.
    /// </para>
    /// </summary>
    public bool IsSameRecord(RecordFace other)
        => Hue == other.Hue
        && (Cd is null) == (other.Cd is null)
        && string.Equals(Album, other.Album, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Where the record is. Two of these are journeys, not resting places. The disc is visible for the
/// whole of both, so neither is a moment when its face may change.</summary>
public enum Deck
{
    /// <summary>Off the deck: shrunk into the top-right corner and faded to nothing. The only place a
    /// record may be re-dressed.</summary>
    Off,

    /// <summary>Flying down onto the deck, fading up as it comes.</summary>
    Dropping,

    /// <summary>On the deck, in plain sight.</summary>
    On,

    /// <summary>Flying off to the corner, fading down as it goes.</summary>
    Lifting,
}

/// <summary>Where to send the record, if anywhere.</summary>
public enum Flight { Stay, Lift, Drop }

/// <summary>What is to be done about an event: put the wanted face on, and/or send the record somewhere. A
/// move with <see cref="Dress"/> set can only ever come back while the record is <see cref="Deck.Off"/>,
/// that is the one guarantee this whole file exists to make.</summary>
public readonly record struct Move(bool Dress, Flight Fly)
{
    public static readonly Move Nothing = new(false, Flight.Stay);
}

/// <summary>
/// The rule a record change obeys, with no record in it.
///
/// <para><b>A record's face only ever changes where nobody can see it change.</b> That is the whole rule.
/// The rule is held in one machine, not spread over several flags, a sequence number and a delay. A delay
/// that must match an animation's length is only a convention, and a convention fails quietly. If it failed
/// here, stopping playback could press the house colour onto a record still sitting on the deck, in front
/// of the listener.</para>
///
/// <para>So it is a machine, and it is separate from anything that can draw. Every arrival (a track
/// starting, a sleeve decoding, a pressing landing, a flight finishing) comes in as a method that only
/// remembers it. Deciding is a second step, <see cref="Reconsider"/>. The split is needed because a record
/// is described in more than one call. The sleeve carries its colour and label, and the folder's disc.png
/// carries its cd. A machine that acted on the first call would act on half a record. That is how an album
/// could come down wearing its house pressing with no cd, while its own cd was in hand. The holder asks
/// once, when the caller has finished speaking.</para>
///
/// <para>Only <see cref="Settle"/> produces a <see cref="Move"/>, only its <see cref="Deck.Off"/> branch
/// can set <see cref="Move.Dress"/>, and nothing else in the app may touch what the record wears (see
/// <c>RecordDisc.Turntable</c>, the only holder of one of these, and the only thing that can reach the
/// layers a face is made of).</para>
///
/// <para>Having no pictures in it makes it testable. A sequence of arrivals can be sent to it in any order,
/// and the guarantee is checked directly. <c>RecordFaceTests</c> does exactly that.</para>
/// </summary>
public sealed class RecordChange
{
    /// <param name="onDeck">Whether a record is already down when this takes over. False for a fresh
    /// library, which is stopped. The disc is parked in the corner and the drawer is closed.</param>
    public RecordChange(bool onDeck)
    {
        Where = onDeck ? Deck.On : Deck.Off;
        _wantOn = onDeck;
    }

    /// <summary>Where the record is right now. Set here and nowhere else: a flight is ordered by this
    /// machine, so this machine is what knows one is under way.</summary>
    public Deck Where { get; private set; }

    /// <summary>The face the record should be wearing, for whoever has to actually put it on.</summary>
    public RecordFace Wanted { get; private set; } = RecordFace.None;

    /// <summary>Whether the vinyl in hand is the wrong colour for the wanted record. The pressing is an
    /// asset decode and cannot be done at the moment the disc is off the deck, because that moment is one
    /// frame long. So it is done ahead of time, and <see cref="Pressed"/> says when it has
    /// landed.</summary>
    public bool NeedsPress => !_pressed || _pressedHue != Wanted.Hue;

    private bool _wantOn;
    private RecordFace _worn = RecordFace.None;
    private bool _dressed;
    private bool _pressed;
    private Windows.UI.Color? _pressedHue;

    /// <summary>True from <see cref="Cue"/> until the record is ready to go down. Without it, a record cued
    /// onto an empty deck drops at once, wearing the last thing it wore. It would then have to come
    /// straight back off to be changed: two flights, and the first one shows the wrong record.
    ///
    /// <para>It is cleared when the record is dressed, not when the sleeve arrives. A sleeve is only half
    /// of a face. Dropping between the two would land the record in the last album's colour, wearing this
    /// album's label. It is also cleared by <see cref="StopWaiting"/>, when nothing is coming at
    /// all.</para></summary>
    private bool _expecting;

    /// <summary>A record is wanted on the deck: something is playing, and a sleeve for it is on its
    /// way.</summary>
    public void Cue()
    {
        _wantOn = true;
        _expecting = true;
    }

    /// <summary>The deck is emptying: nothing is playing any more.</summary>
    public void Park()
    {
        _wantOn = false;
        _expecting = false;
    }

    /// <summary>This is the record that belongs on the deck now. A request, never a write, when it happens,
    /// and whether it happens at all, is this machine's answer and not the caller's.</summary>
    public void Wear(RecordFace face) => Wanted = face;

    /// <summary>The vinyl for a colour has been pressed and is in hand.</summary>
    public void Pressed(Windows.UI.Color? hue)
    {
        _pressed = true;
        _pressedHue = hue;
    }

    /// <summary>A flight finished: the record is wherever it was going.</summary>
    public void Landed()
        => Where = Where switch
        {
            Deck.Lifting => Deck.Off,
            Deck.Dropping => Deck.On,
            _ => Where,
        };

    /// <summary>No sleeve is coming after all. The deck has waited long enough and puts the record down
    /// wearing what it has. A track whose artwork pass failed is still a track that is playing, and an
    /// empty corner is a worse answer than a record with the wrong label. Whatever arrives afterwards is an
    /// ordinary change of record, off the deck like any other.</summary>
    public void StopWaiting() => _expecting = false;

    /// <summary>
    /// What to do about everything that has been said since the last time this was asked.
    ///
    /// <para>Asked once per message, not once per arrival, see the class note. The record may have been
    /// described in several calls and half a description is not a record.</para>
    /// </summary>
    public Move Reconsider() => Settle();

    /// <summary>Whether the disc down there is the wrong record, the only question that starts a
    /// flight.</summary>
    private bool WrongRecord => !_dressed || !_worn.IsSameRecord(Wanted);

    /// <summary>Whether a change of face is both needed and possible. Possible means the vinyl for it is
    /// already pressed, because half a face is not a face. A record that came down in the last album's
    /// colour wearing this album's label is the failure this whole rule exists to prevent.</summary>
    private bool CanDress => WrongRecord && !NeedsPress;

    /// <summary>
    /// The rule itself. Every arrival ends here, and the answer depends only on where the record is, never
    /// on how it got there, and never on how long ago.
    /// </summary>
    private Move Settle()
    {
        switch (Where)
        {
            // In the air, and visible for the whole of it. Nothing is decided mid-flight. Landed calls this
            // again when the flight ends.
            case Deck.Lifting:
            case Deck.Dropping:
                return Move.Nothing;

            // Off the deck: the one place a face may change, so this is the only branch that may say so.
            case Deck.Off:
                if (CanDress)
                {
                    _worn = Wanted;
                    _dressed = true;
                    _expecting = false;   // it is ready now; whatever it was waiting for has come
                    return new Move(Dress: true, _wantOn ? Flight.Drop : Flight.Stay);
                }

                // Down only when it is wearing the right record. WrongRecord here means a change still
                // waiting on its pressing. A disc that drops during that wait arrives in the last album's
                // colour. That is the same failure as changing the record on screen, reached from the other
                // side.
                return _wantOn && _dressed && !_expecting && !WrongRecord
                    ? new Move(false, Flight.Drop)
                    : Move.Nothing;

            // On the deck, where nothing about it may change. If it is the wrong record, or if the deck is
            // emptying, it comes off, and the change happens off the deck.
            case Deck.On:
                return !_wantOn || CanDress ? new Move(false, Flight.Lift) : Move.Nothing;

            default:
                return Move.Nothing;
        }
    }

    /// <summary>Records the flight a <see cref="Move"/> ordered, so <see cref="Where"/> is true from the
    /// moment it is ordered rather than from the moment it is obeyed. Called by the holder as it carries
    /// the move out; splitting it off that way is what lets the answer be a value.</summary>
    public void Flying(Flight flight)
        => Where = flight switch
        {
            Flight.Lift => Deck.Lifting,
            Flight.Drop => Deck.Dropping,
            _ => Where,
        };
}
