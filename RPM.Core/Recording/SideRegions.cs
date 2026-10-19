using RPM.Core.Splitting;

namespace RPM.Core.Recording;

/// <summary>The music on one recorded side: where it actually starts and stops, with the needle drop,
/// the lead-in groove and the run-out left outside.</summary>
public sealed record SideRegion(double StartSeconds, double EndSeconds)
{
    public double DurationSeconds => EndSeconds - StartSeconds;
}

/// <summary>
/// Finds the real music inside a vinyl recording and holds the cut to it.
///
/// <para>A recorded side does not begin where the recording begins. The user starts capture, drops the
/// needle (a loud thump), and waits through the lead-in groove's surface noise. The first track starts
/// some seconds later. At the other end, the run-out hisses until the needle is lifted, which thumps
/// again. Cutting the raw timeline gives track 1 a thump and a minute of hiss for an intro, and the
/// last track a long tail of nothing.</para>
///
/// <para>The thump is why "first sound above the noise floor" is the wrong test. A thump is a
/// transient, often louder than the music. What separates music from handling noise is that music
/// <i>sustains</i>. So a region starts at the first run of sound that lasts
/// <see cref="DefaultMinRunSeconds"/>, and ends after the last one. A thump has come and gone long
/// before that.</para>
/// </summary>
public static class SideRegions
{
    /// <summary>How long sound must hold above the floor to count as music rather than a needle drop, a
    /// click or a pop.</summary>
    public const double DefaultMinRunSeconds = 1.0;

    /// <summary>Kept before the music starts (and after it ends) so a soft attack or a fading last
    /// chord is never clipped by the trim.</summary>
    public const double DefaultPreRollSeconds = 0.25;

    /// <summary>
    /// The music window of each recorded side, in the joined timeline.
    /// <paramref name="sideJoinSeconds"/> are the offsets where one side gives way to the next, so
    /// there is one region per side. A side with no sustained sound at all is returned untrimmed. A
    /// lead-in in the output is better than a side that is silently thrown away.
    /// </summary>
    public static IReadOnlyList<SideRegion> Detect(
        float[] envelopeDb, double windowSeconds, double totalSeconds,
        IReadOnlyList<double> sideJoinSeconds, SilenceDetectorOptions options,
        double minRunSeconds = DefaultMinRunSeconds, double preRollSeconds = DefaultPreRollSeconds)
    {
        ArgumentNullException.ThrowIfNull(envelopeDb);
        ArgumentNullException.ThrowIfNull(options);
        if (envelopeDb.Length == 0 || totalSeconds <= 0)
            return [];

        double threshold = SilenceDetector.EffectiveThresholdDb(envelopeDb, options);
        int minRun = Math.Max(1, (int)(minRunSeconds / windowSeconds));

        var regions = new List<SideRegion>();
        foreach (var (spanStart, spanEnd) in Spans(sideJoinSeconds, totalSeconds))
        {
            int from = Math.Clamp((int)(spanStart / windowSeconds), 0, envelopeDb.Length);
            int to = Math.Clamp((int)Math.Ceiling(spanEnd / windowSeconds), 0, envelopeDb.Length);
            if (to - from < 1)
            {
                regions.Add(new SideRegion(spanStart, spanEnd));
                continue;
            }

            int first = FirstSustained(envelopeDb, from, to, threshold, minRun);
            int last = LastSustained(envelopeDb, from, to, threshold, minRun);
            if (first < 0 || last < 0 || last <= first)
            {
                regions.Add(new SideRegion(spanStart, spanEnd));   // nothing sustained, so do not trim
                continue;
            }

            double start = Math.Max(spanStart, first * windowSeconds - preRollSeconds);
            double end = Math.Min(spanEnd, (last + 1) * windowSeconds + preRollSeconds);
            regions.Add(new SideRegion(start, end));
        }
        return regions;
    }

