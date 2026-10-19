namespace RPM.Core.Analysis;

/// <summary>
/// Everything around the beat model that is not the model itself: how a track that is too long to
/// read at once is cut into windows, how the results of the windows are joined together, and how a
/// curve of per-frame beat scores becomes a list of beats.
///
/// <para>It is separate from the runtime that holds the weights, so it can be tested directly
/// against the reference implementation. <c>BeatModelDecoderTests</c> replays logits recorded from
/// a real track and checks that the beats are the ones the reference produced from the same
/// numbers. Nothing here is arbitrary: each step matches how the model was evaluated, and a mistake
/// moves beats without raising an error.</para>
/// </summary>
public static class BeatModelDecoder
{
    /// <summary>Frames per call. The model is a transformer trained on thirty second excerpts, and
    /// a track is longer than that.</summary>
    public const int ChunkFrames = 1500;

    /// <summary>Frames discarded at each end of every window. The loss used to train the model
    /// ignored the edges of its input, so what the model says there is not reliable. Consecutive
    /// windows overlap by this many frames to cover the gap.</summary>
    public const int BorderFrames = 6;

    /// <summary>Frames that each window advances: the window length minus the overlap at both
    /// ends.</summary>
    public const int StrideFrames = ChunkFrames - 2 * BorderFrames;

    /// <summary>Marks "no window covered this frame" while the windows are joined. It is far enough
    /// below zero to lose every comparison, including the peak test.</summary>
    public const float Unread = -1000;

    /// <summary>How far apart two peaks must be to count as two beats: 3 frames, so 60 ms on either
    /// side. No track has two beats closer together than that.</summary>
    private const int PeakRadius = 3;

    /// <summary>Reads one window of the spectrogram, starting at the given frame. The start may be
    /// negative or run past the end, and the parts outside the track are silence.</summary>
    public delegate (float[] Beat, float[] Downbeat) ReadWindow(int startFrame);

    /// <summary>
    /// Where each window starts, as a frame index into a spectrogram <paramref name="frames"/> long.
    ///
    /// <para>The first start is negative: the first window begins before the track, so that the
    /// border it discards falls outside the music and not over the first beat.</para>
    /// </summary>
    public static IReadOnlyList<int> WindowStarts(int frames)
    {
        var starts = new List<int>();
        for (int start = -BorderFrames; start < frames - BorderFrames; start += StrideFrames)
            starts.Add(start);

        if (starts.Count == 0)
            return [-BorderFrames];

        // The last window is moved back so that it ends with the track and does not run past it. A
        // window that goes past the end is mostly padding, and the model reads padding as silence,
        // which means it would read the track as stopping early.
        if (frames > StrideFrames)
            starts[^1] = frames - (ChunkFrames - BorderFrames);
        return starts;
    }

    /// <summary>
    /// The beats and bar lines of a spectrogram <paramref name="frames"/> long, read one window at a
    /// time through <paramref name="read"/>.
    /// </summary>
    /// <param name="reader">How the two curves of scores become beats and bar lines. When it is
    /// null, <see cref="Peaks"/> is used, which does what the reference implementation's minimal
    /// post-processing does and what <c>BeatModelDecoderTests</c> checks.
    /// <see cref="BarDbn"/> is the other option.</param>
    public static BeatReading Decode(
        int frames, ReadWindow read, CancellationToken ct = default, Reader? reader = null)
    {
        var (beatLogits, downbeatLogits) = ReadLogits(frames, read, ct);
        return (reader ?? Peaks)(beatLogits, downbeatLogits);
    }

    /// <summary>
    /// Turns the model's two curves of per-frame scores into the track's beats and bar lines.
    ///
    /// <para>It handles both at once, not in two calls, because the better readers do not separate
    /// them. A bar line is a beat, and a search that decides which beat of the bar a frame is on
    /// settles both together, instead of picking each and reconciling them afterwards.</para>
    /// </summary>
    public delegate BeatReading Reader(float[] beatLogits, float[] downbeatLogits);

