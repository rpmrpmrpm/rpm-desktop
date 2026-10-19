using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using RPM.Core.Diagnostics;
using RPM.Core.Viz;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The background of a banner card: the room from Pegasus, drawn in the card's own colour. It drifts
/// behind the writing.
///
/// <para>The figures come from <see cref="CardRoomArt"/>, which is <see cref="RoomSource"/> with an entry
/// point around it. This class decides the size that the room is drawn at, how often it is drawn, and
/// whether it is drawn at all.</para>
///
/// <para><b>It drifts on both heads.</b> Uno's Skia renderer has no dirty rectangles, so any frame request
/// repaints the whole window. The repaint is still affordable, so the drift runs on every head. Any
/// caller can turn it off with <see cref="Drift"/>, for a screen that needs the processor time.</para>
///
/// <para>A frame is cheap because of what is drawn, not because of which head draws it. The picture is a
/// small <see cref="WriteableBitmap"/> that the composition scales up, so the shader runs at a tenth of
/// each side. The supporter badge's shine and the cloud badges' pulse do not run from
/// <c>CompositionTarget.Rendering</c>. That is a separate rule: frames must not be requested without
/// end.</para>
///
/// <para><b>Every card with a room moves, and all of them move the same way.</b> A room is drawn at one
/// size and one rate, whichever of the three hosts it is in. The cards behind the plate must not be drawn
/// more cheaply. Half the resolution removes the third family of figures altogether, so a card behind
/// the plate would run a different animation. One tick in four also looks slower, even when the drift is
/// the same. A frame takes about 3.5 ms at a tenth of a 600-point
/// plate. The strip costs that times the number of its cards that are generated lists. That is usually one,
/// and at most three.</para>
///
/// <para><see cref="Live"/> no longer changes what is drawn. It stays because the timing line is split by
/// it. It is also the first thing to use if a room ever needs to be cheaper, by using a rate for each host.
/// See <c>HomeView.ShowRooms</c>. Whether a room draws at all is decided by <see cref="Shown"/>. That is a
/// different question, and it is asked of every card.</para>
///
/// <para><b>Nothing here leaves the drawing thread.</b> A frame is drawn inside a tick, and it is not worth
/// handing to another thread. A worker would gain nothing, and it would add the question of what happens
/// when a frame arrives for a card that has been scrolled away. So the only question left is whether this
/// element is still loaded, sized and wanted. <see cref="Settle"/> asks all of this again on every arrival.
/// It does not remember the order in which the answers came.</para>
/// </summary>
public sealed partial class CardRoom : BorderBase
{
    /// <summary>
    /// The picture, as an <see cref="Image"/> and not as a background brush.
    ///
    /// <para><b>Do not paint it as an <see cref="ImageBrush"/> on this element's own Background.
    /// The room would draw correctly, but it would never move.</b> A brush takes its surface from its
    /// source. Writing new pixels into the bitmap that it holds does not bring the next frame to the
    /// screen. Giving it a different bitmap does not either. An <c>Image</c> with a <see cref="WriteableBitmap"/> source is the
    /// path that WinUI supports for a picture that changes. Every other animated bitmap is written against
    /// it.</para>
    ///
    /// <para>The picture is inside a <see cref="Border"/> of its own, because of what this element is on
    /// each head. On Skia, <c>BorderBase</c> is a Border, and it rounds its child. On WinUI it is a Grid. A
    /// Grid's CornerRadius rounds its background, but it does not clip what is inside it. Without the
    /// Border, the room would have square corners over a card with round corners. That happens on WinUI
    /// only.</para>
    /// </summary>
    private readonly Image _picture = new()
    {
        Stretch = Stretch.Fill,
    };

    private readonly Border _round = new();

    /// <summary>Two bitmaps, drawn into in turn. So the picture is never handed the object that it is
    /// already holding.</summary>
    private WriteableBitmap? _shown;

    private WriteableBitmap? _spare;

    /// <summary>The surface that the room is drawn on, and the buffer under it. Both are made once for each
    /// size. See <see cref="CardRoomArt.Pass"/> for the cost of making them for each frame.</summary>
    private CardRoomArt.Pass? _pass;

    /// <summary>The palette that the picture on screen was drawn in. This lets the element notice when a
    /// different card is handed to it. The element stays in place, and only the binding changes. A room
    /// that redrew only on a resize would keep the colours of the last card.</summary>
    private RoomPalette? _painted;

    /// <summary>The time at which the picture on screen was drawn. When this card steps into the plate, the
    /// drift continues from this time. So going live does not jump to wherever the shared clock
    /// is.</summary>
    private float _moment;

    /// <summary>This room's own zero on the shared clock, set when it joins.</summary>
    private float _origin;

    private bool _beating;
    private bool _drawn;

    /// <summary>The size, in room pixels, of the picture on screen. <see cref="Settle"/> reads it to tell a
    /// resize from a first draw. This is not the size of <see cref="_pass"/>. A still room hands its
    /// picture to
    /// <see cref="Kept"/> and drops its pass right after drawing. So a missing pass does not mean that
    /// nothing has been drawn.</summary>
    private (int Width, int Height) _drawnAt;

    /// <summary>Whether the drift's starting moment has been settled. See <see cref="Settle"/>. It is
    /// settled the first time this room is wanted, not when its first fact arrives.</summary>
    private bool _posed;

    /// <summary>When this element was last given a new size. <see cref="Settle"/> reads it to tell a resize
    /// that is still happening from one that has finished.</summary>
    private long _resized;

    /// <summary>The one-shot timer that asks <see cref="Settle"/> again once a resize has stopped. It is
    /// made on first use and re-armed, not replaced. So a drag holds one timer, however many sizes it goes
    /// through.</summary>
    private DispatcherTimer? _afterResize;

