using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RPM.Core.Analysis;
using RPM.Core.Diagnostics;

namespace RPM.Onnx;

/// <summary>
/// Finds a record's beats and bar lines with Beat This! (Assets/OnnxModels/beat_this.onnx), a
/// transformer trained on annotated music.
///
/// <para>It replaces the weakest part of the DSP chain. Autocorrelation over an onset envelope finds
/// a tempo, but which octave of that tempo (70 or 140) is close to random on a record that can be
/// counted either way, and where the "one" of the bar falls is guessed from where the harmony
/// changes. A listener knows both at once and a correlation cannot, and both decide how a mix is
/// timed: the octave sets how long every fade runs, and the bar phase decides whether two records
/// meet on the one or three beats away from it.</para>
///
/// <para>This class is only the runtime. The spectrogram is in <see cref="MelSpectrogram"/> and
/// everything before and after the model is in <see cref="BeatModelDecoder"/>, both tested against
/// the reference implementation. This class only loads the weights and passes them one window at a
/// time.</para>
///
/// <para>Best-effort everywhere: with no model file, no runtime, or a track the model cannot read,
/// <see cref="Read"/> returns null and the analyser keeps the DSP grid it already has.</para>
/// </summary>
public sealed class OnnxBeatModel : IBeatModel, IDisposable
{
    /// <summary>Below this a track is too short for the model: a window with only a few seconds of
    /// audio is mostly the silence it is padded with.</summary>
    private const int MinFrames = BeatModelDecoder.ChunkFrames / 4;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private InferenceSession? _session;
    private bool _failed;

    /// <summary>The weights that can still be tried, best first, and how far down that list this
    /// machine has gone. Fixed on the first read: the files do not change while the app runs, and
    /// deciding again per track would mean reading the environment again per track.</summary>

