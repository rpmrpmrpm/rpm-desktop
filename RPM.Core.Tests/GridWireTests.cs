using RPM.Core.Analysis;
using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// How a window of a track is sent to the store's beat model.
///
/// <para>The format is the contract between two programs that are deployed separately (the app and
/// the function). The window must arrive close enough to the original for the model, and a body that
/// is not a window must be refused rather than read as beats. Current apps send single precision and
/// older apps send half precision, so both are tested here.</para>
/// </summary>
public class GridWireTests
{
    private static float[] Window(int length)
    {
        var window = new float[length];
        for (int i = 0; i < length; i++)
            window[i] = (float)Math.Sin(i / 7.0) * 12 + i * 1e-4f;
        return window;
    }

    /// <summary>With single precision the function reads exactly what the app would give the model,
    /// so a grid from a slow machine matches a grid from a fast machine in the shared pool.</summary>
    [Fact]
    public void A_window_at_single_precision_comes_back_exactly()
    {
        var window = Window(512);

        var back = GridWire.Unpack(GridWire.Pack(window, GridPrecision.Single), window.Length, GridPrecision.Single);

        Assert.NotNull(back);
        Assert.Equal(window, back);
    }

    [Fact]
    public void A_window_at_half_precision_comes_back_close()
    {
        var window = Window(512);

        var back = GridWire.Unpack(GridWire.Pack(window, GridPrecision.Half), window.Length, GridPrecision.Half);

        Assert.NotNull(back);
        for (int i = 0; i < window.Length; i++)
            Assert.Equal(window[i], back![i], 0.02);
    }

    /// <summary>A window read at the other precision has the wrong length, and is refused rather than
    /// read as twice or half as many values.</summary>
    [Fact]
    public void A_window_read_at_the_other_precision_is_refused()
    {
        var window = Window(512);

        Assert.Null(GridWire.Unpack(GridWire.Pack(window, GridPrecision.Single), window.Length, GridPrecision.Half));
        Assert.Null(GridWire.Unpack(GridWire.Pack(window, GridPrecision.Half), window.Length, GridPrecision.Single));
    }

    /// <summary>A request names its precision. A request with no name comes from an older app, which
    /// packs at half precision. Any other name is not a window.</summary>
    [Fact]
    public void The_precision_a_request_names()
    {
        Assert.Equal(GridPrecision.Half, GridWire.Named(null));
        Assert.Equal(GridPrecision.Half, GridWire.Named(GridWire.Name(GridPrecision.Half)));
        Assert.Equal(GridPrecision.Single, GridWire.Named(GridWire.Name(GridPrecision.Single)));
        Assert.Null(GridWire.Named("double"));
        Assert.Null(GridWire.Named(""));
    }

    /// <summary>The packed window is much smaller than the audio it describes, and within the API
    /// limit (<c>RecordingsController.MostWindowChars</c>, 1,100,000).</summary>
    [Theory]
    [InlineData(GridPrecision.Half)]
    [InlineData(GridPrecision.Single)]
    public void A_window_packs_inside_what_the_api_takes(GridPrecision precision)
    {
        int values = BeatModelDecoder.ChunkFrames * MelSpectrogram.MelBands;
        var rng = new Random(7);
        var window = new float[values];
        for (int i = 0; i < values; i++)
            window[i] = (float)(Math.Sin(i / 501.0) * 6 - 3 + rng.NextDouble() * 0.01);

        var packed = GridWire.Pack(window, precision);

        Assert.True(packed.Length < 1_100_000, $"a window packed to {packed.Length} characters");
        Assert.NotNull(GridWire.Unpack(packed, values, precision));
    }

    [Theory]
    [InlineData(GridPrecision.Half)]
    [InlineData(GridPrecision.Single)]
    public void Something_that_is_not_a_window_is_refused_rather_than_read(GridPrecision precision)
    {
        Assert.Null(GridWire.Unpack(null, 10, precision));
        Assert.Null(GridWire.Unpack("", 10, precision));
        Assert.Null(GridWire.Unpack("not base64 at all !!", 10, precision));

        // The right format but the wrong length: a truncated body, or an answer to another request.
        Assert.Null(GridWire.Unpack(GridWire.Pack(new float[8], precision), 10, precision));
    }
}
