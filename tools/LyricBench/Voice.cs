using RPM.Core.Audio;
using RPM.Core.Lyrics;
using RPM.Onnx;

namespace LyricBench;

/// <summary>
/// Measures whether the aligner's model can be used as a voice detector, before any feature depends
/// on it.
///
/// <para><b>The question.</b> The DJ needs to know where someone is singing on every record, so that a
/// changeover never plays two voices at once. Word timings give that exactly, but only where LRCLIB has
/// a sheet, which is a minority of a library. wav2vec2 is already shipped and its CTC output has a
/// blank ("nothing is said in this frame"), so running it over the whole record could give the same
/// answer for every record with no new weights and no sheet. But it is a SPEECH model used on singing
/// with instruments, so this is measured, not assumed.</para>
///
/// <para><b>The ground truth.</b> Unlike the rest of this bench, this question has an external
/// answer written by a person: an LRC sheet marks its instrumental parts with a note character or an
/// empty line, and <see cref="LyricSheet.IsWordless"/> reads both. So from a wordless line's stamp to
/// the next line's stamp is a range where a person said nobody sings, and the seconds just after a
/// worded line's stamp are a range where a person said someone sings. Neither depends on this app's
/// own results.</para>
///
/// <para><b>What is reported.</b> Separation, as the probability that a random instrumental frame
/// scores emptier than a random sung one (0.5 is random, 1.0 is perfect), at the model's own 20 ms
/// and again smoothed, because the mix rule reads a bar, not a frame, and a curve that is noisy per
/// frame can still be correct per phrase. Then the best single threshold and its error on each side,
/// because a real threshold would be set from that number.</para>
/// </summary>
internal static class Voice
{
    /// <summary>How much audio goes through the model at once. wav2vec2's attention is quadratic in the
    /// window length, so a whole record in one call is not possible. The front end also needs a few
    /// frames of context, so the chunks overlap and only their middles are kept.</summary>
    private const double ChunkSeconds = 20;

    /// <summary>How much of each chunk is context, removed from both ends before the frames are kept.</summary>
    private const double EdgeSeconds = 0.5;

    /// <summary>How far on each side of a stamp is not trusted. Hand-written stamps can be one beat
    /// wrong, and a singer often starts before one (the same error that <see cref="LyricWindow.Slack"/>
    /// allows for). Ground truth is trimmed by this at both ends so the two classes never touch.</summary>
    private const double Slop = 0.5;

    /// <summary>The longest part of a sung line that is counted as sung. A line's stamp gives its start
    /// but not its end, so only the start is used: a singer is certainly singing just after the stamp
    /// of their line, and may have stopped before the end of it.</summary>
    private const double SungHead = 2.5;

    /// <summary>The shortest instrumental range that is counted. Under a couple of bars, a wordless line
    /// is as likely to mark a breath as an instrumental break.</summary>
    private const double LeastBreak = 4.0;

    /// <summary>The windows the curve is averaged over before it is scored again. The first is the
    /// model's own; the others are about the length of a bar and of a phrase, which is what a mix
    /// uses.</summary>
    private static readonly double[] Smooths = [0, 2, 8];

    public static int Run(Placement placement, IReadOnlyList<(string Audio, string Lrc)> pairs, bool quiet)
    {
        if (placement.Vocabulary is not { } alphabet)
        {
            Console.Error.WriteLine("no alphabet beside the weights, so no blank to read");
            return 1;
        }

        var sung = new List<float>[Smooths.Length];
        var quietOnes = new List<float>[Smooths.Length];
        for (var s = 0; s < Smooths.Length; s++)
        {
            sung[s] = [];
            quietOnes[s] = [];
        }

        var records = 0;
        double seconds = 0, took = 0;

        foreach (var (audio, lrc) in pairs)
        {
            LyricSheet sheet;
            try
            {
                sheet = LyricSheet.Parse(File.ReadAllText(lrc));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{Path.GetFileName(lrc)}: {ex.GetType().Name}");
                continue;
            }

            if (!sheet.IsSynced)
                continue;

            var truth = Truth(sheet);
            if (truth.Sung.Count == 0 || truth.Quiet.Count == 0)
                continue;                       // a sheet with no marked breaks gives no ground truth

            float[] mono;
            try
            {
                mono = PcmDecoder.DecodeMono(audio, OnnxAligner.SampleRate);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"{Path.GetFileName(audio)}: {ex.GetType().Name}");
                continue;
            }

            if (mono.Length < OnnxAligner.SampleRate)
                continue;

            var began = System.Diagnostics.Stopwatch.GetTimestamp();
            var curve = Sweep(placement, mono, alphabet.Blank);
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalSeconds;
            if (curve.Length == 0)
                continue;

            records++;
            seconds += (double)mono.Length / OnnxAligner.SampleRate;
            took += elapsed;

            var step = placement.Seconds;
            double? shown = null;

            for (var s = 0; s < Smooths.Length; s++)
            {
                var read = Smooths[s] <= 0 ? curve : Smooth(curve, (int)Math.Round(Smooths[s] / step));
                var mine = Gather(read, step, truth.Sung);
                var theirs = Gather(read, step, truth.Quiet);
                sung[s].AddRange(mine);
                quietOnes[s].AddRange(theirs);
                shown ??= Separation(mine, theirs);
            }

            if (!quiet)
            {
                var perBar = Smooth(curve, (int)Math.Round(2 / step));
                Console.WriteLine(
                    $"{Path.GetFileNameWithoutExtension(audio),-44} " +
                    $"{truth.Sung.Count,3} sung {truth.Quiet.Count,3} break   " +
                    $"sep {Separation(Gather(perBar, step, truth.Sung), Gather(perBar, step, truth.Quiet)):F3}   " +
                    $"{elapsed:F1}s for {mono.Length / (double)OnnxAligner.SampleRate / 60:F1} min");
            }
        }