    /// <summary>
    /// The record's own noise floor, read off the lead-in groove.
    ///
    /// <para>The stretch between the needle going down and the first track starting is the one place on
    /// the record that carries no music. It is the groove and nothing else. So it measures directly
    /// what silence sounds like on this pressing, through this cartridge. Worn records hiss louder, and
    /// quiet ones barely hiss at all. This is much better than inferring the floor from the quietest
    /// few percent of the album, because on a well-filled side those quiet parts are quiet
    /// music.</para>
    ///
    /// <para>The measurement is taken from the last <paramref name="measureSeconds"/> before the music
    /// starts. That is near enough to the music to be groove, and not the electrical silence before the
    /// needle landed. It is a median, so the needle drop itself cannot skew it. The result is null when
    /// the recording opens straight into music and there is no lead-in to read.</para>
    /// </summary>
    public static double? NoiseFloorFromLeadIn(
        float[] envelopeDb, double windowSeconds, SilenceDetectorOptions options,
        double measureSeconds = 2.0, double minRunSeconds = DefaultMinRunSeconds)
    {
        ArgumentNullException.ThrowIfNull(envelopeDb);
        ArgumentNullException.ThrowIfNull(options);
        if (envelopeDb.Length == 0)
            return null;

        double threshold = SilenceDetector.EffectiveThresholdDb(envelopeDb, options);
        int minRun = Math.Max(1, (int)(minRunSeconds / windowSeconds));
        int firstMusic = FirstSustained(envelopeDb, 0, envelopeDb.Length, threshold, minRun);
        if (firstMusic <= 0)
            return null;   // music from the first window, so there is nothing to measure

        int measureWindows = Math.Max(1, (int)(measureSeconds / windowSeconds));
        int from = Math.Max(0, firstMusic - measureWindows);
        var slice = envelopeDb[from..firstMusic];
        if (slice.Length < 3)
            return null;   // too short a run-in to say anything

        var sorted = slice.ToArray();
        Array.Sort(sorted);
        return sorted[sorted.Length / 2];
    }

    /// <summary>Side spans in the joined timeline: [0…join0], [join0…join1], … [joinN…total].</summary>
    private static IEnumerable<(double Start, double End)> Spans(IReadOnlyList<double> joins, double total)
    {
        double previous = 0;
        foreach (var join in joins.Where(j => j > 0 && j < total).OrderBy(j => j))
        {
            yield return (previous, join);
            previous = join;
        }
        yield return (previous, total);
    }

    /// <summary>Index where sound first holds above the floor for <paramref name="minRun"/>
    /// windows.</summary>
    private static int FirstSustained(float[] env, int from, int to, double threshold, int minRun)
    {
        int run = 0;
        for (int i = from; i < to; i++)
        {
            if (env[i] > threshold)
            {
                if (++run >= minRun)
                    return i - run + 1;
            }
            else run = 0;
        }
        return -1;
    }

    /// <summary>Index where sound last held above the floor for <paramref name="minRun"/>
    /// windows.</summary>
    private static int LastSustained(float[] env, int from, int to, double threshold, int minRun)
    {
        int run = 0;
        for (int i = to - 1; i >= from; i--)
        {
            if (env[i] > threshold)
            {
                if (++run >= minRun)
                    return i + run - 1;
            }
            else run = 0;
        }
        return -1;
    }

    /// <summary>
    /// Holds a plan inside the regions: tracks are clipped to the music, anything falling in the dead
    /// air between sides disappears (it intersects no region), and a track that would bridge a side
    /// flip is cut in two.
    ///
    /// <para>Slivers are then merged away. <paramref name="minTrackSeconds"/> is the same bar that the
    /// silence detector holds its own boundaries to. So a piece below it is an artifact of clipping,
    /// not a song. Merging it into its neighbour keeps the audio, and it avoids inventing a ten-second
    /// track. The merge never drops audio: a sliver inside a region is still music.</para>
    /// </summary>
    public static SplitPlan Apply(SplitPlan plan, IReadOnlyList<SideRegion> regions, double minTrackSeconds = 60)
    {
        if (regions.Count == 0 || plan.Tracks.Count == 0)
            return plan;

        var kept = new List<SplitTrack>();
        foreach (var region in regions.OrderBy(r => r.StartSeconds))
        {
            var pieces = new List<SplitTrack>();
            foreach (var track in plan.Tracks.OrderBy(t => t.StartSeconds))
            {
                double start = Math.Max(track.StartSeconds, region.StartSeconds);
                double end = Math.Min(track.EndSeconds, region.EndSeconds);
                if (end <= start)
                    continue;

                // A track reaching into a second region really was two, split by a side flip: only its
                // first piece can claim the title we had for it.
                bool carriesTitle = track.StartSeconds >= region.StartSeconds;
                pieces.Add(carriesTitle
                    ? track with { StartSeconds = start, EndSeconds = end }
                    : track with
                    {
                        StartSeconds = start,
                        EndSeconds = end,
                        Title = null,
                        Confidence = Math.Min(track.Confidence, 0.6),
                        Source = BoundarySource.Silence,
                    });
            }
            kept.AddRange(MergeSlivers(pieces, minTrackSeconds));
        }

        if (kept.Count == 0)
            return plan;   // trimming everything away means detection failed: keep the plan

        var renumbered = kept.Select((t, i) => t with { TrackNumber = (uint)(i + 1) }).ToList();
        return plan with { Tracks = renumbered };
    }

