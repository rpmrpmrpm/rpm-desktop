using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

using RPM.Desktop.Presentation.NowPlaying;

namespace RPM.Desktop.Presentation.Controls;

public sealed partial class RecordDisc
{
    /// <summary>The deck the record sits on. It flies the disc between the corner and the turntable,
    /// presses the vinyl that each album needs, and changes the record's face. It is the only thing that
    /// can change the face.
    ///
    /// <para>It is nested inside <see cref="RecordDisc"/> on purpose. <see cref="RecordDisc.PutOn"/> is
    /// private, so being nested is the whole of its privilege. No other type in the app can reach the
    /// layers that make up a face. The rule this protects is that a record's face changes only where nobody
    /// can see it change. That rule is decided by <see cref="RecordChange"/>. It has no pictures in it, so
    /// it can be tested directly. This class only carries out what it says.
    /// </para>
    ///
    /// <para>Everything else about the record is left outside. ArtDisplay still sizes it, places it and
    /// turns it. Those are things a record does in plain sight.</para>
    /// </summary>
    public sealed class Turntable
    {
        /// <summary>How long the disc takes to reach the corner, or to come back. This is THE clock for the
        /// stopped and playing transition. The shell's drawer and the album wash use it too.</summary>
        public static readonly TimeSpan FlyDuration = TimeSpan.FromSeconds(NowPlaying.ArtDisplay.TransitionSeconds);

        /// <summary>How small the disc is when it has flown into the corner.</summary>
        private const double ExitScale = 0.24;

        /// <summary>How long a cued record waits for its sleeve before it goes down without one. The wait
        /// is generous. The sleeve is a local decode, and it arrives well inside this time. Waiting a
        /// moment too long costs an empty corner. Not waiting costs the wrong record on screen.</summary>
        private static readonly TimeSpan SleevePatience = TimeSpan.FromMilliseconds(1500);

        private readonly RecordDisc _disc;
        private readonly RecordChange _change;
        private readonly ScaleTransform _fly = new();
        private readonly TranslateTransform _wobble = new();

        private DispatcherTimer? _patience;
        private ImageSource? _vinyl;
        private int _press, _flight;

        /// <summary>The colour that is in the press right now, when there is one. <see cref="Carry"/> runs
        /// on every arrival, and <c>NeedsPress</c> stays true until the pressing comes back. Without this
        /// guard, one track change asked for the same disc three times: once for the sleeve, once for the
        /// CD, and once for the cue. Each request was a separate decode, Skia pass and encode, and they
        /// raced each other. The guard is checked before any work starts, so a second press of the same
        /// album and colour is not started.</summary>
        private bool _pressing;
        private Windows.UI.Color? _pressingHue;

        /// <param name="onDeck">Whether a record is already on the deck. False for a fresh library, which
        /// is stopped. The disc then starts parked in the corner, and drops as soon as a track
        /// plays.</param>
        public Turntable(RecordDisc disc, bool onDeck)
        {
            _disc = disc;
            disc.Claim(this);   // Nothing else can now write this record's face. See RecordDisc.
            _change = new RecordChange(onDeck);

            // The wobble orbit is applied to the disc's ROOT, so the record and its shadow orbit together
            // as one body. A cast shadow follows the thing that casts it, not the plinth. The orbit is
            // grouped BEFORE the fly scale. So on the deck it is a straight 1:1 orbit, and in the corner it
            // shrinks with everything else.
            var group = new TransformGroup();
            group.Children.Add(_wobble);   // The orbit comes first.
            group.Children.Add(_fly);      // Then the collapse toward the corner.
            _disc.RenderTransformOrigin = new Windows.Foundation.Point(1, 0);
            _disc.RenderTransform = group;

            _fly.ScaleX = _fly.ScaleY = onDeck ? 1 : ExitScale;
            _disc.Opacity = onDeck ? 1 : 0;

            // Something has to be on the spindle before the first track arrives. Otherwise the header shows
            // a label and a shadow with no record between them. Asking for the empty face gets the house
            // pressing. The record is off the deck, so it goes straight on.
            Wear(RecordFace.None);
        }

