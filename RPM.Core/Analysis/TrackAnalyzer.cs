namespace RPM.Core.Analysis;

/// <summary>
/// Runs the analysis passes over one track's samples and hands back a <see cref="TrackAnalysis"/>.
/// Pure: it takes mono PCM and gives back numbers, so every pass can be tested against a signal
/// built in the test itself. Getting the samples out of a file is
/// <see cref="Audio.PcmDecoder"/>'s job.
/// </summary>
public static class TrackAnalyzer
{
    /// <summary>Everything here is analysed at 22050 Hz mono. Halving the rate halves the work and
    /// loses nothing: the highest thing any of these passes looks at is a 2 kHz overtone.</summary>
    public const int AnalysisRate = 22050;

    /// <param name="beatModel">A trained beat tracker, when the host can run one. It replaces the
    /// onset-and-autocorrelation path for the grid and the bar lines (the two readings the DSP is
    /// least sure of). It is skipped silently when it is absent or declines to read the track.</param>
    /// <param name="keyModel">A trained key detector, on the same terms.</param>
    /// <param name="ct">
    /// Abandons the analysis. It is checked between passes and, inside the beat model, between
    /// windows (one track is several seconds of work and most of it is in there).
    ///
    /// <para>Cancelling throws rather than returning what has been worked out so far. A half-read
    /// track is not a worse analysis, it is a wrong one: the grid would be built from a tempo the
    /// chroma never confirmed and stored against the file as though it had been read properly.</para>
    /// </param>
    public static TrackAnalysis Analyze(
        string filePath,
        ReadOnlySpan<float> mono,
        int sampleRate,
        IBeatModel? beatModel = null,
        IKeyModel? keyModel = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        double duration = sampleRate > 0 ? (double)mono.Length / sampleRate : 0;
        if (mono.Length < sampleRate || sampleRate <= 0)
            return TrackAnalysis.Unanalyzable(filePath, duration);

        // Rhythm. The DSP's tempo is a working number and never an answer: BeatTracker needs it to
        // place beats on the onsets, and those placements are the only thing that leaves this block
        // (ModelBeatGrid.Against slides the model's line onto them). Nothing here reaches
        // TrackAnalysis.Bpm.
        //
        // The grid a mix runs on is the model's, or there is not one. Onset autocorrelation can lock
        // onto the dotted quarter and read two thirds of the true tempo while reporting a confidence
        // of 1.00, because that lock IS a strong clean periodicity. So the DSP can neither get this
        // right nor tell when it is wrong, and a tempo that cannot be doubted is worse than no tempo.
        // A record with no reading is faded rather than matched, which the planner already handles.
        // A record read two thirds slow has every fade in its mix set to the wrong length, and
        // nothing in the app would say so.
        ct.ThrowIfCancellationRequested();
        var onsets = OnsetEnvelope.Compute(mono);
        double frameRate = OnsetEnvelope.FrameRate(sampleRate);
        var tempo = TempoEstimator.Estimate(onsets, frameRate);

        var tracked = tempo.IsKnown ? BeatTracker.Track(onsets, tempo.Bpm, frameRate) : [];

        // One straight line through the tracked beats, so it has no dropped or doubled beats in it.
        // Placement only: where the onsets are, at whatever rate the DSP happened to count them.
        var placed = BeatTracker.Regularize(
            BeatTracker.Refine(onsets, tracked), frameRate, tempo.Bpm, onsets.Length);
        var onsetBeats = new double[placed.Length];
        for (int i = 0; i < placed.Length; i++)
            onsetBeats[i] = OnsetEnvelope.FrameTime(placed[i], sampleRate);

        // Harmony, frame by frame. The average of it gives the key.
        ct.ThrowIfCancellationRequested();
        var chromagram = ChromaAnalyzer.ComputeFrames(mono, sampleRate);
        var key = KeyDetector.Detect(ChromaAnalyzer.Average(chromagram));

        double gridBpm = 0;
        double bpmConfidence = 0;
        double[] beatTimes = [];
        double beatFirst = 0, beatLast = 0;
        int firstDownbeat = 0;
        double downbeatConfidence = 0;
        int beatsPerBar = 4;
        bool modelledGrid = false;

        // The passes above run either way: the energy arc, the cues and the key are read from the
        // same passes whether or not a model is there to read the beat.
        if (ReadBeats(beatModel, mono, sampleRate, onsetBeats, duration, ct)
            is { IsUsable: true } fitted)
        {
            beatTimes = fitted.BeatTimes;
            gridBpm = fitted.Bpm;
            bpmConfidence = fitted.BpmConfidence;

            // Where the pulse was actually heard, before the grid is carried to both ends of the
            // file. The grid cannot tell heard beats from carried ones afterwards, and a changeover
            // timed to a bar line nobody ever played sounds late.
            beatFirst = fitted.ObservedFrom;
            beatLast = fitted.ObservedTo;

            // The bar only moves when the model actually found one. Its bar lines have to agree with
            // each other first (a named bar that is not there sets the length of every fade in the
            // mix). Where they do not agree, the harmony is asked instead, against the model's grid.
            if (fitted.DownbeatConfidence > 0)
            {
                firstDownbeat = fitted.FirstDownbeat;
                beatsPerBar = fitted.BeatsPerBar;
                downbeatConfidence = fitted.DownbeatConfidence;
            }
            else
            {
                var frames = new int[beatTimes.Length];
                for (int i = 0; i < beatTimes.Length; i++)
                    frames[i] = (int)Math.Round(beatTimes[i] * frameRate);

                var harmony = DownbeatDetector.Find(
                    onsets, frames, frameRate, chromagram, ChromaAnalyzer.FrameRate(sampleRate));
                firstDownbeat = harmony.FirstDownbeat;
                downbeatConfidence = harmony.Confidence;
            }
            modelledGrid = true;
        }

        // The key model does not touch the version: what ModelledVersion answers is whether the grid
        // is worth going back for, and a named key is not a grid.
        if (ReadKey(keyModel, mono, sampleRate, ct) is { } modelKey)
            key = new KeyDetector.Result(modelKey.Key, modelKey.Confidence);

        // The record in three bands, in one pass. The level is the energy arc the planner sorts on.
        // The bottom end is what the cues are read from, because where the kick starts is where the
        // record can be mixed into, and that is not the same place the record gets loud. The middle
        // and the top are what a changeover moves separately from each other.
        var bands = EnergyProfile.ComputeBands(mono, sampleRate);
        var levels = bands.Full;
        var bass = bands.Bass;
        var cues = CuePointFinder.Find(levels, duration, beatTimes, firstDownbeat, beatsPerBar, bassLevels: bass);

        // Where the audio actually is, against an absolute floor. The cues above are read at a share
        // of this record's own median level, which moves with the mastering; this does not.
        var edges = AudioEdges.Find(mono, sampleRate);

        // The cues are held inside those edges. A mix-out timed into a track's trailing silence
        // arrives late by the length of the tail, and a needle dropped before the first sound comes
        // in early by the gap in front of it. The cue reading can produce both on its own, because
        // the level it measures against is relative and silence is not.
        double introEnd = cues.IntroEndSeconds;
        double outroStart = cues.OutroStartSeconds;
        if (edges.IsKnown)
        {
            introEnd = Math.Clamp(introEnd, edges.StartSeconds, edges.EndSeconds);
            outroStart = Math.Clamp(outroStart, introEnd, edges.EndSeconds);
        }

        return new TrackAnalysis
        {
            FilePath = filePath,
            DurationSeconds = duration,
            Bpm = gridBpm,
            BpmConfidence = bpmConfidence,
            BeatTimes = beatTimes,
            FirstDownbeat = firstDownbeat,
            DownbeatConfidence = downbeatConfidence,
            BeatsPerBar = beatsPerBar,
            Key = key.Key,
            KeyConfidence = key.Confidence,
            EnergyCurve = levels,
            Intensity = EnergyProfile.Intensity(levels),
            BassCurve = bands.Bass,
            MidCurve = bands.Mid,
            TopCurve = bands.Top,
            VocalCurve = bands.Vocal,
            IntroEndSeconds = introEnd,
            OutroStartSeconds = outroStart,

            // What the bands say about the shape of the record, all of it arithmetic over curves
            // that are already in hand: where the phrases fall, how steadily it is played, where the
            // beat comes back after a breakdown, and how loud it is at the two points a changeover
            // meets it.
            Phrases = TrackShape.Phrases(bands.Bass, beatTimes, firstDownbeat, beatsPerBar),
            // Measured against the tracked beats rather than the grid: the grid is a straight line by
            // construction, so its own intervals say nothing about how steadily the record is played.
            TempoStability = TrackShape.TempoStability(
                [.. tracked.Select(f => OnsetEnvelope.FrameTime(f, sampleRate))], beatTimes),
            DropSeconds = TrackShape.Drops(bands.Bass, beatTimes, firstDownbeat, beatsPerBar),
            IntroLevel = TrackShape.LevelAt(levels, introEnd),
            OutroLevel = TrackShape.LevelAt(levels, outroStart),
            AudioStartSeconds = edges.StartSeconds,
            AudioEndSeconds = edges.EndSeconds,
            BeatFirstSeconds = beatFirst,
            BeatLastSeconds = beatLast,
            // Only the grid's model sets the modelled version. If the key model named a key but the
            // beat model declined the track, the track has no tempo, and marking it as modelled would
            // keep it out of the upgrade pass for good.
            Version = modelledGrid ? TrackAnalysis.ModelledVersion : TrackAnalysis.AnalyzerVersion,
        };
    }

