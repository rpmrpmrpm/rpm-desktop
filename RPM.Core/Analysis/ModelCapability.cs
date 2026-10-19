using System.Globalization;

namespace RPM.Core.Analysis;

/// <summary>Where a DJ set's analysis is worked out.</summary>
public enum ModelPlace
{
    /// <summary>On this machine, with the weights beside the app. This is returned only for a machine
    /// that has been measured and shown to be able to run the model.</summary>
    Local,

    /// <summary>In the cloud, because this machine cannot run the model. The store reads the record
    /// and publishes the result to the pool, and the set takes it from there.</summary>
    Cloud,
}

/// <summary>Why a machine was sent to the cloud. <see cref="None"/> is the machine that passed.</summary>
public enum ModelBlock
{
    None,

    /// <summary>There is no beat model beside the app. The install is broken, or the working copy has
    /// not run scripts/fetch-models.sh.</summary>
    NoWeights,

    /// <summary>The weights are there but the runtime could not open them. Causes include a missing
    /// kernel for an operation in the graph, a native library that does not load, or a file that is
    /// not what its name says.</summary>
    RuntimeWouldNotStart,

    /// <summary>Not enough memory to hold a window without making the machine use its swap
    /// file.</summary>
    TooLittleMemory,

    /// <summary>Not enough cores to read a record while also playing and mixing one.</summary>
    TooFewCores,

    /// <summary>The model runs, but not fast enough to stay one record ahead of the mix.</summary>
    TooSlow,
}

/// <summary>
/// What was actually measured on this machine, which is what <see cref="ModelCapability"/> judges.
///
/// <para>Every field is a measured fact, not an estimate. The window time and the window's memory are
/// taken by running one real window through the real graph on the real runtime (see the desktop's
/// <c>BeatModelProbe</c>), because whether a computer can run the model cannot be known from the
/// processor name.</para>
/// </summary>
/// <param name="ProcessorCount">Cores, as the runtime counts them.</param>
/// <param name="TotalMemoryBytes">The memory the machine has, or the container's limit.</param>
/// <param name="AvailableMemoryBytes">The memory that was free when the test ran.</param>
/// <param name="HasWeights">Whether there is a beat model beside the app.</param>
/// <param name="SessionStarted">Whether the runtime opened those weights and ran a window.</param>
/// <param name="WindowTime">How long one window took, once warm.</param>
/// <param name="WindowMemoryBytes">The most the process grew by while that window ran. Zero when it
/// could not be measured, in which case the figure in
/// <see cref="AnalysisThrottle.WindowMemoryBytes"/> is used.</param>
/// <param name="Runtime">What ran the model: the processor, or the graphics card when the app was
/// asked to use one and it started. It is reported but not judged: the window time is what decides,
/// and it already includes the effect of the graphics card.</param>
public readonly record struct ModelMeasurement(
    int ProcessorCount,
    long TotalMemoryBytes,
    long AvailableMemoryBytes,
    bool HasWeights,
    bool SessionStarted,
    TimeSpan WindowTime,
    long WindowMemoryBytes,
    ModelRuntime Runtime = ModelRuntime.Cpu);

/// <summary>What the test decided, with one line that says so.</summary>
/// <param name="Place">Where analysis runs from here.</param>
/// <param name="Block">Why analysis does not run here, when it does not.</param>
/// <param name="Measurement">What it was decided from.</param>
public readonly record struct ModelVerdict(ModelPlace Place, ModelBlock Block, ModelMeasurement Measurement)
{
    /// <summary>True when this machine may read records for itself.</summary>
    public bool CanRunLocally => Place is ModelPlace.Local;

    /// <summary>The diagnostic line, written to the log ring once at startup. It contains every number
    /// that the decision was made from, so that a machine that stops mixing always has a visible
    /// reason.</summary>
    public string Line
    {
        get
        {
            var m = Measurement;
            string machine =
                $"{m.ProcessorCount} cores, {Gb(m.TotalMemoryBytes)} GB ({Gb(m.AvailableMemoryBytes)} GB free)";

            string window = m.WindowTime.TotalSeconds.ToString(
                "F2", CultureInfo.InvariantCulture);

            string model = !m.HasWeights ? "no weights"
                : !m.SessionStarted ? "the runtime would not start"
                : $"one window {window}s on the " +
                  (m.Runtime is ModelRuntime.DirectMl ? "graphics card" : "processor") +
                  (m.WindowMemoryBytes > 0 ? $", {Gb(m.WindowMemoryBytes)} GB while it ran" : "");

            return CanRunLocally
                ? $"the model runs here: {machine}, {model}"
                : $"the model does not run here ({Why}): {machine}, {model}. " +
                  "A set's records are worked out in the cloud instead.";
        }
    }

    private string Why => Block switch
    {
        ModelBlock.NoWeights => "no beat model beside the app",
        ModelBlock.RuntimeWouldNotStart => "the runtime would not open the weights",
        ModelBlock.TooFewCores => $"fewer than {ModelCapability.LeastCores} cores",
        ModelBlock.TooLittleMemory => "not enough memory for a window",
        ModelBlock.TooSlow => $"slower than {ModelCapability.WindowBudget.TotalSeconds:F1}s a window",
        _ => "no reason",
    };

    /// <summary>Formatted with the invariant culture, like every number in this line. It is a log line
    /// that people read to find out why a machine made its choice, and a decimal comma would make that
    /// harder.</summary>
    private static string Gb(long bytes) =>
        (bytes / (double)(1024 * 1024 * 1024)).ToString("F1", CultureInfo.InvariantCulture);
}

