using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RPM.Core.Threading;

namespace RPM.Desktop.Services.Art;

/// <summary>
/// Says what a photo is about.
///
/// Runs U²-Netp (Assets/OnnxModels/u2netp.onnx, a salient-object detector) over a downscaled copy of an
/// image. It returns a per-pixel map: high where the subject is, low where the background is.
/// <see cref="RPM.Core.Art.SaliencyCrop"/> uses the map to decide which part of an artist backdrop fits
/// in the art display's strip.
///
/// The model is about 4.6 MB and its input is 320 by 320. A run takes a few milliseconds on a CPU. It is
/// still worker-thread work. The feature is best-effort. If the model is missing or the runtime will not
/// start, <see cref="ComputeAsync"/> returns null, and the caller uses its own fallback.
/// </summary>
public sealed partial class SaliencyService : IDisposable
{
    /// <summary>The model's input size. The map comes back at this resolution too.</summary>
    public const int Size = 320;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private InferenceSession? _session;
    private bool _failed;

    /// <summary>
    /// The saliency map for an image already scaled to <see cref="Size"/>×<see cref="Size"/>, given as
    /// row-major RGBA. Null when the model could not be run at all. The caller then uses its fallback.
    /// </summary>
    public async Task<double[]?> ComputeAsync(byte[] rgba, CancellationToken ct = default)
    {
        if (_failed || rgba.Length < Size * Size * 4)
            return null;

        // One image at a time. A second session would double the memory for no gain, and backdrops arrive
        // one at a time anyway.
        if (!await Cancellable.WaitAsync(_gate, ct))
            return null;
        try
        {
            var session = EnsureSession();
            if (session is null)
                return null;

            return await Task.Run(() => Run(session, rgba));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _failed = true;   // A runtime that failed once will fail again, so stop paying for it.
            return null;
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

        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "OnnxModels", "u2netp.onnx");
        if (!File.Exists(path))
        {
            _failed = true;
            return null;
        }

        OnnxLogging.Quieten();

        var options = new SessionOptions
        {
            // Two threads. One backdrop takes about half a second. Do not take the whole machine either. A
            // model that uses every core to crop a photo makes the UI it decorates stutter.
            IntraOpNumThreads = 2,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,
        };

        return _session = new InferenceSession(path, options);
    }

    private static double[] Run(InferenceSession session, byte[] rgba)
    {
        // U²-Net's own normalisation. RGB is scaled to 0..1, then divided by ImageNet's mean and standard
        // deviation. Raw bytes give a map too, but a much worse one.
        float[] mean = [0.485f, 0.456f, 0.406f];
        float[] std = [0.229f, 0.224f, 0.225f];

        var input = new DenseTensor<float>([1, 3, Size, Size]);

        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var i = (y * Size + x) * 4;
                for (var c = 0; c < 3; c++)
                    input[0, c, y, x] = (rgba[i + c] / 255f - mean[c]) / std[c];
            }
        }

        var inputName = session.InputMetadata.Keys.First();
        using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, input)]);

        // There are seven outputs. The first is the map. The rest are side outputs that the training loss
        // used. The sigmoid is already in the graph, so these values are 0..1 probabilities. Applying a
        // sigmoid again would only squash the map. The values are stretched to fill 0..1, because only the
        // relative scores matter. A low-contrast map makes every threshold downstream unreliable.
        var output = results.First().AsTensor<float>();
        var map = new double[Size * Size];

        var min = double.MaxValue;
        var max = double.MinValue;

        for (var i = 0; i < map.Length; i++)
        {
            var v = (double)output.GetValue(i);
            map[i] = v;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        var range = max - min;
        if (range > 1e-6)
            for (var i = 0; i < map.Length; i++)
                map[i] = (map[i] - min) / range;

        return map;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        _gate.Dispose();
    }
}
