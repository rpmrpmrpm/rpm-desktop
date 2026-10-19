using RPM.Core.Viz;

namespace RPM.Core.Tests;

/// <summary>
/// The run of bars either side of a phone's sleeve: how many fit, and which bands each of them reads.
///
/// <para>This is the part that can be wrong without looking wrong. A fold that drops the last band or
/// counts one twice draws a spectrum that moves convincingly and is quietly not the music.</para>
/// </summary>
public class SpectrumRunTests
{
    [Theory]
    [InlineData(70f, 7f, 10)]
    [InlineData(48f, 7f, 6)]
    [InlineData(6f, 7f, 0)]        // no room at all
    public void The_run_is_as_many_bars_as_fit(float length, float pitch, int columns)
        => Assert.Equal(columns, SpectrumRun.Across(length, pitch).Columns);

    /// <summary>Never more columns than there are bands: two columns reading one band stand at the same
    /// height and look like a picket fence.</summary>
    [Fact]
    public void There_is_never_a_bar_without_a_band_of_its_own()
    {
        var run = SpectrumRun.Across(4000f, 1f);
        Assert.Equal(RoomBands.Bands, run.Columns);
    }

    [Fact]
    public void A_run_too_short_to_read_does_not_fit()
    {
        Assert.False(SpectrumRun.Across(14f, 7f).Fits);     // two bars
        Assert.True(SpectrumRun.Across(21f, 7f).Fits);      // three
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-10f)]
    public void Nothing_is_drawn_in_no_room(float length)
    {
        var run = SpectrumRun.Across(length, 7f);

        Assert.Equal(0, run.Columns);
        Assert.False(run.Fits);
        Assert.Equal(0f, run.Pitch);
        Assert.Equal(0f, run.LevelAt(new float[RoomBands.Bands], 0));
    }

    /// <summary>
    /// Every band belongs to exactly one column, and no band is left out. Proved by lighting one band at
    /// a time: exactly one column may answer, and across the whole spectrum every column must have been
    /// the one that answered at least once.
    /// </summary>
    [Fact]
    public void Every_band_belongs_to_exactly_one_column()
    {
        var run = SpectrumRun.Across(70f, 7f);
        var answered = new bool[run.Columns];
        var bands = new float[RoomBands.Bands];

        for (int band = 0; band < bands.Length; band++)
        {
            Array.Clear(bands);
            bands[band] = 1f;

            var lit = new List<int>();
            for (int column = 0; column < run.Columns; column++)
                if (run.LevelAt(bands, column) > 0f)
                    lit.Add(column);

            Assert.True(lit.Count == 1, $"band {band} was read by {lit.Count} columns");
            answered[lit[0]] = true;
        }

        Assert.DoesNotContain(false, answered);
    }

    [Fact]
    public void A_column_is_the_mean_of_what_it_covers()
    {
        var run = SpectrumRun.Across(RoomBands.Bands * 7f / 4f, 7f);   // four bands to a column
        Assert.Equal(RoomBands.Bands / 4, run.Columns);

        var bands = new float[RoomBands.Bands];
        for (int i = 0; i < 4; i++) bands[i] = i;                      // 0, 1, 2, 3

        Assert.Equal(1.5f, run.LevelAt(bands, 0), 4);
    }

    [Fact]
    public void A_column_off_the_end_of_the_run_reads_nothing()
    {
        var run = SpectrumRun.Across(70f, 7f);
        var bands = new float[RoomBands.Bands];
        Array.Fill(bands, 1f);

        Assert.Equal(0f, run.LevelAt(bands, -1));
        Assert.Equal(0f, run.LevelAt(bands, run.Columns));
    }

    /// <summary>The run stands tallest where it leaves the sleeve and settles as it goes outwards. Only
    /// the ROOM the reading is given changes; the reading itself is untouched.</summary>
    [Fact]
    public void The_run_settles_toward_the_rim()
    {
        Assert.Equal(1f, SpectrumRun.Damp(0f, 0.30f, 1.6f), 4);
        Assert.Equal(0.70f, SpectrumRun.Damp(1f, 0.30f, 1.6f), 4);

        double last = 1.01;
        for (float u = 0; u <= 1f; u += 0.05f)
        {
            var here = SpectrumRun.Damp(u, 0.30f, 1.6f);
            Assert.True(here <= last, $"the run should never grow toward the rim: {here} after {last}");
            last = here;
        }
    }

    /// <summary>No damp at all is the plain reading, and the one the Windows version uses: one flat
    /// ceiling across the whole run.</summary>
    [Fact]
    public void No_damp_is_one_flat_ceiling()
    {
        for (float u = 0; u <= 1f; u += 0.1f)
            Assert.Equal(1f, SpectrumRun.Damp(u, 0f, 1.6f), 4);
    }
}
