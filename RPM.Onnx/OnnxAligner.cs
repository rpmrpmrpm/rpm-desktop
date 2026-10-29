using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using RPM.Core.Analysis;
using RPM.Core.Lyrics;
using RPM.Core.Diagnostics;

namespace RPM.Onnx;

/// <summary>
/// Reads a few seconds of audio and gives, for every 20 ms, a score for each letter that may be
/// sung (Assets/OnnxModels/wav2vec2.onnx).
///
/// <para>Only <see cref="CtcAligner"/> reads the output, and that keeps it cheap. The app already
/// has the words (from the lyric sheet), so this does not have to produce a correct transcript. It
/// only has to be good enough for the aligner to place letters that the sheet already gives, which
/// a base model does for one lyric line in much less time than the gap before the next line.</para>
///
/// <para>This class is only the runtime. The path through the frames is in <see cref="CtcAligner"/>
/// and the alphabet is in <see cref="CtcVocabulary"/>, both in Core and covered by tests. This class
/// only opens the weights and passes them a window.</para>
///
/// <para>Best-effort everywhere: with no weights, no runtime, or a window the model cannot read,
/// <see cref="Read"/> returns null and the sheet keeps the timing it already had. That timing is an
/// estimate (see <see cref="LyricTiming"/>), but it works, so no capability check, startup decision or
/// cloud fallback is needed.</para>
/// </summary>
public sealed class OnnxAligner : IDisposable
{
    /// <summary>The sample rate the model was trained at. The decode must produce exactly this: at any
    /// other rate the audio is read at the wrong speed and every letter is placed wrongly.</summary>
    public const int SampleRate = 16000;

    /// <summary>Below this there is not enough audio to place anything: a fifth of a second is ten
    /// frames, and a lyric line is never that short.</summary>
    private const int LeastSamples = SampleRate / 5;

    /// <summary>The weights, quantised version first: eight bits is several times faster on an x86
    /// processor with VNNI, which is the kind of machine this must run on.</summary>
    private static readonly string[] Names = ["wav2vec2.int8.onnx", "wav2vec2.onnx"];

    private const string VocabName = "wav2vec2.vocab.json";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory;
    private InferenceSession? _session;
    private bool _failed;
    private CtcVocabulary? _vocabulary;
    private bool _readVocabulary;

