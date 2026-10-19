using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// The main decision for a session: can this computer read records with the beat model, or do its
/// windows go to the store.
///
/// <para>These tests check that every way of failing has its own name and is not grouped with the
/// others. The debug line at startup is the only place this is visible, and a "no" without a reason
/// cannot be acted on.</para>
/// </summary>
public class ModelCapabilityTests
{
    private const long Gb = 1024L * 1024 * 1024;

    /// <summary>A machine that passes everything: eight cores, sixteen gigabytes with four free, and
    /// a window in half a second, which is typical of an Apple processor.</summary>
    private static ModelMeasurement Good => new(
        ProcessorCount: 8,
        TotalMemoryBytes: 16 * Gb,
        AvailableMemoryBytes: 4 * Gb,
        HasWeights: true,
        SessionStarted: true,
        WindowTime: TimeSpan.FromSeconds(0.5),
        WindowMemoryBytes: (long)(1.0 * Gb));

    [Fact]
    public void A_machine_that_measures_well_reads_its_own_records()
    {
        var verdict = ModelCapability.Judge(Good);

        Assert.True(verdict.CanRunLocally);
        Assert.Equal(ModelPlace.Local, verdict.Place);
        Assert.Equal(ModelBlock.None, verdict.Block);
        Assert.Contains("the model runs here", verdict.Line);
    }

    [Fact]
    public void Too_few_cores_is_the_cloud()
    {
        var verdict = ModelCapability.Judge(Good with { ProcessorCount = 2 });

        Assert.False(verdict.CanRunLocally);
        Assert.Equal(ModelBlock.TooFewCores, verdict.Block);
    }

    /// <summary>Four gigabytes cannot hold a window, the app and an operating system at once. A
    /// machine that tries starts paging, and an audio thread cannot work while it pages.</summary>
    [Fact]
    public void A_small_machine_is_the_cloud()
    {
        var verdict = ModelCapability.Judge(Good with { TotalMemoryBytes = 4 * Gb });

        Assert.False(verdict.CanRunLocally);
        Assert.Equal(ModelBlock.TooLittleMemory, verdict.Block);
    }

    /// <summary>A big machine with nothing free right now gets the same answer. Analysis does not wait
    /// for a better moment: on a machine like this the better moment may never come, and the set
    /// would silently stop beat-matching.</summary>
    [Fact]
    public void A_machine_with_no_memory_free_is_the_cloud()
    {
        var verdict = ModelCapability.Judge(Good with { AvailableMemoryBytes = Gb / 2 });

        Assert.False(verdict.CanRunLocally);
        Assert.Equal(ModelBlock.TooLittleMemory, verdict.Block);
    }

    /// <summary>Free memory is compared with what a window really cost on this machine, not with a
    /// constant: a machine whose window measured small may continue where the constant would have
    /// stopped it.</summary>
    [Fact]
    public void The_memory_a_window_measured_is_what_it_is_judged_against()
    {
        // Two gigabytes free: too little for the documented window, plenty for the small one measured here.
        var measured = Good with { AvailableMemoryBytes = 2 * Gb, WindowMemoryBytes = 300L * 1024 * 1024 };

        Assert.True(ModelCapability.Judge(measured).CanRunLocally);
        Assert.False(ModelCapability.Judge(measured with { WindowMemoryBytes = 3 * Gb }).CanRunLocally);
    }

    [Fact]
    public void No_weights_is_the_cloud()
    {
        var verdict = ModelCapability.Judge(Good with { HasWeights = false, SessionStarted = false });

        Assert.Equal(ModelBlock.NoWeights, verdict.Block);
        Assert.Contains("no weights", verdict.Line);
    }

    [Fact]
    public void Weights_that_will_not_open_are_the_cloud()
    {
        var verdict = ModelCapability.Judge(Good with { SessionStarted = false });

        Assert.Equal(ModelBlock.RuntimeWouldNotStart, verdict.Block);
    }

    /// <summary>A window covers thirty seconds of music. The budget is ten times faster than the
    /// music plays, so three seconds passes and anything over it fails: that machine cannot stay a
    /// record ahead of the mix.</summary>
    [Fact]
    public void A_machine_that_cannot_keep_up_with_the_music_is_the_cloud()
    {
        Assert.True(ModelCapability.Judge(Good with { WindowTime = TimeSpan.FromSeconds(2.9) }).CanRunLocally);

        var slow = ModelCapability.Judge(Good with { WindowTime = TimeSpan.FromSeconds(4) });
        Assert.False(slow.CanRunLocally);
        Assert.Equal(ModelBlock.TooSlow, slow.Block);
        Assert.Contains("does not run here", slow.Line);
    }

    /// <summary>A runtime that does not report free memory is not a reason to send a machine to the
    /// cloud: it has already passed the memory size check, and its window really ran.</summary>
    [Fact]
    public void An_unknown_free_figure_is_not_a_failure()
    {
        Assert.True(ModelCapability.Judge(Good with { AvailableMemoryBytes = 0 }).CanRunLocally);
    }

    /// <summary>The expensive half of the test is skipped on a machine the cheap half has already
    /// rejected. Measuring costs a gigabyte and a second, on the machine least able to spare
    /// either.</summary>
    [Fact]
    public void A_machine_that_cannot_pass_is_never_measured()
    {
        Assert.True(ModelCapability.WorthMeasuring(8, 16 * Gb, 4 * Gb));
        Assert.False(ModelCapability.WorthMeasuring(2, 16 * Gb, 4 * Gb));
        Assert.False(ModelCapability.WorthMeasuring(8, 4 * Gb, 4 * Gb));
        Assert.False(ModelCapability.WorthMeasuring(8, 16 * Gb, Gb / 2));
    }

    /// <summary>The line is the only place any of this is visible, so it includes the numbers the
    /// decision was based on.</summary>
    [Fact]
    public void The_line_says_what_the_machine_is()
    {
        var line = ModelCapability.Judge(Good).Line;

        Assert.Contains("8 cores", line);
        Assert.Contains("16.0 GB", line);
        Assert.Contains("0.50s", line);
        Assert.Contains("processor", line);
    }
}
