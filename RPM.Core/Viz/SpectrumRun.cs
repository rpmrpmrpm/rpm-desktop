namespace RPM.Core.Viz;

/// <summary>
/// A run of bars across a given length: how many of them fit, which bands each one reads, and how tall it
/// is allowed to stand.
///
/// <para>The desktop draws one bar per band, because it has a whole landscape window to run across.
/// A phone does not. Its run sits beside the sleeve, in the margin on each side, which is a couple of
/// inches at most. 128 bars in that space would be a solid block of ink. So the number of columns is
/// however many fit at a legible pitch. Each column reads the bands it covers.</para>
///
/// <para>This is pure, and it sits here rather than beside the drawing. It is the part that can be wrong
/// without looking wrong. A fold that drops the last band, or counts one band twice, gives a run that is
/// quietly not the music.</para>
/// </summary>
public readonly record struct SpectrumRun
{
    /// <summary>The fewest columns worth drawing. Below this the run is not a spectrum. It is a row of
    /// blocks, and the honest thing is to leave it out. See <see cref="Fits"/>.</summary>
    public const int MinColumns = 3;

    private SpectrumRun(int columns, float length)
    {
        Columns = columns;
        Length = length;
    }

    /// <summary>How many bars there are.</summary>
    public int Columns { get; }

    /// <summary>The room the run spans. A caller can place the run without keeping the value it was asked
    /// with.</summary>
    public float Length { get; }

    /// <summary>Whether there is room for a run at all.</summary>
    public bool Fits => Columns >= MinColumns;

    /// <summary>How wide one column is, gap included.</summary>
    public float Pitch => Columns > 0 ? Length / Columns : 0f;

    /// <summary>
    /// The run that fits across <paramref name="length"/> at about <paramref name="pitch"/> a bar.
    /// </summary>
    /// <param name="length">The room the run has, in whatever unit the caller is drawing in.</param>
    /// <param name="pitch">How much room one bar wants, gap included. About seven points on a phone. A
    /// bar and a gap thinner than that read as a texture, not as bars.</param>
    /// <param name="bands">How many bands are folded into it. There are never more columns than bands.
    /// Two columns that read one band stand at the same height, which looks like a picket fence.</param>
    public static SpectrumRun Across(float length, float pitch, int bands = RoomBands.Bands)
    {
        if (!(length > 0) || !(pitch > 0) || bands <= 0) return new SpectrumRun(0, 0);

        int columns = Math.Min((int)(length / pitch), bands);
        return new SpectrumRun(Math.Max(columns, 0), length);
    }

    /// <summary>
    /// The level of one column: the mean of every band it covers.
    ///
    /// <para>Every band belongs to exactly one column, and no band is left out, however awkwardly the two
    /// counts divide. The boundaries come from the column index, not from a stepping cursor. So the last
    /// column ends on the last band.</para>
    /// </summary>
    public float LevelAt(ReadOnlySpan<float> bands, int column)
    {
        if (Columns <= 0 || column < 0 || column >= Columns || bands.Length == 0) return 0f;

        int lo = (int)((long)column * bands.Length / Columns);
        int hi = (int)((long)(column + 1) * bands.Length / Columns);
        if (hi <= lo) hi = lo + 1;
        if (hi > bands.Length) hi = bands.Length;

        float sum = 0f;
        for (int i = lo; i < hi; i++) sum += bands[i];
        return sum / (hi - lo);
    }

    /// <summary>
    /// How much of its full reach a bar at <paramref name="u"/> along the run is given. The run stands
    /// tallest where it leaves the sleeve, and it settles as it travels out to the rim.
    ///
    /// <para>Only the room that the reading is given changes. The reading itself is untouched. So a quiet
    /// band at the rim still reads at its own level, drawn shorter. <paramref name="damp"/> is how much is
    /// given back by the rim. <paramref name="bend"/> is the shape of that giving back. Above one, it is
    /// slack near the sleeve and steepens toward the rim. That reads as a curve, not a wedge.</para>
    /// </summary>
    public static float Damp(float u, float damp, float bend)
        => 1f - damp * MathF.Pow(Math.Clamp(u, 0f, 1f), bend);
}
