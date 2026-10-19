using RPM.Core.Threading;
using System.Diagnostics;
using RPM.Core.Audio;
using RPM.Core.Lyrics;
using RPM.Onnx;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Lyrics;

/// <summary>Finds when each word of a lyric line is sung, by measuring the audio of the record.
/// Without this, a line is shared out across its words by their length (<see cref="LyricTiming"/>).
/// That is right about which half of the line is being sung, but only roughly right about each word.
/// A held note, a rest in the middle of a line or a melisma makes it drift.
/// This class asks the audio instead. A model reads the sound, and <see cref="CtcAligner"/> finds the
/// most likely place for the words that the sheet already gives.
///
/// <para><b>The model is never asked what was sung, only when.</b> The words come from the sheet.
/// This makes the work cheap, because there is no search over possible words, and it is safe,
/// because the highlight cannot land on words the singer did not sing.</para>
///
/// <para><b>This class is independent of beat reading.</b> It shares no service, no verdict and no
/// probe with the DJ feature. The DJ needs beats, so it measures the machine at startup and sends
/// windows to the cloud when the machine is too slow. This class does not need that. Everything
/// below fails by returning null, and the sheet then keeps the timing it already had.</para>
/// </summary>
public sealed partial class LyricAlignment : IDisposable
{
    private readonly OnnxAligner _model = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The record in hand, decoded once. See <see cref="Mono"/>.</summary>
    private string? _path;
    private float[]? _mono;

    /// <summary>The lines already worked out for this record. A line that is played again is not
    /// sent to the model again. The cache is cleared when the record changes.</summary>
    private readonly Dictionary<int, IReadOnlyList<WordSpan>> _lines = [];

    /// <summary>The lines whose measurement was judged not reliable (see <see cref="LyricDoubt"/>).
    /// These are remembered too, so a chorus line that comes round four times is judged once,
    /// not sent through the model each time.</summary>
    private readonly HashSet<int> _doubted = [];

    /// <summary>
    /// Whether this machine has been shown to keep up. This is the only speed check in the class.
    ///
    /// <para>The check is made while lines are aligned, not with a separate startup test. A machine
    /// that cannot align one line inside its own span cannot stay ahead of the singer either. When
    /// that happens, the class stops for the session and the sheet keeps its guessed timing. This
    /// costs one line to find out. A startup test would cost every machine a second and a model load,
    /// even when no lyrics are ever shown.</para>
    /// </summary>
    private bool _tooSlow;

    /// <summary>Whether there is anything to align with. This is false when there are no model
    /// weights, which is the case until scripts/fetch-models.sh has been run.</summary>
    public bool IsAvailable => !_tooSlow && _model.IsAvailable;

    /// <summary>
    /// Returns when each word of <paramref name="line"/> is sung. Returns null where the time could
    /// not be measured: no weights, a machine that cannot keep up, a line with nothing to align, or a
    /// record that cannot be decoded. The caller then keeps the timing it already had.
    ///
    /// <para><b>One span per word of the line, counted the way <see cref="LyricTiming"/> counts words.</b>
    /// This lets the caller read the answer by list position, which is how a view reads it when it has
    /// already built one run per letter. The model's alphabet has fewer characters than that, because it
    /// has no digits, so "12" is spelled as nothing. Those words are filled in from the space between
    /// their measured neighbours instead of being dropped. A list with a hole in it is the one shape a
    /// position-based reader cannot tell is wrong. See <see cref="LyricTiming.Whole"/>.</para>
    /// </summary>
    /// <param name="path">The record's own file.</param>
    /// <param name="sheet">The sheet being followed.</param>
    /// <param name="line">Which line of it to place.</param>
    /// <param name="playhead">The position of the record at the moment of asking. This lets the
    /// answer say whether it arrived before the words it is about were sung, which is the one
    /// measurement that shows whether this keeps up. See <see cref="Report"/>. A negative value
    /// means "not playing", and skips the report.</param>
    public async Task<IReadOnlyList<WordSpan>?> ForLineAsync(
        string? path, LyricSheet sheet, int line, string text,
        double playhead = -1, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sheet);

        if (!IsAvailable || string.IsNullOrWhiteSpace(path)
            || line < 0 || line >= sheet.Lines.Count || string.IsNullOrWhiteSpace(text))
            return null;

