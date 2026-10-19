using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using RPM.Core.Analysis;
using RPM.Fingerprint;

namespace RPM.Core.Cloud;

/// <summary>An analysis, taken apart into the facts that the pool keeps, and put back together again.
///
/// <para>It is taken apart because the pool stamps a generation on each fact, not one on the whole
/// reading. A single stamp can only be improved by discarding it. In a shared pool, discarding it discards
/// it for everybody at once. A better way of finding the bar line would then invalidate every key, every
/// energy curve and every cue point in the pool as well. Kept separately, a new beat tracker replaces the
/// beat grids and nothing else.
/// </para>
///
/// <para>The generations are separate for that reason, and they are expected to drift further apart over
/// time. They are not <see cref="TrackAnalysis.AnalyzerVersion"/>. That number controls this machine's own
/// cache, where discarding everything makes this library analyse again from the start.
/// </para>
/// </summary>
public static partial class AnalysisFacts
{
    /// <summary>The generation of the tempo, beat times, bar line and length fact.
    ///
    /// <para>A grid is only trusted at this generation. A grid published under an older meaning can look
    /// consistent and still be wrong, and nothing in the published fact shows that. So a change that makes
    /// the grids wrong moves this number, and every grid in the pool is measured again. A change to the
    /// analyser that leaves the grids correct does not move it. Bump it only when the grid itself changes
    /// meaning, because a bump re-measures every grid in the pool at once. See
    /// <see cref="TrackAnalysis.AnalyzerVersion"/> for the analyser's own version.</para>
    ///
    /// <para>The current meaning: the beats are searched for, not picked, so a grid cannot hold two beats a
    /// twentieth of a second apart. The bar line is searched for with the beats. A tempo below 70 BPM is
    /// not doubled on its way out. So a published tempo can be off by a factor of two, and the fact does
    /// not say so. The beats travel with the fact, so a grid that bends also travels with no extra fact to
    /// carry the bend.</para>
    /// </summary>
    public const int GridGeneration = 10;

    /// <summary>The musical key. Bump this when the meaning of the key fact changes.</summary>
    public const int KeyGeneration = 1;

    /// <summary>Loudness over the record, and its one-number summary.</summary>
    public const int EnergyGeneration = 1;

    /// <summary>Where to mix in and out. The cues are read from the low band and held inside the measured
    /// audio. So they move on any record with silence at either end.
    /// </summary>
    public const int CuesGeneration = 3;

    /// <summary>Where the audio and the beat actually begin and end.</summary>
    public const int EdgesGeneration = 1;

    /// <summary>The record's bass, mid, top and vocal curves: how much of the record sits in each
    /// band.</summary>
    public const int BandsGeneration = 1;

    /// <summary>The generation of the phrases, tempo steadiness, the drops, and the level at each cue.
    ///
    /// <para>Steadiness is measured against the grid that the beats actually landed on. It is not measured
    /// by dividing the record by one period. For a straight grid the two give the same answer. But the grid
    /// can bend (see <see cref="GridGeneration"/>) on records that do not hold a tempo, so a steadiness
    /// measured the old way would disagree with the grid served beside it. A record that its own grid
    /// follows exactly would then come back as the least steady thing in the library. Steadiness must not
    /// be measured against a straightened grid. That reads 1 for every record.</para>
    ///
    /// <para>The phrases, the drops and the cue levels are arithmetic over the bands and the grid.
    /// <see cref="To"/> works them out again for any row whose shape is missing.</para>
    /// </summary>
    public const int ShapeGeneration = 3;

    // ── the shapes, as they travel ────────────────────────────────────────────
    //
    // Strings rather than numbers throughout, and rounded before they are written. Agreement between two
    // people who measured the same record is what turns a claim into a fact. The store sees agreement only
    // when the two produce the same TEXT. So the rounding is not tidiness. It is the difference between
    // corroboration and a permanent argument.

    private sealed record GridBody(
        [property: JsonPropertyName("duration")] string Duration,
        [property: JsonPropertyName("bpm")] string Bpm,
        [property: JsonPropertyName("bpmConfidence")] string BpmConfidence,
        [property: JsonPropertyName("beats")] string Beats,
        [property: JsonPropertyName("firstDownbeat")] int FirstDownbeat,
        [property: JsonPropertyName("downbeatConfidence")] string DownbeatConfidence,
        [property: JsonPropertyName("beatsPerBar")] int BeatsPerBar);

