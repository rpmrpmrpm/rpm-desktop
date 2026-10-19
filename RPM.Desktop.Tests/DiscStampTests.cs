using RPM.Desktop.Services.Art;
using SkiaSharp;

namespace RPM.Desktop.Tests;

/// <summary>
/// Cutting the disc out of a catalogue's photograph.
///
/// <para>The pictures are drawn, not loaded from files. A photograph of a record is a white table, a round
/// object on it, and a label in the middle of the round object. These three facts are what the detector
/// reads. Drawing them lets a test say exactly what it changes: the corners, the inner edge, or the hole. A
/// JPEG of a real pressing would only say "it worked here".</para>
/// </summary>
public class DiscStampTests
{
    private const int Edge = 400;
    private static readonly SKColor Table = new(0xF2, 0xF2, 0xF2);

    /// <summary>A picture: a background, and whatever the test draws on it.</summary>
    private static byte[] Picture(Action<SKCanvas> draw)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(Edge, Edge, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(Table);
            draw(canvas);
            canvas.Flush();
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static SKPaint Fill(byte r, byte g, byte b) =>
        new() { Color = new SKColor(r, g, b), IsAntialias = true, Style = SKPaintStyle.Fill };

    /// <summary>A CD on a table: one disc filling most of the frame, with the table showing through
    /// its hole.</summary>
    private static byte[] Cd(float radius = 180) => Picture(canvas =>
    {
        using var disc = Fill(0x30, 0x60, 0xC0);
        using var hole = new SKPaint { Color = Table, IsAntialias = true };
        canvas.DrawCircle(Edge / 2f, Edge / 2f, radius, disc);
        canvas.DrawCircle(Edge / 2f, Edge / 2f, radius * 0.125f, hole);
    });

    /// <summary>A record on a table: a black disc with a bright label printed in the middle of
    /// it.</summary>
    private static byte[] Vinyl(float labelFraction = 0.33f) => Picture(canvas =>
    {
        using var record = Fill(0x0A, 0x0A, 0x0A);
        using var label = Fill(0xE0, 0x40, 0x30);
        canvas.DrawCircle(Edge / 2f, Edge / 2f, 180, record);
        canvas.DrawCircle(Edge / 2f, Edge / 2f, 180 * labelFraction, label);
    });

    /// <summary>A CD photographed with the corner of its jewel case in the picture. Most catalogue
    /// photographs look like this, with a booklet leaning against the case and the edge of the sleeve
    /// behind.</summary>
    private static byte[] CdWithClutter() => Picture(canvas =>
    {
        using var disc = Fill(0x30, 0x60, 0xC0);
        canvas.DrawCircle(Edge / 2f, Edge / 2f, 150, disc);
        using var corner = Fill(0x40, 0x40, 0x40);
        canvas.DrawRect(new SKRect(0, 0, 55, 55), corner);
    });

    /// <summary>A CD photographed under a lamp: a clear rim, the dark print inside it, and a soft
    /// shadow on the table down and to the left of it.</summary>
    private static byte[] CdWithShadow() => Picture(canvas =>
    {
        using var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 0x40),
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6),
        };
        canvas.DrawCircle(Edge / 2f - 10, Edge / 2f + 10, 180, shadow);
        using var rim = Fill(0xC0, 0xC4, 0xCC);
        canvas.DrawCircle(Edge / 2f, Edge / 2f, 180, rim);
        using var print = Fill(0x40, 0x3C, 0x38);
        canvas.DrawCircle(Edge / 2f, Edge / 2f, 172, print);
        using var hole = new SKPaint { Color = Table, IsAntialias = true };
        canvas.DrawCircle(Edge / 2f, Edge / 2f, 180 * 0.125f, hole);
    });

    // ── the disc is found where it is, not where the clutter says ──

    /// <summary>
    /// This guards against a crooked stamp. The disc comes out shifted and slightly too large, with a
    /// crescent of table around one side of it and the far edge cut off.
    ///
    /// <para>The circle must not be taken from the bounding box of everything that is not the background. A
    /// photograph is rarely just a disc on a table. Every extra object widens that box on one side. So the
    /// middle of the box is not the middle of the disc, and part of the clutter's width goes straight into
    /// the radius. A jewel-case corner in one corner of the frame can move the centre 25 px and the radius
    /// 25 px, and the result can still pass the score threshold. So the circle is fitted to the disc's own
    /// edges, not to that box.</para>
    /// </summary>
    [Fact]
    public void Something_else_in_the_photograph_does_not_move_the_disc()
    {
        var found = DiscStamp.Detect(CdWithClutter())!;

        Assert.Equal(Edge / 2f, found.CenterX, tolerance: 2f);
        Assert.Equal(Edge / 2f, found.CenterY, tolerance: 2f);
        Assert.Equal(150f, found.Radius, tolerance: 2f);
    }

    /// <summary>
    /// A shadow on the table does not move the disc.
    ///
    /// <para>The shadow differs from the table, so the mask counts it as part of the disc. The mask fit
    /// then moves towards the shadow. For example, on a 600 px photograph of a CD, the cut sat 10 px left
    /// of the disc. The rim was cut off on the right, and a band of table and shadow was included on the
    /// left. The refinement fits the circle to the rim and the print edge, which are sharp. The shadow is
    /// not sharp, so it is not used.</para>
    /// </summary>
    [Fact]
    public void A_shadow_on_the_table_does_not_move_the_disc()
    {
        var found = DiscStamp.Detect(CdWithShadow())!;

        Assert.Equal(Edge / 2f, found.CenterX, tolerance: 1f);
        Assert.Equal(Edge / 2f, found.CenterY, tolerance: 1f);
        Assert.Equal(180f, found.Radius, tolerance: 1.5f);
    }

    /// <summary>…and the hole is punched at a CD's own size. A punch that is too small leaves a ring of the
    /// hole's wall showing.</summary>
    [Fact]
    public void A_cd_hole_is_never_punched_smaller_than_a_cds()
    {
        var small = Picture(canvas =>
        {
            using var disc = Fill(0x30, 0x60, 0xC0);
            using var hole = new SKPaint { Color = Table, IsAntialias = true };
            canvas.DrawCircle(Edge / 2f, Edge / 2f, 180, disc);
            canvas.DrawCircle(Edge / 2f, Edge / 2f, 180 * 0.08f, hole);
        });

        using var stamped = SKBitmap.Decode(DiscStamp.Stamp(small, vinyl: false));
        var half = stamped.Width / 2;

        // Between the measured hole (0.08) and a CD's (0.125): punched.
        Assert.Equal(0, stamped.GetPixel(half + (int)(half * 0.11f), half).Alpha);
        Assert.Equal(255, stamped.GetPixel(half + (int)(half * 0.14f), half).Alpha);
    }

    /// <summary>…and it is still recognised as a disc. Marking a good disc down because of something lying
    /// next to it is the same mistake, seen from the other side.</summary>
    [Fact]
    public void Something_else_in_the_photograph_does_not_stop_it_being_a_disc()
        => Assert.True(DiscStamp.Detect(CdWithClutter())!.Score >= DiscStamp.MinScore);

    /// <summary>A disc photographed right to the edge of the frame keeps its real radius. The bounding box
    /// cannot see past the crop. It would report the frame's half-width instead, and the stamp would have
    /// its rim shaved off.</summary>
    [Fact]
    public void A_disc_photographed_to_the_frame_edge_keeps_its_radius()
    {
        var found = DiscStamp.Detect(Cd(radius: 205))!;
        Assert.Equal(205f, found.Radius, tolerance: 3f);
    }

    // ── is there a disc in this picture at all ──

    /// <summary>This is the purpose of the score. A release's pictures are the sleeve, the inner sleeve,
    /// the insert and, somewhere among them, the record. Nothing in the catalogue says which is which. A
    /// square fills the corners of its own bounding box. A disc leaves them bare, and that difference is
    /// what the score measures.</summary>
    [Fact]
    public void A_square_sleeve_is_not_a_disc()
    {
        var sleeve = Picture(canvas =>
        {
            using var paint = Fill(0x20, 0x80, 0x40);
            canvas.DrawRect(new SKRect(40, 40, Edge - 40, Edge - 40), paint);
        });

        var found = DiscStamp.Detect(sleeve);

        Assert.NotNull(found);
        Assert.True(found!.Score < DiscStamp.MinScore, $"a square scored {found.Score:0.00}");
    }

    [Fact]
    public void A_disc_on_a_table_is_a_disc()
    {
        var found = DiscStamp.Detect(Cd());

        Assert.NotNull(found);
        Assert.True(found!.Score >= DiscStamp.MinScore, $"a disc scored {found.Score:0.00}");
    }

    /// <summary>Found where it really is, in the coordinates of the picture that was passed in, not in
    /// those of the small copy used for counting.</summary>
    [Fact]
    public void The_disc_is_found_at_its_real_size_and_place()
    {
        var found = DiscStamp.Detect(Cd())!;

        Assert.InRange(found.CenterX, Edge / 2f - 6, Edge / 2f + 6);
        Assert.InRange(found.CenterY, Edge / 2f - 6, Edge / 2f + 6);
        Assert.InRange(found.Radius, 170, 190);
    }

    /// <summary>The table shows through a CD's hole, so the hole can be measured rather than
    /// assumed.</summary>
    [Fact]
    public void A_hole_the_background_shows_through_is_measured()
    {
        var found = DiscStamp.Detect(Cd())!;

        Assert.InRange(found.HoleRadius / found.Radius, 0.07f, 0.18f);
    }

    // ── the label inside a record ──

    /// <summary>A photograph of a whole side is mostly grooves. The middle of RPM's own pressing is taken
    /// up by the label. So the label's edge, the brightest step anywhere on the disc, is what must be
    /// found.</summary>
    [Fact]
    public void The_label_inside_a_photographed_record_is_found()
    {
        var found = DiscStamp.Detect(Vinyl())!;

        Assert.True(found.LabelRadius < found.Radius * 0.55f,
            $"label {found.LabelRadius:0} against disc {found.Radius:0}");
        Assert.InRange(found.LabelRadius / found.Radius, 0.25f, 0.45f);
    }

    /// <summary>A scan of the label alone has no grooves around it and no step to find, so the whole
    /// picture is the label. Getting this wrong would stamp a third of a label onto the record.</summary>
    [Fact]
    public void A_scan_of_the_label_alone_is_all_label()
    {
        var label = Picture(canvas =>
        {
            using var paint = Fill(0xE0, 0x40, 0x30);
            canvas.DrawCircle(Edge / 2f, Edge / 2f, 170, paint);
        });

        var found = DiscStamp.Detect(label)!;

        Assert.Equal(found.Radius, found.LabelRadius, 1.0);
    }

    // ── what comes out ──

    [Fact]
    public void A_stamped_cd_is_a_transparent_png()
    {
        var stamped = DiscStamp.Stamp(Cd(), vinyl: false);

        Assert.NotNull(stamped);
        Assert.True(DiscStamp.HasAlpha(stamped!));
    }

    /// <summary>A CD replaces the drawn record completely. It is opaque from the middle out to the rim. It
    /// is transparent only in the corners, which the record never filled, and in the spindle
    /// hole.</summary>
    [Fact]
    public void A_stamped_cd_fills_the_record_and_keeps_its_hole()
    {
        using var stamped = SKBitmap.Decode(DiscStamp.Stamp(Cd(), vinyl: false));
        var half = stamped.Width / 2;

        Assert.Equal(0, stamped.GetPixel(half, half).Alpha);                       // the spindle hole
        Assert.Equal(255, stamped.GetPixel(half, (int)(half * 0.5)).Alpha);        // a quarter out
        Assert.Equal(255, stamped.GetPixel(half, (int)(half * 0.08)).Alpha);       // nearly the rim
        Assert.Equal(0, stamped.GetPixel(4, 4).Alpha);                             // the corner
    }

    /// <summary>A record's label replaces only the middle. Everything outside it stays transparent, so
    /// RPM's own pressing (grooves, lead-in and run-out) still shows around the real label.</summary>
    [Fact]
    public void A_stamped_vinyl_label_leaves_the_grooves_showing()
    {
        using var stamped = SKBitmap.Decode(DiscStamp.Stamp(Vinyl(), vinyl: true));
        var half = stamped.Width / 2;

        Assert.Equal(255, stamped.GetPixel(half, half).Alpha);                     // the label itself
        Assert.Equal(0, stamped.GetPixel(half, (int)(half * 0.4)).Alpha);          // where the grooves are
        Assert.Equal(0, stamped.GetPixel(4, 4).Alpha);
    }

    /// <summary>The label itself must land there, not the whole record shrunk into the middle. The middle
    /// of the stamp must be the colour the label was printed.</summary>
    [Fact]
    public void A_stamped_vinyl_label_is_the_label_and_not_the_shrunken_record()
    {
        using var stamped = SKBitmap.Decode(DiscStamp.Stamp(Vinyl(), vinyl: true));
        var half = stamped.Width / 2;

        // Not the middle. The middle of a whole record is its label too, so it proves nothing. The outer
        // edge of what was stamped is the test. The label should reach all the way to that edge if the
        // inner circle was found. Black grooves should be there if the whole record was shrunk into the
        // label's place.
        var rim = stamped.GetPixel(half + (int)(half * 0.33f * 0.85f), half);
        Assert.True(rim.Red > 0xA0 && rim.Green < 0x80,
            $"the edge of the stamp is {rim} — the record was shrunk in, not its label cut out");
    }

    [Fact]
    public void A_picture_with_nothing_round_in_it_stamps_nothing()
    {
        var sleeve = Picture(canvas =>
        {
            using var paint = Fill(0x20, 0x80, 0x40);
            canvas.DrawRect(new SKRect(40, 40, Edge - 40, Edge - 40), paint);
        });

        Assert.Null(DiscStamp.Stamp(sleeve, vinyl: false));
    }

    // ── a cdart that is already what is wanted ──

    /// <summary>A real cdart carries its transparency and must be left exactly as it is. Reading the
    /// pixels rather than the header, because a photograph saved as a PNG has an alpha channel too and
    /// is opaque in every one of them.</summary>
    [Fact]
    public void A_photograph_saved_as_a_png_does_not_count_as_transparent()
    {
        Assert.False(DiscStamp.HasAlpha(Cd()));
    }

    [Fact]
    public void A_picture_with_holes_in_it_counts_as_transparent()
    {
        Assert.True(DiscStamp.HasAlpha(DiscStamp.Stamp(Cd(), vinyl: false)!));
    }

    // ── whether the picture is the whole record ──
    //
    // Surfaces that draw a disc need this on top of the picture itself. A CD is the whole record, and the
    // pressing under it goes. A label is stuck ON the pressing. If the pressing were removed, the label
    // would turn in mid-air over the disc's own shadow. The Cover Art Archive has a label for most LPs, so
    // this is the ordinary case.

    [Fact]
    public void A_stamped_cd_covers_the_record()
    {
        Assert.True(DiscStamp.Covers(DiscStamp.Stamp(Cd(), vinyl: false)!));
    }

    [Fact]
    public void A_stamped_vinyl_label_does_not_cover_the_record()
    {
        Assert.False(DiscStamp.Covers(DiscStamp.Stamp(Vinyl(), vinyl: true)!));
    }

    /// <summary>A flat photograph that nothing could be cut out of is drawn as it came, clipped round by
    /// the surface. So as far as anything underneath is concerned, it is the whole record.</summary>
    [Fact]
    public void A_flat_picture_covers_the_record()
    {
        Assert.True(DiscStamp.Covers(Cd()));
    }

    [Fact]
    public void Round_says_which_picture_it_handed_back()
    {
        Assert.True(DiscStamp.Round(Cd(), vinyl: false).CoversRecord);
        Assert.False(DiscStamp.Round(Vinyl(), vinyl: true).CoversRecord);
    }

    /// <summary>A real cdart is a transparent PNG of the whole disc. Fanart returns it, and Round leaves it
    /// untouched. It is still the whole record. The alpha it carries is at its corners and its hole, not a
    /// hole where the grooves should be.</summary>
    [Fact]
    public void A_cdart_that_arrived_transparent_covers_the_record()
    {
        var cdart = DiscStamp.Stamp(Cd(), vinyl: false)!;

        Assert.True(DiscStamp.HasAlpha(cdart));
        Assert.True(DiscStamp.Round(cdart, vinyl: false).CoversRecord);
    }
}
