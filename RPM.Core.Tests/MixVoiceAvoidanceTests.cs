using System.Globalization;
using RPM.Core.Analysis;
using RPM.Core.Lyrics;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// The voice rule on the changeover that most sets use.
///
/// <para><see cref="MixPolicy.PhraseBlend"/> is off by default, so an automatic set plays the ordinary
/// beat-matched changeover. These tests check the voice rule on that changeover: the exit moves off a
/// vocal, the incoming cue never moves (its drop is timed against the fade), and where a record has a
/// lyric sheet, the sheet is trusted over the bands.</para>
/// </summary>
public class MixVoiceAvoidanceTests
{
    private const int Bpm = 120;                    // a beat every half second, a bar every two
    private const int BeatsPerBar = 4;

    /// <summary>The default policy, with no phrase blend. This is what an automatic set uses.</summary>
    private static readonly MixPolicy Ordinary = new();

    private static MixTrack Track(
        string name, double duration, double outro, double intro = 0,
        Func<double, bool>? singingAt = null, LyricSheet? sheet = null)
    {
        var beats = new List<double>();
        for (double at = 0; at < duration; at += 60.0 / Bpm)
            beats.Add(at);

        var voice = new List<float>();
        var bass = new List<float>();
        if (singingAt is not null)
            for (double at = 0; at < duration; at += EnergyProfile.StepSeconds)
            {
                voice.Add(singingAt(at) ? 0.8f : 0.05f);
                bass.Add(0.8f);
            }

        var path = $"/library/{name}.flac";
        return new MixTrack
        {
            Source = path,
            Path = path,
            Title = name,
            TaggedDuration = duration,
            Sheet = sheet,
            Analysis = new TrackAnalysis
            {
                FilePath = path,
                DurationSeconds = duration,
                Bpm = Bpm,
                BpmConfidence = 0.9,
                BeatTimes = beats,
                FirstDownbeat = 0,
                BeatsPerBar = BeatsPerBar,
                Key = new MusicalKey(9, true),
                KeyConfidence = 0.8,
                EnergyCurve = [0.3f, 0.3f],
                IntroEndSeconds = intro,
                OutroStartSeconds = outro,
                VocalCurve = voice,
                BassCurve = bass,
            },
        };
    }

    /// <summary>A sheet that has singing over the given stretches and nowhere else.</summary>
    private static LyricSheet Sheet(params (double From, double To)[] spans)
    {
        var lines = new List<string> { "[00:00.00] ♪" };
        foreach (var (from, to) in spans)
        {
            var opens = from + VocalMap.Margin;
            var letters = (int)Math.Round((to - from - 2 * VocalMap.Margin) * LyricWindow.SlowestChars);
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"[{(int)opens / 60:00}:{opens % 60:00.00}] {new string('a', Math.Max(1, letters))}"));
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"[{(int)to / 60:00}:{to % 60:00.00}] ♪"));
        }
        return LyricSheet.Parse(string.Join('\n', lines));
    }

    [Fact]
    public void The_ordinary_changeover_moves_off_a_vocal()
    {
        // The outgoing record sings over the last minute of its body and is quiet before that.
        // Without the rule, the changeover would happen at its outro, inside the singing.
        var outgoing = Track("out", duration: 300, outro: 200, singingAt: at => at >= 150);
        var incoming = Track("in", duration: 300, outro: 280, singingAt: _ => true);

        var plan = TransitionPlanner.Plan(outgoing, Bpm, incoming, new MixOverrides { Policy = Ordinary });

        Assert.True(plan.StartAtSeconds < 150,
            $"handed over at {plan.StartAtSeconds:F1}s, still inside the vocal from 150s");
        Assert.Contains(plan.Trace, t => t.Contains("voices: out "));
    }

    [Fact]
    public void The_ordinary_changeover_never_moves_the_incoming_cue()
    {
        // Its cue makes the new record's drop land as the fade finishes. Whatever the voices say, it
        // stays where the planner put it: only the exit moves.
        var outgoing = Track("out", duration: 300, outro: 200, singingAt: at => at >= 150);
        var incoming = Track("in", duration: 300, outro: 280, intro: 20, singingAt: _ => true);

        var moved = TransitionPlanner.Plan(
            outgoing, Bpm, incoming, new MixOverrides { Policy = Ordinary });

        // The same pair with no singing on the way out: the rule has nothing to do, so the cue is
        // the unchanged one. The two cues must be equal.
        var still = TransitionPlanner.Plan(
            Track("out", duration: 300, outro: 200, singingAt: _ => false), Bpm, incoming,
            new MixOverrides { Policy = Ordinary });

        Assert.Equal(still.CueInSeconds, moved.CueInSeconds, 6);
    }

    [Fact]
    public void A_sheet_is_believed_over_the_bands()
    {
        // The bands say this record sings throughout; its sheet says it sings only up to 100 s. The
        // changeover is placed by the stretch the two AGREE on, which here is the sheet's: a reading
        // that claims the whole record is narrowed to what the other one claims. So there is nothing
        // to move off after 100 s and the changeover stays late in the record.
        var outgoing = Track(
            "out", duration: 300, outro: 200, singingAt: _ => true, sheet: Sheet((10, 100)));
        var incoming = Track("in", duration: 300, outro: 280, singingAt: _ => true);

        var plan = TransitionPlanner.Plan(outgoing, Bpm, incoming, new MixOverrides { Policy = Ordinary });

        Assert.Contains(plan.Trace, t => t.Contains("sheet and bands"));
        Assert.True(plan.StartAtSeconds > 150,
            $"moved to {plan.StartAtSeconds:F1}s, though its sheet says nobody sings after 100s");
    }

    [Fact]
    public void An_instrumental_incoming_record_lets_the_changeover_stay_where_it_was()
    {
        // Nothing to clash with, so the outgoing record's own vocal is not a reason to give up a
        // phrase. A voice over the other record's instrumental is a normal mix.
        var outgoing = Track("out", duration: 300, outro: 200, singingAt: at => at >= 150);
        var incoming = Track(
            "in", duration: 300, outro: 280, singingAt: _ => true,
            sheet: LyricSheet.Parse("[00:00.00] ♪\n[04:00.00] ♪"));

        var plan = TransitionPlanner.Plan(outgoing, Bpm, incoming, new MixOverrides { Policy = Ordinary });

        Assert.Contains(plan.Trace, t => t.Contains("instrumental"));
        Assert.True(plan.StartAtSeconds >= 150,
            $"gave up record at {plan.StartAtSeconds:F1}s for a vocal that clashes with nothing");
    }

    [Fact]
    public void An_unread_pair_is_planned_exactly_as_before()
    {
        var outgoing = Track("out", duration: 300, outro: 200);
        var incoming = Track("in", duration: 300, outro: 280);

        var plan = TransitionPlanner.Plan(outgoing, Bpm, incoming, new MixOverrides { Policy = Ordinary });

        Assert.Contains(plan.Trace, t => t.Contains("neither record read"));
    }
}