    private sealed record KeyBody(
        [property: JsonPropertyName("camelot")] string? Camelot,
        [property: JsonPropertyName("confidence")] string Confidence);

    private sealed record EnergyBody(
        [property: JsonPropertyName("curve")] string Curve,
        [property: JsonPropertyName("intensity")] string Intensity);

    private sealed record CuesBody(
        [property: JsonPropertyName("introEnd")] string IntroEnd,
        [property: JsonPropertyName("outroStart")] string OutroStart);

    private sealed record BandsBody(
        [property: JsonPropertyName("bass")] string Bass,
        [property: JsonPropertyName("mid")] string Mid,
        [property: JsonPropertyName("top")] string Top,
        [property: JsonPropertyName("vocal")] string Vocal);

    private sealed record ShapeBody(
        [property: JsonPropertyName("phraseBars")] int PhraseBars,
        [property: JsonPropertyName("phraseFirstBar")] int PhraseFirstBar,
        [property: JsonPropertyName("phraseConfidence")] string PhraseConfidence,
        [property: JsonPropertyName("tempoStability")] string TempoStability,
        [property: JsonPropertyName("drops")] string Drops,
        [property: JsonPropertyName("introLevel")] string IntroLevel,
        [property: JsonPropertyName("outroLevel")] string OutroLevel);

    private sealed record EdgesBody(
        [property: JsonPropertyName("audioStart")] string AudioStart,
        [property: JsonPropertyName("audioEnd")] string AudioEnd,
        [property: JsonPropertyName("beatFirst")] string BeatFirst,
        [property: JsonPropertyName("beatLast")] string BeatLast);

    /// <summary>The shapes above, as the System.Text.Json source generator describes them.
    /// </summary>
    /// <remarks>Nested and private because the shapes are this file's business, and no caller names them. A
    /// published build uses Native AOT with reflection-based serialisation turned off. So a shape missing
    /// from this list throws at the Serialize call, rather than writing a fact.</remarks>
    [JsonSerializable(typeof(GridBody))]
    [JsonSerializable(typeof(KeyBody))]
    [JsonSerializable(typeof(EnergyBody))]
    [JsonSerializable(typeof(CuesBody))]
    [JsonSerializable(typeof(BandsBody))]
    [JsonSerializable(typeof(ShapeBody))]
    [JsonSerializable(typeof(EdgesBody))]
    private sealed partial class AnalysisFactsJson : JsonSerializerContext;

    private static string Json(GridBody body) => JsonSerializer.Serialize(body, AnalysisFactsJson.Default.GridBody);
    private static string Json(KeyBody body) => JsonSerializer.Serialize(body, AnalysisFactsJson.Default.KeyBody);
    private static string Json(EnergyBody body) => JsonSerializer.Serialize(body, AnalysisFactsJson.Default.EnergyBody);
    private static string Json(CuesBody body) => JsonSerializer.Serialize(body, AnalysisFactsJson.Default.CuesBody);
    private static string Json(BandsBody body) => JsonSerializer.Serialize(body, AnalysisFactsJson.Default.BandsBody);
    private static string Json(ShapeBody body) => JsonSerializer.Serialize(body, AnalysisFactsJson.Default.ShapeBody);
    private static string Json(EdgesBody body) => JsonSerializer.Serialize(body, AnalysisFactsJson.Default.EdgesBody);

    /// <summary>What this machine has to say about a record, as facts.
    ///
    /// <para>A reading with no tempo is still worth publishing. "This record has no beat to match" is a
    /// finding, and every other machine would otherwise spend a decode to learn it. What is not published
    /// is a grid that nobody should mix on. Below the confidence floor, the pool would serve a stranger a
    /// reading that this machine would not use itself.</para>
    /// </summary>
    public static IReadOnlyList<RecordingFactBody> From(TrackAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);

        // A reading that was edited by hand is not a finding about the record. It is one person's
        // preference for how their own set should sound. Published, it would reach strangers as a
        // measurement, and nothing in a fact can tell them otherwise.
        if (analysis.IsEdited)
            return [];

        var facts = new List<RecordingFactBody>(4);