    /// <summary>
    /// A ground asks for no room of its own.
    /// </summary>
    /// <remarks>
    /// <b>Without this, the element can decide the size of the thing it stands behind.</b> The picture is
    /// an <see cref="Image"/> that holds a <see cref="WriteableBitmap"/>. An Image reports the bitmap's own
    /// size as the size it would like. A background brush reports nothing. When the element is put inside
    /// a panel that measures its children, such as a Grid row with Auto height, the room is measured with a finite width and an infinite
    /// height. The room then reports the bitmap's aspect applied to the width on offer. The header sized
    /// itself from that. The room was rebuilt at the header's new size, and the aspect held, so the height
    /// simply followed the width. For example, a header 205 points tall in a 1,085 point pane came out 549
    /// points tall when the window was maximised. That is 2,330 divided by the room's own aspect of 4.2.
    ///
    /// <para>The child is still measured, so it is laid out. Only the answer is discarded. Each host of
    /// this element puts real content in front of it, and takes its size from somewhere else. A card takes
    /// its size from the row it is in, a header from its cover square, and the wizard from the window. So
    /// nothing is sized BY the room.</para>
    /// </remarks>
    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(availableSize);
        return new Size(0, 0);
    }

    public CardRoom()
    {
        IsHitTestVisible = false;
        _round.Child = _picture;
        _round.Visibility = Visibility.Collapsed;
        Child = _round;

        Loaded += (_, _) => Settle();
        Unloaded += (_, _) => Settle();

        // The time is stamped BEFORE Settle runs, because Settle reads it to decide whether the size has
        // stopped changing. See _resized.
        SizeChanged += (_, _) =>
        {
            _resized = Environment.TickCount64;
            Settle();
        };
    }

    /// <summary>The colours the room is painted in. It is null where this card has no colours, and then the
    /// room is not drawn at all. A record's card stands on its blurred sleeve, and then on a photograph of
    /// the act. This property is for the cards that have neither.</summary>
    /// <remarks> It is registered as <c>object</c>, not as <c>RoomPalette?</c>. Boxing a
    /// <c>Nullable&lt;T&gt;</c> boxes the T inside it. So the property system receives a boxed
    /// <see cref="RoomPalette"/>. A property declared with the nullable type would then fail its own type
    /// check.
    /// </remarks>
    public RoomPalette? Palette
    {
        get => GetValue(PaletteProperty) as RoomPalette?;
        set => SetValue(PaletteProperty, value);
    }

    public static readonly DependencyProperty PaletteProperty = DependencyProperty.Register(
        nameof(Palette), typeof(object), typeof(CardRoom),
        new PropertyMetadata(null, OnFactChanged));

    /// <summary>
    /// How many places along the drift this room starts from, past what its colours say.
    /// </summary>
    /// <remarks> Zero for a card whose colours are its own. It exists because two generated lists can hold
    /// the same colour scheme. <see cref="CoverRoom.ListRoom"/> names the pair that does. Two rooms in the
    /// same colours would otherwise start in the same pose. It is a dependency property, and not a plain
    /// one like <see cref="Pace"/>, because it differs for each card and is bound.
    /// </remarks>
    public int Apart
    {
        get => (int)GetValue(ApartProperty);
        set => SetValue(ApartProperty, value);
    }

    public static readonly DependencyProperty ApartProperty = DependencyProperty.Register(
        nameof(Apart), typeof(int), typeof(CardRoom), new PropertyMetadata(0, OnFactChanged));

    /// <summary>Whether this is the card in front. It does not change what is drawn. Every card draws the
    /// same room at the same rate. It is kept because the timing line is split by it. See the note on the
    /// class.</summary>
    public bool Live
    {
        get => (bool)GetValue(LiveProperty);
        set => SetValue(LiveProperty, value);
    }

    public static readonly DependencyProperty LiveProperty = DependencyProperty.Register(
        nameof(Live), typeof(bool), typeof(CardRoom), new PropertyMetadata(false, OnFactChanged));

    /// <summary>Whether the screen this card stands on is being looked at. False takes the room off the
    /// clock.</summary>
    /// <remarks>
    /// A separate fact from <see cref="Live"/>, and it has to be one. <c>Live</c> means "I am the card in
    /// front". That is false for the two cards beside it, and those two drift as well. Gating the clock on
    /// <c>Live</c> would freeze them. So <c>Live</c> does not decide this. Nothing else took over the
    /// decision.
    ///
    /// <para>It is pushed by <c>HomeView.ShowRooms</c> to EVERY card, and it is not read out of the tree
    /// here. An element is told nothing when an ANCESTOR is collapsed, and home is hidden by collapsing its
    /// view. Pushing it to every card is also what makes it safe. The value changes on all of them in the
    /// same turn, so every card settles. That includes a card whose <c>Live</c> was already false. A change
    /// to <c>Live</c> would never settle that card.</para>
    /// </remarks>
    public bool Shown
    {
        get => (bool)GetValue(ShownProperty);
        set => SetValue(ShownProperty, value);
    }

    public static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        nameof(Shown), typeof(bool), typeof(CardRoom), new PropertyMetadata(false, OnFactChanged));

    private static void OnFactChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CardRoom room)
            room.Settle();
    }

    /// <summary>
    /// What this element should be doing, worked out from what is true now: whether it is in the tree,
    /// whether it has a size, which colours it has been given, and whether it is the card in front.
    ///
    /// <para>Called from every arrival and safe to call twice. Membership of the clock is derived here
    /// rather than pushed from whichever handler noticed, which is what keeps a card that is unloaded
    /// and loaded again between two ticks from leaving a subscription behind.</para>
    /// </summary>
    private void Settle()
    {
        // Shown is checked here, and not only in the timing split. Without this check, the room keeps
        // drawing thirty frames a second behind a collapsed screen, for as long as the window is open. One
        // room drifted for a whole nine minute run at 3.35 ms a tick, while none of it was on screen. Keep
        // this check.
        var wanted = IsLoaded
                     && Shown
                     && Palette is not null
                     && ActualWidth > 0
                     && ActualHeight > 0
                     && CardRoomArt.Compiles;

        if (!wanted)
        {
            Beat(false);
            _round.Visibility = Visibility.Collapsed;
            _drawn = false;
            Watch(wanted);
            return;
        }

        // The corner that the card is cut to. It is copied, not bound. It is read again on every arrival,
        // like everything else here. So one number set in the markup cannot become two numbers.
        _round.CornerRadius = CornerRadius;

        if (Palette != _painted)
        {
            // The colours changed. Redraw, and leave the pose alone. The pose is settled below, once.
            // See the note there.
            _painted = Palette;
            _drawn = false;
        }

        // Where this room starts is settled ONCE, the first time the room is wanted. It is not settled when
        // a palette first arrives. Two facts decide it: the colours, and which list this is. They arrive as
        // two separate bindings, in an order that nothing here controls. Seeding on the first of them would
        // pose the room from half the answer.
        //
        // After that the drift carries on, and only the colours change. Most lists answer twice: the mark's
        // colour first, then the picture's. Restarting the pose on the second answer would make a recolour
        // look like the room being replaced. The same goes for a card handed to this host in place of
        // another. That is a step, and it already has a slide on it.
        //
        // BEFORE THE TABLE IS ASKED. This order is required. The moment is part of what decides the
        // picture, so it is part of the key. If the table were asked first, every card would look up a room
        // at moment zero, then draw and store one at its real moment. The two keys would never match, and
        // the table would never answer. It looks as if it works, because the rooms are right. They are
        // simply all being drawn.
        if (!_posed)
        {
            _posed = true;
            _moment = Still;
        }

        var (w, h) = CardRoomArt.Small(ActualWidth, ActualHeight, Fewest, Most);

        // A picture is on screen and the size has changed.
        //
        // NOT REDRAWN WHILE THE SIZE IS STILL MOVING. CardRoomArt.Small is a tenth of each side, so every
        // ten points of window width changes the integer. A rebuild creates a pinned array, a Skia surface
        // and two WriteableBitmaps. Each one would be thrown away ten points later. The cards must stay
        // smooth while the window is resized.
        //
        // Meanwhile, the picture already in hand is drawn. The Image stretches it to the element's new
        // bounds. For a moment its aspect is slightly wrong. That is not visible here. The room is a soft
        // field with no hard edge, and it is already magnified ten times.
        //
        // This must test _drawnAt, not _pass. A still room drops its pass as soon as it has drawn (see
        // Keep). If this test used _pass, every still room would skip the wait, and each one would be
        // rebuilt and redrawn on every frame of a resize. That is far too much work for one resize.
        if (_drawn && (w, h) != _drawnAt)
        {
            if (Environment.TickCount64 - _resized < AfterResizeMs)
            {
                AskAgainAfterTheResize();
                Beat(Drift);
                Watch(wanted);
                return;
            }

            _drawn = false;   // the size has settled: borrow or draw the room at it, below
        }

        // A still room that has already been drawn is shown as it is, and nothing is built. This is what
        // makes a wall of cards cheap. See Kept, which explains what a card costs without it. This check
        // comes before Rebuild below. A card that can borrow a picture does not need its own buffer or
        // surface.
        if (!Drift && !_drawn && Palette is { } wanted2 && Cached(KeyFor(wanted2, _moment, w, h)) is { } already)
        {
            _picture.Source = already;
            _drawn = true;
            _drawnAt = (w, h);
            _round.Visibility = Visibility.Visible;
            Watch(wanted);
            return;
        }

        // Build the room now if there is no pass at this size. That happens when nothing is on screen, or
        // when the size has settled (above). A room with a picture on screen and a size that is still
        // changing never gets here.
        if (_pass is null || w != _pass.Width || h != _pass.Height)
            Rebuild(w, h);

        // Every card that has a room joins the clock, on either head. How much time each card takes is
        // decided in Tick. A card whose Drift is off is drawn once and then left.
        Beat(Drift);

        // Draw now if nothing is on screen yet. Do not wait for a tick. A card behind the plate takes one
        // tick in four, and an eighth of a second with nothing on screen is visible.
        if (!_drawn)
            Draw(_moment);

        Watch(wanted);
    }

    /// <summary>How long a size must stay the same before the room is drawn at it again. It is long enough
    /// that a drag rebuilds once, at the end, and not at every tenth point. It is short enough that, when
    /// the user lets go of a window edge, the room appears at its proper size almost at once.</summary>
    private const int AfterResizeMs = 140;

    /// <summary>The surface, the buffer and the two bitmaps, all made for one size. Everything that costs
    /// anything about a size change is here. So the caller must be careful about when it runs.</summary>
    private void Rebuild(int width, int height)
    {
        _pass?.Dispose();
        _pass = new CardRoomArt.Pass(width, height);
        _shown = new WriteableBitmap(width, height);
        _spare = new WriteableBitmap(width, height);
        _drawn = false;
    }

    /// <summary>Arms the one-shot that re-asks <see cref="Settle"/> once the drag has stopped. Re-armed
    /// on every size, so it fires once after the last one rather than once per size.</summary>
    private void AskAgainAfterTheResize()
    {
        _afterResize ??= MakeAfterResize();
        if (_afterResize is not { } timer)
            return;

        // Stop before Start. Starting a DispatcherTimer that is already running does not restart it.
        // Without the Stop, the rebuild could land in the middle of the next drag, and not after it.
        timer.Stop();
        timer.Start();
    }

    private DispatcherTimer? MakeAfterResize()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(AfterResizeMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Settle();   // Asks every fact again, including whether the size is still the same
        };
        return timer;
    }

    /// <summary>The last line this element logged. This stops it from logging thirty lines a
    /// second.</summary>
    private string? _said;

    /// <summary>
    /// What this element concluded. It is logged to the diagnostic ring whenever the answer changes.
    /// </summary>
    /// <remarks> A room that draws once and never moves looks the same as a room whose clock never started.
    /// It also looks the same as a room that is never told it is in front, and as one whose picture reaches
    /// the screen only the first time. None of these can be told apart by looking at the card, and each
    /// needs a different fix. The log line says which fact was missing.
    ///
    /// <para>Read it with <c>rpmscope log --channel rooms</c>, next to the tick line that
    /// <see cref="CardRoomBeat"/> writes. An element that reports <c>live=True beating=True</c> while no
    /// tick line appears has a clock that did not start. An element that reports both while the card sits
    /// still has a picture that is not reaching the screen.</para>
    /// </remarks>
    private void Watch(bool wanted)
    {
        if (!Scope.On)
            return;

        var line = $"loaded={IsLoaded} size={ActualWidth:F0}x{ActualHeight:F0} "
                   + $"palette={Palette is not null} live={Live} shown={Shown} drifts={Drift} "
                   + $"compiles={CardRoomArt.Compiles} wanted={wanted} beating={_beating} "
                   + $"drawn={_drawn}";

        if (line == _said)
            return;

        _said = line;
        Scope.Said("rooms", line);
    }

    /// <summary>
    /// How close in this card is on the room, where one is the room as it was tuned.
    /// </summary>
    /// <remarks>
    /// <b>A card is a window onto the room</b>, and this is how wide that window is. A larger value is
    /// closer: fewer figures across the card, each one bigger. That is what makes a lobe read as a fall of
    /// light and not as a mottle.
    ///
    /// <para>The default is one, so nothing changes for a caller that does not set it.</para>
    ///
    /// <para>The value has a lower limit, and it is not trusted as given. A zoom of zero divides the whole
    /// plane down to one point. The card then comes out as a single flat colour. That is the one failure
    /// here that looks deliberate.</para>
    /// </remarks>
    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(CardRoom), new PropertyMetadata(1.0, OnFactChanged));

    /// <summary>
    /// Where on the room's endless plane this card is looking, in card heights.
    /// </summary>
    /// <remarks>
    /// <b>This does what <see cref="Apart"/> cannot do.</b> <see cref="Apart"/> moves a card along the
    /// DRIFT, and the drift is slow by design. Two cards far apart along it still show the same figures in
    /// nearly the same places. So every head looked like every other head, however it was seeded. This
    /// moves the window instead.
    ///
    /// <para><b>It does not travel far.</b> The room is a CLUSTER of lobes around the origin. They are
    /// placed at <c>cos(a) * 0.70</c> and similar values, so there are none beyond about one. A window moved much
    /// further than that looks only at the ground colour. Half a card's height either way covers about the
    /// whole useful range. See <c>GenreTile.Reach</c>, which is the one caller that moves at all. It says
    /// what was measured.</para>
    ///
    /// <para>It is kept apart from <see cref="Apart"/>, not folded into it, because the two are different
    /// questions. A card can want one without the other. The playlist cards want the drift spread and
    /// nothing else, while a wall of genres wants both.</para>
    /// </remarks>
    public Point Shift
    {
        get => (Point)GetValue(ShiftProperty);
        set => SetValue(ShiftProperty, value);
    }

    public static readonly DependencyProperty ShiftProperty = DependencyProperty.Register(
        nameof(Shift), typeof(Point), typeof(CardRoom), new PropertyMetadata(default(Point), OnFactChanged));

    /// <summary> Where this room fades out across its own width, as a pair of values: solid until <c>X</c>,
    /// and gone by <c>Y</c>. Both are shares of the width. <c>0,0</c> is the default, and it is a room that does not
    /// fade at all.
    /// </summary>
    /// <remarks>
    /// <b>It is for the leading edge of a photograph.</b> A card that is a colour on the left and a picture
    /// on the right hides the join. It lays the colour OVER the picture, and lets it fade out before the
    /// middle. Then there is no edge on the card, because nothing on it changes where the picture starts. A
    /// flat brush could do that layer, but a flat colour looks dull when it covers a third of the width.
    /// With the fade, this room can be that layer.
    ///
    /// <para><b>The same room drawn twice is what makes the join seamless.</b> A card puts one of these
    /// under the picture with no fade, and a second one over it with the fade. Both use the same palette,
    /// the same seed and the same size. So they are the same picture, and the faded one sits exactly on top
    /// of the plain one. At the picture's leading edge, the faded copy is still solid, and what is under it
    /// is a copy of itself. So nothing is visible there, whether or not the picture has arrived. If the two
    /// are made different, for example with a different <see cref="Apart"/> or a different size, a seam
    /// appears again.</para>
    ///
    /// <para>The fade is baked into the pixels, not applied as a mask. The alternative is the composition
    /// surface and mask brush that <c>ScrollHem</c> uses. That needs a second visual, and a live copy of
    /// this one for each card. Here it is one multiply over a picture that is a tenth of each side. The
    /// buffer was just written anyway. The buffer is premultiplied BGRA, so all four channels are
    /// scaled.</para>
    /// </remarks>
    public Point Fade
    {
        get => (Point)GetValue(FadeProperty);
        set => SetValue(FadeProperty, value);
    }

    public static readonly DependencyProperty FadeProperty = DependencyProperty.Register(
        nameof(Fade), typeof(Point), typeof(CardRoom), new PropertyMetadata(default(Point), OnFactChanged));

    /// <summary>
    /// Puts <see cref="Fade"/> over the pixels just drawn.
    /// </summary>
    /// <remarks> The fade curve, <c>t²(3−2t)</c>. Every other fall in the app is shaped with this curve. A
    /// simple linear fade would leave a visible corner, which is a line drawn down the picture. This whole
    /// property exists to remove that line.
    /// </remarks>
    private void Dissolve(CardRoomArt.Pass pass)
    {
        var (from, to) = (Fade.X, Fade.Y);
        if (to <= from || to <= 0)
            return;

        var pixels = pass.Pixels;
        var stride = pass.Width * 4;

        for (var x = 0; x < pass.Width; x++)
        {
            // The column's centre rather than its left edge: a ramp read off the edge is half a pixel
            // out at both ends, which shows on a picture this small before it is scaled up ten times.
            var at = (x + 0.5) / pass.Width;
            if (at <= from)
                continue;

            byte scale;
            if (at >= to)
            {
                scale = 0;
            }
            else
            {
                var t = (at - from) / (to - from);
                scale = (byte)Math.Clamp(Math.Round((1 - (t * t * (3 - (2 * t)))) * 255), 0, 255);
            }

            for (var y = 0; y < pass.Height; y++)
            {
                var at4 = (y * stride) + (x * 4);
                pixels[at4] = (byte)(pixels[at4] * scale / 255);
                pixels[at4 + 1] = (byte)(pixels[at4 + 1] * scale / 255);
                pixels[at4 + 2] = (byte)(pixels[at4 + 2] * scale / 255);
                pixels[at4 + 3] = (byte)(pixels[at4 + 3] * scale / 255);
            }
        }
    }

    /// <summary>
    /// Whether this room drifts at all. It is on by default. It is turned off for a screen that needs the
    /// processor cores.
    /// </summary>
    /// <remarks>
    /// <b>The setup wizard is the only caller that turns it off.</b> It does so for the length of the
    /// import press. On that screen the room is the only other thing asking for frames, and the press needs
    /// every core. Anywhere else, the cost of a frame is not a concern. A frame is the shader at a tenth of
    /// the card's size. On the Skia head, it is also a repaint of the whole window. That repaint was
    /// measured on macOS and accepted.
    ///
    /// <para>Setting it asks <see cref="Settle"/> again. So turning it off during the press stops the clock
    /// at once, and not at the next resize.</para>
    /// </remarks>
    public bool Drift
    {
        get => _drift;
        set
        {
            if (_drift == value) return;
            _drift = value;
            Settle();
        }
    }

    private bool _drift = true;

    /// <summary>
    /// How fast this room runs, as a multiple of the rate the shader is tuned at.
    ///
    /// <para><b>Why a room might need to be slower than a card.</b> The rate in <c>CardRoomArt.Rate</c> is
    /// set for a banner card, which is a fifth of a screen and is only glanced at. The setup wizard draws
    /// the same room across the WHOLE window. The same angular motion carries a lobe three times as far. A
    /// motion that reads as a slow drift on a card looks like the picture swirling about on the
    /// wizard.</para>
    ///
    /// <para>It is applied to the CLOCK that the shader is given, and not to one rate inside it. So drift,
    /// orbit, wobble and the three envelopes keep the proportions they were tuned with.
    /// <c>CardRoomArt.Rate</c> does the same thing, one level up.</para>
    ///
    /// <para>Changing it does not jump the picture. The room's stored moment stays in unscaled clock time.
    /// Only the value given to the shader is scaled. So a room already on screen carries on from where it
    /// is, at the new speed.</para>
    /// </summary>
    public double Pace { get; set; } = 1.0;

    /// <summary>
    /// The fewest pixels this room is drawn across, whatever a tenth of the element works out at.
    /// </summary>
    /// <remarks> Every room is drawn at a tenth of the size it is shown at. This is the lowest value of
    /// that. A plate is 352 points tall, and its tenth is 35 rows, which is what the picture was tuned at.
    /// A square card is a quarter of that size, so its tenth would be fewer rows than the plate has. So a
    /// square uses 32 as its floor. That keeps the plate's detail at every tile size that the wall's slider
    /// reaches.
    ///
    /// <para>This is a floor, not a divisor of its own. A divisor tuned for one tile size becomes a cost
    /// that changes with the slider. At the largest tile size, a divisor gave 87 by 87 pixels, and took
    /// 10.7 ms of every 31 ms tick, for one card. See
    /// <see cref="CardRoomArt.Small(double, double, int)"/>.</para>
    ///
    /// <para>It is a plain property and not a dependency property, like <see cref="Pace"/>. The host that
    /// builds the element sets it once, and it is never bound. Changing it after the first frame takes
    /// effect at the next resize, when the size is asked again.</para>
    /// </remarks>
    public int Fewest { get; set; } = CardRoomArt.Least;

    /// <summary>
    /// The most pixels one frame of this room may be, or 0 for no ceiling.
    /// </summary>
    /// <remarks>
    /// <b>The opposite of <see cref="Fewest"/>, and it is needed for the opposite reason.</b> Fewest is a
    /// floor for an element that is small enough that a tenth of it stops looking like a room. Most is a
    /// ceiling for an element whose size is set by the WINDOW. The playlist header is as wide as the pane.
    /// A tenth of it on a 3,840 point monitor is 9,400 pixels, which takes about 13 ms a frame. That does
    /// not fit in the 31 ms tick, and it slows the whole clock. A card cannot grow like this, because a
    /// card's size is set by the row it is in.
    ///
    /// <para>The ceiling is on the pixel count, not on a side, because the pixel count is what a frame
    /// costs. See
    /// <see cref="CardRoomArt.Small(double, double, int, int)"/>, where the proportion is kept.</para>
    ///
    /// <para>Like <see cref="Fewest"/>, it is a plain property. The host that builds the element sets it
    /// once. Changing it after the first frame takes effect at the next resize.</para>
    /// </remarks>
    public int Most { get; set; }

    /// <summary>Where in the drift a card of this colour and list begins. The answer comes from
    /// <see cref="CardRoomArt.Still(RoomPalette, int)"/>. So a cover composed off the thread and this
    /// element show the same room at the same moment.</summary>
    private float Still => Palette is { } p ? CardRoomArt.Still(p, Apart) : 0f;

    private void Beat(bool on)
    {
        if (on == _beating)
            return;

        _beating = on;
        if (!on)
        {
            CardRoomBeat.Leave(Tick);
            return;
        }

        // Carry on from the picture that is on screen. Do not cut to wherever the shared clock is now. The
        // card that has just stepped into the plate was already showing a room, so the step is a slide, not
        // a cut.
        _origin = CardRoomBeat.Moment - _moment;
        CardRoomBeat.Join(Tick);
    }

    /// <summary>
    /// One frame, for every card that has a room and not only the one in front.
    /// </summary>
    /// <remarks> Every card with a room is drawn on each tick. The cards behind the plate are drawn at the
    /// same rate as the card in front. The cost is described on the class. The <c>rooms</c> log channel
    /// reports it for each tick, on the machine it runs on.
    /// </remarks>
    private void Tick(float beat) => Draw(beat - _origin);

    /// <summary>
    /// Everything that decides what a still room looks like.
    /// </summary>
    /// <remarks> It holds every input, and that is the point. Two cards that agree on every field are
    /// looking at the same picture, and they may share it. Two cards that differ in any field must not
    /// share it. A field left out would let a card wear another card's room, and that must never happen. So
    /// this is a record of the arguments that the draw takes, not a hand-written string. Adding an argument
    /// to the draw without adding it here will not compile.
    /// </remarks>
    private readonly record struct RoomKey(
        RoomPalette Palette, float Moment, float Zoom, double ShiftX, double ShiftY,
        double FadeFrom, double FadeTo, int Width, int Height);

    /// <summary>
    /// The still rooms drawn so far, ready for the next card that wants one of them.
    /// </summary>
    /// <remarks>
    /// <b>Why a wall of cards needs this.</b> A genre card draws TWO rooms: one under the photograph, and a
    /// faded copy over it. The wall recycles its containers. So scrolling back to a shelf creates them
    /// again, and pays for both again. Each room is a pinned buffer, a Skia surface, two bitmaps and a
    /// shader pass. When a dozen cards arrive in one frame, they pop in slowly.
    ///
    /// <para><b>Still rooms only.</b> A drifting room draws a new picture on every tick. It would fill this
    /// table with pictures that nobody asks for twice.</para>
    ///
    /// <para><b>The bitmap is held, not copied, and it is never written to again.</b> A card that stores
    /// its picture here lets go of its own buffer and surface in the same turn (see <see cref="Keep"/>). So
    /// nothing can write into a bitmap that another card is showing. A card whose colours or size change
    /// then rebuilds from nothing.</para>
    ///
    /// <para>It is capped. When the cap is reached, the whole table is cleared, not one entry at a time.
    /// What fills it is a library with more shelves than the cap. In that case no single entry is the one
    /// to drop. The alternative is a least-recently-used list. That is a second structure to keep in step,
    /// for a table that holds pictures a tenth of a card in size.</para>
    /// </remarks>
    private static readonly Dictionary<RoomKey, WriteableBitmap> Kept = [];

    /// <summary>The number of still rooms held. It is enough for a genre wall at three sizes. A pane fold
    /// moves every card between two sizes and back. The wall can hold 27 genres at two rooms each, which is
    /// 54 for each size, and the size is part of the key. With a limit of 96, a fold and an unfold filled
    /// the table. It was cleared each time, and nothing was ever borrowed. A room is a tenth of its card on
    /// each side, a few kilobytes. So 256 is under a megabyte for genre cards.</summary>
    private const int MostKept = 256;

    /// <summary>The number of rooms borrowed from the table, and the number drawn from nothing, for the
    /// run. These are logged on the <c>rooms</c> channel. A table that never answers and a table that is
    /// never asked look the same from outside. The cards are right either way, and they are simply all
    /// being drawn. A lookup made before the room has been posed misses every key, because the moment is
    /// part of the key.</summary>
    private static int _borrowed;

    private static int _drew;

    /// <summary>The key for what this card would draw right now. One method, so the lookup and the
    /// store cannot disagree about what counts.</summary>
    private RoomKey KeyFor(RoomPalette palette, float moment, int width, int height) => new(
        palette,
        (float)(moment * Pace),
        (float)Math.Max(0.05, Zoom),
        Shift.X,
        Shift.Y,
        Fade.X,
        Fade.Y,
        width,
        height);

    /// <summary>The picture this card would draw, if somebody has already drawn it.</summary>
    private WriteableBitmap? Cached(RoomKey key)
    {
        lock (Kept)
        {
            if (!Kept.TryGetValue(key, out var kept))
                return null;

            _borrowed++;
            return kept;
        }
    }

    /// <summary>
    /// Hands a finished still room to the table, and lets go of what drew it.
    /// </summary>
    /// <remarks> The pinned buffer and the Skia surface are released here, and not at some later tidy-up.
    /// They are the expensive part of what a card holds, and a still room has no use for them again. What
    /// stays is the bitmap. The table now owns it, and this card is only showing it.
    /// </remarks>
    private void Keep(RoomKey key, WriteableBitmap picture)
    {
        lock (Kept)
        {
            if (Kept.Count >= MostKept)
                Kept.Clear();

            Kept[key] = picture;
            _drew++;
        }

        // The log line is written on every draw, and not on every settle. A wall that fills for the first
        // time draws one room for each card. That is the cost it is meant to pay. A wall that is scrolled
        // back over should draw none at all. The two counts next to each other show which of these is
        // happening.
        Scope.Said("rooms", $"rooms drawn {_drew}, borrowed {_borrowed}, held {Kept.Count}");

        _pass?.Dispose();
        _pass = null;
        _spare = null;
    }

    /// <summary>Draws one frame into the bitmap. If the room fails to draw, the card keeps the flat wash
    /// underneath it, and not a black plate. That is why this can fail quietly.</summary>
    private void Draw(float moment)
    {
        if (_spare is not { } next || _pass is not { } pass || Palette is not { } palette)
            return;

        var began = Scope.On ? Stopwatch.GetTimestamp() : 0L;

        if (!pass.Draw(
                palette,
                (float)(moment * Pace),
                (float)Math.Max(0.05, Zoom),
                ((float)Shift.X, (float)Shift.Y)))
        {
            Beat(false);
            _round.Visibility = Visibility.Collapsed;
            return;
        }

        // This runs after the shader and before the buffer is shown. The fade is a property of what is
        // shown, not of what the room is. So the drift's own moment is not changed by it.
        Dissolve(pass);

        // WHICH BITMAP IS WRITTEN INTO IS NOT THE SAME ANSWER ON THE TWO HEADS. On Skia, it is the
        // difference between a room that drifts and a room that flickers.
        //
        // WinUI: the new bitmap is handed over, not written into. The picture is holding the other one. A
        // source that is the same object as last frame is one that it has no reason to read again.
        //
        // Skia: the new pixels are written into the bitmap that the Image is ALREADY showing, and then it
        // is only invalidated. On Uno, the Image clears its sprite's brush as soon as Source is assigned,
        // and refills it on the next measure pass (see Uno's Image.skia.cs, OnSourceChanged: "We clear the
        // old image first. We do NOT wait for the new image to load"). So handing it a second bitmap leaves
        // a window with no picture on the sprite. A frame presented inside that window draws the card with
        // no room on it. Any single such frame is seen by nobody. Thirty times a second, some of them land,
        // and that is the flicker. Invalidate goes through ImageSource.InvalidateSource instead. That
        // re-opens the source and pushes the new pixels through the subscription that the Image already
        // holds, without clearing the brush.
#if RPM_WINUI
        var into = next;
#else
        var into = _shown ?? next;
#endif

        using (var buffer = into.PixelBuffer.AsStream())
            buffer.Write(pass.Pixels, 0, pass.Pixels.Length);
        into.Invalidate();

#if RPM_WINUI
        (_shown, _spare) = (next, _shown);
        _picture.Source = next;
#else
        // Assigned once, on the first frame after a Rebuild, and never again for that bitmap.
        if (!ReferenceEquals(_picture.Source, into))
            _picture.Source = into;
#endif

        _moment = moment;
        _drawn = true;
        _drawnAt = (pass.Width, pass.Height);
        _round.Visibility = Visibility.Visible;

        if (began != 0)
            CardRoomBeat.Spent(Stopwatch.GetElapsedTime(began).TotalMilliseconds, Live);

        // A still room is finished as soon as it is drawn. So it goes to the table, and this card lets go
        // of what drew it. A drifting room is a different picture on the next tick. It would fill the table
        // with pictures that nobody asks for twice.
        if (!Drift)
            Keep(KeyFor(palette, moment, pass.Width, pass.Height), into);
    }
}