    /// <summary>
    /// The reader the app uses, and the others that a measurement can ask for by name.
    ///
    /// <list type="table">
    /// <item><term>(anything else)</term><description><see cref="Counted"/>: the bar search, its octave checked</description></item>
    /// <item><term><c>bars</c></term><description><see cref="Bars"/>: the bar search alone</description></item>
    /// <item><term><c>beat</c></term><description><see cref="BeatDbn"/> for the beats, peaks for the bar lines</description></item>
    /// <item><term><c>peaks</c></term><description><see cref="Peaks"/>, the reference implementation's minimal post-processing</description></item>
    /// </list>
    ///
    /// <para>The choice is made here and not beside either model, because every reader of a track
    /// must agree. The weights can run on this machine or be asked of the store, and the numbers are
    /// the same either way. A track read one way and then the other must give the same grid, because
    /// the grids go into one shared pool, and two different readings of one track cannot be
    /// reconciled there.</para>
    ///
    /// <para>On the 661 dance tracks of GiantSteps Tempo (see bench/RESULTS.md), the search improves
    /// Accuracy1 by nine points and Accuracy2 by eleven points compared with peak picking. The
    /// reason is not a better tempo reading, since both agree on tempo. The reason is that peak
    /// picking can give the grid two beats eighty milliseconds apart, which a real track cannot
    /// play, and everything later is built on a grid that never existed. The <em>bar</em> search
    /// improves on the beat search in the bar line, which peak picking reads worst and which every
    /// fade in a mix is measured in. The paper for Beat This! reports downbeat continuity on GTZAN
    /// going from 67.3 to 73.3 CMLt and from 79.1 to 87.8 AMLt with it.</para>
    /// </summary>
    /// <param name="name">The value of <c>RPM_BEAT_DECODER</c>, or null for the app's own choice.</param>
    public static Reader Chosen(string? name) =>
        name?.Trim().ToLowerInvariant() switch
        {
            "peaks" => Peaks,
            "beat" => (beat, bar) => WithBars(BeatDbn.Beats(beat, MelSpectrogram.FrameRate), bar),
            "bars" => Bars,
            _ => Counted,
        };

    /// <summary>
    /// The score threshold for "maybe a beat", as opposed to the places where the model was
    /// <em>sure</em> of a beat. It is well below zero, so a reading at this threshold contains every
    /// beat of the track and also some things that are not beats.
    /// </summary>
    private const double MaybeABeat = -0.5;

    /// <summary>
    /// The bar search, with its octave checked against a reading that keeps every "maybe" beat.
    ///
    /// <para>The search reads a track at one tempo. It is usually right about the beat, but it can be
    /// wrong about which beats to count. On GiantSteps Tempo, 56 of the 125 wrong readings are
    /// exactly half or double the true tempo. The evidence that settles this is available and was
    /// not used by the search: the frames to which the model gave a middling score. A track that is
    /// counted at half speed has a "maybe" halfway between every pair of beats, all the way through,
    /// and a track that really has that tempo does not.</para>
    ///
    /// <para>So the two readings are compared, and the search's beats are moved by a power of two to
    /// agree with the dense reading. The bounds are loose on purpose. The dense reading picks up
    /// things that are not beats and so runs fast, and the decision is about an octave, not a
    /// tempo. Anything between two thirds and one and a half of the dense reading is left
    /// alone.</para>
    ///
    /// <para><b>The gain.</b> On GiantSteps Tempo, Accuracy1 improves by 1.5 points: thirteen of the
    /// forty-three tracks read at half the true tempo are corrected, at the cost of one track read
    /// at double and two that fall off the octave ladder entirely. On GTZAN it changes Accuracy1 by
    /// nothing and Accuracy2 by a tenth of a point, which is two tracks. So it is a gain on the
    /// music this app is for, and costs nothing on other music.</para>
    ///
    /// <para>The idea comes from the reconciliation in the benchmark script attached to
    /// CPJKU/beat_this#13, where a dense reading and a confident reading are fitted separately and
    /// the confident one is moved into the octave of the dense one. Here it is applied to a search
    /// and not to a second peak-picking pass, and it moves the beats and not only the tempo number,
    /// because everything later counts bars from the grid.</para>
    /// </summary>
    public static BeatReading Counted(float[] beatLogits, float[] downbeatLogits)
    {
        var reading = Bars(beatLogits, downbeatLogits);
        double searched = MedianGap(reading.BeatTimes);
        double dense = MedianGap(PickPeaks(beatLogits, MaybeABeat));
        if (searched <= 0 || dense <= 0)
            return reading;

        // The comparison uses periods and not tempi, so "twice as fast" means "half the gap".
        int octaves = 0;
        for (double gap = searched; gap > dense * 1.5 && octaves < 2; gap /= 2)
            octaves++;
        for (double gap = searched; gap < dense * 0.66 && octaves > -2; gap *= 2)
            octaves--;

        return octaves switch
        {
            0 => reading,
            > 0 => Split(reading, octaves),
            _ => Thin(reading, -octaves),
        };
    }