        facts.Add(new RecordingFactBody(
            RecordingFact.Grid,
            GridGeneration,
            Json(new GridBody(
                Seconds(analysis.DurationSeconds),
                Seconds(analysis.Bpm),
                Unit(analysis.BpmConfidence),
                Times(analysis.BeatTimes),
                analysis.FirstDownbeat,
                Unit(analysis.DownbeatConfidence),
                analysis.BeatsPerBar)),
            analysis.Modelled,
            // A record with no tempo is a confident finding about a record with no tempo. Publishing it at
            // the tracker's own confidence, which is zero, would make the store refuse it. The next machine
            // would then decode the same silence to learn the same thing.
            analysis.Bpm > 0 ? Clamp(analysis.BpmConfidence) : 1));

        if (analysis.Key.IsKnown)
            facts.Add(new RecordingFactBody(
                RecordingFact.Key,
                KeyGeneration,
                Json(new KeyBody(analysis.Key.Camelot, Unit(analysis.KeyConfidence))),
                analysis.Modelled,
                Clamp(analysis.KeyConfidence)));

        if (analysis.EnergyCurve.Count > 0)
            facts.Add(new RecordingFactBody(
                RecordingFact.Energy,
                EnergyGeneration,
                Json(new EnergyBody(Levels(analysis.EnergyCurve), Unit(analysis.Intensity)))));

        if (analysis.OutroStartSeconds > 0)
            facts.Add(new RecordingFactBody(
                RecordingFact.Cues,
                CuesGeneration,
                Json(new CuesBody(
                    Seconds(analysis.IntroEndSeconds), Seconds(analysis.OutroStartSeconds)))));

        // Only when they were measured. A machine running an older analyser has no edges to report. Zeroes
        // would read as a record that is silent from its first sample.
        if (analysis.HasEdges)
            facts.Add(new RecordingFactBody(
                RecordingFact.Edges,
                EdgesGeneration,
                Json(new EdgesBody(
                    Seconds(analysis.AudioStartSeconds),
                    Seconds(analysis.AudioEndSeconds),
                    Seconds(analysis.BeatFirstSeconds),
                    Seconds(analysis.BeatLastSeconds)))));

        // The bands are rounded to three places, not five. Each one is read against its own median. No code
        // compares two bands to the last decimal. Five places for four curves would cost four times as much
        // as a grid in every row, for a precision that nobody uses.
        if (analysis.HasBands)
            facts.Add(new RecordingFactBody(
                RecordingFact.Bands,
                BandsGeneration,
                Json(new BandsBody(
                    Curve(analysis.BassCurve),
                    Curve(analysis.MidCurve),
                    Curve(analysis.TopCurve),
                    Curve(analysis.VocalCurve)))));

        // This is small, and it is the part that a changeover is planned from.
        if (analysis.Phrases.IsKnown || analysis.TempoStability > 0 || analysis.DropSeconds.Count > 0)
            facts.Add(new RecordingFactBody(
                RecordingFact.Shape,
                ShapeGeneration,
                Json(new ShapeBody(
                    analysis.Phrases.Bars,
                    analysis.Phrases.FirstBar,
                    Unit(analysis.Phrases.Confidence),
                    Unit(analysis.TempoStability),
                    Times(analysis.DropSeconds),
                    Unit(analysis.IntroLevel),
                    Unit(analysis.OutroLevel)))));