        lock (_lines)
        {
            if (string.Equals(path, _path, StringComparison.Ordinal))
            {
                if (_lines.TryGetValue(line, out var known))
                    return known;

                // Already judged and found unreliable. Return null rather than an empty list: the
                // reader then keeps the spread timing it already shows, and an empty list would remove it.
                if (_doubted.Contains(line))
                    return null;
            }
        }

        // Run off the UI thread. This is a decode and a model run, and it happens while a record is
        // already playing.
        var (placed, spans) = await Cancellable.RunAsync(
            () => Place(path!, sheet, line, text, playhead, ct), ct);

        return placed ? spans : null;
    }

    private IReadOnlyList<WordSpan>? Place(
        string path, LyricSheet sheet, int line, string text, double playhead, CancellationToken ct)
    {
        // One line at a time. The model has its own queue, but the decode does not. Two records
        // decoding at once behind a playing one must be avoided. If the record changes while this call
        // waits for the gate, the line is dropped without a message.
        if (!Cancellable.Enter(_gate, ct))
            return null;

        try
        {
            var mono = Mono(path, ct);
            if (mono is null || _model.Vocabulary is not { } alphabet)
                return null;

            // The steps are in Core (LineAligner). They are shared with the server's transcoder, so the
            // server and this machine place a line by the same rule. This class keeps only this machine's
            // guard and log.
            var took = 0.0;
            var outcome = LineAligner.Place(
                mono, OnnxAligner.SampleRate, OnnxAligner.SamplesPerFrame, sheet.Lines, line, text, alphabet,
                window =>
                {
                    var began = Stopwatch.GetTimestamp();
                    var heard = _model.Read(window, ct);
                    took = Stopwatch.GetElapsedTime(began).TotalSeconds;
                    return heard is { } h ? new HeardWindow(h.Scores, h.Frames, h.Vocab) : null;
                });

            var from = sheet.Lines[line].Seconds;
            switch (outcome)
            {
                case LineAligner.Outcome.Doubted:
                    Said.Line("align", $"line {line} at {from:F2}s placed a word on a hold with the rest " +
                                    "crushed in behind it; the sheet keeps its own timing for this line.");
                    lock (_lines)
                    {
                        if (string.Equals(path, _path, StringComparison.Ordinal))
                            _doubted.Add(line);
                    }
                    return null;

                case LineAligner.Outcome.Placed placedLine:
                    // The one speed check. A machine that cannot place a line inside the line's own length
                    // cannot stay ahead of the singer. A highlight that arrives after its words is worse
                    // than the guessed timing it replaces.
                    if (took > placedLine.Window)
                    {
                        Said.Line("align", $"a {placedLine.Window:F1}s line took {took:F1}s to place; " +
                                        "the sheet keeps its own timing from here.");
                        _tooSlow = true;
                        return null;
                    }

                    if (placedLine.Rescued)
                        Said.Line("align", $"line {line} at {from:F2}s ran past its window; " +
                                        $"its tail is shared out to {placedLine.Wall:F2}s rather than stacked on it.");

                    var told = placedLine.Words;
                    if (told.Count != placedLine.Spelled)
                        Said.Line("align", $"line {line} at {from:F2}s has {told.Count} words but the model " +
                                        $"could spell {placedLine.Spelled}; the rest are placed between their neighbours.");

                    lock (_lines)
                    {
                        if (string.Equals(path, _path, StringComparison.Ordinal))
                            _lines[line] = told;
                    }

                    Report(line, from, placedLine.Window, took, playhead, told);
                    return told;

                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Said.Line("align", $"{Path.GetFileName(path)} would not align " +
                            $"({ex.GetType().Name}: {ex.Message}); the sheet keeps its own timing.");
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── whether it keeps up, and whether it is right ──

    /// <summary>Logs every line's numbers, not only the unusual ones. This is off unless
    /// <c>RPM_ALIGN_DIAG=1</c> is set in the environment.</summary>
    private static readonly bool Diag =
        RPM.Core.Diagnostics.DevSwitch.Read("RPM_ALIGN_DIAG") is "1" or "true";

    private int _placed;
    private double _slowest;
    private double _tightest = double.MaxValue;

    /// <summary>Measures what one alignment cost and what it found. There are two questions.
    ///
    /// <para><b>Does it keep up.</b> The question is whether the answer arrived before the words it is
    /// about were sung. The playhead at the moment of asking, plus the time the work took, is compared
    /// with the start of the line. A positive lead means the highlight was ready in time. A negative
    /// lead means the singer got there first, so the reader saw the guessed timing for part of the
    /// line.</para>
    ///
    /// <para><b>Is it right.</b> The model's first word is compared with the start time the sheet gives
    /// for the line. They should agree within a fraction of a second. A large gap means one of them is
    /// wrong: the sheet has a start time that is too early, or the model has found the words somewhere
    /// else.</para>
    ///
    /// <para>Normal lines are logged only when <see cref="Diag"/> is set. A late line, or one whose words
    /// are far from the sheet, is always logged, because those are real failures.</para>
    /// </summary>
    private void Report(
        int line, double from, double window, double took, double playhead,
        IReadOnlyList<WordSpan> placed)
    {
        _placed++;
        _slowest = Math.Max(_slowest, took);

        // Where the record had got to by the time this answer was ready, and how long that left.
        var ready = playhead >= 0 ? playhead + took : double.NaN;
        var lead = playhead >= 0 ? from - ready : double.NaN;
        if (!double.IsNaN(lead))
            _tightest = Math.Min(_tightest, lead);

        // What the model thinks, against what the sheet says. The sheet stamps the START of a line and
        // nothing else, so the END below is the model's alone. There is nothing to compare it with.
        var drift = placed.Count > 0 ? placed[0].From - from : 0;
        var ends = placed.Count > 0 ? placed[^1].To : from;

        var late = !double.IsNaN(lead) && lead < 0;
        var adrift = Math.Abs(drift) > 1.0;

        if (!Diag && !late && !adrift)
            return;

        var mark = late ? "LATE " : adrift ? "ADRIFT " : "";
        var when = double.IsNaN(lead)
            ? "not playing"
            : $"ready at {ready:F2}s, {(lead >= 0 ? $"{lead:F2}s before" : $"{-lead:F2}s AFTER")} it is sung";

        Said.Line("align",
            $"{mark}line {line} at {from:F2}s: window {window:F1}s, took {took * 1000:F0}ms, " +
            $"{when}; sung {placed[0].From:F2}s to {ends:F2}s " +
            $"(starts {drift:+0.00;-0.00}s vs the sheet), {placed.Count} words");
    }

    /// <summary>Prints the running totals when a record is released. The totals for a whole song are
    /// the useful number, not the figures for any single line.</summary>
    private void Summarise()
    {
        if (_placed == 0)
            return;

        var tightest = _tightest == double.MaxValue ? "unknown" : $"{_tightest:F2}s";
        Said.Line("align", $"{_placed} lines placed: slowest {_slowest * 1000:F0}ms, " +
                        $"tightest lead {tightest}.");

        _placed = 0;
        _slowest = 0;
        _tightest = double.MaxValue;
    }

    /// <summary>
    /// The record as mono at the model's sample rate. It is decoded once and kept while the record plays.
    ///
    /// <para>Decoding is the slow part, and a record has many lines. The samples are kept rather than read
    /// again for each line. Four minutes at 16 kHz is about 15 MB, which is small next to the model's own
    /// memory use. Slicing an array is cheap, where seeking in a file for every line is not.</para>
    ///
    /// <para>Only one record is held. The previous one is dropped as soon as another is asked for, because
    /// a record that is not playing does not need its words followed.</para>
    /// </summary>
    private float[]? Mono(string path, CancellationToken ct)
    {
        if (string.Equals(path, _path, StringComparison.Ordinal) && _mono is not null)
            return _mono;

        Summarise();

        lock (_lines)
        {
            _lines.Clear();
            _doubted.Clear();
            _path = null;
            _mono = null;
        }

        var mono = PcmDecoder.DecodeMono(path, OnnxAligner.SampleRate, ct);
        if (mono.Length == 0)
            return null;

        lock (_lines)
        {
            _path = path;
            _mono = mono;
        }
        return mono;
    }

    /// <summary>Lets go of the record in hand. This is called when the words leave the screen. The audio is
    /// the largest item held here, so it is released when no view is showing it.</summary>
    public void Release()
    {
        Summarise();

        lock (_lines)
        {
            _lines.Clear();
            _doubted.Clear();
            _path = null;
            _mono = null;
        }
    }

    public void Dispose()
    {
        Release();
        _model.Dispose();
        _gate.Dispose();
    }
}