    /// <summary>The median gap between consecutive beats, or zero when there are too few beats to
    /// say.</summary>
    private static double MedianGap(IReadOnlyList<double> beats)
    {
        if (beats.Count < MinBeats)
            return 0;

        var gaps = new List<double>(beats.Count - 1);
        for (int i = 1; i < beats.Count; i++)
            if (beats[i] - beats[i - 1] > 0)
                gaps.Add(beats[i] - beats[i - 1]);

        if (gaps.Count == 0)
            return 0;

        gaps.Sort();
        return gaps[gaps.Count / 2];
    }

    /// <summary>Fewer beats than this is not a track with a tempo, and not something to move by an
    /// octave. It is the same eight beats that <see cref="ModelBeatGrid.MinBeats"/> requires.</summary>
    private const int MinBeats = 8;

    /// <summary>The track counted twice as fast, once per octave: a beat is added halfway between
    /// every pair. The bar lines do not move, because they are still bar lines on beats that are
    /// still there. The bar simply has twice as many beats, which is what counting a track twice as
    /// fast means.</summary>
    private static BeatReading Split(BeatReading reading, int octaves)
    {
        var beats = reading.BeatTimes;
        for (int n = 0; n < octaves; n++)
        {
            var finer = new List<double>(beats.Count * 2);
            for (int i = 0; i < beats.Count; i++)
            {
                finer.Add(beats[i]);
                if (i + 1 < beats.Count)
                    finer.Add((beats[i] + beats[i + 1]) / 2);
            }
            beats = finer;
        }

        return new BeatReading { BeatTimes = beats, DownbeatTimes = reading.DownbeatTimes };
    }

    /// <summary>
    /// The track counted half as fast, once per octave: every second beat is kept.
    ///
    /// <para>Which beats are kept matters. The bar lines must survive, because the search knows them
    /// better than any other source here. So the kept beats are the ones with the same parity as the
    /// first bar line, and a bar line that falls on a kept beat stays a bar line.</para>
    /// </summary>
    private static BeatReading Thin(BeatReading reading, int octaves)
    {
        var beats = reading.BeatTimes;
        var bars = reading.DownbeatTimes;

        for (int n = 0; n < octaves; n++)
        {
            int phase = bars.Count > 0 ? Math.Max(0, NearestIndex(beats, bars[0])) % 2 : 0;

            var coarser = new List<double>(beats.Count / 2 + 1);
            for (int i = phase; i < beats.Count; i += 2)
                coarser.Add(beats[i]);

            if (coarser.Count < MinBeats)
                return reading;

            var kept = new List<double>(bars.Count);
            foreach (double bar in bars)
                if (coarser.BinarySearch(bar) >= 0)
                    kept.Add(bar);

            beats = coarser;
            bars = kept;
        }

        return new BeatReading { BeatTimes = beats, DownbeatTimes = bars };
    }

