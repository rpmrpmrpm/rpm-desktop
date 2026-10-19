using RPM.Desktop.Presentation.NowPlaying;

namespace RPM.Desktop.Tests;

/// <summary>
/// The one rule a record change obeys: <b>a record's face only ever changes where nobody can see it
/// change.</b>
///
/// <para>The rule is kept out of the view, in <see cref="RecordChange"/>. That class has no pictures in it,
/// so it can be asked directly. A mistake here fails quietly. Stopping playback could press the house azure
/// onto a record still sitting on the deck, so it turns blue in front of the listener.</para>
///
/// <para><see cref="Never_dresses_a_record_that_is_on_screen"/> is the one that matters. The rest are
/// the journeys it has to get right on the way.</para>
/// </summary>
public class RecordFaceTests
{
    private static readonly RecordFace Blue = new("Kind of Blue", Colors(0x2A, 0x4B, 0x8D), null, null);
    private static readonly RecordFace Red = new("Red", Colors(0x8D, 0x2A, 0x2A), null, null);

    private static Windows.UI.Color Colors(byte r, byte g, byte b)
        => Windows.UI.Color.FromArgb(0xFF, r, g, b);

    /// <summary>Stands in for a decoded picture. Nothing here looks at it. What matters about a cd is that
    /// there IS one. This host has no application to build a real bitmap in. Whether it covers the whole
    /// record is the drawing layer's question (see <c>RecordDisc.PutOn</c>). The machine only counts
    /// it.</summary>
    private static readonly ScannedDisc? AnyPicture = new ScannedDisc(
        (Microsoft.UI.Xaml.Media.Imaging.BitmapImage)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(Microsoft.UI.Xaml.Media.Imaging.BitmapImage)),
        CoversRecord: true);

    // ── the rule ──

    /// <summary>
    /// Every ordering of every arrival, walked exhaustively to a fixed depth, asserting the one thing:
    /// no move that dresses the record ever comes back while it is anywhere but off the deck.
    ///
    /// <para>Brute force on purpose. The failure this guards against is not a wrong branch. It is an ORDER
    /// nobody had thought of, such as a sleeve landing between a stop and the lift it triggers. The only
    /// honest way to test for an order nobody thought of is to try them all.</para>
    /// </summary>
    [Fact]
    public void Never_dresses_a_record_that_is_on_screen()
    {
        // Every sequence of six arrivals, in every order, from both starting states. This is brute force on
        // purpose. The risk is an ORDER nobody has thought of, such as a sleeve landing between a stop and
        // the lift it triggers. The only honest way to test for that is to try them all.
        const int depth = 6;
        var trails = 0;

        foreach (var onDeck in new[] { false, true })
            foreach (var trail in Every(depth))
            {
                trails++;
                var change = new RecordChange(onDeck);
                foreach (var step in trail)
                {
                    var move = Arrivals[step].Fire(change);

                    if (move.Dress)
                        Assert.True(change.Where == Deck.Off,
                                    $"dressed a record that was {change.Where}, "
                                    + $"from {(onDeck ? "playing" : "stopped")} through {Read(trail)}");

                    change.Flying(move.Fly);
                }
            }

        Assert.Equal(2 * (int)Math.Pow(Arrivals.Length, depth), trails);
    }

    /// <summary>Every trail of <paramref name="depth"/> arrivals, as indices into <see cref="Arrivals"/>.
    /// A trail of six covers every shorter one as a prefix, so nothing is missed by not walking them
    /// separately.</summary>
    private static IEnumerable<int[]> Every(int depth)
    {
        var trail = new int[depth];
        while (true)
        {
            yield return trail;

            var at = depth - 1;
            while (at >= 0 && ++trail[at] == Arrivals.Length)
                trail[at--] = 0;
            if (at < 0)
                yield break;
        }
    }

    private static string Read(int[] trail) => string.Join(" → ", trail.Select(i => Arrivals[i].Name));

    /// <summary>Everything that can land on its own clock, as far as the record is concerned. The stale
    /// press is in there deliberately: a pressing that finishes for a colour nobody wants any more is an
    /// ordinary thing to happen and must not dress anything.</summary>
    private static readonly (string Name, Func<RecordChange, Move> Fire)[] Arrivals =
    [
        ("cue", Told(c => c.Cue())),
        ("park", Told(c => c.Park())),
        ("wear-blue", Told(c => c.Wear(Blue))),
        ("wear-red", Told(c => c.Wear(Red))),
        ("wear-blue-cd", Told(c => c.Wear(Blue with { Cd = AnyPicture }))),
        ("wear-none", Told(c => c.Wear(RecordFace.None))),
        ("pressed", Told(c => c.Pressed(c.Wanted.Hue))),
        ("pressed-stale", Told(c => c.Pressed(Red.Hue))),
        ("landed", Told(c => c.Landed())),
        ("stop-waiting", Told(c => c.StopWaiting())),

        // …and one step that only DECIDES. The holder asks once per message however many arrivals that
        // message carried, so a decision reached after two pieces of news, or after none, is an
        // ordering that really happens and belongs in the walk with the rest.
        ("reconsider", c => c.Reconsider()),
    ];

    /// <summary>An arrival is only remembered, which is all an arrival does. Deciding is the separate step
    /// above. A trail that never takes that step is a half-told record, and the split exists to prevent
    /// that.</summary>
    private static Func<RecordChange, Move> Told(Action<RecordChange> arrival)
        => c => { arrival(c); return Move.Nothing; };

    // ── the journeys ──

    [Fact]
    public void A_first_record_waits_for_its_sleeve_and_lands_wearing_it()
    {
        var change = new RecordChange(onDeck: false);

        // Nothing playing: the deck presses the house pressing onto the parked disc and leaves it there.
        Fire(change, c => c.Wear(RecordFace.None));
        Assert.True(Fire(change, c => c.Pressed(null)).Dress);
        Assert.Equal(Deck.Off, change.Where);

        // Play. The disc must NOT drop yet: it has nothing on it but the empty pressing.
        Assert.Equal(Flight.Stay, Fire(change, c => c.Cue()).Fly);

        // The sleeve arrives, then its pressing…
        Fire(change, c => c.Wear(Blue));
        var move = Fire(change, c => c.Pressed(Blue.Hue));

        // …and the record is dressed and sent down in the same breath. One flight, and it lands wearing
        // its own label rather than fading one on afterwards.
        Assert.True(move.Dress);
        Assert.Equal(Flight.Drop, move.Fly);
        Assert.Equal(Deck.Dropping, change.Where);

        Fire(change, c => c.Landed());
        Assert.Equal(Deck.On, change.Where);
    }

    [Fact]
    public void A_change_of_album_lifts_first_and_dresses_up_there()
    {
        var change = Playing(Blue);

        Fire(change, c => c.Wear(Red));
        var lift = Fire(change, c => c.Pressed(Red.Hue));
        Assert.False(lift.Dress);                 // NOT on the deck
        Assert.Equal(Flight.Lift, lift.Fly);

        var dressed = Fire(change, c => c.Landed());
        Assert.True(dressed.Dress);               // …only now, off the deck
        Assert.Equal(Flight.Drop, dressed.Fly);
    }

    [Fact]
    public void The_next_track_of_the_same_album_does_not_move_the_record()
    {
        var change = Playing(Blue);

        // A fresh decode of the same sleeve: a new value, the same RECORD.
        var again = Blue with { Label = null, Cd = null };
        Assert.Equal(Move.Nothing, Fire(change, c => c.Wear(again)));
        Assert.Equal(Deck.On, change.Where);
    }

    /// <summary>Stopping is the risky case. The empty face must not be pressed on while the record is still
    /// in plain sight, or it turns blue on screen.</summary>
    [Fact]
    public void Stopping_takes_the_record_off_before_it_goes_back_to_the_house_pressing()
    {
        var change = Playing(Blue);

        // The sleeve empties first (SetText then ShowCover(null)), and the press for it lands…
        Fire(change, c => c.Wear(RecordFace.None));
        var move = Fire(change, c => c.Pressed(null));

        Assert.False(move.Dress);                 // NOT while it is down there
        Assert.Equal(Flight.Lift, move.Fly);

        // …then the display goes idle, and only at the top does the face change.
        Fire(change, c => c.Park());
        var up = Fire(change, c => c.Landed());
        Assert.True(up.Dress);
        Assert.Equal(Flight.Stay, up.Fly);        // and it stays up: nothing is playing
        Assert.Equal(Deck.Off, change.Where);
    }

    [Fact]
    public void A_sleeve_that_never_comes_still_puts_the_record_down()
    {
        var change = new RecordChange(onDeck: false);
        Fire(change, c => c.Wear(RecordFace.None));
        Fire(change, c => c.Pressed(null));

        Assert.Equal(Flight.Stay, Fire(change, c => c.Cue()).Fly);   // waiting

        // The artwork pass died. An empty corner is a worse answer than a plain record.
        Assert.Equal(Flight.Drop, Fire(change, c => c.StopWaiting()).Fly);
    }

    /// <summary>
    /// The cd is read a moment after the sleeve, and it can arrive after the record is already dressed for
    /// its album. A change must not then be decided before the cd is mentioned. Otherwise the album shows
    /// the drawn record instead of its photographed cd. This happens when the pressing comes from a
    /// cache, because the whole change is then decided before the cd has been mentioned.
    /// </summary>
    [Fact]
    public void A_cd_that_arrives_after_the_record_was_dressed_still_goes_on()
    {
        var change = new RecordChange(onDeck: false);
        Fire(change, c => c.Cue());

        // The sleeve, and its pressing straight back out of the cache: the record goes down without a cd.
        Fire(change, c => c.Wear(Blue));
        var down = Fire(change, c => c.Pressed(Blue.Hue));
        Assert.True(down.Dress);
        Assert.Equal(Flight.Drop, down.Fly);
        Fire(change, c => c.Landed());

        // Then the folder's disc.png turns up. Same album, same colour. It is still a different record,
        // because one is wearing a photographed cd and the other is not.
        var withCd = Blue with { Cd = AnyPicture };
        Assert.Equal(Flight.Lift, Fire(change, c => c.Wear(withCd)).Fly);

        var dressed = Fire(change, c => c.Landed());
        Assert.True(dressed.Dress);
        Assert.Equal(Flight.Drop, dressed.Fly);
    }

    /// <summary>
    /// A record is described in two calls: the sleeve, then the folder's cd. Both are in the same message.
    /// Deciding after the first call decides about half a record. The disc goes down in its pressing with
    /// no cd, and has to come straight back up when the cd is mentioned a line later.
    ///
    /// <para>The symptom is the plain record appearing instead of the cd while the user switches quickly.
    /// Switching quickly is when the disc is up in the corner, where a change can happen at once. The
    /// colour is the house pressing when the sleeve has none to give.</para>
    /// </summary>
    [Fact]
    public void A_sleeve_and_its_cd_in_one_message_are_one_change()
    {
        var sleeve = new RecordFace("Blue Train", null, null, null);
        var andItsCd = sleeve with { Cd = AnyPicture };

        // Parked, wearing the house pressing. That is the state a stopped player sits in, and it leaves the
        // press for "no colour" already in hand. That is the case with no press to wait for, so the first
        // half of a description can be acted on the moment it arrives.
        var change = Parked();
        Fire(change, c => c.Cue());

        var move = Fire(change, c => c.Wear(sleeve), c => c.Wear(andItsCd));

        Assert.True(move.Dress);
        Assert.Equal(Flight.Drop, move.Fly);
        Assert.NotNull(change.Wanted.Cd);   // it goes down wearing its cd, not without it

        // …and once down, nothing more is owed. No second flight to fetch the cd.
        Fire(change, c => c.Landed());
        Assert.Equal(Move.Nothing, change.Reconsider());

        // The contrast, and the reason the two steps are separate at all: a machine asked after only the
        // first half answers about half a record, and sends it down with no cd on it.
        var early = Parked();
        Fire(early, c => c.Cue());
        Assert.Equal(Flight.Drop, Fire(early, c => c.Wear(sleeve)).Fly);
        Assert.Null(early.Wanted.Cd);
    }

    [Fact]
    public void A_third_album_arriving_mid_flight_is_the_one_that_lands()
    {
        var change = Playing(Blue);

        Fire(change, c => c.Wear(Red));
        Fire(change, c => c.Pressed(Red.Hue));       // lifts for Red
        Assert.Equal(Deck.Lifting, change.Where);

        // …and while it is on its way up, a third record is asked for.
        var third = new RecordFace("Blue Train", Colors(0x11, 0x33, 0x77), null, null);
        Assert.Equal(Move.Nothing, Fire(change, c => c.Wear(third)));

        // Its pressing is not in yet, so landing dresses nothing. The record waits off the deck rather than
        // coming down in the wrong colour.
        var landed = Fire(change, c => c.Landed());
        Assert.False(landed.Dress);
        Assert.Equal(Deck.Off, change.Where);

        var dressed = Fire(change, c => c.Pressed(third.Hue));
        Assert.True(dressed.Dress);
        Assert.Equal(Flight.Drop, dressed.Fly);
    }

    // ── helpers ──

    /// <summary>Tells the machine one thing and then asks it what to do, which is the pair the turntable
    /// makes for a message carrying a single arrival.</summary>
    private static Move Fire(RecordChange change, Action<RecordChange> arrival)
    {
        arrival(change);
        var move = change.Reconsider();
        change.Flying(move.Fly);
        return move;
    }

    /// <summary>Two arrivals in ONE message, decided once at the end: a sleeve and the cd that follows
    /// it, which is how every record is actually described.</summary>
    private static Move Fire(RecordChange change, Action<RecordChange> first, Action<RecordChange> then)
    {
        first(change);
        then(change);
        var move = change.Reconsider();
        change.Flying(move.Fly);
        return move;
    }

    /// <summary>A stopped player: the disc in the corner wearing the house pressing, that pressing in
    /// hand. Where the app sits before anybody presses play.</summary>
    private static RecordChange Parked()
    {
        var change = new RecordChange(onDeck: false);
        Fire(change, c => c.Wear(RecordFace.None), c => c.Pressed(null));
        Assert.Equal(Deck.Off, change.Where);
        return change;
    }

    /// <summary>A machine with <paramref name="face"/> on the deck and playing: the state most of these
    /// start from.</summary>
    private static RecordChange Playing(RecordFace face)
    {
        var change = new RecordChange(onDeck: false);
        Fire(change, c => c.Cue());
        Fire(change, c => c.Wear(face));
        Fire(change, c => c.Pressed(face.Hue));
        Fire(change, c => c.Landed());
        Assert.Equal(Deck.On, change.Where);
        return change;
    }
}