/// <summary>
/// Whether this computer can read records with the beat model well enough to DJ with. This is decided
/// once at startup from a measurement, not guessed for each track.
///
/// <para><b>Why a startup test.</b> <see cref="AnalysisThrottle"/> decides from the state of the
/// machine at the moment it is asked. If memory is short, the sweep waits and asks again fifteen
/// seconds later. On a machine that is too small, that wait never ends, so the records after the one
/// playing are never read and the set stops beat-matching without any message. A mix that silently
/// becomes a shuffle is the worst outcome.</para>
///
/// <para><b>How it works.</b> There is one test at startup (the real graph, on the real runtime,
/// timed, with the process watched while it runs) and one decision that stands for the whole
/// session. A machine that passes reads its own records and never waits for a better moment, because
/// it has already been shown to have the memory. A machine that fails never loads the weights and
/// asks the store instead, as a phone does.</para>
///
/// <para>This class is pure and lives in <c>RPM.Core</c>, so the thresholds can be read and tested
/// without a runtime. The measuring is done by the desktop, because the runtime is there.</para>
/// </summary>
public static class ModelCapability
{
    /// <summary>
    /// The fewest cores a machine may have and still read its own records.
    ///
    /// <para>Two cores are reserved before analysis starts, for the UI thread and the audio thread
    /// (<see cref="AnalysisThrottle"/> reserves them), and the model's session needs two more
    /// (<c>IntraOpNumThreads = 2</c>). Below four, reading a record and playing one share the same
    /// cores, and the listener hears dropouts.</para>
    ///
    /// <para>This is a plain count, not a judgement of the type of core. An efficiency core counts
    /// the same as a performance core, because the threshold is about contention: how many threads can
    /// run elsewhere while the model uses two. How <em>fast</em> the cores are is not guessed from
    /// their number. It is measured on the machine itself, against <see cref="WindowBudget"/>. A
    /// laptop with four slow cores passes this check and fails that one, which is correct.</para>
    /// </summary>
    public const int LeastCores = 4;

    /// <summary>
    /// The least total memory a machine may have.
    ///
    /// <para>One window peaks near a gigabyte, and the app itself uses several hundred megabytes with a
    /// record playing, artwork loaded and a library open. On four gigabytes that is most of the
    /// machine before the operating system is counted, so the machine pages. Paging while the audio
    /// thread has only one block's time to fill a block causes a dropout. Eight gigabytes is the
    /// first size where a window is a share of the machine and not all of it.</para>
    /// </summary>
    public const long LeastMemoryBytes = 8L * 1024 * 1024 * 1024;

    /// <summary>
    /// How long one window may take.
    ///
    /// <para>A window is <c>BeatModelDecoder.ChunkFrames</c> frames at
    /// <c>MelSpectrogram.FrameRate</c>, which is thirty seconds of music. The set needs the next
    /// record to be read while the current one plays. The budget is ten times faster than the
    /// music: nine windows for a four-minute record take twenty-seven seconds of model time, and
    /// the rest of the record's length is left for the decode, the key model, the DSP passes, the
    /// changeover and the other work of the machine. For reference, an Apple processor takes about
    /// half a second a window and an ordinary x86 laptop about one and a half, so this fails only
    /// a machine that cannot keep up, not one that is merely slower.</para>
    /// </summary>
    public static readonly TimeSpan WindowBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The verdict for a machine.
    ///
    /// <para>The order of the checks only changes what is reported: the first problem found is the one
    /// named. The cheapest checks come first, so a machine with no weights is never timed.</para>
    /// </summary>
    public static ModelVerdict Judge(ModelMeasurement measurement)
    {
        var block = Block(measurement);
        return new ModelVerdict(
            block is ModelBlock.None ? ModelPlace.Local : ModelPlace.Cloud, block, measurement);
    }

    /// <summary>
    /// Whether it is worth loading the weights and timing a window at all.
    ///
    /// <para>A machine that fails on cores or memory fails whatever the model measures, and the
    /// measurement costs a gigabyte and a second, on the machine least able to spare either. So the
    /// cheap checks run first and the expensive measurement is skipped when they fail.</para>
    /// </summary>
    public static bool WorthMeasuring(int processorCount, long totalMemoryBytes, long availableMemoryBytes)
        => processorCount >= LeastCores
           && totalMemoryBytes >= LeastMemoryBytes
           && availableMemoryBytes >= Needed(0);

    /// <summary>What a window and the memory around it need, in bytes. This is the measured cost
    /// when there is one, and otherwise the figure the throttle uses.</summary>
    public static long Needed(long windowMemoryBytes) =>
        Math.Max(windowMemoryBytes, AnalysisThrottle.WindowMemoryBytes) + AnalysisThrottle.HeadroomBytes;

    private static ModelBlock Block(ModelMeasurement m)
    {
        if (m.ProcessorCount < LeastCores)
            return ModelBlock.TooFewCores;

        if (m.TotalMemoryBytes > 0 && m.TotalMemoryBytes < LeastMemoryBytes)
            return ModelBlock.TooLittleMemory;

        if (!m.HasWeights)
            return ModelBlock.NoWeights;

        if (!m.SessionStarted)
            return ModelBlock.RuntimeWouldNotStart;

        // Zero means the runtime did not report the free memory. An unknown value is not a failure:
        // the machine has already passed the total memory check, and the window it ran did not fail.
        // So only a figure that is present and too small sends the machine to the cloud.
        if (m.AvailableMemoryBytes > 0 && m.AvailableMemoryBytes < Needed(m.WindowMemoryBytes))
            return ModelBlock.TooLittleMemory;

        return m.WindowTime > WindowBudget ? ModelBlock.TooSlow : ModelBlock.None;
    }
}