/// <summary>
/// The one clock every drifting card room is drawn on.
///
/// <para>The clock is shared for the same reason as <see cref="BadgeBreath"/>. Two cards that each run
/// their own stopwatch would draw their rooms out of step. The cost of a frame is the repaint, not the
/// arithmetic. The clock is shared by design, so that all drifting rooms stay in step.</para>
///
/// <para>This uses a timer, not <c>CompositionTarget.Rendering</c>, for the same reason. While any handler
/// is attached to that event, each finished frame asks for the next one. So the app rasters its whole
/// window at the display's rate, whether anything changed or not. Throttling inside the handler does not
/// help, because the loop comes from the subscription.</para>
/// </summary>
internal static class CardRoomBeat
{
    /// <summary>
    /// How often a frame is requested. It is about thirty times a second, not the display's rate.
    /// </summary>
    /// <remarks> Fifteen a second looks jerky, even though the frame interval is short. The room is a soft
    /// field with no edges. So what the eye catches is the whole picture changing at once, and not a shape
    /// jumping. At fifteen a second, that change is visible as a stutter.
    ///
    /// <para><b>A rate is only real if the work inside it fits.</b> At the resolution that was used first,
    /// a frame cost 9 ms of a 66 ms tick. The resolution was reduced together with this rate, not after it.
    /// See
    /// <see cref="CardRoomArt.Coarse"/>, which holds the arithmetic.</para>
    ///
    /// <para><b>The value is twenty, for a rate of about thirty-two a second, and the arithmetic is not
    /// what it looks like.</b> A <c>DispatcherTimer</c> is not a fixed-rate clock. It schedules the next
    /// tick when the handler RETURNS. Ticks are delivered on the system tick, which is 15.6 ms here. So the
    /// wait is the interval PLUS the drawing time, rounded up to a whole system tick. A number near the
    /// boundary gains a whole extra tick.
    ///
    /// <para>On the WinUI head, these were measured three times. A request for 66 ms with 9 ms of drawing
    /// came back as 78 ms, which is five system ticks. A request for 33 ms with 9 ms of drawing came back
    /// as 46.6 ms, which is three ticks. A request for 30 ms with 3.3 ms of drawing also came back as 46.6
    /// ms. The reason is that 33.3 ms does not fit in two ticks. A request for 20 ms with 3.3 ms of drawing
    /// is 23.3 ms, which fits in two ticks. So it arrives every 31.2 ms. The spare time is deliberate.
    /// Three rooms on one tick take about 10 ms, and 30 ms still fits in two ticks.</para>
    /// </remarks>
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(20);

