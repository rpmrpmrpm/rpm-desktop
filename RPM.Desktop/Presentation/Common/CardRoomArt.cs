using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using RPM.Core.Viz;
using SkiaSharp;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The flat room, drawn small enough to be drawn often, for the back of a banner card.
///
/// <para>The figures are <see cref="RoomSource"/> unchanged. Pegasus compiles the same text as GL,
/// and this class compiles it as SkSL. This file adds only the entry point around the figures and
/// the arithmetic that draws one frame into a bitmap.</para>
///
/// <para><b>It also draws the playlist header, and it is the only room in the app that can do that
/// within the frame budget.</b> A raymarched volume (24 samples along each ray, with 24 figures per
/// sample) takes about 185 ms a frame on eight pool workers. It can only be drawn once each time a
/// list is opened. This room has 15 figures on a warped plane and no march, so one frame fits
/// between two timer ticks.</para>
///
/// <para><b>The music is not used, and the envelopes are not constants.</b> Pegasus's room swells on
/// the bass, moves on the middle and lights on the air. A card on the home screen is not the thing
/// being played. Driving it from the player would make every card pulse to a record that is on none
/// of them. The three breathe on slow cycles of their own instead. With constant values the room
/// has only the figures' drift, and it reads as a still picture at a card's size.</para>
///
/// <para><b>Drawn at a tenth of each side, with a floor under it and a ceiling over it.</b> The field
/// is soft by design: no edge in it is narrower than the skirt of a lobe. So the element that shows
/// it does the enlargement, and that costs nothing here. The tenth mainly sets the frame rate (see
/// <see cref="Coarse"/>). <see cref="Small(double, double, int, int)"/> handles the other two cases:
/// an element too small for a tenth to show detail, and one too large to afford a tenth. See
/// <see cref="CardRoom"/>, which owns the bitmap and decides how often this is called.</para>
/// </summary>
internal static partial class CardRoomArt
{
    /// <summary>
    /// How much smaller the room is drawn than it is shown, on the card in front.
    /// </summary>
    /// <remarks>
    /// <b>This sets the frame rate.</b> Skia draws a runtime effect on the CPU with its SkSL
    /// interpreter. This effect has three loops, so the cost of a frame is almost all in the pixel
    /// count. Reusing the surface and the buffer, instead of building them for each frame, made
    /// almost no difference. At a sixth of each side a frame took long enough that the room ran at
    /// about 11 frames a second, which looks like chopping when the room is moving.
    ///
    /// <para>A tenth is a third of the pixels, so the same work gives nearly three times the frame
    /// rate. Nothing visible is lost. The field has no edge narrower than the skirt of a lobe, and
    /// the element that shows it already magnifies the picture about six times.</para>
    /// </remarks>
    internal const int Coarse = 10;

    /// <summary>The smallest picture worth drawing, on either side. Below this the figures are only a
    /// few pixels across, and the room looks like noise.</summary>
    internal const int Least = 8;

    /// <summary>
    /// How much quicker the card's room runs than Pegasus's.
    /// </summary>
    /// <remarks>
    /// Pegasus fills a screen and is watched closely. A card is a fifth of that size and is only
    /// glanced at. At Pegasus's own rate a figure crosses about two per cent of the plate in a
    /// second, which is too slow to see. The rate is applied to the clock, not to each rate in turn.
    /// Drift, orbit and wobble then keep the proportions they were tuned with (the same argument as
    /// <c>RoomSource.AirRate</c>).
    /// </remarks>
    private const float Rate = 2.2f;

    /// <summary>
    /// What the three envelopes breathe around, and how far either side.
    /// </summary>
    /// <remarks>
    /// Without a swell the room has only the drift, and it looks still. What makes Pegasus's room look
    /// alive is the music: it swells the figures and stirs the warp. Bass alone is worth 62% of a
    /// lobe's radius there. This is the same swell without a record. It has three cycles of different
    /// lengths, none a multiple of another, so the room never returns to the same pose.
    ///
    /// <para>The values are low, because a card is a background rather than a display.</para>
    /// </remarks>
    private const float Bass = 0.20f;

    private const float BassSwing = 0.16f;

    private const float Mid = 0.16f;

    private const float MidSwing = 0.14f;

    private const float Air = 0.12f;

    private const float AirSwing = 0.11f;