        return facts;
    }

    /// <summary>The analysis that the pool holds for a record, as this machine would have measured it.
    /// Returns null when what is there is not enough to mix on.
    ///
    /// <para>The grid is the minimum. Without it there is no tempo, no bar line, and nothing to align a
    /// changeover to. A key on its own is not worth a row. Everything else is laid over the grid when it is
    /// present, and is left at its default when it is not. So a record whose energy curve has not been
    /// published yet still arrives ready for beat matching.</para>
    /// </summary>
    /// <param name="filePath">Written into the returned analysis. The pool's row describes the audio. This
    /// is the file in front of the caller.</param>
    public static TrackAnalysis? To(KnownRecording? known, string filePath)
    {
        if (known?.Fact(RecordingFact.Grid) is not { } gridFact || gridFact.Generation != GridGeneration)
            return null;

        GridBody? grid;
        try { grid = JsonSerializer.Deserialize(gridFact.Value, AnalysisFactsJson.Default.GridBody); }
        catch (JsonException) { return null; }

        if (grid is null || !TryNumber(grid.Duration, out var duration) || duration <= 0)
            return null;

        var analysis = new TrackAnalysis
        {
            FilePath = filePath,
            DurationSeconds = duration,
            Bpm = TryNumber(grid.Bpm, out var bpm) ? bpm : 0,
            BpmConfidence = TryNumber(grid.BpmConfidence, out var bpmConfidence) ? bpmConfidence : 0,
            BeatTimes = ReadTimes(grid.Beats),
            FirstDownbeat = Math.Max(0, grid.FirstDownbeat),
            DownbeatConfidence = TryNumber(grid.DownbeatConfidence, out var downbeat) ? downbeat : 1,
            BeatsPerBar = grid.BeatsPerBar > 0 ? grid.BeatsPerBar : 4,
            // Stamped as this machine's own current reading, and as modelled only if what arrived was
            // modelled. A fetched grid that claimed to be modelled when it was not would stop the upgrade
            // pass from ever looking at it again.
            Version = gridFact.Modelled ? TrackAnalysis.ModelledVersion : TrackAnalysis.AnalyzerVersion,
            OutroStartSeconds = duration,
        };

        if (known.Fact(RecordingFact.Key) is { } keyFact && keyFact.Generation == KeyGeneration)
        {
            try
            {
                var body = JsonSerializer.Deserialize(keyFact.Value, AnalysisFactsJson.Default.KeyBody);
                if (body is not null)
                    analysis = analysis with
                    {
                        Key = MusicalKey.FromCamelot(body.Camelot),
                        KeyConfidence = TryNumber(body.Confidence, out var confidence) ? confidence : 0,
                    };
            }
            catch (JsonException) { }
        }

        if (known.Fact(RecordingFact.Energy) is { } energyFact && energyFact.Generation == EnergyGeneration)
        {
            try
            {
                var body = JsonSerializer.Deserialize(energyFact.Value, AnalysisFactsJson.Default.EnergyBody);
                if (body is not null)
                    analysis = analysis with
                    {
                        EnergyCurve = ReadLevels(body.Curve),
                        Intensity = TryNumber(body.Intensity, out var intensity) ? intensity : 0,
                    };
            }
            catch (JsonException) { }
        }

        if (known.Fact(RecordingFact.Edges) is { } edgesFact && edgesFact.Generation == EdgesGeneration)
        {
            try
            {
                var body = JsonSerializer.Deserialize(edgesFact.Value, AnalysisFactsJson.Default.EdgesBody);
                if (body is not null
                    && TryNumber(body.AudioStart, out var audioStart)
                    && TryNumber(body.AudioEnd, out var audioEnd)
                    && audioEnd > audioStart)
                    analysis = analysis with
                    {
                        AudioStartSeconds = audioStart,
                        AudioEndSeconds = audioEnd,
                        BeatFirstSeconds = TryNumber(body.BeatFirst, out var beatFirst) ? beatFirst : 0,
                        BeatLastSeconds = TryNumber(body.BeatLast, out var beatLast) ? beatLast : 0,
                    };
            }
            catch (JsonException) { }
        }

        if (known.Fact(RecordingFact.Bands) is { } bandsFact && bandsFact.Generation == BandsGeneration)
        {
            try
            {
                var body = JsonSerializer.Deserialize(bandsFact.Value, AnalysisFactsJson.Default.BandsBody);
                if (body is not null)
                    analysis = analysis with
                    {
                        BassCurve = ReadLevels(body.Bass),
                        MidCurve = ReadLevels(body.Mid),
                        TopCurve = ReadLevels(body.Top),
                        VocalCurve = ReadLevels(body.Vocal),
                    };
            }
            catch (JsonException) { }
        }

        if (known.Fact(RecordingFact.Shape) is { } shapeFact && shapeFact.Generation == ShapeGeneration)
        {
            try
            {
                var body = JsonSerializer.Deserialize(shapeFact.Value, AnalysisFactsJson.Default.ShapeBody);
                if (body is not null)
                    analysis = analysis with
                    {
                        Phrases = new PhraseGrid(
                            Math.Max(0, body.PhraseBars),
                            Math.Max(0, body.PhraseFirstBar),
                            TryNumber(body.PhraseConfidence, out var phrase) ? phrase : 0),
                        TempoStability = TryNumber(body.TempoStability, out var stability) ? stability : 0,
                        DropSeconds = ReadTimes(body.Drops),
                        IntroLevel = TryNumber(body.IntroLevel, out var intro) ? intro : 0,
                        OutroLevel = TryNumber(body.OutroLevel, out var outro) ? outro : 0,
                    };
            }
            catch (JsonException) { }
        }

        // Whatever the shape fact did not carry is worked out here, from what did arrive.
        //
        // The phrases, the drops and the level at each cue are arithmetic over the bass curve and the beat
        // grid. Both have just arrived. So a shape that is missing, or that was written under a generation
        // this build does not accept, is not missing any input. It only lacks the calculation. Doing it
        // here stops a borrowed reading from being stored as current while it is permanently missing these
        // values.
        //
        // Steadiness is the exception, and is left at zero. It is the difference between where the beats
        // were heard and where the grid puts them. Only the grid travels, so that difference cannot be
        // recovered here.
        if (analysis.HasBands && analysis.BeatTimes.Count > 0 && !analysis.Phrases.IsKnown)
            analysis = analysis with
            {
                Phrases = TrackShape.Phrases(
                    analysis.BassCurve, analysis.BeatTimes, analysis.FirstDownbeat, analysis.BeatsPerBar),
                DropSeconds = analysis.DropSeconds.Count > 0
                    ? analysis.DropSeconds
                    : TrackShape.Drops(
                        analysis.BassCurve, analysis.BeatTimes, analysis.FirstDownbeat, analysis.BeatsPerBar),
            };

        if (known.Fact(RecordingFact.Cues) is { } cuesFact && cuesFact.Generation == CuesGeneration)
        {
            try
            {
                var body = JsonSerializer.Deserialize(cuesFact.Value, AnalysisFactsJson.Default.CuesBody);
                if (body is not null
                    && TryNumber(body.IntroEnd, out var intro)
                    && TryNumber(body.OutroStart, out var outro)
                    && outro > intro)
                    analysis = analysis with { IntroEndSeconds = intro, OutroStartSeconds = outro };
            }
            catch (JsonException) { }
        }

        // The level at each cue, once the cues are known. It is worked out from the energy curve that
        // arrived with the reading. It costs nothing, and it is the same number that this machine would
        // have measured.
        if (analysis.EnergyCurve.Count > 0 && analysis.IntroLevel <= 0 && analysis.OutroLevel <= 0)
            analysis = analysis with
            {
                IntroLevel = TrackShape.LevelAt(analysis.EnergyCurve, analysis.IntroEndSeconds),
                OutroLevel = TrackShape.LevelAt(analysis.EnergyCurve, analysis.OutroStartSeconds),
            };

        return analysis;
    }

    // ── numbers, spelled the one way ──────────────────────────────────────────

    /// <summary>A time or a tempo, to four places. This matches the precision that the mirror stores beat
    /// times at, and it is far finer than a changeover can be heard to be wrong by.</summary>
    private static string Seconds(double value) =>
        Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>A 0..1 reading, to three places.</summary>
    private static string Unit(double value) =>
        Math.Round(Clamp(value), 3).ToString("0.###", CultureInfo.InvariantCulture);

    private static double Clamp(double value) =>
        double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    private static string Times(IReadOnlyList<double> values) =>
        string.Join(',', values.Select(Seconds));

    /// <summary>A band curve, to three places. This is finer than anything that reads it. It is about a
    /// quarter of the size of the five-place form that the energy curve uses.
    ///
    /// <para>Rounded away from zero, not to even. The default rounds a value that sits exactly on a half to
    /// the nearest even digit. So two machines that computed 0.0625 and 0.0625000001 would write 0.062 and
    /// 0.063. The store sees agreement only when it sees the same text, and it would then read one record
    /// as two claims forever. Rounding away from zero puts both on 0.063.</para>
    /// </summary>
    private static string Curve(IReadOnlyList<float> values) =>
        string.Join(',', values.Select(v =>
            Math.Round(v, 3, MidpointRounding.AwayFromZero).ToString("0.###", CultureInfo.InvariantCulture)));

    private static string Levels(IReadOnlyList<float> values) =>
        string.Join(',', values.Select(v => Math.Round(v, 5).ToString("0.#####", CultureInfo.InvariantCulture)));

    private static bool TryNumber(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);

    private static double[] ReadTimes(string? packed)
    {
        if (string.IsNullOrEmpty(packed))
            return [];

        var parts = packed.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var values = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            values[i] = TryNumber(parts[i], out var value) ? value : 0;
        return values;
    }

    private static float[] ReadLevels(string? packed)
    {
        if (string.IsNullOrEmpty(packed))
            return [];

        var parts = packed.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var values = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            values[i] = TryNumber(parts[i], out var value) ? (float)value : 0;
        return values;
    }
}