    private static readonly List<Action<float>> Rooms = [];
    private static readonly Stopwatch Clock = new();
    private static DispatcherTimer? _timer;

    /// <summary>The clock time, in seconds. A room that is about to join reads it, so it can work out its
    /// own offset.</summary>
    public static float Moment => (float)Clock.Elapsed.TotalSeconds;

    /// <param name="paint">Called with <see cref="Moment"/> every <see cref="Beat"/>, until the same
    /// delegate is passed to <see cref="Leave"/>. UI thread only.</param>
    public static void Join(Action<float> paint)
    {
        if (Rooms.Contains(paint))
            return;

        Rooms.Add(paint);
        if (_timer is not null)
            return;

        // The first room to arrive starts the clock. The clock RESUMES. It is never restarted. A room works
        // out its offset from where the clock stands. If the clock went back to zero between one card and
        // the next, the new card would be behind its own last frame. The timer is started through
        // Reconsider. So a room that joins while nobody can see it stays stopped.
        _timer = new DispatcherTimer { Interval = Beat };
        _timer.Tick += OnTick;
        Reconsider();
    }

    /// <summary> Whether the beat is stopped while rooms are drifting, because nobody can see them. This is
    /// the one expression that decides it. <see cref="Reconsider"/> reads it. A new reason is another term
    /// here, and whatever raises that reason must call Reconsider.
    /// </summary>
    /// <remarks> One fact is used: the window is minimised (<c>WindowSight</c>). A minimised
    /// window stops the beat, which lowers the home screen's cost from about 30% of a core to about 12%.
    /// <c>WindowSight.Changed</c> is subscribed below.
    ///
    /// <para><b>Pegasus covering the shell is not a term here.</b> Such a term would stop every room in
    /// the app, including Pegasus's own home. That home is a second <c>HomeView</c> on this same beat, so
    /// under Pegasus nothing would drift. The shell's rooms are taken off the clock by their own screen
    /// instead. <c>ShellPage.Cover</c> sets <c>HomeView.Covered</c> on the main window's home. That home's cards are
    /// told they are not shown, so they leave the beat, and Pegasus's cards stay on it.</para>
    /// </remarks>
    private static bool Held => Services.Shell.WindowSight.Minimised;

