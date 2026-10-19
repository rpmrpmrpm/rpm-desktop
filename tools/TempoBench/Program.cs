using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using RPM.Core.Analysis;
using RPM.Core.Audio;
using RPM.Onnx;

namespace TempoBench;

/// <summary>
/// Runs RPM's analyser over a public tempo corpus and reports how often it agrees with the corpus,
/// on the two measures that every published tempo table uses.
///
/// <para>The goal is not only the accuracy number, but a number that can be compared with the
/// nineteen systems in tempo_eval's report for the same corpus and the same reference version. So
/// nothing here is custom: the annotations come from a tempo_eval checkout, the tolerance is
/// tempo_eval's, and the analyser and weights are the ones the app ships.</para>
///
/// <para>Each track is read twice from a single pass of the model: once as the app would store it,
/// and once as the model's own beats before RPM fits a grid to them. The model is Beat This, so only
/// the difference between the two columns comes from RPM.</para>
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        // The percentages are compared with a published table, so they are printed in the same
        // format, whatever the machine's culture settings are.
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine(
                """
                tempobench --corpus <name> --audio <dir> --refs <dir> [options]

                  --corpus   Corpus name as tempo_eval spells it (gtzan, ballroom, ...).
                  --audio    Folder of audio files, searched recursively.
                  --refs     A tempo_eval checkout's annotations/references folder.
                  --version  Reference annotation version (default: gtzan 1.0, ballroom 2.0).
                  --models   Folder holding beat_this.onnx (default: RPM.Desktop's assets).
                  --jobs     Tracks decoded at once (default 4). The model reads one at a time.
                  --limit    Stop after this many tracks, for a quick look.
                  --out      Where the per-track CSV goes (default bench/out).
                  --cache    Folder to keep the model's per-frame curves in. The first run over a
                             corpus fills it and costs the usual 45 minutes; every run after it
                             reads the curves back and takes about a second, which is what makes a
                             threshold sweepable. Invalidated by a change to the weights or to the
                             spectrogram, never by a change to what is made of them.

                Environment:
                  RPM_BEAT_DECODER   peaks | beat | (unset, the bar search). What turns the curves
                                     into beats — the thing a cached run is for comparing.
                  RPM_BEAT_MODEL     fp32 | int8. Which weights to run.
                """);
            return 0;
        }

        string corpus = Arg(args, "--corpus") ?? "gtzan";
        string audioDir = Arg(args, "--audio") ?? throw new ArgumentException("--audio is required.");
        string refsDir = Arg(args, "--refs") ?? throw new ArgumentException("--refs is required.");
        string version = Arg(args, "--version") ?? DefaultVersion(corpus);
        string models = Arg(args, "--models") ?? DefaultModelDirectory();
        string outDir = Arg(args, "--out") ?? "bench/out";
        int jobs = int.TryParse(Arg(args, "--jobs"), out int j) ? j : 4;
        int limit = int.TryParse(Arg(args, "--limit"), out int l) ? l : int.MaxValue;

        var reference = References.Load(refsDir, corpus, version);
        Console.WriteLine($"{corpus}: {reference.Count} reference tempi at version {version}.");

        // Beat annotations, when the corpus has them. Without them the drift cannot be measured,
        // and a message says so instead of printing a table of zeroes.
        var annotated = References.LoadBeats(refsDir, corpus);
        Console.WriteLine(annotated.Count > 0
            ? $"{annotated.Count} of them carry annotated beats, so drift is measured too."
            : "No annotated beats in this corpus: tempo only, no drift.");

        var files = Directory
            .EnumerateFiles(audioDir, "*", SearchOption.AllDirectories)
            .Where(f => Audio.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => reference.ContainsKey(Path.GetFileNameWithoutExtension(f)))
            .OrderBy(f => f, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        Console.WriteLine($"{files.Count} of them have audio under {audioDir}.");
        if (files.Count == 0)
            return 1;

        using var model = new OnnxBeatModel(models);
        if (!model.IsAvailable)
        {
            // Without the weights this would measure the DSP fallback with no warning, which is not
            // what the app does and not what the number is for.
            Console.Error.WriteLine($"No beat model in {models}. Run scripts/fetch-models.sh.");
            return 1;
        }

        // The model's output, kept between runs. Everything this benchmark compares reads only those
        // two curves, and producing them takes nearly all of the run time. So with a cache, only the
        // first run is slow and every later run is only computation.
        string? cacheRoot = Arg(args, "--cache");
        var cache = cacheRoot is null ? null : new LogitCache(cacheRoot, models);
        var reader = BeatModelDecoder.Chosen(Environment.GetEnvironmentVariable("RPM_BEAT_DECODER"));
        if (cacheRoot is not null)
            Console.WriteLine($"Reading the model's curves from {cacheRoot}, filling it where they are not there yet.");

        var readings = new ConcurrentBag<Reading>();
        var failures = new ConcurrentBag<string>();
        int done = 0;
        var clock = Stopwatch.StartNew();

        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = jobs }, file =>
        {
            var stem = Path.GetFileNameWithoutExtension(file);
            try
            {
                var track = Stopwatch.StartNew();
                var mono = PcmDecoder.DecodeMono(file, TrackAnalyzer.AnalysisRate);
                if (mono.Length == 0)
                {
                    failures.Add($"{stem}: would not decode");
                    return;
                }

                // One pass of the model, read twice. Passing TrackAnalyzer a plain OnnxBeatModel
                // would run the weights a second time to get the same output.
                //
                // With --cache, not even one pass: the model's curves for this track are read from
                // disk and only the decoding is computed again. That reduces a forty-five-minute run
                // to about a second, so a threshold can be swept.
                var kept = new KeptReading(
                    cache is null ? model : new CachedBeatModel(model, cache, corpus, stem, reader));
                var analysis = TrackAnalyzer.Analyze(file, mono, TrackAnalyzer.AnalysisRate, kept);

                // The same beats fitted again with the fold off. This costs one computation pass and
                // no model time, because the model output is already available.
                double unfolded = kept.Last is { } read
                    ? ModelBeatGrid.Fit(read, (double)mono.Length / TrackAnalyzer.AnalysisRate).Bpm
                    : 0;

                var (beats, shortGaps, longGaps) = Gaps(kept.Last);
                var constant = ConstantFit.Of(kept.Last);
                double splitHalf = ConstantFit.SplitHalfPercent(kept.Last);

                // The three grids against the annotated beats, when there are any: what the app
                // stores, the same beats fitted to one tempo, and the model's own reading.
                annotated.TryGetValue(stem, out var truth);
                var fittedDrift = Drift.Of(analysis.BeatTimes, truth);
                var constantDrift = Drift.Of(ConstantGrid(constant, kept.Last), truth);
                var rawDrift = Drift.Of(kept.Last?.BeatTimes, truth);

                // The error a changeover would actually have, with the tempo read by both methods.
                // This pair shows whether the newer method helps on real music, not only on a
                // scattered synthetic grid.
                int changeoverBeats = 16 * Math.Max(1, analysis.BeatsPerBar);
                var barSpan = Drift.OverChangeover(
                    truth, at => analysis.BpmAt(at, analysis.BeatsPerBar), changeoverBeats);
                var changeoverSpan = Drift.OverChangeover(
                    truth, at => analysis.BpmAt(at, changeoverBeats), changeoverBeats);

                readings.Add(new Reading(
                    stem, reference[stem], analysis.Bpm, RawBpm(kept.Last), unfolded,
                    analysis.BpmConfidence, track.Elapsed.TotalSeconds, beats, shortGaps, longGaps,
                    constant.Bpm, constant.Scatter, constant.OnTheLine, constant.Merged,
                    fittedDrift, constantDrift, rawDrift, barSpan, changeoverSpan, splitHalf,
                    analysis.BeatsPerBar, analysis.DownbeatConfidence));
            }
            catch (Exception ex)
            {
                failures.Add($"{stem}: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                int at = Interlocked.Increment(ref done);
                if (at % 25 == 0)
                    Console.Write($"\r{at}/{files.Count} ({clock.Elapsed.TotalSeconds / at:F1}s each)   ");
            }
        });

        Console.WriteLine($"\r{done}/{files.Count} in {clock.Elapsed.TotalMinutes:F1} min.          ");
        if (cache is not null)
            Console.WriteLine($"  the model ran on {cache.Misses} tracks and was remembered on {cache.Hits}.");
        foreach (var failure in failures.Order())
            Console.WriteLine($"  skipped {failure}");

        var board = new Scoreboard(corpus);
        foreach (var r in readings.OrderBy(r => r.Stem, StringComparer.Ordinal))
            board.Add(r);

        Report(board, corpus, version, outDir);
        return 0;
    }

    /// <summary>
    /// Wraps the model so that the analyser's own call is the one that is read twice: the analyser
    /// gets its beats, and the run keeps a copy of the output.
    /// </summary>
    private sealed class KeptReading(IBeatModel inner) : IBeatModel
    {
        public BeatReading? Last { get; private set; }

        public BeatReading? Read(ReadOnlySpan<float> mono, int sampleRate, CancellationToken ct = default) =>
            Last = inner.Read(mono, sampleRate, ct);
    }

    /// <summary>
    /// The constant fit as a grid, so it can be measured against the annotated beats in the same way
    /// as the other two. Its phase is the model's first beat, which is the only phase it has.
    /// </summary>
    private static double[] ConstantGrid(ConstantFit.Result fit, BeatReading? reading)
    {
        if (fit.Bpm <= 0 || reading is not { BeatTimes.Count: > 0 })
            return [];

        double period = 60.0 / fit.Bpm;
        double first = reading.BeatTimes[0];
        double last = reading.BeatTimes[^1];

        var grid = new List<double>((int)((last - first) / period) + 2);
        for (double at = first; at <= last + period; at += period)
            grid.Add(at);

        return [.. grid];
    }

    /// <summary>How irregular the model's beat sequence is: how many gaps are too short to be one beat,
    /// and how many are long enough to have a missing beat inside them.</summary>
    private static (int Beats, int Short, int Long) Gaps(BeatReading? reading)
    {
        if (reading is null || reading.BeatTimes.Count < 4)
            return (reading?.BeatTimes.Count ?? 0, 0, 0);

        var gaps = new List<double>(reading.BeatTimes.Count - 1);
        for (int i = 1; i < reading.BeatTimes.Count; i++)
            gaps.Add(reading.BeatTimes[i] - reading.BeatTimes[i - 1]);

        var sorted = gaps.Order().ToArray();
        double median = sorted[sorted.Length / 2];
        if (median <= 0)
            return (reading.BeatTimes.Count, 0, 0);

        return (reading.BeatTimes.Count,
                gaps.Count(g => g < 0.6 * median),
                gaps.Count(g => g > 1.6 * median));
    }

    /// <summary>The model's own tempo: the median gap between its beats, which is what a tempo
    /// estimator built directly on the model would report.</summary>
    private static double RawBpm(BeatReading? reading)
    {
        if (reading is null || reading.BeatTimes.Count < 4)
            return 0;

        var gaps = new List<double>(reading.BeatTimes.Count - 1);
        for (int i = 1; i < reading.BeatTimes.Count; i++)
        {
            double gap = reading.BeatTimes[i] - reading.BeatTimes[i - 1];
            if (gap > 0)
                gaps.Add(gap);
        }

        if (gaps.Count == 0)
            return 0;

        gaps.Sort();
        double median = gaps[gaps.Count / 2];
        return median > 0 ? 60.0 / median : 0;
    }

    private static void Report(Scoreboard board, string corpus, string version, string outDir)
    {
        Console.WriteLine();
        Console.WriteLine($"{corpus} @ {version} — {board.Count} tracks, {board.Unread} with no tempo read");
        Console.WriteLine($"  {"",-22} {"Acc1",8} {"Acc2",9} {"Acc0",8}");
        Console.WriteLine(board.Row(Scoreboard.Fitted, "rpm (grid fitted)"));
        Console.WriteLine(board.Row(Scoreboard.Unfolded, "rpm, fold off"));
        Console.WriteLine(board.Row(Scoreboard.Constant, "one tempo, least sq"));
        Console.WriteLine(board.Row(Scoreboard.Raw, "beat model raw"));

        // Drift: the error a changeover would actually have. Shown next to the planner's limit,
        // because a number in milliseconds is not useful without it.
        if (board.Readings.Any(r => r.FittedDrift.IsUsable))
        {
            const double BudgetMs = 15;
            Console.WriteLine();
            Console.WriteLine($"  drift over an eight-bar changeover, against the {BudgetMs:F0} ms a beat-match allows");
            Console.WriteLine($"  {"",-22} {"median",9} {"90th",9} {"jitter",8} {"over budget",10} {"tracks",7}");
            Console.WriteLine(board.DriftRow(r => r.FittedDrift, "rpm (grid fitted)", BudgetMs));
            Console.WriteLine(board.DriftRow(r => r.ConstantDrift, "one tempo, least sq", BudgetMs));
            Console.WriteLine(board.DriftRow(r => r.RawDrift, "beat model raw", BudgetMs));

            Console.WriteLine();
            Console.WriteLine("  what a changeover inherits, by the span its tempo was read over");
            Console.WriteLine($"  {"",-22} {"median",9} {"worst",9} {"",8} {"over budget",10} {"tracks",7}");
            Console.WriteLine(board.DriftRow(r => r.BarSpan, "one bar (was)", BudgetMs));
            Console.WriteLine(board.DriftRow(r => r.ChangeoverSpan, "the changeover (is)", BudgetMs));
        }

        // How precisely the tempo can be read, with no reference: the two halves of a sequenced
        // record have the same tempo, so their difference is measurement error.
        var halves = board.Readings.Select(r => r.SplitHalf).Where(v => v > 0).Order().ToList();
        if (halves.Count > 0)
        {
            const double BudgetPercent = 0.05;
            Console.WriteLine();
            Console.WriteLine($"  the two halves of each record, read apart ({halves.Count} tracks)");
            Console.WriteLine($"    median   {halves[halves.Count / 2],8:F4} %   ({halves[halves.Count / 2] / 100 * 30 * 1000,6:F1} ms over a 30 s changeover)");
            Console.WriteLine($"    75th     {halves[(int)(0.75 * halves.Count)],8:F4} %");
            Console.WriteLine($"    90th     {halves[(int)(0.90 * halves.Count)],8:F4} %");
            Console.WriteLine($"    inside {BudgetPercent:F2} %: {(double)halves.Count(v => v <= BudgetPercent) / halves.Count,7:P1}");
        }

        // The bar. No corpus here annotates it, but it is still useful to see. This does not check
        // whether the bar length is right (nothing here can), only whether the reader reports a bar
        // at all and how consistently it places the "one". A bar phase agreement below 100% means
        // some bar lines are on a different beat of the bar than the others.
        var barred = board.Readings.Where(r => r.Fitted > 0).ToList();
        if (barred.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  the bar, as the grid reads it:");
            foreach (var g in barred.GroupBy(r => r.BeatsPerBar).OrderBy(g => g.Key))
                Console.WriteLine($"    {(g.Key == 0 ? "none found" : $"{g.Key} to the bar"),-16} {g.Count(),5}" +
                                  $"   bar phase agreed {g.Average(r => r.BarConfidence),6:P1}");

            Console.WriteLine($"    {"every bar line on the same beat of the bar",-42} " +
                              $"{(double)barred.Count(r => r.BarConfidence >= 0.999) / barred.Count,6:P1}");
        }

        Console.WriteLine();
        Console.WriteLine("  where the fitted readings went, by factor of the reference:");
        foreach (var (factor, count) in board.Octaves(Scoreboard.Fitted))
            Console.WriteLine($"    {(factor == 0 ? "no octave fits" : $"x{factor:0.###}"),-16} {count,5}");

        // Per genre, for the corpora whose file names start with a genre. This quickly shows when an
        // average overall number is caused by one kind of music that the reader handles badly.
        var byGenre = board.Readings
            .GroupBy(r => r.Stem.Split('.')[0])
            .Where(g => g.Count() >= 10)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        if (byGenre.Count > 1)
        {
            Console.WriteLine();
            Console.WriteLine("  by genre (fitted):");
            foreach (var genre in byGenre)
            {
                var slice = new Scoreboard(genre.Key);
                foreach (var r in genre)
                    slice.Add(r);

                var halved = genre.Select(r => r.SplitHalf).Where(v => v > 0).Order().ToList();
                string precision = halved.Count == 0
                    ? ""
                    : $"   tempo {halved[halved.Count / 2] / 4,7:F4} % ({(double)halved.Count(v => v / 4 <= 0.05) / halved.Count,5:P0} inside)";
                Console.WriteLine(slice.Row(Scoreboard.Fitted, "  " + genre.Key) + precision);
            }
        }

        Directory.CreateDirectory(outDir);
        var csv = Path.Combine(outDir, $"{corpus}.csv");
        using var writer = new StreamWriter(csv);
        writer.WriteLine("track,reference,fitted,raw,unfolded,confidence,octave,seconds,beats,short_gaps,long_gaps,constant,scatter,on_the_line,merged,split_half,drift_pct,drift_ms,jitter_ms,coverage,per_bar,bar_confidence");
        foreach (var r in board.Readings)
        {
            writer.WriteLine(string.Join(',',
                r.Stem,
                r.Reference.ToString("0.###", CultureInfo.InvariantCulture),
                r.Fitted.ToString("0.###", CultureInfo.InvariantCulture),
                r.Raw.ToString("0.###", CultureInfo.InvariantCulture),
                r.Unfolded.ToString("0.###", CultureInfo.InvariantCulture),
                r.Confidence.ToString("0.###", CultureInfo.InvariantCulture),
                TempoAccuracy.OctaveOf(r.Fitted, r.Reference).ToString("0.###", CultureInfo.InvariantCulture),
                r.Seconds.ToString("0.##", CultureInfo.InvariantCulture),
                r.Beats.ToString(CultureInfo.InvariantCulture),
                r.ShortGaps.ToString(CultureInfo.InvariantCulture),
                r.LongGaps.ToString(CultureInfo.InvariantCulture),
                r.Constant.ToString("0.###", CultureInfo.InvariantCulture),
                r.Scatter.ToString("0.####", CultureInfo.InvariantCulture),
                r.OnTheLine.ToString("0.###", CultureInfo.InvariantCulture),
                r.Merged.ToString(CultureInfo.InvariantCulture),
                r.SplitHalf.ToString("0.#####", CultureInfo.InvariantCulture),
                r.FittedDrift.Percent.ToString("0.####", CultureInfo.InvariantCulture),
                r.FittedDrift.ChangeoverMs.ToString("0.#", CultureInfo.InvariantCulture),
                r.FittedDrift.JitterMs.ToString("0.#", CultureInfo.InvariantCulture),
                r.FittedDrift.Coverage.ToString("0.###", CultureInfo.InvariantCulture),
                r.BeatsPerBar.ToString(CultureInfo.InvariantCulture),
                r.BarConfidence.ToString("0.###", CultureInfo.InvariantCulture)));
        }

        Console.WriteLine();
        Console.WriteLine($"  per track: {csv}");
    }

    private static readonly string[] Audio = [".wav", ".au", ".mp3", ".flac", ".m4a", ".ogg", ".aiff"];

    /// <summary>The reference version that each corpus's published table uses, so a run with default
    /// options can be compared with it. Pass another on the command line to use it.</summary>
    private static string DefaultVersion(string corpus) => corpus switch
    {
        "ballroom" => "2.0",
        _ => "1.0",
    };

    /// <summary>The weights the app builds with, in a checkout. A published app has them next to the
    /// binary, but this tool is never published.</summary>
    private static string DefaultModelDirectory()
    {
        var here = AppContext.BaseDirectory;
        for (var dir = new DirectoryInfo(here); dir is not null; dir = dir.Parent)
        {
            var assets = Path.Combine(dir.FullName, "RPM.Desktop", "Assets", "OnnxModels");
            if (Directory.Exists(assets))
                return assets;
        }

        return OnnxBeatModel.ModelDirectory;
    }

    private static string? Arg(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