    /// <summary>The index of the beat that is closest to a time.</summary>
    private static int NearestIndex(IReadOnlyList<double> beats, double seconds)
    {
        int best = -1;
        double nearest = double.MaxValue;
        for (int i = 0; i < beats.Count; i++)
        {
            double off = Math.Abs(beats[i] - seconds);
            if (off < nearest)
                (nearest, best) = (off, i);
        }
        return best;
    }

    /// <summary>
    /// The bar search, falling back to the beat search and then to the peaks when the search gives
    /// no result.
    ///
    /// <para>The search gives no result on a track that is quiet everywhere (it trims to the part
    /// above its threshold, and there may be none) and on a track where the calculation runs out of
    /// range. In both cases a reading of the beats alone is still worth having, so the analyser is
    /// not left with nothing.</para>
    /// </summary>
    public static BeatReading Bars(float[] beatLogits, float[] downbeatLogits)
    {
        var bars = BarDbn.Read(beatLogits, downbeatLogits, MelSpectrogram.FrameRate);
        if (bars.BeatTimes.Length > 0)
            return new BeatReading { BeatTimes = bars.BeatTimes, DownbeatTimes = bars.DownbeatTimes };

        var beats = BeatDbn.Beats(beatLogits, MelSpectrogram.FrameRate);
        return beats.Length > 0 ? WithBars(beats, downbeatLogits) : Peaks(beatLogits, downbeatLogits);
    }

    /// <summary>A set of beats with the bar-line peaks moved onto them. The bar lines are moved onto
    /// <em>these</em> beats and not onto any other reading of them, because everything later counts
    /// a bar as a number of beats on the grid, and a bar line that is not one of the beats cannot be
    /// counted.</summary>
    private static BeatReading WithBars(double[] beats, float[] downbeatLogits) => new()
    {
        BeatTimes = beats,
        DownbeatTimes = SnapToBeats(PickPeaks(downbeatLogits), beats),
    };

    /// <summary>
    /// The peaks of each curve, with the bar lines moved onto the nearest beat.
    ///
    /// <para>This is the reference implementation's own minimal post-processing. It is the default
    /// because the decoder's tests are written against it.</para>
    /// </summary>
    public static BeatReading Peaks(float[] beatLogits, float[] downbeatLogits)
    {
        var beats = PickPeaks(beatLogits);
        return new BeatReading
        {
            BeatTimes = beats,
            DownbeatTimes = SnapToBeats(PickPeaks(downbeatLogits), beats),
        };
    }

    /// <summary>
    /// What the model said about every frame of the track, joined from its windows: one score per
    /// frame for the beats and one for the bar lines, on the logit scale.
    /// </summary>
    public static (float[] Beat, float[] Downbeat) ReadLogits(
        int frames, ReadWindow read, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(read);

        var beatLogits = new float[frames];
        var downbeatLogits = new float[frames];
        Array.Fill(beatLogits, Unread);
        Array.Fill(downbeatLogits, Unread);

        // Windows are written last first, so where two windows overlap, the earlier one wins. A
        // frame near the start of a window has more context to its right than a frame near the end
        // of a window has to its left, so the earlier window gives the better reading.
        var starts = WindowStarts(frames);
        for (int w = starts.Count - 1; w >= 0; w--)
        {
            // Between windows is the only place where the work can be cancelled. One window takes
            // half a second on a fast machine and several times that on a slow one, and a four
            // minute track is nine windows. Without this check, a set that was stopped, or a
            // playlist that the listener left, would still be read for a long time.
            ct.ThrowIfCancellationRequested();

            int start = starts[w];
            var (beat, downbeat) = read(start);

            // The borders are dropped here and not at inference. The window includes them because
            // they are context for the frames in the middle.
            for (int i = BorderFrames; i < ChunkFrames - BorderFrames; i++)
            {
                int at = start + i;
                if (at < 0 || at >= frames)
                    continue;
                beatLogits[at] = beat[i];
                downbeatLogits[at] = downbeat[i];
            }
        }

        return (beatLogits, downbeatLogits);
    }