        /// <summary>Where the record is. This is for anything that needs to know whether it is safe to look
        /// at the record. By construction, nothing outside this class uses it.</summary>
        public Deck Where => _change.Where;

        /// <summary>A record is wanted on the deck, because playback has started. The disc does not drop
        /// yet. It waits for the sleeve, so that it lands already wearing it.</summary>
        public void Cue()
        {
            _change.Cue();
            Later();

            // Wait, but not forever. See SleevePatience.
            _patience?.Stop();
            _patience = new DispatcherTimer { Interval = SleevePatience };
            _patience.Tick += (_, _) =>
            {
                StopPatience();
                _change.StopWaiting();
                Later();
            };
            _patience.Start();
        }

        /// <summary>The deck is emptying, because playback has stopped. The record comes off. Whatever it
        /// wears next goes on once it is up there.</summary>
        public void Park()
        {
            StopPatience();
            _change.Park();
            Later();
        }

        /// <summary>This is the record that belongs on the deck now. When it actually goes on is decided
        /// elsewhere, not by this caller.</summary>
        public void Wear(RecordFace face)
        {
            _change.Wear(face);
            Later();
        }

        /// <summary>One frame of the eccentric-pressing orbit, driven from the spin angle.
        ///
        /// <para><b>A CD does not wobble.</b> The orbit is an eccentric PRESSING. That is a record stamped
        /// a hair off its own centre hole. A moulded disc has no such fault. A photographed CD that wobbled
        /// would look like the picture sliding about on top of the record, not like the record turning.
        /// Keeping the picture still is the one thing it is there for. This is decided here, not by the
        /// caller, because the record's face is this class's business and nothing else's.</para>
        ///
        /// <para><b>A record wearing its own LABEL still wobbles.</b> A label is a picture of the middle
        /// third, stuck onto the app's pressing. The pressing is a record, so the fault is still there, and
        /// the orbit is still correct. Only a picture of the WHOLE disc is different. So the question is
        /// asked of <see cref="ScannedDisc.CoversRecord"/>, not of whether there is a picture at
        /// all.</para>
        /// </summary>
        public void Wobble(double x, double y)
        {
            _wobble.X = _wearingCd ? 0 : x;
            _wobble.Y = _wearingCd ? 0 : y;
        }

        /// <summary>Whether the disc on the deck is a photographed CD: the whole face, not a label stuck on
        /// the pressing. It is written where the face goes on. So it describes what the record IS wearing,
        /// not what was asked for.</summary>
        private bool _wearingCd;

        /// <summary>The same orbit, when the compositor turns the record (the WinUI head).
        /// <paramref name="amplitudePx"/> is the amplitude in pixels. Zero means no orbit.
        ///
        /// <para>The compositor runs the orbit by itself once it has started, so nothing asks again. The
        /// turn keeps going across a track change, and the face changes in the middle of it.
        /// <see cref="Wobble"/> is called every frame and reads <see cref="_wearingCd"/> every frame, but
        /// the compositor does not. A caller that starts the compositor orbit directly never checks for a
        /// CD, so on the WinUI head every CD would wobble. This class keeps the amplitude. <see cref="Dress"/>
        /// decides the orbit again whenever the face changes.</para>
        /// </summary>
        public void Orbit(double amplitudePx)
        {
            _orbitPx = amplitudePx;
            ApplyOrbit();
        }

        /// <summary>The compositor orbit's amplitude in pixels, while the compositor turns the record. It
        /// is 0 while it does not.</summary>
        private double _orbitPx;

        private void ApplyOrbit()
        {
            if (_orbitPx > 0 && !_wearingCd)
                Common.Spin.Wobble(_disc, _disc.Turning, _orbitPx);
            else
                Common.Spin.Steady(_disc);
        }

        /// <summary>Asks the machine what to do, once, when the message that brought the news is over.
        ///
        /// <para>The call is queued, not answered on the spot. That is the whole purpose of this method. A
        /// record is described in more than one call: first the sleeve, then the folder's CD. Deciding
        /// inside the first call would decide about half a record. The disc would be dressed and dropped
        /// wearing its pressing and no CD. It would then have to come straight back up when the CD was
        /// mentioned a line later. This showed when the user switched tracks quickly, because the disc is
        /// then in the corner, where a change can happen at once.</para>
        ///
        /// <para>The UI thread runs one message to the end. So everything said in that message is known by
        /// the time this runs. Several arrivals in one message cost one decision, not one each.</para>
        /// </summary>
        private bool _asked;

