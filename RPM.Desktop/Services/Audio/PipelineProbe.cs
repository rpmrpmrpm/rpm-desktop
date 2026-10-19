using System.Diagnostics;
using System.Text;

namespace RPM.Desktop.Services.Audio;

/// <summary>Timing checkpoints along the audio-to-pixels pipeline. A stall can then be traced to one
/// stage, instead of being guessed at. Each stage reports two values. The first is how often it ran,
/// and the longest gap between runs. The second is how long each run took. A stage whose rate stays the
/// same while its cost grows is doing too much work. A stage whose cost stays the same while its gap
/// grows is being held back by another stage. That difference is the purpose of these measurements.
///
/// The marks use no locks and do not allocate, so the probe cannot cause a stall itself. Only
/// <see cref="Report"/> allocates, once a second, on the calling thread.
/// </summary>
public static class PipelineProbe
{
    public enum Stage
    {
        /// <summary>ffmpeg produced one PCM block, about 50 ms of audio.</summary>
        Decode,
        /// <summary>The block was handed to the sink. This call blocks while the device is full, so it
        /// sets the pace.</summary>
        Sink,
        /// <summary>The visualiser copied the frame that the sink is playing.</summary>
        Copy,
        /// <summary>Spectrum analysis: FFT and smoothing.</summary>
        Fft,
        /// <summary>Upload of the audio texture to the GPU.</summary>
        Upload,
        /// <summary>A visualiser frame started. This gives the real frame rate of the
        /// visualiser.</summary>
        Frame,
        /// <summary>The draw calls for one visualiser frame.</summary>
        Draw,
        /// <summary>The amp's own bar and scope visualiser tick. It runs on the UI thread, together
        /// with the stages above.</summary>
        Amp,
    }

    private const int N = 8;
    private static readonly string[] Names = ["decode", "sink", "copy", "fft", "upload", "frame", "draw", "amp"];

    /// <summary>Off by default. The checkpoints are always in place, so a pacing question can be
    /// answered by measurement. They cost nothing and print nothing until this is switched on.
    ///
    /// <para>Set it with <c>RPM_PIPE_DIAG=1</c> in the environment. The wall's stopwatch is set the
    /// same way. The probe was built for the case where the device runs dry. The question it answers is
    /// which stage had no block ready: the one that produces the blocks, or the one that hands them to
    /// the device.</para>
    /// </summary>
    public static bool Enabled =
        RPM.Core.Diagnostics.DevSwitch.Read("RPM_PIPE_DIAG") is "1" or "true";

    private static readonly long[] _count = new long[N];
    private static readonly long[] _last = new long[N];
    private static readonly long[] _maxGap = new long[N];
    private static readonly long[] _work = new long[N];
    private static readonly long[] _workMax = new long[N];
    private static long _windowStart;

    public static long Now => Stopwatch.GetTimestamp();

    /// <summary>Records that a stage ran, and the time since it last ran.</summary>
    public static void Mark(Stage stage)
    {
        if (!Enabled) return;
        int i = (int)stage;
        long now = Stopwatch.GetTimestamp();
        long prev = Interlocked.Exchange(ref _last[i], now);
        if (prev != 0)
        {
            long gap = now - prev;
            if (gap > Volatile.Read(ref _maxGap[i])) Volatile.Write(ref _maxGap[i], gap);
        }
        Interlocked.Increment(ref _count[i]);
    }

    /// <summary>Records how long a stage took. Use it with the value of <see cref="Now"/> taken at the
    /// start of the stage.</summary>
    public static void Work(Stage stage, long startTicks)
    {
        if (!Enabled) return;
        int i = (int)stage;
        long d = Stopwatch.GetTimestamp() - startTicks;
        Interlocked.Add(ref _work[i], d);
        if (d > Volatile.Read(ref _workMax[i])) Volatile.Write(ref _workMax[i], d);
    }

    /// <summary>Marks a stage and returns the timestamp to hand back to <see cref="Work"/>.</summary>
    public static long Begin(Stage stage)
    {
        Mark(stage);
        return Stopwatch.GetTimestamp();
    }

    /// <summary>True when a full window of samples has collected and <see cref="Report"/> will write a
    /// line. This is for callers that can afford the marks but not the writing. The audio pump reads
    /// it, and hands the report to a background thread, because debug output can block on a
    /// pipe.</summary>
    public static bool Due
    {
        get
        {
            if (!Enabled) return false;
            long start = Volatile.Read(ref _windowStart);
            return start == 0 || Stopwatch.GetTimestamp() - start >= Stopwatch.Frequency;
        }
    }

    /// <summary>Writes one line per second that covers every stage, then resets the window. It is cheap
    /// to call on every frame. It does work only when a second has passed.</summary>
    public static void Report()
    {
        if (!Enabled) return;
        long now = Stopwatch.GetTimestamp();
        long start = Volatile.Read(ref _windowStart);
        if (start == 0) { Volatile.Write(ref _windowStart, now); return; }

        double elapsed = (now - start) / (double)Stopwatch.Frequency;
        if (elapsed < 1.0) return;
        if (Interlocked.CompareExchange(ref _windowStart, now, start) != start) return;

        var sb = new StringBuilder();
        for (int i = 0; i < N; i++)
        {
            long c = Interlocked.Exchange(ref _count[i], 0);
            long gap = Interlocked.Exchange(ref _maxGap[i], 0);
            long work = Interlocked.Exchange(ref _work[i], 0);
            long wmax = Interlocked.Exchange(ref _workMax[i], 0);
            if (c == 0 && work == 0) continue;

            sb.Append(' ').Append(Names[i]).Append('=').Append((c / elapsed).ToString("F0")).Append("/s");
            sb.Append(" gap").Append(Ms(gap).ToString("F0"));
            if (work > 0)
                sb.Append(" w").Append((Ms(work) / Math.Max(1, c)).ToString("F1"))
                  .Append('/').Append(Ms(wmax).ToString("F0"));
        }
        RPM.Core.Diagnostics.Said.Line("pipe", sb.ToString().TrimStart());

        static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }
}
