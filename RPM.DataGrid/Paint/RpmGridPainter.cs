using RPM.DataGrid.Model;
using RPM.DataGrid.Text;
using SkiaSharp;

namespace RPM.DataGrid.Paint;

/// <summary>
/// Draws the whole grid: the bands, the rows, the cells, the rules and the header.
///
/// <para>One method and a few reusable paints. There are no per-cell elements, so there is nothing
/// to recycle, nothing to prepare, and no element that can show one row while the grid expects
/// another. Recycled row elements are a common source of that kind of bug.</para>
///
/// <para>No per-cell clip. Text is cut with an ellipsis to its column before it is shaped, so a cell
/// cannot overrun its neighbour, and hundreds of saves and restores per frame would gain nothing.
/// The rows area and the header band each get one clip.</para>
/// </summary>
public sealed class RpmGridPainter : IDisposable
{
    private readonly TextShop _shop;

    private readonly SKPaint _fill = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly SKPaint _text = new() { IsAntialias = true };

    private readonly List<ColumnSlot> _slots = [];

    /// <summary>
    /// The icon face, for the glyph columns, the tree's state marks and the header's sort and filter
    /// marks. Font Awesome in the app; in a test, whatever face the host interns.
    /// </summary>
    public int IconFace { get; set; }

    public RpmGridPainter(TextShop shop)
    {
        ArgumentNullException.ThrowIfNull(shop);
        _shop = shop;
    }

    /// <summary>How many cells the last paint drew. Diagnostics.</summary>
    public int CellsDrawn { get; private set; }

    /// <summary>How many rows the last paint drew.</summary>
    public int RowsDrawn { get; private set; }

    /// <summary>
    /// Whether the last paint drew anything that is still moving, so the host knows to ask for
    /// another frame.
    ///
    /// <para>Reported by the paint, not decided by the host: the mark is on a row, the row scrolls,
    /// and only the paint that just ran knows whether it is on screen now. A host that started a
    /// timer when playback started would keep the window repainting for the whole length of every
    /// track, whether the mark was visible or not.</para>
    /// </summary>
    public bool DrewAnimation { get; private set; }

    public void Paint(SKCanvas canvas, GridFrame frame)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(frame);

        CellsDrawn = 0;
        RowsDrawn = 0;
        DrewAnimation = false;

        var p = frame.Palette;

        // Start the frame by REPLACING what is under it, not by drawing on top of it.
        //
        // SKCanvasElement draws into the window's own Skia canvas and caches the result until
        // Invalidate (see the Uno docs for the control). Nothing clears that background for us, so
        // a frame that only draws over it lets every earlier frame show through. A translucent
        // background does exactly that, and the list is smeared across every offset it has been
        // scrolled to.
        //
        // Src rather than Clear: the grid's background is translucent (#33000000 over the app's
        // glass), so it cannot be an opaque fill, and the Src blend mode makes a translucent fill
        // replace the old pixels instead of adding to them. Other Skia surfaces in this app draw an
        // opaque background, so they do not need this.
        if (frame.Width <= 0 || frame.Height <= 0)
            return;

        if (frame.PaintsOwnGround)
        {
            _fill.Color = p.Surface;
            _fill.BlendMode = SKBlendMode.Src;
            canvas.DrawRect(0, 0, (float)frame.Width, (float)frame.Height, _fill);
            _fill.BlendMode = SKBlendMode.SrcOver;
        }

        var count = frame.Source.Count;
        var range = frame.Rows.RangeAt(frame.ScrollY, frame.RowsHeight, count, overscan: 0);
        var columns = frame.Columns.RangeAt(frame.ScrollX, frame.Width, overscan: 0);
        frame.Columns.SlotsInto(columns, _slots);

        PaintRows(canvas, frame, range, count);

