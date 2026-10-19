using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// Holds the beat model's two choices (which weights, which runtime) to what they promise.
///
/// <para>Neither is arithmetic, and both are the kind of thing that goes wrong quietly. A run asked
/// for the quantised weights and handed the float32 ones back measures the wrong model and settles
/// the question the wrong way; a machine that is not Windows asked to use DirectML gets a session
/// that cannot start and reads no beats at all.</para>
/// </summary>
public class BeatModelChoiceTests
{
    private const string Dir = "/models";

    private static readonly string Int8 = Path.Combine(Dir, BeatModelChoice.Int8FileName);
    private static readonly string Float32 = Path.Combine(Dir, BeatModelChoice.Float32FileName);

    private static Func<string, bool> Present(params string[] paths) => paths.Contains;

    /// <summary>
    /// The float32 weights are the beat model, whatever else is on disk. Grids go into a pool shared
    /// by every user, and two reports of a record count as agreeing only when they are the same text.
    /// The float32 weights read a window to within a millionth on arm64 and x64; the quantised ones
    /// differ by a thousandth. A working copy with a quantised file left in its output
    /// folder must not publish its grids.
    /// </summary>
    [Fact]
    public void The_float32_weights_are_used_even_when_the_quantised_ones_are_there()
    {
        Assert.Equal(Float32, BeatModelChoice.Pick(Dir, Present(Int8, Float32)));
    }

    [Fact]
    public void The_float32_weights_are_used_when_they_are_all_there_is()
    {
        Assert.Equal(Float32, BeatModelChoice.Pick(Dir, Present(Float32)));
    }

    [Fact]
    public void The_quantised_weights_alone_are_not_used_unless_asked_for()
    {
        Assert.Null(BeatModelChoice.Pick(Dir, Present(Int8)));
    }

    [Fact]
    public void Nothing_on_disk_is_null_rather_than_a_path_that_will_not_open()
    {
        Assert.Null(BeatModelChoice.Pick(Dir, Present()));
    }

    [Theory]
    [InlineData("int8")]
    [InlineData("INT8")]
    [InlineData("  int8  ")]
    public void Asking_for_the_quantised_weights_gets_them(string requested)
    {
        Assert.Equal(Int8, BeatModelChoice.Pick(Dir, Present(Int8, Float32), requested));
    }

    [Theory]
    [InlineData("fp32")]
    [InlineData("float32")]
    public void Asking_for_the_float32_weights_gets_them_over_the_quantised_ones(string requested)
    {
        Assert.Equal(Float32, BeatModelChoice.Pick(Dir, Present(Int8, Float32), requested));
    }

    /// <summary>
    /// The point of the environment variable is measuring one model against the other. Falling back
    /// to whatever happens to be on disk would make a run that says int8 and reads float32 look like
    /// a result.
    /// </summary>
    [Fact]
    public void A_request_for_weights_that_are_not_there_is_not_quietly_given_the_others()
    {
        Assert.Null(BeatModelChoice.Pick(Dir, Present(Float32), "int8"));
        Assert.Null(BeatModelChoice.Pick(Dir, Present(Int8), "fp32"));
    }

    [Fact]
    public void An_unreadable_request_gets_the_float32_weights()
    {
        Assert.Equal(Float32, BeatModelChoice.Pick(Dir, Present(Int8, Float32), "gpu"));
        Assert.Equal(Float32, BeatModelChoice.Pick(Dir, Present(Int8, Float32), ""));
    }

    /// <summary>One file, never a second try: a fallback from one set of weights to the other would
    /// publish the other's grids to the pool, and turn a measurement of one model into a measurement
    /// of the other without saying so.</summary>
    [Fact]
    public void There_is_never_a_second_try()
    {
        Assert.Equal([Float32], BeatModelChoice.Candidates(Dir, Present(Int8, Float32)));
        Assert.Equal([Float32], BeatModelChoice.Candidates(Dir, Present(Float32)));
        Assert.Empty(BeatModelChoice.Candidates(Dir, Present(Int8)));
        Assert.Empty(BeatModelChoice.Candidates(Dir, Present()));
        Assert.Equal([Int8], BeatModelChoice.Candidates(Dir, Present(Int8, Float32), "int8"));
        Assert.Equal([Float32], BeatModelChoice.Candidates(Dir, Present(Int8, Float32), "fp32"));
    }

    [Theory]
    [InlineData("dml")]
    [InlineData("DirectML")]
    public void DirectML_is_used_on_windows_when_it_is_asked_for(string requested)
    {
        Assert.Equal(ModelRuntime.DirectMl, BeatModelChoice.PickRuntime(requested, isWindows: true));
    }

    [Fact]
    public void DirectML_is_not_used_where_it_does_not_exist()
    {
        Assert.Equal(ModelRuntime.Cpu, BeatModelChoice.PickRuntime("dml", isWindows: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cpu")]
    [InlineData("cuda")]
    public void The_processor_is_what_a_machine_gets_unasked(string? requested)
    {
        Assert.Equal(ModelRuntime.Cpu, BeatModelChoice.PickRuntime(requested, isWindows: true));
    }
}
