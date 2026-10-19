using RPM.Core.Analysis;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Audio;

/// <summary>
/// Decides where this machine's records are analysed: on this machine, or in the cloud. The decision
/// is made once, at startup, from a measurement (see <see cref="BeatModelProbe"/>). It applies for the
/// whole session.
///
/// <para>The decision is made once, not for each track. A check before each track would wait for a
/// quiet moment, and on a small machine that moment may never come. The set would then keep playing but
/// stop beat matching. A machine that passes the test never waits again. A machine that fails never
/// loads the weights, and asks the cloud store instead.</para>
///
/// <para>The answer is a task, not a flag, because the test takes a window of time, and a set can be
/// started before the test finishes. Anything that needs the answer waits for it. The wait is at most
/// a second, and it happens only once.</para>
/// </summary>
public sealed class DjModelService
{
    private readonly TaskCompletionSource<ModelVerdict> _measured =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Func<ModelMeasurement> _measure;

    private int _started;

    public DjModelService()
        : this(() => BeatModelProbe.Measure())
    {
    }

    /// <summary>For tests. Takes the measurement as a delegate, so the order of the test relative to
    /// the governor can be checked without loading the beat model.</summary>
    internal DjModelService(Func<ModelMeasurement> measure) => _measure = measure;

    /// <summary>The verdict, once there is one. It is null while the test is still running. Callers
    /// that cannot wait must treat null as "not yet", never as "no".</summary>
    public ModelVerdict? Verdict { get; private set; }

    /// <summary>True when the test has shown that the machine can run the model. False while the test
    /// is still running, so no weights are loaded before the test finishes.</summary>
    public bool CanRunLocally => Verdict?.CanRunLocally == true;

    /// <summary>Waits for the verdict. If the test has not started, it starts now. It does not wait for
    /// the governor, because the caller is already waiting for the answer.</summary>
    public Task<ModelVerdict> VerdictAsync()
    {
        Measure();
        return _measured.Task;
    }

    /// <summary>
    /// Runs the test at startup, once the governor allows processor work. That is after the splash
    /// reveal and after the earlier startup waves have finished.
    ///
    /// <para>The test does not start at once. It would compete for the same processor cores as the home
    /// banner's face and saliency models. Those models prepare the pictures that the reveal waits for.
    /// On an M4 the test took about 1.3 s, and the splash lasts about 1.4 s. Nothing waits for this
    /// answer at startup. A set started before the test has run calls <see cref="VerdictAsync"/>, which
    /// runs the test then.</para>
    ///
    /// <para>The governor slot is held until the test finishes. The governor then counts the test as
    /// working, and it does not start the next wave on top of it.</para>
    /// </summary>
    public void BeginWhenAdmitted(Shell.Governor governor)
    {
        var sweep = governor.Pass(
            "beat model test", Shell.Lane.Processor, Shell.Wave.Last, notBefore: Shell.Phase.Ready);

        _ = Task.Run(async () =>
        {
            using var slot = await sweep.Next(CancellationToken.None).ConfigureAwait(false);
            if (!slot.Go)
                return;

            Measure();
            await _measured.Task.ConfigureAwait(false);
        });
    }

    /// <summary>Runs the test, once, on a background thread, and says what it decided.</summary>
    private void Measure()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        _ = Task.Run(() =>
        {
            ModelVerdict verdict;
            try
            {
                verdict = ModelCapability.Judge(_measure());
            }
            catch (Exception ex)
            {
                // The test must not make anything fail. A machine whose measurement throws cannot be
                // shown to run the model. The answer for that machine is the cloud, and this is not
                // treated as an error.
                Said.Line("dj", $"the model test would not run ({ex.GetType().Name}: {ex.Message})");
                verdict = ModelCapability.Judge(new ModelMeasurement(
                    Environment.ProcessorCount, TotalMemoryBytes: 0, AvailableMemoryBytes: 0,
                    HasWeights: true, SessionStarted: false, WindowTime: TimeSpan.Zero,
                    WindowMemoryBytes: 0));
            }

            Verdict = verdict;
            Said.Line("dj", verdict.Line);
            _measured.TrySetResult(verdict);
        });
    }
}
