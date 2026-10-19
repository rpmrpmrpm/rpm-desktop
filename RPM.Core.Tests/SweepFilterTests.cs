using RPM.Core.Audio;

namespace RPM.Core.Tests;

/// <summary>
/// The low pass that a record is rolled out under and rolled in from underneath: closed, it leaves a
/// kick and a bassline; open, it is not there at all.
/// </summary>
public class SweepFilterTests
{
    private const int Rate = 44100;
    private const int Channels = 2;
    private const int Frames = 8192;

    /// <summary>A sine at <paramref name="hz"/>, and how loud it still is once the filter has
    /// processed it. Run over several blocks so the answer is the settled response and not the first
    /// milliseconds of the filter starting up.</summary>
    private static double Through(SweepFilter filter, double hz)
    {
        var block = new float[Frames * Channels];
        double heard = 0;

        for (int pass = 0; pass < 4; pass++)
        {
            for (int f = 0; f < Frames; f++)
            {
                var v = (float)Math.Sin(2 * Math.PI * hz * (pass * Frames + f) / Rate);
                for (int c = 0; c < Channels; c++)
                    block[f * Channels + c] = v;
            }

            filter.Process(block, 0, Frames);

            if (pass < 3)
                continue;

            double sum = 0;
            for (int i = 0; i < Frames * Channels; i++)
                sum += block[i] * block[i];
            heard = Math.Sqrt(sum / (Frames * Channels));
        }

        // Against the RMS of the sine itself, so 1 is untouched.
        return heard / (1 / Math.Sqrt(2));
    }

    [Fact]
    public void Open_it_is_not_there_at_all()
    {
        var filter = new SweepFilter(Rate, Channels);
        filter.SetCutoff(SweepFilter.OpenHz);

        // Not "near enough one": the block is returned untouched, and anything measured a tiny amount
        // under one is the sine not finishing a whole cycle in the window.
        Assert.False(filter.IsClosing);
        Assert.True(Through(filter, 60) > 0.99, "a low left alone");
        Assert.True(Through(filter, 8000) > 0.99, "and a high left alone");
    }

    [Fact]
    public void Closed_it_leaves_the_bottom_end_and_takes_everything_else()
    {
        var filter = new SweepFilter(Rate, Channels);
        filter.SetCutoff(SweepFilter.ClosedHz);

        Assert.True(filter.IsClosing);

        // A kick and a bassline live under the cutoff and come through.
        Assert.True(Through(filter, 50) > 0.9, "the bottom end should survive a closed filter");

        // A voice, a guitar, a hat: twelve dB an octave twice over, so a couple of octaves up is
        // most of the way gone and the top is not there at all.
        Assert.True(Through(filter, 1000) < 0.1, "the middle should be most of the way gone");
        Assert.True(Through(filter, 8000) < 0.01, "the top should be gone");
    }

    [Fact]
    public void Halfway_up_it_has_the_middle_back_and_not_the_top()
    {
        var filter = new SweepFilter(Rate, Channels);
        filter.SetCutoff(1500);

        Assert.True(Through(filter, 50) > 0.9);
        Assert.True(Through(filter, 800) > 0.7, "the middle should be back by here");
        Assert.True(Through(filter, 12000) < 0.05, "and the top should not be");
    }

    [Fact]
    public void A_reset_filter_is_open_again()
    {
        var filter = new SweepFilter(Rate, Channels);
        filter.SetCutoff(SweepFilter.ClosedHz);
        filter.Reset();

        Assert.False(filter.IsClosing);
        Assert.Equal(SweepFilter.OpenHz, filter.CutoffHz, 3);
    }
}
