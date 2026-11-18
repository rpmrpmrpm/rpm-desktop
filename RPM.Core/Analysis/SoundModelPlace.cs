namespace RPM.Core.Analysis;

/// <summary>
/// Where the sound pass hears records, and when this computer has the room to hear the next one.
///
/// <para><b>The beat model's verdict, without its moment.</b> The sound pass follows the verdict that
/// <c>DjModelService</c> takes once per session (decided 2026-11-17): a computer too small or too slow
/// for the beat model sends its records' features to the cloud. But one part of that verdict is about a
/// moment, not about the computer: free memory when the test was due. On 2026-11-18 a 16-core computer
/// with 16 GB started with a browser holding 11 GB and 0.5 GB free, the test was skipped, and the whole
/// library would have gone to the cloud for that session. The DJ is right to give up at once: a set
/// cannot wait for memory. The sound pass can. So a computer turned away only for free memory hears its
/// own records, and waits before each one until there is room (<see cref="HasRoom"/>).</para>
///
/// <para><b>What that costs.</b> Such a computer was never timed, so a slow one with plenty of memory
/// but a busy startup also hears its own records. That is acceptable: CLAP has no deadline, and on a
/// slow processor a record takes a few seconds instead of one.</para>
/// </summary>
public static class SoundModelPlace
{
    /// <summary>
    /// What hearing one record costs in memory, at its peak: the eight-bit session and three clips.
    /// Measured at 239 MB with ONNX Runtime 1.20.1 on a Ryzen 7 5700G (rpm-notes
    /// <c>PLAN-VALENCE-MOODS.md</c>), rounded up.
    /// </summary>
    public const long ModelBytes = 300L * 1024 * 1024;

    /// <summary>Where this computer hears its records, from the session's beat model verdict.</summary>
    public static ModelPlace For(ModelVerdict verdict) =>
        verdict.Place == ModelPlace.Local || verdict.Block == ModelBlock.TooLittleFreeMemory
            ? ModelPlace.Local
            : ModelPlace.Cloud;

    /// <summary>
    /// Whether there is room to hear the next record now: <see cref="ModelBytes"/> and the headroom every
    /// background analysis leaves (<see cref="AnalysisThrottle.HeadroomBytes"/>), about a gigabyte.
    /// </summary>
    /// <param name="availableBytes">The memory the machine could still hand out. Zero or less means the
    /// runtime did not say, which is not a reason to wait, as in <see cref="AnalysisThrottle"/>.</param>
    public static bool HasRoom(long availableBytes) =>
        availableBytes <= 0 || availableBytes >= ModelBytes + AnalysisThrottle.HeadroomBytes;
}
