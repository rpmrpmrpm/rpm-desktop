using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;
using RPM.Core.Analysis;
using RPM.Core.Threading;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Audio;

/// <summary>
/// Checks whether the beat model runs on this computer, and fast enough to DJ with.
///
/// <para>The result is not guessed from a processor name or a core count. The weights are opened with
/// the same options the analyser uses. One real window of the real graph is run on the real runtime,
/// and the process memory is sampled while it runs. The result is a measurement: how long one window
/// took, and how much memory the window needed. <see cref="ModelCapability"/> turns that into the
/// decision the session uses.</para>
///
/// <para>One window costs a second or two, and about a gigabyte while it runs. An analysis costs the
/// same, and that is the point, because the check must measure the real cost. The check is skipped on
/// a machine that the cheap checks already rule out (see <see cref="ModelCapability.WorthMeasuring"/>).
/// A machine with little free memory is never asked to run it.</para>
/// </summary>
public static class BeatModelProbe
{
    /// <summary>How often the process memory is sampled while a window runs. The peak is what matters,
    /// and it is usually reached in the middle of the window. The interval must be short enough to
    /// catch the peak, and long enough that sampling does not add noticeable work.</summary>
    private static readonly TimeSpan Sampling = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Measures this machine. Never throws: anything that goes wrong is a machine that cannot run
    /// the model, which is a verdict rather than an error.
    /// </summary>
    public static ModelMeasurement Measure(CancellationToken ct = default)
    {
        var (total, available) = Memory();
        int cores = Environment.ProcessorCount;

        var path = BeatModelChoice.Pick(
            OnnxBeatModel.ModelDirectory, File.Exists,
            RPM.Core.Diagnostics.DevSwitch.Read("RPM_BEAT_MODEL"));

        var blank = new ModelMeasurement(
            cores, total, available, HasWeights: path is not null,
            SessionStarted: false, WindowTime: TimeSpan.Zero, WindowMemoryBytes: 0);

        if (path is null || !ModelCapability.WorthMeasuring(cores, total, available))
            return blank;

        try
        {
            OnnxLogging.Quieten();

            // The weights are opened on the same runtime the analyser uses, so the measured time
            // matches the time a track takes. On Windows that is the graphics card if it was requested
            // (RPM_ONNX_PROVIDER=dml), and the processor otherwise. If the card was requested but does
            // not start, the processor is used, as the analyser does.
            var (session, runtime) = Open(path);
            using var _ = session;

            // One window of silence. The input shape is fixed, so a window costs the same whatever it
            // contains. An empty spectrogram therefore costs the same as a real one.
            var mel = new float[BeatModelDecoder.ChunkFrames][];
            for (int i = 0; i < mel.Length; i++)
                mel[i] = new float[MelSpectrogram.MelBands];

            long peak = 0;

            // Two windows are run. The first pays the graph's one-time warm-up, such as the runtime's
            // first allocations and the kernel choice. Timing it would fail machines for work that is
            // done only once. The second window is what each of a track's nine windows costs.
            Run(session, mel, ct, ref peak);
            var window = Run(session, mel, ct, ref peak);

            return blank with
            {
                SessionStarted = true,
                WindowTime = window,
                WindowMemoryBytes = peak,
                Runtime = runtime,
            };
        }
        catch (Exception ex)
        {
            // Any of these means the model cannot run here: a runtime with no kernel for an operator in
            // the graph, a native library that does not load, or a quantised file made by a newer
            // onnxruntime. None of them is a reason to stop the app from starting.
            Said.Line("dj", $"the beat model would not run here ({ex.GetType().Name}: {ex.Message})");
            return blank;
        }
    }

    /// <summary>
    /// Opens the weights on the runtime the analyser would use, falling back to the processor when
    /// the graphics card will not have them.
    /// </summary>
    private static (InferenceSession Session, ModelRuntime Runtime) Open(string path)
    {
        var asked = BeatModelChoice.PickRuntime(
            RPM.Core.Diagnostics.DevSwitch.Read("RPM_ONNX_PROVIDER"), OperatingSystem.IsWindows());

        if (asked is ModelRuntime.DirectMl)
        {
            try
            {
                return (new InferenceSession(path, OnnxBeatModel.Options(ModelRuntime.DirectMl)),
                        ModelRuntime.DirectMl);
            }
            catch (Exception ex)
            {
                Said.Line("dj", $"DirectML would not start ({ex.GetType().Name}: {ex.Message}); " +
                                "the machine is measured on its processor, which is what it will use.");
            }
        }

        return (new InferenceSession(path, OnnxBeatModel.Options(ModelRuntime.Cpu)), ModelRuntime.Cpu);
    }

    /// <summary>One window, timed, with the process watched while it runs.</summary>
    private static TimeSpan Run(InferenceSession session, float[][] mel, CancellationToken ct, ref long peak)
    {
        long baseline = WorkingSet();
        long high = baseline;

        using var watching = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            while (!watching.IsCancellationRequested)
            {
                long now = WorkingSet();
                if (now > high)
                    Interlocked.Exchange(ref high, now);

                // The sampler stops as soon as the window has been timed. The delay returns false when
                // it is cancelled, so no exception is thrown.
                if (!await Cancellable.DelayAsync(Sampling, watching.Token).ConfigureAwait(false))
                    return;
            }
        }, CancellationToken.None);

        var clock = Stopwatch.StartNew();
        OnnxBeatModel.Infer(session, mel, start: 0);
        clock.Stop();

        watching.Cancel();
        try { sampler.Wait(TimeSpan.FromSeconds(1), ct); } catch { }

        long grew = Math.Max(0, Volatile.Read(ref high) - baseline);
        if (grew > peak)
            peak = grew;

        return clock.Elapsed;
    }

    /// <summary>The memory the process holds now, in bytes. This includes native allocations, which is
    /// what a model window is made of. The value is zero when the platform does not report
    /// it.</summary>
    private static long WorkingSet()
    {
        try { return Environment.WorkingSet; }
        catch { return 0; }
    }

    /// <summary>
    /// The machine's memory, and what is free of it.
    ///
    /// <para>The garbage collector updates these figures only when it collects, not when they are read.
    /// A new process has not collected yet, so a collection is forced first. A generation zero
    /// collection takes a few milliseconds. Without it, the figures would be empty.</para>
    /// </summary>
    private static (long Total, long Available) Memory()
    {
        try
        {
            GC.Collect(0, GCCollectionMode.Forced, blocking: true);
            var info = GC.GetGCMemoryInfo();
            long total = info.TotalAvailableMemoryBytes;
            long available = total > 0 && info.MemoryLoadBytes > 0
                ? Math.Max(0, total - info.MemoryLoadBytes)
                : 0;
            return (total, available);
        }
        catch
        {
            return (0, 0);
        }
    }
}