    /// <summary>
    /// Trims each track down to the music inside it, dropping the silence around it.
    ///
    /// <para>A detected boundary sits at the <i>quietest point</i> of the gap between two tracks. That
    /// is the middle of the silence, not its edge. That is right for a disc, where the gap is part of
    /// the album. It is wrong for a record, where it means every track opens with half of the gap
    /// before the music starts and closes with the other half. Trimming each track to its own sustained
    /// sound leaves the silence in neither. It also applies the same test that keeps a needle drop from
    /// being mistaken for the start of side one.</para>
    ///
    /// <para>Vinyl only. A track whose span holds no sustained sound is passed through untouched. So is
    /// a segue, where the sound never stops and there is nothing to trim.</para>
    /// </summary>
    public static SplitPlan TrimTracksToMusic(
        SplitPlan plan, float[] envelopeDb, double windowSeconds, SilenceDetectorOptions options,
        double minRunSeconds = DefaultMinRunSeconds, double preRollSeconds = DefaultPreRollSeconds)
    {
        ArgumentNullException.ThrowIfNull(envelopeDb);
        ArgumentNullException.ThrowIfNull(options);
        if (envelopeDb.Length == 0 || plan.Tracks.Count == 0)
            return plan;

        double threshold = SilenceDetector.EffectiveThresholdDb(envelopeDb, options);
        int minRun = Math.Max(1, (int)(minRunSeconds / windowSeconds));

        var trimmed = new List<SplitTrack>(plan.Tracks.Count);
        foreach (var track in plan.Tracks)
        {
            int from = Math.Clamp((int)(track.StartSeconds / windowSeconds), 0, envelopeDb.Length);
            int to = Math.Clamp((int)Math.Ceiling(track.EndSeconds / windowSeconds), 0, envelopeDb.Length);

            int first = FirstSustained(envelopeDb, from, to, threshold, minRun);
            int last = LastSustained(envelopeDb, from, to, threshold, minRun);
            if (first < 0 || last < 0 || last <= first)
            {
                trimmed.Add(track);   // nothing sustained in here, so leave it as it was
                continue;
            }

            trimmed.Add(track with
            {
                StartSeconds = Math.Max(track.StartSeconds, first * windowSeconds - preRollSeconds),
                EndSeconds = Math.Min(track.EndSeconds, (last + 1) * windowSeconds + preRollSeconds),
            });
        }
        return plan with { Tracks = trimmed };
    }

    /// <summary>Folds any piece shorter than the bar into the neighbour it touches, preferring the one
    /// before it. The last piece standing is never merged away, however short the side turned
    /// out.</summary>
    private static List<SplitTrack> MergeSlivers(List<SplitTrack> pieces, double minTrackSeconds)
    {
        int guard = pieces.Count;
        while (pieces.Count > 1 && guard-- > 0)
        {
            int i = pieces.FindIndex(p => p.DurationSeconds < minTrackSeconds);
            if (i < 0)
                break;

            if (i > 0)
                pieces[i - 1] = pieces[i - 1] with { EndSeconds = pieces[i].EndSeconds };
            else
                pieces[i + 1] = pieces[i + 1] with { StartSeconds = pieces[i].StartSeconds };
            pieces.RemoveAt(i);
        }
        return pieces;
    }
}
