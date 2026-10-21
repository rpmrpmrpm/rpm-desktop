using System.Globalization;
using RPM.Core.Audio;
using RPM.Core.Lyrics;
using RPM.Onnx;

namespace LyricBench;

/// <summary>
/// Scores the lyric aligner on real records, so a change to it (especially new weights) can be
/// compared with the version it replaces.
///
/// <para>There is no ground truth for this. No public corpus of pop records has word timings, and
/// creating one by hand is the work the aligner exists to avoid. So everything measured here is
/// something the sheet already knows (a line does not run past where the next one starts), something
/// the model reports about itself (how confident it was, how much of the window it spent on blank),
/// or something a reader would see. None of them is accuracy, but together they change when the
/// aligner gets better or worse, which is what a bench needs.</para>
///
/// <para><b>--against is the closest thing to a reference.</b> Give it a second recording of the same
/// take (an acapella, or another master), and the same words are placed on both, so the two results
/// can be subtracted. That is not accuracy either: it is REPEATABILITY. It matters because a threshold
/// tuned finer than the aligner's own spread is fitting noise. On Aaliyah's "Are You That Somebody?"
/// against its acapella, the spread is about a third of a second, and the acapella scored no better
/// than the album version. So separating the vocals before alignment does not help.</para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(Usage);
            return 0;
        }

        var models = Arg(args, "--models")
                     ?? Path.Combine("..", "..", "RPM.Desktop", "Assets", "OnnxModels");

        var placement = new Placement(models);
        if (!placement.IsAvailable)
        {
            Console.Error.WriteLine($"no weights under {models} — run scripts/fetch-models.sh");
            return 1;
        }

        var pairs = Pairs(args);
        if (pairs.Count == 0)
        {
            Console.Error.WriteLine("nothing to score: give --audio and --lrc, or --manifest");
            return 1;
        }

        if (args.Contains("--voice"))
            return Voice.Run(placement, pairs, args.Contains("--quiet"));

        if (args.Contains("--offset"))
            return Offset.Run(placement, pairs, Arg(args, "--move"), Arg(args, "--cache"));

        if (Arg(args, "--words") is { } range)
            return Words(placement, pairs[0], range);

        if (Arg(args, "--against") is { } reference)
            return Against(placement, pairs[0], reference, Arg(args, "--shift"));

        var quiet = args.Contains("--quiet");
        var total = new Tally();

        foreach (var (audio, lrc) in pairs)
        {
            if (Score(placement, audio, lrc) is not { } one)
                continue;

            total.Add(one);
            if (!quiet)
                Console.WriteLine(one.Row(Path.GetFileNameWithoutExtension(audio)));
        }

        Console.WriteLine();
        Console.WriteLine(total.Report());
        return 0;
    }

    private const string Usage =
        """
        lyricbench --audio <record> --lrc <sheet> [options]
        lyricbench --manifest <file> [options]

          --audio     A record to place the sheet's words against.
          --lrc       Its sheet. Synced — an unsynced one has nothing to place.
          --manifest  Lines of "<record>TAB<sheet>", for scoring a set in one go. Blank lines and
                      lines opening with # are skipped.
          --models    Folder holding wav2vec2.onnx and its vocab (default: RPM.Desktop's assets).
                      The point of the flag: put a new set of weights somewhere and score them
                      against the shipped ones.
          --against   A second recording of the SAME take — an acapella, or another master. The
                      words are placed off both and the answers subtracted, which says how much
                      this aligner can tell apart on this material.
          --shift     Where --against sits in the record's clock, in seconds. Worked out by
                      correlation when not given, and printed either way.
          --words     Dump every word of these lines rather than the summary, e.g. 8-19.
          --voice     Score the model as a VOICE DETECTOR rather than as an aligner: sweep the whole
                      record for the blank's probability and set it against the breaks the sheet's
                      own wordless lines mark. A different question from the rest of this bench,
                      and the only one here with outside ground truth. See Voice.
          --offset    Find how far the whole sheet is out of time with the record (SheetOffset), as
                      the transcoder does before it places the lines.
          --move      With --offset: move every stamp of the sheet by these seconds first, e.g.
                      -8,-3,0,3,8. The search should find minus each amount. See Offset.
          --cache     With --offset: a folder to keep each record's reading in, so a second run
                      does not hear it again.
          --quiet     Totals only, no per-record lines.

        Nothing here writes anything. It reads records, prints numbers, and exits.
        """;

    // ── the scorecard ──

    /// <summary>
    /// The results for one record, or for a set of records.
    ///
    /// <para>Three groups, for three questions. <b>Does the sweep follow the singer</b>: the overshoot
    /// past the next line's stamp, which is the sheet's own limit and the only external fact
    /// available. <b>How confident was the model</b>: its confidence, and how much of the window the
    /// path spent on blank; new weights would change these two. <b>What errors remain</b>: words split
    /// by a hole, which neither a better window nor a better sheet can fix.</para>
    /// </summary>
    private sealed class Tally
    {
        public int Records, Lines, Words, Late, Pinned, Holed, Parked;
        public double WorstLate, Overshoot, Drift, Score, Blank, Frames, Hole, Slowest;

        public void Add(Tally other)
        {
            Records += Math.Max(1, other.Records);
            Lines += other.Lines;
            Words += other.Words;
            Late += other.Late;
            Pinned += other.Pinned;
            Holed += other.Holed;
            Parked += other.Parked;
            WorstLate = Math.Max(WorstLate, other.WorstLate);
            Slowest = Math.Max(Slowest, other.Slowest);
            Overshoot += other.Overshoot;
            Drift += other.Drift;
            Score += other.Score;
            Blank += other.Blank;
            Frames += other.Frames;
            Hole += other.Hole;
        }

        public string Row(string name) =>
            $"  {Trim(name, 36),-36} {Lines,4} lines {Words,5} words  " +
            $"late {Late,3} (worst {WorstLate,5:F2}s)  holes {Holed,3}  " +
            $"score {Score / Math.Max(1, Lines),6:F2}  blank {100 * Blank / Math.Max(1, Frames),3:F0}%";

        public string Report()
        {
            var lines = Math.Max(1, Lines);
            var words = Math.Max(1, Words);

            return $"""
                {Records} records, {Lines} lines, {Words} words

                does the sweep track the singer
                   past the next line's stamp by over 0.3s   {Late} lines ({100.0 * Late / lines:F0}%), worst {WorstLate:F2}s
                   mean overshoot                            {Overshoot / lines:F2}s
                   mean |first word vs the sheet's stamp|    {Drift / lines:F2}s
                   first word pinned to the window's start   {Pinned} lines ({100.0 * Pinned / lines:F0}%)

                what the model made of it
                   mean token score                          {Score / lines:F2}
                   frames the path spent on blank            {100 * Blank / Math.Max(1, Frames):F0}%
                   words split by a hole over {Placement.Hole:F1}s          {Holed} ({100.0 * Holed / words:F1}%)
                   mean such hole                            {Hole / Math.Max(1, Holed):F2}s
                   parked: a word on a hold, the rest crushed {Parked} lines ({100.0 * Parked / lines:F1}%), which the app drops

                how it runs
                   slowest line placed                       {Slowest * 1000:F0} ms
                """;
        }
    }

    private static Tally? Score(Placement placement, string audio, string lrc)
    {
        var sheet = Read(lrc);
        var mono = sheet is null ? [] : PcmDecoder.DecodeMono(audio, OnnxAligner.SampleRate);
        if (sheet is null || mono.Length == 0)
        {
            Console.Error.WriteLine($"skipped {Path.GetFileName(audio)}: no synced sheet, or it would not decode");
            return null;
        }

        var tally = new Tally { Records = 1 };
        for (var i = 0; i < sheet.Lines.Count; i++)
        {
            var text = sheet.Lines[i].Text ?? "";
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var from = sheet.Lines[i].Seconds;
            var until = i + 1 < sheet.Lines.Count ? sheet.Lines[i + 1].Seconds : double.PositiveInfinity;
            if (placement.Place(mono, text, from, until) is not { } placed)
                continue;

            tally.Lines++;
            tally.Words += placed.Words.Length;
            tally.Score += placed.Score;
            tally.Blank += placed.Frames - placed.OnTokens;
            tally.Frames += placed.Frames;
            tally.Holed += placed.Holed;
            if (LyricDoubt.Parked(placed.Words))
                tally.Parked++;
            tally.Hole += placed.Holed > 0 ? placed.WorstHole : 0;
            tally.Slowest = Math.Max(tally.Slowest, placed.Took);

            var first = placed.Words[0].From;
            tally.Drift += Math.Abs(first - from);

            // Placed on the very first frame of the window: the model has not found when the word
            // begins, only that it is not later than this. Counted separately from a real placement.
            if (first - placed.Window.Opens < 0.1)
                tally.Pinned++;

            if (double.IsFinite(until))
            {
                var over = placed.Words[^1].To - until;
                tally.Overshoot += Math.Max(0, over);
                tally.WorstLate = Math.Max(tally.WorstLate, over);
                if (over > 0.3)
                    tally.Late++;
            }
        }

        return tally;
    }

    // ── the same take, twice ──

    private static int Against(
        Placement placement, (string Audio, string Lrc) pair, string reference, string? given)
    {
        var sheet = Read(pair.Lrc);
        var mono = PcmDecoder.DecodeMono(pair.Audio, OnnxAligner.SampleRate);
        var other = PcmDecoder.DecodeMono(reference, OnnxAligner.SampleRate);
        if (sheet is null || mono.Length == 0 || other.Length == 0)
        {
            Console.Error.WriteLine("one of the two records would not decode, or there is no synced sheet");
            return 1;
        }

        var shift = given is not null ? double.Parse(given, CultureInfo.InvariantCulture) : Lag(mono, other);
        Console.WriteLine($"{Path.GetFileName(reference)} sits at {shift:+0.00;-0.00}s in " +
                          $"{Path.GetFileName(pair.Audio)}'s clock" +
                          (given is null ? " (found by correlation)" : ""));

        int lines = 0, words = 0, far = 0, veryFar = 0, holedHere = 0, holedThere = 0;
        double sumAbs = 0, sumSigned = 0, scoreHere = 0, scoreThere = 0;

        for (var i = 0; i < sheet.Lines.Count; i++)
        {
            var text = sheet.Lines[i].Text ?? "";
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var from = sheet.Lines[i].Seconds;
            var until = i + 1 < sheet.Lines.Count ? sheet.Lines[i + 1].Seconds : double.PositiveInfinity;

            var here = placement.Place(mono, text, from, until);

            // The reference has its own clock: the stamps are shifted on the way in and the result is
            // shifted on the way out, so both are compared in the same clock.
            var there = placement.Place(
                other, text, from - shift, double.IsFinite(until) ? until - shift : until, shift);

            if (here is not { } a || there is not { } b || a.Words.Length != b.Words.Length)
                continue;

            lines++;
            scoreHere += a.Score;
            scoreThere += b.Score;
            holedHere += a.Holed;
            holedThere += b.Holed;

            for (var w = 0; w < a.Words.Length; w++)
            {
                var apart = a.Words[w].From - b.Words[w].From;
                words++;
                sumAbs += Math.Abs(apart);
                sumSigned += apart;
                if (Math.Abs(apart) > 0.3)
                    far++;
                if (Math.Abs(apart) > 1.0)
                    veryFar++;
            }
        }

        if (words == 0)
        {
            Console.Error.WriteLine("nothing lined up between the two — is it the same take?");
            return 1;
        }

        Console.WriteLine($"""

            {lines} lines, {words} words placed off both

               mean |apart|             {sumAbs / words:F2}s
               bias                     {sumSigned / words:+0.00;-0.00}s   (a bias is the offset being off, not the aligner)
               apart by over 0.3s       {far} ({100.0 * far / words:F0}%)
               apart by over 1s         {veryFar} ({100.0 * veryFar / words:F0}%)

               mean token score         {scoreHere / lines:F2} here, {scoreThere / lines:F2} on the reference
               words split by a hole    {holedHere} here, {holedThere} on the reference

            The spread above is what this aligner can tell apart on this material. A threshold tuned
            finer than it is fitting noise. If the reference is a cleaner recording of the same voice
            and scores no better, the aligner is not being held back by the recording.
            """);
        return 0;
    }

    /// <summary>
    /// The offset of one recording in the other's clock.
    ///
    /// <para>Correlated on short-time ENERGY, not on the waveform. Two masters of one take are
    /// different mixes and their phase can differ completely (an acapella does not cancel against its
    /// own mix), but the voice starts and stops at the same times in both, and that is all this needs
    /// to find.</para>
    /// </summary>
    private static double Lag(float[] a, float[] b)
    {
        const int Hop = 160;            // 10 ms
        const int Reach = 10 * 100;     // ten seconds either way, in hops

        var here = Envelope(a);
        var there = Envelope(b);

        var best = double.NegativeInfinity;
        var at = 0;

        for (var lag = -Reach; lag <= Reach; lag++)
        {
            double dot = 0;
            var n = 0;
            for (var i = 0; i < there.Length; i++)
            {
                var j = i + lag;
                if (j < 0 || j >= here.Length)
                    continue;
                dot += there[i] * here[j];
                n++;
            }

            // Not enough overlap to be meaningful: otherwise a lag that puts one past the end of the
            // other could win on a few loud frames.
            if (n < there.Length / 2)
                continue;

            if (dot / n > best)
            {
                best = dot / n;
                at = lag;
            }
        }

        return at * Hop / (double)OnnxAligner.SampleRate;

        static double[] Envelope(float[] x)
        {
            var e = new double[x.Length / Hop];
            for (var i = 0; i < e.Length; i++)
            {
                double sum = 0;
                for (var j = i * Hop; j < (i + 1) * Hop; j++)
                    sum += x[j] * x[j];

                // Logarithm, so the correlation measures the shape of the performance, not which
                // master is louder.
                e[i] = Math.Log(Math.Sqrt(sum / Hop) + 1e-6);
            }

            double mean = 0;
            foreach (var v in e)
                mean += v;
            mean /= Math.Max(1, e.Length);

            for (var i = 0; i < e.Length; i++)
                e[i] -= mean;

            return e;
        }
    }

    // ── one line, spelled out ──

    private static int Words(Placement placement, (string Audio, string Lrc) pair, string range)
    {
        var sheet = Read(pair.Lrc);
        var mono = sheet is null ? [] : PcmDecoder.DecodeMono(pair.Audio, OnnxAligner.SampleRate);
        if (sheet is null || mono.Length == 0)
        {
            Console.Error.WriteLine("no synced sheet, or the record would not decode");
            return 1;
        }

        var dash = range.IndexOf('-');
        var lo = int.Parse(dash < 0 ? range : range[..dash], CultureInfo.InvariantCulture);
        var hi = dash < 0 ? lo : int.Parse(range[(dash + 1)..], CultureInfo.InvariantCulture);

        for (var i = Math.Max(0, lo); i <= hi && i < sheet.Lines.Count; i++)
        {
            var text = sheet.Lines[i].Text ?? "";
            var from = sheet.Lines[i].Seconds;
            var until = i + 1 < sheet.Lines.Count ? sheet.Lines[i + 1].Seconds : double.PositiveInfinity;

            if (string.IsNullOrWhiteSpace(text))
            {
                Console.WriteLine($"[{i,3}] {from,8:F2}  (break)");
                continue;
            }

            Console.WriteLine();
            Console.WriteLine($"[{i,3}] stamp {from,8:F2}  next {until,8:F2}   {text}");

            if (placement.Place(mono, text, from, until) is not { } placed)
            {
                Console.WriteLine("      not placed");
                continue;
            }

            Console.WriteLine($"      window [{placed.Window.Opens:F2}, {placed.Wall:F2}]  " +
                              $"score {placed.Score:F2}  {placed.Took * 1000:F0} ms" +
                              (LyricDoubt.Parked(placed.Words) ? "  PARKED — the app drops this line" : ""));

            var row = "      ";
            foreach (var word in placed.Words)
            {
                var held = word.To - word.From;

                // ! too short to have been sung; ~ long enough to be a held note or a hole.
                var mark = held < 0.15 ? "!" : held > 1.0 ? "~" : " ";

                row += $"{text.Substring(word.Start, word.Length)}[{word.From:F2}-{word.To:F2}]{mark} ";
                if (row.Length > 96)
                {
                    Console.WriteLine(row);
                    row = "      ";
                }
            }

            if (row.Trim().Length > 0)
                Console.WriteLine(row);
        }

        return 0;
    }

    // ── odds and ends ──

    private static LyricSheet? Read(string lrc)
    {
        try
        {
            var sheet = LyricSheet.Parse(File.ReadAllText(lrc));
            return sheet.IsSynced ? sheet : null;
        }
        catch
        {
            return null;   // a record with an unreadable sheet is skipped; the run does not stop
        }
    }

    private static List<(string Audio, string Lrc)> Pairs(string[] args)
    {
        var pairs = new List<(string, string)>();

        if (Arg(args, "--manifest") is { } manifest && File.Exists(manifest))
        {
            foreach (var line in File.ReadAllLines(manifest))
            {
                if (line.Length == 0 || line[0] == '#')
                    continue;

                var parts = line.Split('\t', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                    pairs.Add((parts[0], parts[1]));
            }
        }

        if (Arg(args, "--audio") is { } audio && Arg(args, "--lrc") is { } lrc)
            pairs.Add((audio, lrc));

        return pairs;
    }

    private static string Trim(string text, int width) =>
        text.Length <= width ? text : text[..(width - 1)] + "…";

    private static string? Arg(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