    /// <summary>Where the app keeps its weights: next to the executable, where the csproj copies them.</summary>
    public static string ModelDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "OnnxModels");

    public OnnxAligner(string? modelDirectory = null) =>
        _directory = modelDirectory ?? ModelDirectory;

    private string? ModelPath
    {
        get
        {
            foreach (var name in Names)
            {
                var path = Path.Combine(_directory, name);
                if (File.Exists(path))
                    return path;
            }
            return null;
        }
    }

    /// <summary>
    /// Whether this machine has weights to read with. Checked before a track is decoded for
    /// alignment, because on a machine with no weights the decode would be wasted.
    ///
    /// <para>A file check, not a load: loading the model to answer would cost too much. A file that
    /// exists but does not open still fails once, and after that this is false.</para>
    /// </summary>
    public bool IsAvailable => !_failed && ModelPath is not null && Vocabulary is not null;

    /// <summary>
    /// The model's own alphabet, read from the JSON file next to the weights.
    ///
    /// <para>Next to the weights, not in the source, because every CTC model has a different alphabet,
    /// with a different order, case and punctuation. A table in the app would be correct for one
    /// export and wrong for the next, with no error: the wrong alphabet still aligns, but puts every
    /// word in the wrong place.</para>
    /// </summary>
    public CtcVocabulary? Vocabulary
    {
        get
        {
            if (_readVocabulary)
                return _vocabulary;

            _readVocabulary = true;
            try
            {
                var path = Path.Combine(_directory, VocabName);
                if (!File.Exists(path))
                    return null;

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;

                var tokens = new Dictionary<char, int>();
                foreach (var token in root.GetProperty("tokens").EnumerateObject())
                {
                    // One character each. The model's special tokens (<pad>, <s>, <unk>) are longer
                    // than one character and are not sung letters, so they are left out of the
                    // alphabet: a lyric never needs one, and the blank is used by its id instead.
                    if (token.Name.Length == 1)
                        tokens[token.Name[0]] = token.Value.GetInt32();
                }

                var blank = root.TryGetProperty("blank", out var b) ? b.GetInt32() : 0;
                var delimiter = root.TryGetProperty("delimiter", out var d)
                                && d.GetString() is { Length: 1 } text
                    ? text[0]
                    : '|';

                return _vocabulary = tokens.Count > 0
                    ? new CtcVocabulary(tokens, blank, delimiter)
                    : null;
            }
            catch (Exception ex)
            {
                Said.Line("align", $"{VocabName} would not read ({ex.GetType().Name}); " +
                                "the sheet keeps its own timing.");
                return null;
            }
        }
    }

    /// <summary>
    /// The model's output for one window: a score per token per frame, frame-major, ready for
    /// <see cref="CtcAligner.Align"/>. Null when there is no model to read it with.
    /// </summary>
    /// <param name="mono">The window, mono, at <see cref="SampleRate"/>.</param>
    public (float[] Scores, int Frames, int Vocab)? Read(
        ReadOnlySpan<float> mono, CancellationToken ct = default)
    {
        if (_failed || mono.Length < LeastSamples)
            return null;

        // One window at a time. Unlike the beat model, this is not about memory (a lyric line is four
        // seconds, not thirty) but about CPU: alignment runs in the background while a track is
        // playing, and two at once would take too much CPU from playback and the UI.
        _gate.Wait(ct);
        try
        {
            var session = EnsureSession();
            if (session is null)
                return null;

            var audio = new DenseTensor<float>([1, mono.Length]);
            for (var i = 0; i < mono.Length; i++)
                audio[0, i] = mono[i];

            using var outputs = session.Run(
                [NamedOnnxValue.CreateFromTensor("audio", audio)]);

            var logits = outputs.First().AsTensor<float>();
            if (logits.Dimensions.Length != 3)
                return null;

            var frames = logits.Dimensions[1];
            var vocab = logits.Dimensions[2];
            if (frames <= 0 || vocab <= 0)
                return null;

            // Copied, not passed on: the tensor belongs to the results that the `using` above
            // disposes, and the aligner reads it after this returns.
            var scores = new float[frames * vocab];
            for (var i = 0; i < scores.Length; i++)
                scores[i] = logits.GetValue(i);

            return (scores, frames, vocab);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A window the model cannot read is a line that keeps its estimated timing. After one
            // failure the model is not used again this session: a model that fails on one line fails
            // on the next, and each attempt costs a line of delay.
            Said.Line("align", $"the model would not read a window ({ex.GetType().Name}: " +
                            $"{ex.Message}); the sheet keeps its own timing.");
            _failed = true;
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>How many audio samples one output frame covers: the stride of the model's
    /// convolutional front end. Used to convert a frame index back into a time.</summary>
    public static int SamplesPerFrame => 320;

    private InferenceSession? EnsureSession()
    {
        if (_session is not null)
            return _session;

        var path = ModelPath;
        if (path is null)
        {
            // Expected on a working copy (the file is fetched, not committed). The app continues
            // without the model, following the sheet line by line with the words spread across each
            // line. Logged so it is clear why one machine follows the singer and another does not.
            Said.Line("align", $"no aligner in {_directory} — the sheet keeps its own timing. " +
                            "Run scripts/fetch-models.sh.");
            _failed = true;
            return null;
        }

        OnnxLogging.Quieten();

        // The same options as the beat model, on purpose: two threads so a model cannot take every
        // core while a track is playing, and no memory arena so a window's peak memory is not kept by
        // the process for the rest of its life. Both reasons are about this app, not about beats, so
        // both models use the same settings.
        try
        {
            var session = OnnxSession.Open(path, OnnxBeatModel.Options(ModelRuntime.Cpu));
            Said.Line("align", $"aligner on the processor, from {Path.GetFileName(path)}.");
            return _session = session;
        }
        catch (Exception ex)
        {
            Said.Line("align", $"{Path.GetFileName(path)} would not open ({ex.GetType().Name}: " +
                            $"{ex.Message}); the sheet keeps its own timing.");
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
