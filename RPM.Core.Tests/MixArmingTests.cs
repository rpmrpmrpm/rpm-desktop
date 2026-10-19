using RPM.Core.Analysis;
using RPM.Core.Audio;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// Whether the changeover the set actually runs is the changeover the plan describes.
///
/// <para>The plan is not advice. It names the second the mix starts on, and the mix lab draws a mark
/// there, so a set that begins its changeover somewhere else has not merely mistimed something, it
/// has made every readout in the app a lie. The mark sits on the waveform, playback passes it
/// with the other deck empty, and the mix arrives later from nowhere.</para>
///
/// <para>The gap this catches is between two different answers to "when does this record hand over".
/// <see cref="TransitionPlanner.MixOutPoint"/> is the outro, and it is what the set watches for. A
/// plan's <see cref="TransitionPlan.StartAtSeconds"/> is where the changeover was laid out to begin,
/// which for anything that finishes ON the outro rather than starting there is a whole changeover
/// earlier.</para>
/// </summary>
public class MixArmingTests
{
    private const int Rate = 44100, Channels = 2, Block = 2205;
    private const int Bpm = 120;

    private sealed class ToneSource(double seconds, double tempoRatio) : IPcmSource
    {
        private long _left = (long)(seconds * Rate / Math.Max(0.001, tempoRatio));

        public string Label => "tone";

        public int Read(float[] buffer, int frames)
        {
            if (_left <= 0)
                return 0;

            int give = (int)Math.Min(frames, _left);
            for (int i = 0; i < give * Channels; i++)
                buffer[i] = 0.5f;
            _left -= give;
            return give;
        }

        public void Dispose() => _left = 0;
    }

    private static MixTrack Track(string name, double duration, double outro, double intro = 0)
    {
        var beats = new List<double>();
        for (double at = 0; at < duration; at += 60.0 / Bpm)
            beats.Add(at);

        string path = $"/library/{name}.flac";
        return new MixTrack
        {
            Source = path,
            Path = path,
            Title = name,
            TaggedDuration = duration,
            Analysis = new TrackAnalysis
            {
                FilePath = path,
                DurationSeconds = duration,
                Bpm = Bpm,
                BpmConfidence = 0.9,
                BeatTimes = beats,
                FirstDownbeat = 0,
                BeatsPerBar = 4,
                Key = new MusicalKey(9, true),
                KeyConfidence = 0.8,
                EnergyCurve = [0.3f, 0.3f],
                IntroEndSeconds = intro,
                OutroStartSeconds = outro,
                Phrases = new PhraseGrid(32, 0, 1),
            },
        };
    }

    /// <summary>What the set did: the plan it settled on, and where the record playing actually was
    /// when the changeover began.</summary>
    private static (TransitionPlan Plan, double Began, double Cued) Run(MixOverrides? asked)
    {
        var tracks = new[]
        {
            Track("out", duration: 300, outro: 200),
            Track("in", duration: 300, outro: 280),
        };

        using var dj = new Dj(
            new DjCrate(tracks), Rate, Channels,
            options => new ToneSource(300 - options.StartSeconds, options.TempoRatio),
            cueRunner: work => { work(); return Task.CompletedTask; },
            chance: () => 0.5)
        {
            Overrides = asked,
        };

        var block = new float[Block * Channels];
        TransitionPlan? plan = null;
        double began = -1, cued = -1;

        for (int i = 0; i < 40_000; i++)
        {
            if (dj.Read(block, Block) <= 0)
                break;

            plan ??= dj.PendingPlan;

            // Where the record playing was when the second deck first had a record on it at all.
            if (cued < 0 && dj.CuedTrack is not null)
                cued = dj.LivePositionSeconds;

            if (began < 0 && dj.ChangeoverProgress > 0)
            {
                began = dj.LivePositionSeconds;
                break;
            }
        }

        Assert.NotNull(plan);
        Assert.True(began > 0, "the set never began a changeover");
        return (plan, began, cued);
    }

    /// <summary>
    /// The ordinary changeover starts where its plan says: its start point and the point the set
    /// watches for are the same value.
    /// </summary>
    [Fact]
    public void The_beat_matched_blend_begins_where_its_plan_says()
    {
        var (plan, began, _) = Run(asked: null);

        Assert.Equal(TransitionStyle.BeatMatched, plan.Style);
        Assert.True(
            Math.Abs(began - plan.StartAtSeconds) <= plan.BarSeconds,
            $"the plan named {MixInputs.Sec(plan.StartAtSeconds)} and the changeover began at " +
            $"{MixInputs.Sec(began)}, {began - plan.StartAtSeconds:+0.0;-0.0}s out");
    }

    /// <summary>
    /// The phrase blend must also start where its plan says.
    ///
    /// <para>It is laid out to FINISH on the outro rather than start there, so its first bar is two
    /// phrases and a quiet-phrase search back from the point the set is watching for. If the set
    /// watched only for the outro, then by the time it cued a record the playback position would
    /// already be most of a minute past the second the plan named. A plan whose start is behind the
    /// playback position cannot be started at, so the mix would land on the next downbeat instead,
    /// with no lead-in and nowhere near its mark.</para>
    /// </summary>
    [Fact]
    public void The_phrase_blend_begins_where_its_plan_says()
    {
        var asked = new MixOverrides { Style = TransitionStyle.PhraseBlend };
        var (plan, began, _) = Run(asked);

        Assert.Equal(TransitionStyle.PhraseBlend, plan.Style);
        Assert.True(
            Math.Abs(began - plan.StartAtSeconds) <= plan.BarSeconds,
            $"the plan named {MixInputs.Sec(plan.StartAtSeconds)} and the changeover began at " +
            $"{MixInputs.Sec(began)}, {began - plan.StartAtSeconds:+0.0;-0.0}s out");
    }

    /// <summary>
    /// And the record coming in is on its deck before the changeover is drawn as starting, not after.
    ///
    /// <para>What the workbench shows is a mark on the outgoing record and a record on the second
    /// deck. If the deck is still empty when playback reaches the mark, everything on screen says a
    /// changeover is happening and nothing is.</para>
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData(TransitionStyle.PhraseBlend)]
    public void The_record_coming_in_is_cued_before_the_mark_it_is_drawn_against(TransitionStyle? style)
    {
        var asked = style is { } wanted ? new MixOverrides { Style = wanted } : null;
        var (plan, _, cued) = Run(asked);

        Assert.True(cued > 0, "nothing was ever cued");
        Assert.True(
            cued <= plan.StartAtSeconds,
            $"the changeover is drawn from {MixInputs.Sec(plan.StartAtSeconds)} and the record coming " +
            $"in did not reach its deck until {MixInputs.Sec(cued)}, " +
            $"{cued - plan.StartAtSeconds:+0.0;-0.0}s late");
    }
}