    /// <summary>
    /// The room compiled once. Building a runtime effect takes milliseconds, and reusing it costs
    /// nothing.
    ///
    /// <para>Null when it will not compile. A room that compiles but draws nothing is not caught by
    /// the compiler, so a test checks that it compiles.</para>
    /// </summary>
    private static readonly Lazy<SKRuntimeEffect?> Program = new(() =>
        SKRuntimeEffect.CreateShader(Sksl(), out _));

    /// <summary>Whether the room can be drawn at all on this machine.</summary>
    internal static bool Compiles => Program.Value is not null;

    /// <summary>The shared figures with this card's own entry point around them. SkSL's entry point is
    /// <c>half4 main(float2)</c>, and its origin is the top left, where GLSL's is the bottom left.</summary>
    internal static string Sksl() => SkslSource.Translate($$"""
        uniform float2 uResolution;
        uniform float  uTime;
        uniform float  uZoom;
        uniform float2 uShift;
        uniform float3 uGround;
        uniform float3 uTint;
        uniform float3 uGlow;
        uniform float3 uShade;

        const float CARD_RATE = {{Num(Rate)}};
        const float CARD_BASS = {{Num(Bass)}};
        const float CARD_BASS_SWING = {{Num(BassSwing)}};
        const float CARD_MID  = {{Num(Mid)}};
        const float CARD_MID_SWING = {{Num(MidSwing)}};
        const float CARD_AIR  = {{Num(Air)}};
        const float CARD_AIR_SWING = {{Num(AirSwing)}};

        {{RoomSource.Glsl}}

        half4 main(float2 fragCoord)
        {
            // Skia counts y down from the top and the room was written counting up from the bottom.
            float2 flipped = float2(fragCoord.x, uResolution.y - fragCoord.y);

            // The origin in the middle and the plane scaled by HEIGHT, which is what keeps the figures
            // round on a plate twice as wide as it is tall.
            //
            // Then WHERE ON THAT PLANE this card is looking, and HOW CLOSE. The room is an endless
            // field rather than a picture with edges, so a card is a window onto it: moving the window
            // gives a different part of the field and narrowing it gives bigger figures. Both are one
            // card's own business and neither touches the drift, so two cards that differ in these are
            // the same room seen from two places rather than two rooms.
            //
            // Divided, so a larger uZoom is closer in. uZoom is never zero (CardRoom.Zoom floors it),
            // and a zero here would collapse the whole plane onto one colour.
            float2 p = (flipped - 0.5 * uResolution) / uResolution.y / uZoom + uShift;

            float t = uTime * CARD_RATE;

            // The three cycles are 11.9, 8.8 and 17.0 seconds long at this rate, which share no common
            // multiple worth the name, and they start out of phase with one another. So the three never
            // swell together and the room does not appear to beat.
            float bass = CARD_BASS + CARD_BASS_SWING * sin(t * 0.53);
            float mid  = CARD_MID  + CARD_MID_SWING  * sin(t * 0.71 + 1.7);
            float air  = CARD_AIR  + CARD_AIR_SWING  * sin(t * 0.37 + 3.1);

            float3 c = rpmRoom(p, t, bass, mid, air, uGround, uTint, uGlow, uShade);
            return half4(half3(clamp(c, 0.0, 1.0)), 1.0);
        }
        """);

    /// <summary>
    /// The size a plate of this size is drawn at.
    /// </summary>
    /// <remarks>
    /// Every card uses the same size, and the cards behind the plate are not drawn smaller to save
    /// work. A smaller drawing is not the same picture. The third family of figures is only about two
    /// pixels across at that size, and it disappears into the wash. A card behind the plate would then
    /// run a different animation from the card in front of it.
    /// </remarks>
    internal static (int Width, int Height) Small(double width, double height) =>
        Small(width, height, Least);

    /// <summary>
    /// The same, with a floor of the caller's own choosing.
    /// </summary>
    /// <remarks>
    /// <b>The divisor is the same tenth for every room. What a small surface needs is a FLOOR.</b> A
    /// tenth of a 760 by 352 plate is 76 by 35, which reads as a room. A tenth of a 260-point square
    /// is 26 by 26. That is fewer rows than the plate has. A tenth of the smallest tile the wall
    /// slider reaches is coarser still, and the third family of figures shows as about a pixel.
    ///
    /// <para>This was measured, and the first answer was wrong in the other direction. A square card
    /// with a divisor of three is 87 by 87 at the wall's usual tile. That is 7,569 pixels, against the
    /// plate's 2,660. A frame costs its pixel count and little else. So a divisor tuned for one tile
    /// size becomes a cost that changes with the slider.</para>
    /// </remarks>
    /// <param name="fewest">The fewest pixels on either side. A square card passes 32, which is close
    /// to the plate's 35 rows at every tile size the slider can reach.</param>
    internal static (int Width, int Height) Small(double width, double height, int fewest)
        => Small(width, height, fewest, most: 0);

