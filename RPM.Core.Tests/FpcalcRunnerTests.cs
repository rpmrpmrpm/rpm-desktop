using RPM.Fingerprint.Chroma;

namespace RPM.Core.Tests;

/// <summary>
/// Checks which fpcalc failures are facts about a file and which are not.
///
/// <para>This matters more than it seems. A failure classed <see cref="FpcalcFailure.Unreadable"/> is
/// written into the mirror as an empty fingerprint, and the file is then never read again until its
/// bytes change. If a broken install is classed that way, one machine with no fpcalc marks a whole
/// collection as unfingerprintable, silently and permanently.</para>
/// </summary>
public class FpcalcRunnerTests
{
    // The exact wording fpcalc 1.6.0 uses, taken from real runs, not from reading its source.
    [Theory]
    [InlineData("ERROR: Error decoding the audio source (Invalid data found when processing input)",
        FpcalcFailure.Unreadable)]
    [InlineData("ERROR: Could not find any audio stream in the file (Stream not found)",
        FpcalcFailure.Unreadable)]
    [InlineData("ERROR: Could not open the input file (No such file or directory)",
        FpcalcFailure.Unopened)]
    [InlineData("ERROR: Unknown option -nonsense", FpcalcFailure.Unavailable)]
    [InlineData("ERROR: No input files", FpcalcFailure.Unavailable)]
    public void Classify_reads_what_fpcalc_actually_says(string stderr, FpcalcFailure expected)
        => Assert.Equal(expected, FpcalcRunner.Classify(stderr));

    // A version of fpcalc that fails in a way this code does not know must not be taken as a fact
    // about the file. A wrong Unknown costs one more read on the next pass; a wrong Unreadable loses
    // the track for good.
    [Theory]
    [InlineData("")]
    [InlineData("ERROR: something no version of fpcalc has said yet")]
    [InlineData("Segmentation fault")]
    public void Classify_will_not_guess_a_verdict(string stderr)
        => Assert.Equal(FpcalcFailure.Unknown, FpcalcRunner.Classify(stderr));

    [Theory]
    [InlineData(FpcalcFailure.Unreadable, true)]
    [InlineData(FpcalcFailure.Unavailable, false)]
    [InlineData(FpcalcFailure.Unopened, false)]
    [InlineData(FpcalcFailure.Unknown, false)]
    public void Only_an_unreadable_file_is_a_verdict(FpcalcFailure failure, bool expected)
        => Assert.Equal(expected, new FpcalcException(failure, "test").IsVerdictOnFile);

    /// <summary>The broken-install case, end to end: no fpcalc anywhere, so without this every track
    /// in the library would be marked unfingerprintable.</summary>
    [Fact]
    public void A_missing_fpcalc_is_never_a_verdict_on_the_file()
    {
        var was = FpcalcRunner.FpcalcPath;
        try
        {
            FpcalcRunner.FpcalcPath = Path.Combine(Path.GetTempPath(), "no-fpcalc-here-8f2a1c");
            var ex = Assert.Throws<FpcalcException>(() => FpcalcRunner.Run("whatever.flac"));
            Assert.Equal(FpcalcFailure.Unavailable, ex.Failure);
            Assert.False(ex.IsVerdictOnFile);
        }
        finally
        {
            FpcalcRunner.FpcalcPath = was;
        }
    }
}
