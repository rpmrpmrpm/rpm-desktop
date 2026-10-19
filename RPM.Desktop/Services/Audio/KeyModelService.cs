using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RPM.Core.Analysis;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Audio;

/// <summary>
/// Names a record's key with S-KEY (Assets/OnnxModels/skey.onnx), Deezer's key detector.
///
/// <para>The model decides between major and minor. A chromagram compared with key profiles often gets
/// the note right but the mode wrong. A key and its relative minor contain the same twelve notes. They
/// differ only in which notes the music leans on, which is a judgement and not a sum. A wrong mode has
/// a real cost. On the Camelot wheel, a key and its relative minor sit at the same position. A record
/// that would mix well is then scored as a clash, and the set skips it.</para>
///
/// <para>The model computes its own constant-Q transform. So unlike the beat model, no spectrogram is
/// built here. It takes the waveform as it is.</para>
/// </summary>
public sealed partial class KeyModelService : IKeyModel, IDisposable
{
    /// <summary>Samples per call. This is a 15 second excerpt, the length the model was trained on. The
    /// exported graph accepts only this length.</summary>
    private const int WindowSamples = MelSpectrogram.SampleRate * 15;

    /// <summary>How many windows of a record are read. A key describes the whole record. The twelfth
    /// window rarely changes an answer that the first eleven agreed on, and each window costs
    /// time.</summary>
    private const int MaxWindows = 12;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private InferenceSession? _session;
    private bool _failed;

    private static string ModelPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "OnnxModels", "skey.onnx");

    /// <summary>Whether the model file is present and can be read. This follows the same rule as
    /// <see cref="OnnxBeatModel.IsAvailable"/>.</summary>
    public bool IsAvailable => !_failed && File.Exists(ModelPath);

    /// <inheritdoc />
    public KeyReading? Read(ReadOnlySpan<float> mono, int sampleRate, CancellationToken ct = default)
    {
        if (_failed || sampleRate != MelSpectrogram.SampleRate || mono.Length < WindowSamples)
            return null;

        _gate.Wait(ct);
        try
        {
            var session = EnsureSession();
            return session is null ? null : Run(session, mono, ct);
        }
        catch (OperationCanceledException)
        {
            // A cancelled call is not a failure. The catch below would stop this machine from reading
            // keys at all.
            throw;
        }
        catch (Exception ex)
        {
            Said.Line("analysis", $"key model stopped: {ex.GetType().Name}: {ex.Message}. " +
                            "The chromagram is used from here on.");
            _failed = true;
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

        var path = ModelPath;
        if (!File.Exists(path))
        {
            Said.Line("analysis", $"no key model at {path} — falling back to the chromagram. " +
                            "Run scripts/fetch-models.sh.");
            _failed = true;
            return null;
        }

        OnnxLogging.Quieten();

        return _session = new InferenceSession(path, new SessionOptions
        {
            IntraOpNumThreads = 2,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR,

            // Off for the same reason as in the beat model. See OnnxBeatModel.
            EnableCpuMemArena = false,
        });
    }

    private static KeyReading? Run(InferenceSession session, ReadOnlySpan<float> mono, CancellationToken ct)
    {
        // The samples are peak normalised, as the model's own loader does it. A key does not depend on
        // how loud a record was mastered. The model was only trained on normalised input.
        float peak = 0;
        foreach (float sample in mono)
            peak = Math.Max(peak, Math.Abs(sample));
        if (peak <= 0)
            return null;

        int windows = Math.Min(MaxWindows, mono.Length / WindowSamples);
        if (windows == 0)
            return null;

        // The windows are spread across the record, not taken from the start. The first minute of a
        // track is often an intro on one chord, so it does not represent the whole song.
        int step = mono.Length / windows;

        var name = session.InputMetadata.Keys.First();
        var totals = new double[KeyModelLabels.Count];

        for (int w = 0; w < windows; w++)
        {
            ct.ThrowIfCancellationRequested();

            int start = Math.Min(w * step, mono.Length - WindowSamples);
            var input = new DenseTensor<float>([1, 1, WindowSamples]);
            for (int i = 0; i < WindowSamples; i++)
                input[0, 0, i] = mono[start + i] / peak;

            using var results = session.Run([NamedOnnxValue.CreateFromTensor(name, input)]);
            var scores = results.First().AsTensor<float>();
            for (int k = 0; k < totals.Length; k++)
                totals[k] += scores.GetValue(k);
        }

        // The scores are averaged over the windows, as the reference implementation does for a whole
        // track. The outputs are already probabilities, because the softmax is part of the graph. So
        // the mean is also a probability.
        int best = 0, runnerUp = -1;
        for (int k = 1; k < totals.Length; k++)
            if (totals[k] > totals[best])
                best = k;
        for (int k = 0; k < totals.Length; k++)
            if (k != best && (runnerUp < 0 || totals[k] > totals[runnerUp]))
                runnerUp = k;

        double winner = totals[best] / windows;
        double second = runnerUp < 0 ? 0 : totals[runnerUp] / windows;

        // The confidence is the margin over the runner-up. <see cref="KeyDetector"/> reports the same
        // value, on the same scale, and everything downstream reads it that way. A record that sits
        // between two keys should not be mixed as if it had one clear key.
        return new KeyReading(KeyModelLabels.Order[best], Math.Clamp(winner - second, 0, 1));
    }

    public void Dispose()
    {
        _session?.Dispose();
        _session = null;
        _gate.Dispose();
    }
}