    /// <summary>Where the app keeps its weights: next to the executable, where the csproj copies
    /// them. A caller with weights somewhere else (a measurement run against a checkout, not an
    /// install) passes the directory to the constructor instead.</summary>
    public static string ModelDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "OnnxModels");

    private readonly string _directory;

    public OnnxBeatModel(string? modelDirectory = null) =>
        _directory = modelDirectory ?? ModelDirectory;

    /// <summary>
    /// The weights to open: the float32 file. See <see cref="BeatModelChoice"/>, which also documents
    /// the two environment variables that fix the choice for a measurement.
    /// </summary>
    private string? ModelPath =>
        BeatModelChoice.Pick(_directory, File.Exists,
                             RPM.Core.Diagnostics.DevSwitch.Read("RPM_BEAT_MODEL"));

    /// <summary>
    /// How the scores become beats and bar lines. The choice itself is made by
    /// <see cref="BeatModelDecoder.Chosen"/>, so that a track read here and a track read by the store
    /// get the same grid. This only reads the environment variable that fixes it for a
    /// measurement.
    /// </summary>
    private static BeatModelDecoder.Reader Reader =>
        BeatModelDecoder.Chosen(RPM.Core.Diagnostics.DevSwitch.Read("RPM_BEAT_DECODER"));

    /// <summary>
    /// Whether this machine has weights at all. Checked before a stored analysis is discarded to be
    /// made again: when this is false, analysing the track again costs a whole decode and gives the
    /// result that is already stored.
    ///
    /// <para>A file check, not a load: it is checked once per track, and loading eighty megabytes to
    /// answer would cost too much. A file that exists but does not open still fails once, and
    /// <see cref="_failed"/> makes this false after that.</para>
    /// </summary>
    public bool IsAvailable => !_failed && ModelPath is not null;

    /// <inheritdoc />
    public BeatReading? Read(ReadOnlySpan<float> mono, int sampleRate, CancellationToken ct = default)
    {
        var logits = ReadLogits(mono, sampleRate, ct);
        return logits is { } curves ? Reader(curves.Beat, curves.Downbeat) : null;
    }

    /// <summary>
    /// The model's raw output for every frame of a track: one score per frame for the beats and one
    /// for the bar lines, on the logit scale.
    ///
    /// <para>The expensive half of <see cref="Read"/>, and the half that does not change when the
    /// decoding changes. Public so that a measurement can run the weights once over a corpus and then
    /// try every decoding at almost no cost. See <c>tools/TempoBench</c>, where this reduces about
    /// forty-five minutes to about one second.</para>
    ///
    /// <para>Null in the same cases as <see cref="Read"/>: no weights, no runtime, or a track that is
    /// too short.</para>
    /// </summary>
    public (float[] Beat, float[] Downbeat)? ReadLogits(
        ReadOnlySpan<float> mono, int sampleRate, CancellationToken ct = default)
    {
        if (_failed || sampleRate != MelSpectrogram.SampleRate)
            return null;

        // One track at a time. The analyser runs up to four decodes at once and all of them call
        // this; the queue stops four windows from being in the model at the same time. This is not
        // about the weights (one session holds one copy of them for all callers) but about the memory
        // a window uses while it runs, which is close to a gigabyte, not the eighty megabytes on disk.
        // Resident memory is about 1.0 GB for one window, 2.4 GB for two and 3.5 GB for four, and the
        // extra speed is not worth that on a machine that is also playing music.
        _gate.Wait(ct);
        try
        {
            var session = EnsureSession();
            if (session is null)
                return null;

            var mel = MelSpectrogram.Compute(mono);
            if (mel.Length < MinFrames)
                return null;

            try
            {
                return BeatModelDecoder.ReadLogits(
                    mel.Length, start => Infer(session, mel, start), ct);
            }
            catch (OperationCanceledException)
            {
                // Cancelled, not failed. The code below would stop this machine reading beats for
                // the rest of the session, only because the user stopped the analysis.
                throw;
            }
            catch (Exception ex)
            {
                // No second file to try: only one set of weights publishes grids to the shared pool
                // (see BeatModelChoice), so after a failure here the DSP grid is used, logged once.
                Said.Line("analysis", $"beat model stopped: {ex.GetType().Name}: {ex.Message}. " +
                                "The DSP grid is used from here on.");
                _failed = true;
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private InferenceSession? EnsureSession()
    {
        if (_session is not null)
            return _session;

        var path = BeatModelChoice.Pick(
            _directory, File.Exists, RPM.Core.Diagnostics.DevSwitch.Read("RPM_BEAT_MODEL"));
        if (path is null)
        {
            // The one failure that is logged clearly. It is expected on a working copy (the file is
            // ninety megabytes and fetched, not committed), and the app continues without it, using
            // the DSP analysis only. Logged so it is clear why a machine reads tempos an octave
            // wrong.
            Said.Line("analysis", $"no beat model in {_directory} — falling back to the " +
                            "DSP grid. Run scripts/fetch-models.sh.");
            _failed = true;
            return null;
        }

        OnnxLogging.Quieten();

        var runtime = BeatModelChoice.PickRuntime(
            RPM.Core.Diagnostics.DevSwitch.Read("RPM_ONNX_PROVIDER"), OperatingSystem.IsWindows());

        if (runtime is ModelRuntime.DirectMl)
        {
            try
            {
                var session = new InferenceSession(path, Options(ModelRuntime.DirectMl));
                Said.Line("analysis", $"beat model on DirectML, from {Path.GetFileName(path)}.");
                return _session = session;
            }
            catch (Exception ex)
            {
                // A machine with no DirectX 12 device, a runtime without DirectML, or a graphics
                // card without enough memory for a window: none of these stop beat reading, they
                // only move it to the CPU like on every other machine. Logged once, because GPU use
                // was requested and did not happen.
                Said.Line("analysis", $"DirectML would not start ({ex.GetType().Name}: " +
                                $"{ex.Message}); the beat model runs on the processor.");
            }
        }

        Said.Line("analysis", $"beat model on the processor, from {Path.GetFileName(path)}.");
        return _session = new InferenceSession(path, Options(ModelRuntime.Cpu));
    }

    /// <summary>How the session is set up. This decides most of what a window costs.</summary>
    public static SessionOptions Options(ModelRuntime runtime)
    {
        var options = new SessionOptions
        {
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,

            // Two threads, the same as the app's other models: analysis runs several tracks at once
            // under its own limit, and a model that takes every core for one track slows the others
            // and the UI. More threads help little: a window takes about 644 ms on one thread,
            // 516 ms on two and 497 ms on four, and more is slower.
            IntraOpNumThreads = 2,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,

            // The runtime's own allocator keeps every block it has used and never releases one, so
            // the peak memory of a thirty-second window through a transformer stays with the process
            // for its whole life. This app analyses a track and then plays it for a long time, so
            // that memory is held for no benefit. With the arena off, each inference allocates from
            // the system and releases the memory after. It is slightly slower per window, which is
            // acceptable because analysis runs in the background.
            EnableCpuMemArena = false,

            // Also off for memory. When on, the runtime plans one block large enough for every
            // intermediate tensor of a window and allocates it at once: about 1.4 GB resident,
            // against about 0.97 GB with planning off, with no measurable difference in time over
            // repeated runs. Turning it off saves about 400 MB on a machine that is also playing
            // music.
            EnableMemoryPattern = false,
        };

        // DirectML runs one node at a time and does its own allocation, and the runtime documents
        // both the memory planning above and parallel execution as unsupported with it. The planning
        // is already off, and the execution mode must be set explicitly.
        if (runtime is ModelRuntime.DirectMl)
        {
            options.ExecutionMode = ExecutionMode.ORT_SEQUENTIAL;
            options.AppendExecutionProvider_DML(0);
        }

        return options;
    }

    /// <summary>One window through the model. Frames outside the track are fed as silence.</summary>
    public static (float[] Beat, float[] Downbeat) Infer(InferenceSession session, float[][] mel, int start)
    {
        int chunk = BeatModelDecoder.ChunkFrames;
        var input = new DenseTensor<float>([1, chunk, MelSpectrogram.MelBands]);
        for (int i = 0; i < chunk; i++)
        {
            int at = start + i;
            if (at < 0 || at >= mel.Length)
                continue;

            var row = mel[at];
            for (int b = 0; b < MelSpectrogram.MelBands; b++)
                input[0, i, b] = row[b];
        }

        var name = session.InputMetadata.Keys.First();
        using var results = session.Run([NamedOnnxValue.CreateFromTensor(name, input)]);

        // By name, not by position: the graph declares two outputs and their order is not
        // guaranteed. Reading them in the wrong order would swap the beats and the bar lines.
        var byName = results.ToDictionary(r => r.Name, r => r.AsTensor<float>());
        var beat = byName["beat"];
        var downbeat = byName["downbeat"];

        var beatFrames = new float[chunk];
        var downbeatFrames = new float[chunk];
        for (int i = 0; i < chunk; i++)
        {
            beatFrames[i] = beat.GetValue(i);
            downbeatFrames[i] = downbeat.GetValue(i);
        }
        return (beatFrames, downbeatFrames);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        _gate.Dispose();
    }
}