        private void Later()
        {
            if (_asked)
                return;
            _asked = true;

            // The result is checked, and that is not just tidiness. _asked is cleared INSIDE the callback.
            // So if the enqueue does not take, _asked stays set. Every later call then returns at the line
            // above, for the rest of the run. The record would never drop, and nothing would say why.
            var queue = _disc.DispatcherQueue;
            var took = queue is not null && queue.TryEnqueue(() =>
            {
                _asked = false;
                Carry(_change.Reconsider());
            });

            if (!took)
            {
                _asked = false;
                RPM.Core.Diagnostics.Scope.Said(
                    "window", $"turntable could not queue: dispatcher {(queue is null ? "null" : "refused")}");
            }
        }

        /// <summary>Does what a move says to do, and starts the pressing that the next move will
        /// need.</summary>
        private void Carry(Move move)
        {
            RPM.Core.Diagnostics.Scope.Said(
                "window", $"turntable move: dress {move.Dress}, fly {move.Fly}, press {_change.NeedsPress}");

            if (move.Dress)
                Dress();

            if (move.Fly != Flight.Stay)
            {
                if (move.Fly == Flight.Drop)
                    StopPatience();   // It is going down, so it is not waiting for anything any more.
                Fly(move.Fly);
            }

            if (_change.NeedsPress)
                Press(_change.Wanted.Hue);
        }

        /// <summary>Asks for a colour, unless that colour is already being pressed.</summary>
        private void Press(Windows.UI.Color? hue)
        {
            if (_pressing && _pressingHue == hue)
                return;

            _pressing = true;
            _pressingHue = hue;
            _ = PressAsync(hue);
        }

        /// <summary>The one place in the app where a record's face changes.
        ///
        /// <para>The check is not a defence against callers. There is no caller except this file. It is the
        /// invariant, written where it could be broken. The day somebody calls
        /// <see cref="RecordChange.Settle"/> and adds a branch, the record must not start changing its
        /// clothes in front of the listener.</para>
        /// </summary>
        private void Dress()
        {
            Debug.Assert(_change.Where == Deck.Off, "a record's face may only change while it is off the deck");
            if (_change.Where != Deck.Off || _vinyl is null)
                return;

            _disc.PutOn(_vinyl, _change.Wanted.Label, _change.Wanted.Cd);
            _wearingCd = _change.Wanted.Cd is { CoversRecord: true };

            // The orbit was left where the last frame put it. A CD going on stops the frames that would
            // bring it back to the centre. So it is zeroed here. Otherwise the picture stays a little off
            // the middle for as long as the CD is on.
            if (_wearingCd)
                Wobble(0, 0);

            // The compositor's orbit is decided again for the new face. This is safe, because the record is
            // off the deck and cannot be seen.
            if (_orbitPx > 0)
                ApplyOrbit();
        }