    /// <summary>
    /// The same, with a ceiling on the whole frame as well as a floor on each side.
    /// </summary>
    /// <remarks>
    /// <b>The ceiling limits the PIXEL COUNT, because that is what a frame costs.</b> The floor is set
    /// for each side, because a small element loses rows. The ceiling is for large elements, and a
    /// per-side limit would not bound anything there. The playlist header is nine times as wide as it
    /// is tall. A cap on the long side alone lets the short side carry the cost. A cap on the short
    /// side throws away the resolution that a wide strip needs.
    ///
    /// <para>The shape is kept, so a capped room is the same picture drawn smaller, not a squashed one.
    /// The floor is applied again afterwards and wins where the two disagree. An element that asks for
    /// both is asking for something that cannot be had, and the answer that looks better costs more.</para>
    /// </remarks>
    /// <param name="fewest">The fewest pixels on either side. A square card passes 32.</param>
    /// <param name="most">The most pixels the whole frame may be, or 0 for no ceiling. The playlist
    /// header sets one, because its width is the window's width. See <c>PlaylistView.RoomBudget</c>.</param>
    internal static (int Width, int Height) Small(double width, double height, int fewest, int most)
    {
        var floor = Math.Max(1, fewest);
        var w = Math.Max(floor, (int)Math.Round(width / Coarse));
        var h = Math.Max(floor, (int)Math.Round(height / Coarse));

        if (most <= 0 || (long)w * h <= most)
            return (w, h);

        var shrink = Math.Sqrt(most / ((double)w * h));
        return (Math.Max(floor, (int)Math.Round(w * shrink)),
                Math.Max(floor, (int)Math.Round(h * shrink)));
    }

    /// <summary>
    /// One size of room, and everything that can be made once for it.
    /// </summary>
    /// <remarks>
    /// <para><b>The surface is made once and drawn into again and again. The buffer under it is reused
    /// too.</b> The first version made an <c>SKSurface</c> for each frame, then copied the pixels out
    /// into an array. At thirty frames a second that meant an allocation, a raster target and a full
    /// copy of the picture, every time.</para>
    ///
    /// <para><b>The gain was small.</b> On the WinUI head, a frame of a 600-point plate went from 9.4 ms
    /// to 9.0 ms. Skia's cost is the pixel count and the loops inside the effect, not the setup around
    /// it. The frame rate comes from <see cref="Coarse"/> instead. Reuse stays, because a per-frame
    /// allocation and a full copy are wasted work anyway.</para>
    ///
    /// <para>The buffer is pinned for the life of the pass, because the surface writes into it directly.
    /// <c>SKSurface.Create</c> over our own memory makes the raster target the same array that the
    /// bitmap is filled from. So there is no read-back at all.</para>
    ///
    /// <para>The format is Bgra8888 with Premul alpha, because that is what a <c>WriteableBitmap</c>'s
    /// buffer holds. Any other format shows a picture with red and blue swapped, and gives no error.</para>
    /// </remarks>
    internal sealed partial class Pass : IDisposable
    {
        private GCHandle _held;
        private readonly SKSurface? _surface;

        internal Pass(int width, int height)
        {
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);

            var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            Pixels = new byte[info.BytesSize];
            _held = GCHandle.Alloc(Pixels, GCHandleType.Pinned);
            _surface = SKSurface.Create(info, _held.AddrOfPinnedObject(), info.RowBytes);
        }

        internal int Width { get; }

        internal int Height { get; }

        /// <summary>
        /// The frame as an image, for a caller that composes it into something rather than handing it
        /// to a bitmap.
        /// </summary>
        /// <remarks>
        /// This is a copy, taken from the surface. <see cref="Pixels"/> is the raster target itself, and
        /// the next <see cref="Draw"/> overwrites it. A caller that keeps the result needs its own copy.
        /// Null before the first draw and where the surface was not made.
        /// </remarks>
        internal SKImage? Snapshot() => _surface?.Snapshot();

        /// <summary>What the last <see cref="Draw"/> left behind, ready to be handed to a bitmap.</summary>
        internal byte[] Pixels { get; }