    /// <summary>
    /// The DSP's grid with what the model knows folded into it, or null to carry on with the DSP's
    /// alone.
    ///
    /// <para>Read <em>against</em> the grid rather than in place of it: the model settles the tempo
    /// and the bar, which are the counting judgements, and the DSP's onset-accurate beats are used
    /// only to slide that line into place. See <see cref="ModelBeatGrid.Against"/>.</para>
    ///
    /// <para>A model is a file on disk and a runtime that has to start. Both can fail, and neither
    /// is worth losing an analysis over. Anything thrown on the way is swallowed here, because the
    /// caller already has a working answer and the only question is whether there is a better one.
    /// The exception is cancellation: it is not a model failing but the caller saying it no longer
    /// wants any of this, so it has to reach the caller.</para>
    /// </summary>
    private static ModelBeatGrid.Result? ReadBeats(
        IBeatModel? model, ReadOnlySpan<float> mono, int sampleRate,
        IReadOnlyList<double> dspBeats, double duration,
        CancellationToken ct)
    {
        if (model is null)
            return null;

        try
        {
            var reading = model.Read(mono, sampleRate, ct);
            return reading is null
                ? null
                : ModelBeatGrid.Against(reading, dspBeats, duration);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The model's key, or null to keep the chromagram's. Fails the same way.</summary>
    private static KeyReading? ReadKey(
        IKeyModel? model, ReadOnlySpan<float> mono, int sampleRate, CancellationToken ct)
    {
        if (model is null)
            return null;

        try
        {
            var reading = model.Read(mono, sampleRate, ct);
            return reading is { Key.IsKnown: true } ? reading : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