    /// <summary>
    /// The times, in seconds, where the model is both sure and has a local maximum.
    ///
    /// <para>Both conditions are needed. A local maximum makes the broad hump that the model puts
    /// around a beat give one beat and not five. Above zero (where the score crosses even odds)
    /// stops a track that fades out from leaving a ridge of "maybe" scores that would produce beats
    /// after the music has stopped.</para>
    /// </summary>
    public static double[] PickPeaks(float[] logits, double threshold = 0)
    {
        ArgumentNullException.ThrowIfNull(logits);

        var frames = new List<double>();
        for (int i = 0; i < logits.Length; i++)
        {
            if (logits[i] <= threshold)
                continue;

            bool isPeak = true;
            int from = Math.Max(0, i - PeakRadius), to = Math.Min(logits.Length - 1, i + PeakRadius);
            for (int j = from; j <= to && isPeak; j++)
                if (logits[j] > logits[i])
                    isPeak = false;

            if (isPeak)
                frames.Add(i);
        }

        return Merge(frames);
    }

    /// <summary>
    /// A run of neighbouring frames becomes the single frame in its middle.
    ///
    /// <para>A flat top (two adjacent frames with exactly equal scores) passes the peak test twice,
    /// and would otherwise give two beats forty milliseconds apart.</para>
    ///
    /// <para>Each frame is compared with the running average of the group so far, not with the
    /// previous frame. This is how the reference does it, and it is a different rule. For frames 10,
    /// 11 and 12, the average has already moved to 10.5 when 12 is considered, so 12 is one and a
    /// half frames away and starts a group of its own. Comparing with the previous frame would join
    /// all three, and a chain of adjacent frames would become a single beat however long it
    /// was.</para>
    /// </summary>
    private static double[] Merge(List<double> frames)
    {
        if (frames.Count == 0)
            return [];

        var times = new List<double>(frames.Count);
        double group = frames[0];
        int size = 1;

        for (int i = 1; i < frames.Count; i++)
        {
            if (frames[i] - group <= 1)
            {
                size++;
                group += (frames[i] - group) / size;    // the average, updated in place
            }
            else
            {
                times.Add(group);
                group = frames[i];
                size = 1;
            }
        }
        times.Add(group);

        for (int i = 0; i < times.Count; i++)
            times[i] /= MelSpectrogram.FrameRate;
        return [.. times];
    }

    /// <summary>
    /// Moves every bar line onto the nearest beat, and removes any that land on the same beat.
    ///
    /// <para>The beats and the bar lines are read from separate outputs and can differ by a frame.
    /// A bar line that is not exactly a beat cannot be counted in beats, and the whole grid that
    /// follows is built on counting in beats.</para>
    /// </summary>
    public static double[] SnapToBeats(double[] downbeats, double[] beats)
    {
        if (beats.Length == 0 || downbeats.Length == 0)
            return [];

        var snapped = new List<double>(downbeats.Length);
        foreach (double at in downbeats)
        {
            int nearest = Array.BinarySearch(beats, at);
            if (nearest < 0)
            {
                int after = ~nearest;
                int before = after - 1;
                nearest = after >= beats.Length ? before
                    : before < 0 ? after
                    : at - beats[before] <= beats[after] - at ? before : after;
            }

            if (snapped.Count == 0 || snapped[^1] != beats[nearest])
                snapped.Add(beats[nearest]);
        }
        return [.. snapped];
    }
}
