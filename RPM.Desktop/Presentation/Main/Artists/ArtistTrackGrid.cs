namespace RPM.Desktop.Presentation.Main.Artists;

/// <summary>
/// Where each item of the All tracks listing sits. Songs go in columns of equal width. Each group
/// heading (a letter or a year) takes a full-width line of its own and starts a new line.
///
/// <para>Every position is calculated from the item kinds, the column count and three fixed heights.
/// The result is the same whether or not an item is realized. Nothing is estimated from measured
/// rows.</para>
/// </summary>
internal sealed class ArtistTrackGrid
{
    private readonly double[] _tops;
    private readonly double[] _bottoms;
    private readonly int[] _columns;

    /// <param name="headers">For each item, whether it is a header.</param>
    public ArtistTrackGrid(
        IReadOnlyList<bool> headers, int columns, double rowHeight, double rowSpacing, double headerHeight)
    {
        ColumnCount = Math.Max(1, columns);
        var n = headers.Count;
        _tops = new double[n];
        _bottoms = new double[n];
        _columns = new int[n];

        var pitch = rowHeight + rowSpacing;
        double y = 0;
        var column = 0;
        for (var i = 0; i < n; i++)
        {
            if (headers[i])
            {
                // A header closes the line of songs before it, however full that line is.
                if (column > 0)
                {
                    y += pitch;
                    column = 0;
                }

                _tops[i] = y;
                _bottoms[i] = y + headerHeight;
                _columns[i] = -1;
                y += headerHeight;
                continue;
            }

            _tops[i] = y;
            _bottoms[i] = y + rowHeight;
            _columns[i] = column;
            if (++column == ColumnCount)
            {
                column = 0;
                y += pitch;
            }
        }

        Height = n == 0 ? 0 : _bottoms[n - 1];
    }

    public int Count => _tops.Length;

    public int ColumnCount { get; }

    /// <summary>The bottom of the last item. There is no spacing after it.</summary>
    public double Height { get; }

    public double Top(int index) => _tops[index];

    public double Bottom(int index) => _bottoms[index];

    /// <summary>The song's column, or -1 for a header, which spans every column.</summary>
    public int Column(int index) => _columns[index];

    /// <summary>How many columns fit in <paramref name="width"/>. The result is always at least one.
    /// This stops a zero width (a minimized window, or the left pane opening) from dividing by
    /// zero.</summary>
    public static int Columns(double width, double minItemWidth, double spacing)
        => Math.Max(1, (int)Math.Floor((width + spacing) / (minItemWidth + spacing)));

    /// <summary>
    /// The first and last item that overlap the band from <paramref name="top"/> to
    /// <paramref name="bottom"/>, or (-1, -1) when none do.
    ///
    /// <para>Uses a binary search at each end. This works because the tops and bottoms never decrease
    /// as the index grows. Items are placed in order, one line at a time, and never overlap.</para>
    /// </summary>
    public (int First, int Last) ItemsIn(double top, double bottom)
    {
        if (Count == 0 || bottom <= top)
            return (-1, -1);

        // First item whose bottom is below the band's top.
        int low = 0, high = Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_bottoms[mid] > top)
                high = mid;
            else
                low = mid + 1;
        }
        var first = low;

        // Last item whose top is above the band's bottom.
        low = 0;
        high = Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_tops[mid] < bottom)
                low = mid + 1;
            else
                high = mid;
        }
        var last = low - 1;

        return first <= last ? (first, last) : (-1, -1);
    }
}
