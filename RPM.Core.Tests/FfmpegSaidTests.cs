using RPM.Fingerprint;

namespace RPM.Core.Tests;

/// <summary>
/// <see cref="FfmpegSaid"/>: ffmpeg's output reduced to the sentence somebody can act on. Both
/// fixtures here are real stderr from a library that produced them at app start.
/// </summary>
public class FfmpegSaidTests
{
    /// <summary>A 30-channel file given to the rendition transcode's <c>-ac 2</c>. Five lines, no
    /// repeats, and the reason is the first of them: everything after it is a consequence.</summary>
    private const string Rematrix =
        "[auto_aresample_0 @ 0000029d74737080] [SWR @ 0000029d750e2680] Rematrix is needed between 30 channels and stereo but there is not enough information to do it\n" +
        "[auto_aresample_0 @ 0000029d74737080] Failed to configure output pad on auto_aresample_0\n" +
        "[af#0:0 @ 0000029d73271340] Error reinitializing filters!\n" +
        "Failed to inject frame into filter network: Invalid argument\n" +
        "Error while filtering: Invalid argument\n";

    /// <summary>A damaged AAC. The same two sentences about a different channel element, repeated
    /// many times. This class exists to reduce that kind of output.</summary>
    private static string DamagedAac(int packets)
    {
        var said = new System.Text.StringBuilder();
        for (int i = 0; i < packets; i++)
        {
            said.Append($"[aac @ 0000029d732739c0] channel element {i % 4}.{i} is not allocated\n");
            said.Append("[aist#0:0/aac @ 0000029d73330d40] Error submitting packet to decoder: " +
                        "Invalid data found when processing input\n");
        }
        said.Append("Conversion failed!\n");
        return said.ToString();
    }

    [Fact]
    public void The_cause_is_kept_rather_than_the_wreckage_under_it()
    {
        var said = FfmpegSaid.Short(Rematrix);

        Assert.StartsWith("[auto_aresample_0] [SWR] Rematrix is needed between 30 channels and stereo", said);
        Assert.DoesNotContain("Error while filtering", said);
    }

    [Fact]
    public void A_flood_of_one_complaint_counts_as_one_complaint()
    {
        var said = FfmpegSaid.Short(DamagedAac(400));

        // 801 lines in, three out, and the line AFTER the repeats is still reached. This is why lines
        // are compared by shape and not by text.
        Assert.Equal(3, said.Split("; ").Length);
        Assert.Contains("channel element", said);
        Assert.Contains("Error submitting packet to decoder", said);
        Assert.Contains("Conversion failed!", said);
    }

    [Fact]
    public void The_pointer_goes_and_the_tag_stays()
    {
        var said = FfmpegSaid.Short("[aac @ 0000029d732739c0] channel element 3.12 is not allocated");

        Assert.Equal("[aac] channel element 3.12 is not allocated", said);
    }

    [Fact]
    public void Nothing_said_reads_as_nothing_said()
    {
        Assert.Equal("", FfmpegSaid.Short(null));
        Assert.Equal("", FfmpegSaid.Short("   \n\n  \n"));
    }

    [Fact]
    public void A_carriage_return_does_not_end_a_line()
    {
        // ffmpeg redraws its progress line with a bare carriage return. Splitting only on the pair
        // would return the whole run as one line, and the repeats would not be reduced.
        var said = FfmpegSaid.Short("size=1kB time=00:00:01\rsize=2kB time=00:00:02\nOutput file is empty");

        Assert.Equal(2, said.Split("; ").Length);
        Assert.EndsWith("Output file is empty", said);
    }

    [Fact]
    public void What_comes_back_fits_where_it_is_going()
    {
        var said = FfmpegSaid.Short(string.Join("\n", Enumerable.Range(0, 3)
            .Select(i => $"[filter_{i} @ 00007f8b2c004a00] {new string('x', 400)} number {i}")));

        Assert.True(said.Length <= 303, $"a {said.Length}-character reason is not a reason");
        Assert.EndsWith("...", said);
    }
}