        /// <summary>Sends the disc to the corner, or back to the deck. It asks the machine what to do next
        /// once the disc is actually there.
        ///
        /// <para>The next step waits for the FADE's own completion, not for a timer of the same length.
        /// They are not the same thing. An animation of 420 ms takes 420 ms of frames. The moment the
        /// record lifts is the busiest moment for the whole window: the drawer slides, the wash turns over,
        /// and the grids re-pin. A timer that fires while the disc is still at a third of its opacity would
        /// dress a record the user can still see. That shows as a sudden colour change.</para>
        ///
        /// <para>The OPACITY storyboard is the one that is awaited, because opacity is what "nobody can see
        /// it" means. The scales finish together with it, and one of the three has to be the answer.</para>
        /// </summary>
        private void Fly(Flight flight)
        {
            var inbound = flight == Flight.Drop;
            _change.Flying(flight);
            var gen = ++_flight;

            Tween(_fly, "ScaleX", inbound ? 1 : ExitScale);
            Tween(_fly, "ScaleY", inbound ? 1 : ExitScale);

            // This reports what the flight actually did to the disc, a moment after it should have landed.
            // From the outside, two things look the same. Either the tween did not move these values, or it
            // moved them and something else hides the disc. This line tells them apart.
            RPM.Core.Diagnostics.Scope.Said(
                "window",
                $"fly {flight} from opacity {_disc.Opacity:F2} scale {_fly.ScaleX:F2}");
            Services.Shell.Ticker.Once(_disc.DispatcherQueue, FlyDuration + TimeSpan.FromMilliseconds(250), () =>
                RPM.Core.Diagnostics.Scope.Said(
                    "window",
                    $"flew {flight}: opacity {_disc.Opacity:F2} scale {_fly.ScaleX:F2} " +
                    $"vis {_disc.Visibility} size {_disc.ActualWidth:F0}x{_disc.ActualHeight:F0}"));
            Tween(_disc, "Opacity", inbound ? 1 : 0, landed: () =>
            {
                // A flight that was replaced before it finished still runs to its end, and it still reports
                // that. Nothing here stops a storyboard, because stopping one resets the property to its
                // base value for a frame. Only the flight that is still current may land.
                if (gen != _flight)
                    return;
                _change.Landed();
                Later();
            });
        }

        /// <summary>Presses the album's vinyl ahead of the moment it is needed. The disc is off the deck
        /// for only one frame, and an asset decode does not fit in one frame.
        ///
        /// <para>The yield is the load-bearing line. A colour that was pressed before comes back from a
        /// cache. Without the yield, the await would finish ON THE SAME STACK. So the pressing would land,
        /// the record would be dressed, and the whole change would be decided inside the call that only
        /// wanted to say what the sleeve was, before the caller had mentioned the CD. Without the
        /// yield, a record could miss its CD cover whenever its colour was already in the cache.</para>
        ///
        /// <para>Posting the rest to the dispatcher makes this impossible rather than unlikely. The UI
        /// thread runs one message to the end. So a continuation queued behind it cannot cut into a caller
        /// that is still describing the record.</para>
        /// </summary>
        private async Task PressAsync(Windows.UI.Color? hue)
        {
            var gen = ++_press;
            await Task.Yield();
            if (gen != _press)
                return;   // A different colour was asked for before this one got going.

            // No hue is not a failure. A sleeve with nothing to say about its own colour gets the house
            // pressing, not a black disc. The house pressing is the app's azure, pressed EXACTLY. The
            // colour was chosen, not read off a sleeve, so it must not be brightened.
            var source = hue is { } colour
                ? await NowPlaying.VinylPressing.PressAsync(colour)
                : await NowPlaying.VinylPressing.PressBodyAsync(NowPlaying.VinylPressing.DefaultHue);

            if (gen != _press)
                return;   // A newer colour was asked for while this one was pressing.

            _pressing = false;

            // A colour that will not press is not a reason to leave the record parked. The record keeps the
            // pressing it already has. The machine is told that this colour is settled, so the change goes
            // through with the wrong vinyl rather than not at all. Only the very first press of a session
            // has nothing to fall back on. Then there is truly nothing to put on the spindle.
            if (source is not null)
                _vinyl = source;
            if (_vinyl is not null)
            {
                _change.Pressed(hue);
                Later();
            }
        }

        private void StopPatience()
        {
            _patience?.Stop();
            _patience = null;
        }

        /// <summary>One dependent-animation tween on the shared flight clock. The callback runs only when
        /// the tween has really finished.</summary>
        private static void Tween(DependencyObject target, string property, double to, Action? landed = null)
        {
            var anim = new DoubleAnimation
            {
                To = to,
                Duration = new Duration(FlyDuration),
                EnableDependentAnimation = true,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(anim, target);
            Storyboard.SetTargetProperty(anim, property);

            var storyboard = new Storyboard();
            storyboard.Children.Add(anim);
            if (landed is not null)
                storyboard.Completed += (_, _) => landed();
            Services.Shell.Storyboards.Play(storyboard);
        }
    }
}
