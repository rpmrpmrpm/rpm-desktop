using RPM.Core.Analysis;
using RPM.Desktop.Services.Audio;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Tests;

/// <summary>
/// When the beat model test runs. At startup it runs only once the governor admits it. A set that asks
/// for the verdict runs it at once.
///
/// <para>Run at launch, the test would take about 1.3 s on a pool thread and in the ONNX workers, while the
/// home reveal waits about 1.4 s for the banner's models. So the startup run waits for the governor, and a
/// set that asks for the verdict does not wait for it.</para>
/// </summary>
public class DjModelServiceTests
{
    private static ModelMeasurement Capable() => new(
        ProcessorCount: 10,
        TotalMemoryBytes: 16L << 30,
        AvailableMemoryBytes: 8L << 30,
        HasWeights: true,
        SessionStarted: true,
        WindowTime: TimeSpan.FromSeconds(1),
        WindowMemoryBytes: 500L << 20);

    /// <summary>A governor whose run has not reached any phase: no window, nothing admitted.</summary>
    private static Governor Cold() => new(new UiPulse(null));

    [Fact]
    public async Task The_startup_test_waits_while_the_run_is_cold()
    {
        var calls = 0;
        var models = new DjModelService(() => { Interlocked.Increment(ref calls); return Capable(); });

        models.BeginWhenAdmitted(Cold());
        await Task.Delay(300);

        Assert.Equal(0, Volatile.Read(ref calls));
        Assert.Null(models.Verdict);
    }

    [Fact]
    public async Task A_set_asking_for_the_verdict_runs_the_test_without_waiting_for_the_governor()
    {
        var calls = 0;
        var models = new DjModelService(() => { Interlocked.Increment(ref calls); return Capable(); });
        models.BeginWhenAdmitted(Cold());

        var verdict = await models.VerdictAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(verdict.CanRunLocally);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public async Task An_admitted_startup_test_runs_once()
    {
        var calls = 0;
        var models = new DjModelService(() => { Interlocked.Increment(ref calls); return Capable(); });

        models.BeginWhenAdmitted(Governor.Unattended);
        var verdict = await models.VerdictAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);

        Assert.True(verdict.CanRunLocally);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.True(models.CanRunLocally);
    }
}
