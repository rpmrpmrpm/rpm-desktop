using RPM.DataGrid.Model;
using RPM.DataGrid.Paint;
using RPM.DataGrid.Text;
using SkiaSharp;

namespace RPM.DataGrid.Tests;

/// <summary>
/// A grid painted into a bitmap, with the questions a test wants to ask of the result: where the ink
/// is, what colour a pixel is, whether two renders differ.
///
/// <para>This is why the painter is in a plain library rather than inside the Uno project. The app's
/// own test host has no application, no window and no dispatcher, so nothing there can raster
/// anything; here "is the selected row washed" and "does a title stay inside its column" are
/// assertions rather than screenshots somebody looks at.</para>
/// </summary>
public sealed class PaintHarness : IDisposable
{
    private readonly SkiaFaces _faces;

    public PaintHarness(IEnumerable<GridColumn> columns, IGridSource source, int width = 400, int height = 240)
    {
        _faces = new SkiaFaces(Face());
        Shop = new TextShop(_faces);
        Painter = new RpmGridPainter(Shop);

        Width = width;
        Height = height;

        Frame = new GridFrame
        {
            Columns = new GridColumns(columns),
            Source = source,
            Selection = new GridSelection(),
            Width = width,
            Height = height,
        };
    }

    public TextShop Shop { get; }

    public RpmGridPainter Painter { get; }

    public GridFrame Frame { get; }

    public int Width { get; }

    public int Height { get; }

    private static SKTypeface Face()
    {
        foreach (var name in new[] { "Helvetica", "Arial", "Liberation Sans", "DejaVu Sans", "Segoe UI" })
        {
            var f = SKTypeface.FromFamilyName(name);
            if (f is not null)
                return f;
        }

        return SKTypeface.Default;
    }

    /// <summary>Paints the frame as it stands and returns the raster.</summary>
    public Shot Render()
    {
        var bitmap = new SKBitmap(Width, Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        Painter.Paint(canvas, Frame);
        return new Shot(bitmap);
    }

    public void Dispose()
    {
        Painter.Dispose();
        Shop.Dispose();
        _faces.Dispose();
    }
}

/// <summary>One painted frame, and what can be asked of it.</summary>
public sealed class Shot(SKBitmap bitmap) : IDisposable
{
    public SKBitmap Bitmap { get; } = bitmap;

    public int Width => Bitmap.Width;

    public int Height => Bitmap.Height;

    public SKColor At(int x, int y) => Bitmap.GetPixel(x, y);

    /// <summary>The colour the grid's ground is, taken from a corner the rows do not reach.</summary>
    public SKColor Ground => At(Width - 1, Height - 1);

    /// <summary>Whether a pixel differs from the ground by more than rounding.</summary>
    public bool IsInk(int x, int y) => Differs(At(x, y), Ground);

    /// <summary>How many pixels in a band of rows differ from the ground.</summary>
    public int InkIn(int x0, int y0, int x1, int y1)
    {
        var n = 0;
        for (var y = Math.Max(0, y0); y < Math.Min(Height, y1); y++)
            for (var x = Math.Max(0, x0); x < Math.Min(Width, x1); x++)
                if (IsInk(x, y))
                    n++;
        return n;
    }

    /// <summary>The leftmost x in a band of rows that has ink, or -1.</summary>
    public int FirstInkX(int y0, int y1, int from = 0)
    {
        for (var x = from; x < Width; x++)
            for (var y = Math.Max(0, y0); y < Math.Min(Height, y1); y++)
                if (IsInk(x, y))
                    return x;
        return -1;
    }

    /// <summary>The rightmost x in a band of rows that has ink, or -1.</summary>
    public int LastInkX(int y0, int y1)
    {
        for (var x = Width - 1; x >= 0; x--)
            for (var y = Math.Max(0, y0); y < Math.Min(Height, y1); y++)
                if (IsInk(x, y))
                    return x;
        return -1;
    }

    /// <summary>
    /// The box enclosing every pixel that differs from another render of the same size, or null when
    /// the two are identical.
    ///
    /// <para>The primitive most of these tests actually want. Asking where the ink is against the
    /// grid's own ground does not work: the ground is a banded row on half the list and the rule at
    /// a column's edge is ink too, so "is there ink here" answers yes about the chrome. Rendering
    /// the same frame twice with one thing changed and asking what moved answers about that thing.</para>
    /// </summary>
    public (int Left, int Top, int Right, int Bottom)? DiffBox(Shot other)
    {
        ArgumentNullException.ThrowIfNull(other);

        int left = int.MaxValue, top = int.MaxValue, right = -1, bottom = -1;

        for (var y = 0; y < Math.Min(Height, other.Height); y++)
        {
            for (var x = 0; x < Math.Min(Width, other.Width); x++)
            {
                if (!Differs(At(x, y), other.At(x, y)))
                    continue;

                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        }

        return right < 0 ? null : (left, top, right, bottom);
    }

    /// <summary>Every pixel of a band, for comparing one render against another.</summary>
    public byte[] Band(int y0, int y1)
    {
        y0 = Math.Clamp(y0, 0, Height);
        y1 = Math.Clamp(y1, y0, Height);

        var stride = Width * 4;
        var all = Bitmap.Bytes;
        var slice = new byte[(y1 - y0) * stride];
        Array.Copy(all, y0 * stride, slice, 0, slice.Length);
        return slice;
    }

    public static bool Differs(SKColor a, SKColor b) =>
        Math.Abs(a.Red - b.Red) > 2 || Math.Abs(a.Green - b.Green) > 2 ||
        Math.Abs(a.Blue - b.Blue) > 2 || Math.Abs(a.Alpha - b.Alpha) > 2;

    public void Dispose() => Bitmap.Dispose();
}

/// <summary>A list of rows already in memory, for tests that are not about fetching.</summary>
public sealed class ListSource(IReadOnlyList<object?> rows) : IGridSource
{
    private readonly List<object?> _rows = [.. rows];

    public int Count => _rows.Count;

    public object? Row(int index) => index >= 0 && index < _rows.Count ? _rows[index] : null;

    public object? Peek(int index) => Row(index);

    public void Prefetch(int first, int end)
    {
        Asked = (first, end);
    }

    /// <summary>The last range the grid said it was about to draw.</summary>
    public (int First, int End) Asked { get; private set; } = (-1, -1);

    public event Action? Reset;
    public event Action<int, int>? Landed;
    public event Action<int, int>? Inserted;
    public event Action<int, int>? Removed;

    public void RaiseReset() => Reset?.Invoke();

    public void RaiseLanded(int first, int end) => Landed?.Invoke(first, end);

    public void RaiseInserted(int first, int end) => Inserted?.Invoke(first, end);

    public void RaiseRemoved(int first, int end) => Removed?.Invoke(first, end);
}