        if (frame.ShowHeader)
            PaintHeader(canvas, frame);
    }

    // ── Rows ────────────────────────────────────────────────────────

    private void PaintRows(SKCanvas canvas, GridFrame frame, (int First, int End) range, int count)
    {
        var m = frame.Metrics;
        var p = frame.Palette;

        canvas.Save();
        canvas.ClipRect(SKRect.Create(0, (float)frame.RowsTop, (float)frame.Width, (float)frame.RowsHeight));

        // Into the list's own coordinates: a row at index i has its top at i * rowHeight, whatever
        // is scrolled. Every y below is a list y, and the translate is the only place the scroll
        // appears.
        canvas.Translate((float)-frame.ScrollX, (float)(frame.RowsTop - frame.ScrollY));

        var rowHeight = (float)m.RowHeight;
        var stripWidth = (float)Math.Max(frame.Columns.TotalWidth, frame.Width + frame.ScrollX);

        for (var i = range.First; i < range.End; i++)
        {
            var top = (float)frame.Rows.TopOf(i);
            var carried = frame.Carried?.Contains(i) == true;
            if (carried)
                top += (float)frame.CarryOffsetY;

            var row = frame.Source.Row(i);
            PaintRowGround(canvas, frame, i, row, top, rowHeight, stripWidth, carried);

            PaintCells(canvas, frame, row, i, top, rowHeight);

            RowsDrawn++;
        }

        PaintRules(canvas, frame, range);

        if (frame.Drop is { } drop)
        {
            _fill.Color = p.DropLine;
            var y = (float)frame.Rows.TopOf(Math.Clamp(drop.Row, 0, count));
            canvas.DrawRect(0, y - 1, stripWidth, 2, _fill);
        }

        canvas.Restore();

        if (frame.Band is { } band)
            PaintBand(canvas, frame, band);
    }

    /// <summary>The band, the selection wash, the hover and the focus ring. What is under the text.</summary>
    private void PaintRowGround(
        SKCanvas canvas, GridFrame frame, int index, object? row, float top, float height, float width, bool carried)
    {
        var p = frame.Palette;
        var selected = frame.IsWashed(index, row);
        var hovered = index == frame.HoverRow;

        if (index % 2 == 1)
        {
            _fill.Color = p.RowBand;
            canvas.DrawRect(0, top, width, height, _fill);
        }

        var wash = selected
            ? hovered ? p.RowSelectedHover : p.RowSelected
            : hovered ? p.RowHover : SKColors.Transparent;

        if (wash.Alpha > 0)
        {
            _fill.Color = wash;
            canvas.DrawRect(0, top, width, height, _fill);
        }

        if (selected)
        {
            _fill.Color = p.SelectionBar;
            canvas.DrawRect(0, top, frame.Metrics.SelectionBarWidth, height, _fill);
        }

        if (carried)
        {
            // A row being carried is drawn away from its place, so its old place would otherwise
            // look like a missing row. A half-strength wash shows where it was.
            _fill.Color = p.RowSelected;
            canvas.DrawRect(0, top, width, height, _fill);
        }

        if (index == frame.Selection.Focus && !selected)
        {
            _stroke.Color = p.FocusRing;
            canvas.DrawRect(0.5f, top + 0.5f, width - 1, height - 1, _stroke);
        }
    }

    /// <summary>One line down the right of each visible column, the full height of the rows drawn.
    /// One draw per column, not one per cell.</summary>
    private void PaintRules(SKCanvas canvas, GridFrame frame, (int First, int End) range)
    {
        if (_slots.Count == 0 || range.End <= range.First)
            return;

        _fill.Color = frame.Palette.Rule;

        var top = (float)frame.Rows.TopOf(range.First);
        var bottom = (float)frame.Rows.TopOf(range.End);

        foreach (var slot in _slots)
        {
            var x = (float)(slot.X + slot.Width) - 1;
            canvas.DrawRect(x, top, 1, bottom - top, _fill);
        }
    }

    private void PaintBand(SKCanvas canvas, GridFrame frame, GridBand band)
    {
        var rect = SKRect.Create(
            (float)Math.Min(band.X1, band.X2),
            (float)Math.Min(band.Y1, band.Y2),
            (float)Math.Abs(band.X2 - band.X1),
            (float)Math.Abs(band.Y2 - band.Y1));

        _fill.Color = frame.Palette.BandFill;
        canvas.DrawRect(rect, _fill);

        _stroke.Color = frame.Palette.BandEdge;
        canvas.DrawRect(rect, _stroke);
    }

    // ── Cells ───────────────────────────────────────────────────────

    private void PaintCells(SKCanvas canvas, GridFrame frame, object? row, int index, float top, float height)
    {
        var m = frame.Metrics;
        var p = frame.Palette;
        var columns = frame.Columns.Visible;
        var baseline = Baseline(top, height, m.FontSize);

        foreach (var slot in _slots)
        {
            if (slot.Index >= columns.Count)
                continue;

            var column = columns[slot.Index];
            var x = (float)slot.X;
            var w = (float)slot.Width;

            // A row whose page has not arrived draws nothing, not a placeholder's ToString. The row
            // exists and is still loading; it is not blank.
            if (row is null)
                continue;

            var value = column.Read(row);
            if (value.IsBlank && value.Kind != CellKind.Tick)
            {
                CellsDrawn++;
                continue;
            }

            switch (value.Kind)
            {
                case CellKind.Text:
                    DrawText(canvas, frame, value.Text, x, w, baseline, column.Align, p.Text);
                    break;

                case CellKind.Glyph:
                case CellKind.GlyphButton:
                    DrawGlyph(canvas, frame, value, index, slot.Index, x, w, top, height);
                    break;

                case CellKind.Tick:
                    DrawTick(canvas, frame, value.Tick, x, w, top, height);
                    break;

                case CellKind.Tree:
                    DrawTree(canvas, frame, column, value, index, x, w, top, height, baseline);
                    break;

                case CellKind.TitleBadge:
                    DrawTitle(canvas, frame, value, x, w, top, height, baseline);
                    break;

                case CellKind.PlayingBars:
                    DrawBars(canvas, frame, x, w, top, height);
                    DrewAnimation = true;
                    break;

                case CellKind.Art:
                    DrawArt(canvas, frame, value.ArtKey, x, w, top, height);
                    break;
            }

            CellsDrawn++;
        }
    }

    private void DrawText(
        SKCanvas canvas, GridFrame frame, string? s, float x, float w, float baseline,
        CellAlign align, SKColor colour)
    {
        if (string.IsNullOrEmpty(s))
            return;

        var pad = frame.Metrics.CellPadX;
        var room = w - pad * 2;
        if (room <= 1)
            return;

        var shaped = _shop.Get(s, frame.Metrics.FontSize, room);
        if (shaped.IsEmpty)
            return;

        var at = align switch
        {
            CellAlign.Right => x + w - pad - shaped.Width,
            CellAlign.Centre => x + (w - shaped.Width) / 2,
            _ => x + pad,
        };

        _text.Color = colour;
        shaped.Draw(canvas, at, baseline, _text);
    }

    private void DrawGlyph(
        SKCanvas canvas, GridFrame frame, CellValue value, int row, int column,
        float x, float w, float top, float height)
    {
        if (string.IsNullOrEmpty(value.Glyph))
            return;

        var m = frame.Metrics;
        var colour = value.GlyphColour != 0 ? new SKColor(value.GlyphColour) : frame.Palette.Text;

        // A glyph button being held is dimmed, not moved. A templated Button moves by a pixel on
        // press, and at ten pixels that looks like a flicker.
        if (value.Kind == CellKind.GlyphButton && row == frame.PressedRow && column == frame.PressedColumn)
            colour = colour.WithAlpha((byte)(colour.Alpha * 0.5));

        var shaped = _shop.GetIcon(value.Glyph, IconFace, m.IconSize);
        if (shaped.IsEmpty)
            return;

        _text.Color = colour;
        shaped.Draw(canvas, x + (w - shaped.Width) / 2, Baseline(top, height, m.IconSize), _text);
    }

    /// <summary>
    /// The album art cell: a square, as tall as the row will allow, inset by the cell padding.
    ///
    /// <para>The well (the empty square) is drawn whether or not the picture is there. A cover
    /// arrives when its decode finishes, which is after the first frame that needs it. A cell that
    /// drew nothing until then would make the column fill in at random while the user
    /// scrolls.</para>
    /// </summary>
    private void DrawArt(
        SKCanvas canvas, GridFrame frame, string? key, float x, float w, float top, float height)
    {
        var m = frame.Metrics;
        var side = Math.Min(w - m.CellPadX * 2, height - 2);
        if (side <= 2)
            return;

        var box = SKRect.Create(x + (w - side) / 2, top + (height - side) / 2, side, side);
        var radius = Math.Min(3f, side / 6);

        var picture = key is null ? null : frame.Art?.Invoke(key);
        if (picture is null)
        {
            _fill.Color = frame.Palette.ArtWell;
            canvas.DrawRoundRect(box, radius, radius, _fill);
            return;
        }

        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(box, radius, radius), antialias: true);
        canvas.DrawImage(picture, box, ArtSampling);
        canvas.Restore();
    }

    /// <summary>How a cover is scaled down to a row. Mipmaps, because the step is large (a 1000 px
    /// scan into a 24 px cell), and nearest-neighbour at that ratio gives noise, not a
    /// picture.</summary>
    private static readonly SKSamplingOptions ArtSampling =
        new(SKFilterMode.Linear, SKMipmapMode.Linear);

    private void DrawTick(SKCanvas canvas, GridFrame frame, TickState state, float x, float w, float top, float height)
    {
        if (state == TickState.None)
            return;

        var m = frame.Metrics;
        var p = frame.Palette;
        var side = m.TickSize;
        var box = SKRect.Create(x + (w - side) / 2, top + ((float)height - side) / 2, side, side);

        if (state == TickState.Off)
        {
            _stroke.Color = p.TickEdge;
            _stroke.IsAntialias = true;
            canvas.DrawRoundRect(Inset(box, 0.5f), 3, 3, _stroke);
            _stroke.IsAntialias = false;
            return;
        }

        _fill.Color = p.TickFill;
        canvas.DrawRoundRect(box, 3, 3, _fill);

        _stroke.Color = p.TickMark;
        _stroke.StrokeWidth = 1.6f;
        _stroke.IsAntialias = true;
        _stroke.StrokeCap = SKStrokeCap.Round;

        if (state == TickState.On)
        {
            // A tick, in the box's own proportions so it scales with the metric.
            var l = box.Left;
            var t = box.Top;
            using var path = new SKPath();
            path.MoveTo(l + side * 0.26f, t + side * 0.52f);
            path.LineTo(l + side * 0.44f, t + side * 0.70f);
            path.LineTo(l + side * 0.76f, t + side * 0.32f);
            canvas.DrawPath(path, _stroke);
        }
        else
        {
            // Partly: a bar, because some of what is under this row is ticked and some is not.
            var y = box.MidY;
            canvas.DrawLine(box.Left + side * 0.26f, y, box.Right - side * 0.26f, y, _stroke);
        }

        _stroke.StrokeWidth = 1;
        _stroke.IsAntialias = false;
        _stroke.StrokeCap = SKStrokeCap.Butt;
    }

    private void DrawTree(
        SKCanvas canvas, GridFrame frame, GridColumn column, CellValue value, int index,
        float x, float w, float top, float height, float baseline)
    {
        var m = frame.Metrics;
        var p = frame.Palette;

        var pen = x + m.CellPadX + value.Depth * m.Indent;
        var right = x + w - m.CellPadX;

        if ((value.Marks & TreeMarks.Expandable) != 0)
        {
            DrawExpander(canvas, p, pen, top + (float)height / 2, m.ExpanderSize,
                (value.Marks & TreeMarks.Expanded) != 0);
        }

        pen += m.ExpanderSize + 4;

        // The grip replaces the state glyph while the pointer is on a row that can be moved. This is
        // decided here and not by the row: the hovered row is stored ONCE, in the frame, so a row
        // that scrolls away from a still pointer cannot keep an out-of-date hover state.
        var grip = (value.Marks & TreeMarks.Draggable) != 0
            && index == frame.HoverRow
            && column.GripGlyph is { Length: > 0 };

        if ((value.Marks & TreeMarks.Playing) != 0)
            DrewAnimation = true;

        if (grip)
        {
            var glyph = _shop.GetIcon(column.GripGlyph, IconFace, m.IconSize);
            if (!glyph.IsEmpty)
            {
                _text.Color = p.Text;
                glyph.Draw(canvas, pen, Baseline(top, height, m.IconSize), _text);
                pen += glyph.Width + 5;
            }
        }
        else if ((value.Marks & TreeMarks.Playing) != 0)
        {
            // The playing row's glyph slot holds the mark instead of a glyph, and the text after it
            // moves on by the mark's own width.
            var span = Bars.Length * m.BarWidth + (Bars.Length - 1) * m.BarGap;
            DrawBars(canvas, frame, pen, span, top, height);
            pen += span + 5;
        }
        else if (!string.IsNullOrEmpty(value.Glyph))
        {
            var glyph = _shop.GetIcon(value.Glyph, IconFace, m.IconSize);
            if (!glyph.IsEmpty)
            {
                _text.Color = value.GlyphColour != 0 ? new SKColor(value.GlyphColour) : p.Text;
                glyph.Draw(canvas, pen, Baseline(top, height, m.IconSize), _text);
                pen += glyph.Width + 5;
            }
        }

        var room = right - pen;
        if (room <= 1 || string.IsNullOrEmpty(value.Text))
            return;

        var shaped = _shop.Get(value.Text, m.FontSize, room);
        _text.Color = p.Text;
        shaped.Draw(canvas, pen, baseline, _text);
    }

    private void DrawExpander(SKCanvas canvas, GridPalette p, float x, float midY, float size, bool open)
    {
        _fill.Color = p.Expander;

        var h = size * 0.55f;
        using var path = new SKPath();

        if (open)
        {
            path.MoveTo(x, midY - h / 2);
            path.LineTo(x + size, midY - h / 2);
            path.LineTo(x + size / 2, midY + h);
        }
        else
        {
            path.MoveTo(x + size * 0.25f, midY - size / 2);
            path.LineTo(x + size * 0.25f + h * 1.1f, midY);
            path.LineTo(x + size * 0.25f, midY + size / 2);
        }

        path.Close();
        canvas.DrawPath(path, _fill);
    }

    private void DrawTitle(
        SKCanvas canvas, GridFrame frame, CellValue value, float x, float w, float top, float height, float baseline)
    {
        var m = frame.Metrics;
        var pad = m.CellPadX;
        var room = w - pad * 2 - (value.Badge ? m.BadgeSize + m.BadgeGap : 0);
        if (room <= 1)
            return;

        var shaped = _shop.Get(value.Text, m.FontSize, room);
        _text.Color = frame.Palette.Text;
        shaped.Draw(canvas, x + pad, baseline, _text);

        if (!value.Badge)
            return;

        // The badge sits directly after the name, not at the far edge of the column, so it looks
        // like a mark on the title and not like a separate column.
        DrawBadge(canvas, frame, x + pad + shaped.Width + m.BadgeGap, top + ((float)height - m.BadgeSize) / 2, m.BadgeSize);
    }

    /// <summary>The explicit badge: a rounded square with an E in it.</summary>
    private void DrawBadge(SKCanvas canvas, GridFrame frame, float x, float y, float side)
    {
        _fill.Color = frame.Palette.Text.WithAlpha(0x4D);
        canvas.DrawRoundRect(SKRect.Create(x, y, side, side), 2.5f, 2.5f, _fill);

        var letter = _shop.Get("E", side * 0.72f);
        if (letter.IsEmpty)
            return;

        _text.Color = frame.Palette.Surface.WithAlpha(0xFF);
        letter.Draw(canvas, x + (side - letter.Width) / 2, Baseline(y, side, side * 0.72f), _text);
    }

    /// <summary>
    /// The three-bar now-playing mark. The same shape the app's own <c>PlayingBars</c> uses: three
    /// periods that do not divide into one another, a third of a cycle apart, so the bars never
    /// move in step.
    /// </summary>
    private void DrawBars(SKCanvas canvas, GridFrame frame, float x, float w, float top, float height)
    {
        var m = frame.Metrics;
        var t = frame.BarsClock;

        var span = Bars.Length * m.BarWidth + (Bars.Length - 1) * m.BarGap;
        var pen = x + (w - span) / 2;

        // The bars stand on the baseline of the text beside them, which is where the app's own mark
        // puts them.
        var foot = Baseline(top, height, m.FontSize);

        _fill.Color = frame.Palette.Text;

        foreach (var (full, period, min, phase) in Bars)
        {
            var mid = (min + full) / 2;
            var amp = (full - min) / 2;
            var h = (float)(mid + amp * Math.Sin(2 * Math.PI * t / period + phase));

            canvas.DrawRect(pen, foot - h, m.BarWidth, h, _fill);
            pen += m.BarWidth + m.BarGap;
        }
    }

    private static readonly (float Full, double Period, float Min, double Phase)[] Bars =
    [
        (13, 1.28, 4, 0),
        (12, 0.96, 5, 2 * Math.PI / 3),
        (10, 1.52, 4, 4 * Math.PI / 3),
    ];

    // ── Header ──────────────────────────────────────────────────────

    private void PaintHeader(SKCanvas canvas, GridFrame frame)
    {
        var m = frame.Metrics;
        var p = frame.Palette;
        var height = (float)m.HeaderHeight;

        canvas.Save();
        canvas.ClipRect(SKRect.Create(0, 0, (float)frame.Width, height));

        _fill.Color = p.Header;
        canvas.DrawRect(0, 0, (float)frame.Width, height, _fill);

        canvas.Translate((float)-frame.ScrollX, 0);

        var columns = frame.Columns.Visible;
        var baseline = Baseline(0, height, m.HeaderFontSize);

        foreach (var slot in _slots)
        {
            if (slot.Index >= columns.Count)
                continue;

            var column = columns[slot.Index];
            var x = (float)slot.X;
            var w = (float)slot.Width;

            if (slot.Index == frame.HoverHeader)
            {
                _fill.Color = p.HeaderHover;
                canvas.DrawRect(x, 0, w, height, _fill);
            }

            var pen = x + m.HeaderPadX;
            var right = x + w - m.HeaderPadX;

            // The marks are placed from the right first and the text is fitted to the space left, so
            // a sorted column's name is shortened for its arrow and never drawn under it.
            if (frame.FilteredColumns?.Contains(column.Id) == true)
                right -= DrawFilterMark(canvas, frame, right, height);

            if (column.Id == frame.SortColumn)
                right -= DrawSortMark(canvas, frame, right, height, frame.SortDescending);

            var room = right - pen;
            if (room > 1 && !string.IsNullOrEmpty(column.Header))
            {
                var shaped = _shop.Get(column.Header, m.HeaderFontSize, room);
                _text.Color = p.HeaderText;
                shaped.Draw(canvas, pen, baseline, _text);
            }

            _fill.Color = p.Rule;
            canvas.DrawRect(x + w - 1, 0, 1, height, _fill);
        }

        canvas.Restore();

        // The rule under the whole header, drawn unscrolled so it runs the full width whatever the
        // columns add up to.
        _fill.Color = p.Rule;
        canvas.DrawRect(0, height - 1, (float)frame.Width, 1, _fill);
    }

    /// <summary>
    /// The sort arrow, drawn rather than set in an icon font.
    ///
    /// <para>A path and not a glyph, so the header does not depend on a font: the host supplies the
    /// icon face, and a grid built without one would show nothing where the arrow belongs. A
    /// triangle at nine pixels also looks better drawn as a path than hinted as a glyph.</para>
    /// </summary>
    private float DrawSortMark(SKCanvas canvas, GridFrame frame, float right, float height, bool descending)
    {
        var w = frame.Metrics.HeaderMarkSize;
        var h = w * 0.55f;
        var x = right - w;
        var mid = height / 2;

        _fill.Color = frame.Palette.HeaderMark;

        using var path = new SKPath();
        if (descending)
        {
            path.MoveTo(x, mid - h / 2);
            path.LineTo(x + w, mid - h / 2);
            path.LineTo(x + w / 2, mid + h / 2);
        }
        else
        {
            path.MoveTo(x, mid + h / 2);
            path.LineTo(x + w, mid + h / 2);
            path.LineTo(x + w / 2, mid - h / 2);
        }

        path.Close();
        canvas.DrawPath(path, _fill);

        return w + 4;
    }

    /// <summary>The filter mark: a funnel, drawn for the same reason as the arrow.</summary>
    private float DrawFilterMark(SKCanvas canvas, GridFrame frame, float right, float height)
    {
        var w = frame.Metrics.HeaderMarkSize;
        var x = right - w;
        var top = (height - w) / 2;

        _fill.Color = frame.Palette.HeaderMark;

        using var path = new SKPath();
        path.MoveTo(x, top);
        path.LineTo(x + w, top);
        path.LineTo(x + w * 0.6f, top + w * 0.45f);
        path.LineTo(x + w * 0.6f, top + w);
        path.LineTo(x + w * 0.4f, top + w * 0.82f);
        path.LineTo(x + w * 0.4f, top + w * 0.45f);
        path.Close();
        canvas.DrawPath(path, _fill);

        return w + 4;
    }

    // ── Shared ──────────────────────────────────────────────────────

    /// <summary>
    /// Where text sits in a row of a given height: the line box centred on the row, and the baseline
    /// where that puts it. Not the row's middle, which would place the text half a descender too
    /// low.
    /// </summary>
    private float Baseline(float top, float height, float fontSize)
    {
        var metrics = _shop.Metrics(fontSize);
        return top + (height - (metrics.Descent - metrics.Ascent)) / 2 - metrics.Ascent;
    }

    private static SKRect Inset(SKRect r, float by) =>
        new(r.Left + by, r.Top + by, r.Right - by, r.Bottom - by);

    public void Dispose()
    {
        _fill.Dispose();
        _stroke.Dispose();
        _text.Dispose();
    }
}