        /// <summary>
        /// One frame into <see cref="Pixels"/>.
        /// </summary>
        /// <param name="moment">Seconds. The figures drift from their own phases, starting here.</param>
        /// <param name="zoom">How close in. One is the room as it was tuned. Larger is closer, which gives
        /// fewer and bigger figures across the same card.</param>
        /// <param name="shift">Where on the room's endless plane this card looks, in the same units the
        /// plane is scaled in. One is a card's height. Two cards with different values look at two parts
        /// of one room.</param>
        /// <returns>False where the room will not compile or the surface was not made. The card then keeps
        /// its flat wash instead of showing a black plate.</returns>
        internal bool Draw(RoomPalette palette, float moment, float zoom, (float X, float Y) shift)
        {
            if (Program.Value is not { } effect || _surface is null)
                return false;

            using var paint = new SKPaint
            {
                Shader = Shade(effect, palette, Width, Height, moment, zoom, shift),
            };
            _surface.Canvas.DrawRect(SKRect.Create(Width, Height), paint);
            return true;
        }

        public void Dispose()
        {
            _surface?.Dispose();
            if (_held.IsAllocated)
                _held.Free();
        }
    }

    private static SKShader Shade(
        SKRuntimeEffect effect, RoomPalette p, int w, int h, float moment, float zoom, (float X, float Y) shift)
    {
        var uniforms = new SKRuntimeEffectUniforms(effect)
        {
            ["uResolution"] = new[] { (float)w, h },
            ["uTime"] = moment,
            ["uZoom"] = zoom,
            ["uShift"] = new[] { shift.X, shift.Y },
            ["uGround"] = new[] { p.Ground.X, p.Ground.Y, p.Ground.Z },
            ["uTint"] = new[] { p.Tint.X, p.Tint.Y, p.Tint.Z },
            ["uGlow"] = new[] { p.Glow.X, p.Glow.Y, p.Glow.Z },
            ["uShade"] = new[] { p.Shade.X, p.Shade.Y, p.Shade.Z },
        };
        return effect.ToShader(uniforms);
    }

    /// <summary>How far apart in the drift two rooms of different colours start. Any value works. What
    /// matters is that two cards do not share a pose, and that one card always has the same pose when it
    /// is drawn.</summary>
    private const float Spread = 37.0f;

    /// <summary>
    /// Where in the drift a room of these colours begins.
    /// </summary>
    /// <remarks>
    /// This is here, not on <see cref="CardRoom"/>, because a cover made off the UI thread needs the same
    /// answer. The still picture on a playlist's square and the moving one on the home screen are then
    /// the same room at the same moment. That stops the card looking like a different picture from the
    /// cover it stands for.
    /// </remarks>
    internal static float Still(RoomPalette palette) =>
        (palette.Tint.X + palette.Glow.Y + palette.Shade.Z) * Spread;

    /// <summary>
    /// The same, for a room that has to start somewhere other than its colours alone would put it.
    /// </summary>
    /// <remarks>
    /// Two generated lists can have the same colourway. See <see cref="CoverRoom.ListRoom"/>, where the
    /// pair that does is named. Their colours are equal, so <see cref="Still(RoomPalette)"/> gives them
    /// the same moment, and they share a pose as well as a colour. A distance along the drift separates
    /// them.
    ///
    /// <para><see cref="Spread"/> is the unit. It is the same distance that two different colours are
    /// already apart. The value is a phase, and nothing bounds it.</para>
    /// </remarks>
    /// <param name="apart">How many places along. Zero is <see cref="Still(RoomPalette)"/>.</param>
    internal static float Still(RoomPalette palette, int apart) =>
        Still(palette) + (Spread * apart);

    /// <summary>The palette that a card's own colour paints. A generated list has only one colour, so the
    /// tint and the accent are the same. <see cref="RoomPalette.From"/> then derives a hue for the second
    /// family. See its own remarks for why that is a last resort, and why it suits this use.</summary>
    internal static RoomPalette Palette(Windows.UI.Color ink) =>
        RoomPalette.From(new Vector3(ink.R / 255f, ink.G / 255f, ink.B / 255f), null, null);

    /// <summary>A float as GLSL spells it. Interpolating a float straight into the source uses the current
    /// culture. On a Dutch machine <c>0.20</c> would be written <c>0,20</c>, and the shader would fail with
    /// an error about a type conversion. So this always uses the invariant culture, with a decimal
    /// point.</summary>
    private static string Num(float value) => value.ToString("0.0###", CultureInfo.InvariantCulture);
}