    static CardRoomBeat() => Services.Shell.WindowSight.Changed += Reconsider;

    /// <summary>
    /// Stops or starts the timer and the clock together to match <see cref="Held"/>. Called by whoever
    /// changes one of its facts, on the UI thread. The clock stops with the timer, so a room that comes
    /// back carries on from the picture it left.
    /// </summary>
    public static void Reconsider()
    {
        if (_timer is not { } timer)
            return;

        if (Held)
        {
            timer.Stop();
            Clock.Stop();
        }
        else if (!timer.IsEnabled)
        {
            Clock.Start();
            timer.Start();
        }
    }

    public static void Leave(Action<float> paint)
    {
        if (!Rooms.Remove(paint) || Rooms.Count > 0 || _timer is not { } timer)
            return;

        _timer = null;
        timer.Stop();
        timer.Tick -= OnTick;

        // The clock is stopped, not reset. A room that joins later works out its offset from where the
        // clock stands. If the clock went back to zero, that room would be behind its own last frame.
        Clock.Stop();
    }

    /// <summary>The number of ticks delivered. It is only used for logging. See <c>CardRoom.Watch</c> for
    /// what this line is compared with.</summary>
    private static int _ticks;

    private static double _frontMs;
    private static int _frontFrames;
    private static double _behindMs;
    private static int _behindFrames;