        if (records == 0)
        {
            Console.Error.WriteLine("no record had both a marked break and a sung line");
            return 1;
        }

        Console.WriteLine();
        Console.WriteLine($"{records} records, {seconds / 60:F0} min of audio swept in {took / 60:F1} min " +
                          $"({seconds / Math.Max(took, 1e-6):F1}x real time)");
        Console.WriteLine();
        Console.WriteLine("  smoothed   frames sung   frames break   mean blank sung   mean blank break   separation   best cut");

        for (var s = 0; s < Smooths.Length; s++)
        {
            var (cut, gets) = BestCut(sung[s], quietOnes[s]);
            Console.WriteLine(
                $"  {(Smooths[s] <= 0 ? "20 ms" : $"{Smooths[s]:F0} s"),-9}" +
                $"{sung[s].Count,13}{quietOnes[s].Count,15}" +
                $"{Mean(sung[s]),18:F3}{Mean(quietOnes[s]),19:F3}" +
                $"{Separation(sung[s], quietOnes[s]),13:F3}" +
                $"   {cut:F2} ({gets:P0} right)");
        }

        return 0;
    }

    // ── what the sheet says ──

    /// <summary>
    /// The ranges a person wrote down: where someone is singing, and where nobody is.
    ///
    /// <para>Both are trimmed by <see cref="Slop"/> at each end and neither may reach the other,
    /// because a stamp is hand-written and can be one beat wrong. Everything uncertain is left out
    /// (the rest of a sung line after its start, and the end of the record after the last stamp), so
    /// the ground truth is smaller but correct.</para>
    /// </summary>
    private static (List<(double From, double To)> Sung, List<(double From, double To)> Quiet) Truth(
        LyricSheet sheet)
    {
        List<(double, double)> sung = [], quiet = [];

        for (var i = 0; i < sheet.Lines.Count; i++)
        {
            var line = sheet.Lines[i];
            var next = i + 1 < sheet.Lines.Count ? sheet.Lines[i + 1].Seconds : double.NaN;
            if (double.IsNaN(next) || next <= line.Seconds)
                continue;                       // the last line, or a sheet that is not in time order

            if (LyricSheet.IsWordless(line.Text))
            {
                // A marked break. All of it counts, except the slop at each end.
                var from = line.Seconds + Slop;
                var to = next - Slop;
                if (to - from >= LeastBreak)
                    quiet.Add((from, to));
            }
            else
            {
                // A sung line. Only its start: nothing gives its end.
                var from = line.Seconds + Slop;
                var to = Math.Min(line.Seconds + SungHead, next - Slop);
                if (to - from >= 0.5)
                    sung.Add((from, to));
            }
        }

        return (sung, quiet);
    }

    // ── what the model says ──

    /// <summary>
    /// How empty the model finds every 20 ms of the record: the blank's own probability, zero where it
    /// detects a letter and one where it detects nothing.
    /// </summary>
    private static float[] Sweep(Placement placement, float[] mono, int blank)
    {
        var chunk = (int)(ChunkSeconds * OnnxAligner.SampleRate);
        var edge = (int)(EdgeSeconds * OnnxAligner.SampleRate);
        var perFrame = OnnxAligner.SamplesPerFrame;
        var frames = mono.Length / perFrame;
        var curve = new float[frames];
        var written = new bool[frames];

        for (var at = 0; at < mono.Length; at += chunk)
        {
            // Read with context on each side, and keep only the middle: the model's front end needs
            // context that it does not have at the very start of a window, and a join every twenty
            // seconds would look like a voice starting.
            var from = Math.Max(0, at - edge);
            var to = Math.Min(mono.Length, at + chunk + edge);
            if (to - from < OnnxAligner.SampleRate / 5)
                break;

            if (placement.Read(mono.AsSpan(from, to - from)) is not { } read)
                continue;

            // Where the model's own frames are in the record: its stride is fixed, so frame f of this
            // window is sample from + f * perFrame.
            var offset = from / perFrame;
            for (var f = 0; f < read.Frames; f++)
            {
                var frame = offset + f;
                if (frame < 0 || frame >= frames)
                    continue;

                var sample = frame * perFrame;
                if (sample < at || sample >= at + chunk)
                    continue;                   // context, belongs to a neighbouring chunk

                curve[frame] = Blank(read.Scores.AsSpan(f * read.Vocab, read.Vocab), blank);
                written[frame] = true;
            }
        }

        // A frame with no value is one the model failed on, which is not silence. It takes the value
        // of the frame before; zero would read as a voice.
        for (var f = 0; f < frames; f++)
            if (!written[f])
                curve[f] = f > 0 ? curve[f - 1] : 1f;

        return curve;
    }

    /// <summary>The blank's probability in one frame, from a softmax over the model's logits.</summary>
    private static float Blank(ReadOnlySpan<float> logits, int blank)
    {
        var most = float.NegativeInfinity;
        foreach (var v in logits)
            most = Math.Max(most, v);

        double sum = 0;
        for (var i = 0; i < logits.Length; i++)
            sum += Math.Exp(logits[i] - most);

        return sum > 0 ? (float)(Math.Exp(logits[blank] - most) / sum) : 1f;
    }

    /// <summary>The curve averaged over a window, so it can be scored at the length a mix uses, not at
    /// the model's own.</summary>
    private static float[] Smooth(float[] curve, int width)
    {
        if (width <= 1 || curve.Length == 0)
            return curve;

        var half = width / 2;
        var run = new double[curve.Length + 1];
        for (var i = 0; i < curve.Length; i++)
            run[i + 1] = run[i] + curve[i];

        var smoothed = new float[curve.Length];
        for (var i = 0; i < curve.Length; i++)
        {
            var from = Math.Max(0, i - half);
            var to = Math.Min(curve.Length, i + half + 1);
            smoothed[i] = (float)((run[to] - run[from]) / (to - from));
        }

        return smoothed;
    }

    /// <summary>Every value of the curve that is inside one of these ranges.</summary>
    private static List<float> Gather(
        float[] curve, double step, IReadOnlyList<(double From, double To)> spans)
    {
        var found = new List<float>();
        foreach (var (from, to) in spans)
        {
            var first = Math.Max(0, (int)Math.Ceiling(from / step));
            var last = Math.Min(curve.Length - 1, (int)Math.Floor(to / step));
            for (var i = first; i <= last; i++)
                found.Add(curve[i]);
        }
        return found;
    }

    // ── the score ──

    /// <summary>
    /// The probability that a range where nobody sings scores emptier than one where someone sings:
    /// the area under the ROC curve, computed by ranking, not by trying thresholds.
    ///
    /// <para>0.5 is random: the model gives no information. 1.0 is perfect separation. Ties count 0.5
    /// each, which matters here because a quantised model puts many frames at exactly 1.0.</para>
    /// </summary>
    private static double Separation(List<float> sung, List<float> quiet)
    {
        if (sung.Count == 0 || quiet.Count == 0)
            return double.NaN;

        var all = new (float Value, bool Quiet)[sung.Count + quiet.Count];
        for (var i = 0; i < sung.Count; i++)
            all[i] = (sung[i], false);
        for (var i = 0; i < quiet.Count; i++)
            all[sung.Count + i] = (quiet[i], true);

        Array.Sort(all, (a, b) => a.Value.CompareTo(b.Value));

        // Mann-Whitney: the rank sum of the quiet frames, with ties sharing a mid-rank.
        double rankSum = 0;
        for (var i = 0; i < all.Length;)
        {
            var j = i;
            while (j + 1 < all.Length && all[j + 1].Value.Equals(all[i].Value))
                j++;

            var rank = (i + j) / 2.0 + 1;
            for (var k = i; k <= j; k++)
                if (all[k].Quiet)
                    rankSum += rank;

            i = j + 1;
        }

        var n = (double)quiet.Count;
        return (rankSum - n * (n + 1) / 2) / (n * sung.Count);
    }

    /// <summary>The single threshold that classifies the most frames correctly, and how many that is.
    /// A real threshold would be set from this, and it shows most simply whether this is usable.</summary>
    private static (double Cut, double Right) BestCut(List<float> sung, List<float> quiet)
    {
        if (sung.Count == 0 || quiet.Count == 0)
            return (double.NaN, double.NaN);

        double best = 0, cut = 0;
        for (var t = 0.0; t <= 1.0; t += 0.01)
        {
            var right = 0;
            foreach (var v in sung)
                if (v < t) right++;             // sung frames score low, so below the threshold
            foreach (var v in quiet)
                if (v >= t) right++;

            var share = right / (double)(sung.Count + quiet.Count);
            if (share > best)
                (best, cut) = (share, t);
        }

        return (cut, best);
    }

    private static double Mean(List<float> values)
    {
        if (values.Count == 0)
            return double.NaN;

        double sum = 0;
        foreach (var v in values)
            sum += v;
        return sum / values.Count;
    }
}
