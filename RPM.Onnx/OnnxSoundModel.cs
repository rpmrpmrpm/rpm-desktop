using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RPM.Core.Analysis;
using RPM.Core.Diagnostics;

namespace RPM.Onnx;

/// <summary>
/// The audio half of the CLAP model (Assets/OnnxModels/clap_audio.int8.onnx), run on this machine.
///
/// <para>This class is only the runtime. The features are built by <see cref="ClapFeatures"/> and the
/// clips' outputs are averaged by <see cref="SoundEmbedding"/>, both shared with the cloud, which runs
/// this same class for a machine that cannot.</para>
///
/// <para><b>Eight-bit weights.</b> 77 MB against 281 MB for float32, faster on a processor, and about half
/// the memory while it runs. On 1,320 tracks the rankings of the four feeling lists agree with float32
/// at a Spearman correlation of 0.999, and the top ten of each list keeps 9 or 10 of its 10 songs.
/// Half-precision weights were tried and run 40% slower on a processor.</para>
///
/// <para>Best-effort, like every model in the app: with no file, no runtime, or a failure,
/// <see cref="Embed"/> returns null and the record has no embedding.</para>
/// </summary>
public sealed class OnnxSoundModel : ISoundModel, IDisposable
{
    /// <summary>The file the app ships.</summary>
    public const string FileName = "clap_audio.int8.onnx";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private InferenceSession? _session;
    private bool _failed;

    /// <param name="modelDirectory">Where the file is. Null for the app's own folder of weights, where
    /// the csproj copies them.</param>
    public OnnxSoundModel(string? modelDirectory = null) =>
        _path = Path.Combine(modelDirectory ?? OnnxBeatModel.ModelDirectory, FileName);

    /// <summary>Whether this machine has the weights and they have not failed. A file check, not a load:
    /// the load costs a second and about 140 MB, and is made by the first <see cref="Embed"/>.</summary>
    public bool IsAvailable => !_failed && File.Exists(_path);

    /// <inheritdoc />
    public float[][]? Embed(IReadOnlyList<float[]> features, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(features);
        if (_failed || features.Count == 0 || features.Any(f => f.Length != ClapFeatures.ValuesPerClip))
            return null;

        // One record at a time. One session holds one copy of the weights for every caller, but each run
        // allocates its own working memory, and a second run at once doubles that on a machine that is
        // also playing music.
        _gate.Wait(ct);
        try
        {
            var session = EnsureSession();
            if (session is null)
                return null;

            try
            {
                return Infer(session, features);
            }
            catch (Exception ex)
            {
                Said.Line("analysis", $"sound model stopped: {ex.GetType().Name}: {ex.Message}. " +
                                      "Records get no sound embedding from here on.");
                _failed = true;
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Closes the session and gives its memory back. The next <see cref="Embed"/> opens it again.
    ///
    /// <para>The sound pass reads a library once and then only new records, so the session is idle most of
    /// the time. The beat model keeps its session open because a DJ set needs the next record at once;
    /// nothing here is in a hurry.</para>
    /// </summary>
    public void Release()
    {
        _gate.Wait();
        try
        {
            _session?.Dispose();
            _session = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// How the session is set up. The same as the beat model's (see <see cref="OnnxBeatModel.Options"/>):
    /// two threads, and no memory arena or memory pattern, so the memory of a run is given back after it.
    /// </summary>
    public static SessionOptions Options() => new()
    {
        LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        IntraOpNumThreads = 2,
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        EnableCpuMemArena = false,
        EnableMemoryPattern = false,
    };

    /// <summary>The clips through the model, as one batch.</summary>
    public static float[][] Infer(InferenceSession session, IReadOnlyList<float[]> features)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(features);

        var input = new DenseTensor<float>([features.Count, 1, ClapFeatures.Frames, ClapFeatures.MelBands]);
        var buffer = input.Buffer.Span;
        for (int c = 0; c < features.Count; c++)
            features[c].AsSpan().CopyTo(buffer.Slice(c * ClapFeatures.ValuesPerClip, ClapFeatures.ValuesPerClip));

        var name = session.InputMetadata.Keys.First();
        using var results = session.Run([NamedOnnxValue.CreateFromTensor(name, input)]);
        var output = results.First().AsTensor<float>();

        var embeddings = new float[features.Count][];
        for (int c = 0; c < features.Count; c++)
        {
            var row = new float[SoundEmbedding.Dimensions];
            for (int i = 0; i < row.Length; i++)
                row[i] = output[c, i];
            embeddings[c] = row;
        }
        return embeddings;
    }

    private InferenceSession? EnsureSession()
    {
        if (_session is not null)
            return _session;

        if (!File.Exists(_path))
        {
            Said.Line("analysis", $"no sound model at {_path}; records get no sound embedding here. " +
                                  "Run scripts/fetch-models.sh.");
            _failed = true;
            return null;
        }

        try
        {
            OnnxLogging.Quieten();
            _session = OnnxSession.Open(_path, Options());
            Said.Line("analysis", $"sound model on the processor, from {FileName}.");
            return _session;
        }
        catch (Exception ex)
        {
            Said.Line("analysis", $"the sound model would not open ({ex.GetType().Name}: {ex.Message}).");
            _failed = true;
            return null;
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        _gate.Dispose();
    }
}