    /// <summary>
    /// What one frame cost, kept apart for the card in front and the cards behind it.
    /// </summary>
    /// <remarks> The two numbers show how much the cards behind the front card add to each frame. They are
    /// measured on the machine that runs the app, not calculated.
    /// </remarks>
    internal static void Spent(double ms, bool front)
    {
        if (front)
        {
            _frontMs += ms;
            _frontFrames++;
        }
        else
        {
            _behindMs += ms;
            _behindFrames++;
        }
    }

    private static void OnTick(object? sender, object e)
    {
        var moment = Moment;

        if (Scope.On && ++_ticks % 30 == 0)
        {
            var front = _frontFrames > 0 ? _frontMs / _frontFrames : 0;
            var behind = _behindFrames > 0 ? _behindMs / _behindFrames : 0;
            Scope.Said("rooms",
                $"tick {_ticks}, {Rooms.Count} drifting, moment {moment:F1}s, "
                + $"front {_frontFrames}x{front:F2}ms, behind {_behindFrames}x{behind:F2}ms, "
                + $"{(_frontMs + _behindMs) / 30:F2}ms a tick");
            _frontMs = _behindMs = 0;
            _frontFrames = _behindFrames = 0;
        }

        // Iterates backwards, so that a room can stop drifting during its own draw without causing the next
        // room in the list to be skipped.
        for (var i = Rooms.Count - 1; i >= 0; i--)
            Rooms[i](moment);
    }
}
