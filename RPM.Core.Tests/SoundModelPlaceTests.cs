using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// Where the sound pass hears records: the beat model's verdict, except that a computer turned away only
/// for free memory at startup hears its own and waits for room.
/// </summary>
public class SoundModelPlaceTests
{
    private const long Gb = 1024L * 1024 * 1024;

    private static readonly ModelMeasurement Good = new(
        ProcessorCount: 8,
        TotalMemoryBytes: 16 * Gb,
        AvailableMemoryBytes: 4 * Gb,
        HasWeights: true,
        SessionStarted: true,
        WindowTime: TimeSpan.FromSeconds(0.5),
        WindowMemoryBytes: Gb);

    [Fact]
    public void A_computer_that_runs_the_beat_model_hears_its_own_records()
        => Assert.Equal(ModelPlace.Local, SoundModelPlace.For(ModelCapability.Judge(Good)));

    /// <summary>The case of 2026-11-18: sixteen gigabytes, a browser holding most of them, the test
    /// skipped. The DJ goes to the cloud for the session; the sound pass stays here.</summary>
    [Fact]
    public void A_busy_startup_keeps_the_records_here()
    {
        var verdict = ModelCapability.Judge(Good with
        {
            AvailableMemoryBytes = Gb / 2, SessionStarted = false, WindowTime = TimeSpan.Zero, WindowMemoryBytes = 0,
        });

        Assert.Equal(ModelPlace.Cloud, verdict.Place);
        Assert.Equal(ModelPlace.Local, SoundModelPlace.For(verdict));
    }

    /// <summary>A computer that is too small, too slow, has too few cores or cannot open the weights sends
    /// its records' features to the cloud, as decided.</summary>
    [Fact]
    public void A_weak_computer_sends_its_records_to_the_cloud()
    {
        Assert.Equal(ModelPlace.Cloud, SoundModelPlace.For(ModelCapability.Judge(Good with { ProcessorCount = 2 })));
        Assert.Equal(ModelPlace.Cloud, SoundModelPlace.For(ModelCapability.Judge(Good with { TotalMemoryBytes = 4 * Gb })));
        Assert.Equal(ModelPlace.Cloud, SoundModelPlace.For(ModelCapability.Judge(Good with { WindowTime = TimeSpan.FromSeconds(4) })));
        Assert.Equal(ModelPlace.Cloud, SoundModelPlace.For(ModelCapability.Judge(Good with { SessionStarted = false })));
        Assert.Equal(ModelPlace.Cloud, SoundModelPlace.For(ModelCapability.Judge(Good with { HasWeights = false })));
    }

    [Fact]
    public void A_record_is_heard_when_about_a_gigabyte_is_free()
    {
        var needed = SoundModelPlace.ModelBytes + AnalysisThrottle.HeadroomBytes;

        Assert.True(SoundModelPlace.HasRoom(needed));
        Assert.False(SoundModelPlace.HasRoom(needed - 1));
        Assert.False(SoundModelPlace.HasRoom(Gb / 2));
        Assert.True(SoundModelPlace.HasRoom(4 * Gb));

        // The runtime did not say: not a reason to wait.
        Assert.True(SoundModelPlace.HasRoom(0));
    }
}
