using System.Globalization;
using RPM.Core.Analysis;
using RPM.Core.Lyrics;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// Choosing both ends of a changeover at once so that two singers never land on top of each other.
///
/// <para>The behaviours worth pinning: the standing answer is kept unless something is strictly better,
/// the record coming in can move as well as the one going out, and neither is allowed to wander further
/// than the bounds say.</para>
/// </summary>
public class VoicePlacementTests
{
    private const double Bpm = 128;
    private static readonly double Bar = 4 * 60.0 / Bpm;          // 1.875s
    private static readonly double Phrase = 8 * Bar;              // 15s

    /// <summary>A record with a grid on it and nothing else measured. The voices come from a sheet.</summary>
    private static TrackAnalysis Gridded(double duration, double introEnd = 0)
    {
        var beats = new List<double>();
        for (double at = 0; at < duration; at += 60.0 / Bpm)
            beats.Add(at);

        return new TrackAnalysis
        {
            FilePath = "/library/x.flac",
            DurationSeconds = duration,
            Bpm = Bpm,
            BpmConfidence = 0.9,
            BeatTimes = beats,
            FirstDownbeat = 0,
            BeatsPerBar = 4,          // and so BarsPerPhrase is 8, from PhraseBeats
            IntroEndSeconds = introEnd,
            AudioEndSeconds = duration,
            AudioStartSeconds = 0,
        };
    }

    private static VocalMap Sings(params (double From, double To)[] spans)
    {
        var lines = new List<string>();
        foreach (var (from, to) in spans)
        {
            var opens = from + VocalMap.Margin;
            var letters = (int)Math.Round((to - from - 2 * VocalMap.Margin) * LyricWindow.SlowestChars);
            lines.Add(string.Create(CultureInfo.InvariantCulture,
                $"[{(int)opens / 60:00}:{opens % 60:00.00}] {new string('a', Math.Max(1, letters))}"));
        }
        return VocalMap.FromSheet(LyricSheet.Parse(string.Join('\n', lines)));
    }

    private static readonly VocalMap Instrumental =
        VocalMap.FromSheet(LyricSheet.Parse("[00:00.00] ♪\n[09:00.00] ♪"));

    [Fact]
    public void Nothing_moves_when_the_standing_point_is_already_clean()
    {
        var placed = VoicePlacement.Choose(
            new(Gridded(300), Instrumental, 195, Phrase),
            new(Gridded(300), Instrumental, 30, Phrase),
            Phrase, VoicePlacement.CueIn.Free);

        Assert.True(placed.Unmoved);
        Assert.Equal(195, placed.StartAt, 3);
        Assert.Equal(30, placed.CueIn, 3);
        Assert.False(placed.Voices.Clash);
    }

    [Fact]
    public void Nothing_moves_when_neither_record_has_been_read()
    {
        var placed = VoicePlacement.Choose(
            new(Gridded(300), VocalMap.Unknown, 200, Phrase),
            new(Gridded(300), VocalMap.Unknown, 30, Phrase),
            Phrase, VoicePlacement.CueIn.Free);

        Assert.True(placed.Unmoved);
    }

    [Fact]
    public void The_record_coming_in_moves_rather_than_the_one_going_out()
    {
        // The outgoing record sings right where the changeover was going to happen, and so does the
        // first phrase of the incoming one. But the incoming record stops singing after a phrase, so
        // one step forward on THAT record fixes it, and nothing has to be given up on the other.
        var placed = VoicePlacement.Choose(
            new(Gridded(300), Sings((195, 230)), 200, Phrase),
            new(Gridded(300), Sings((28, 40)), 30, Phrase),
            Phrase, VoicePlacement.CueIn.Free);

        Assert.False(placed.Voices.Clash);
        Assert.Equal(0, placed.StepsBack);
        Assert.True(placed.StepsIn > 0, "the incoming cue should have moved");
        Assert.Equal(200, placed.StartAt, 3);        // where it was: candidates step from it
    }

    [Fact]
    public void The_record_going_out_moves_when_that_is_what_it_takes()
    {
        // The incoming record sings from its body start onwards for a long time, so moving it in does
        // not help. The outgoing record has a breakdown a couple of phrases back.
        var placed = VoicePlacement.Choose(
            new(Gridded(300), Sings((190, 260)), 200, Phrase),
            new(Gridded(300), Sings((30, 200)), 30, Phrase),
            Phrase, VoicePlacement.CueIn.Free);

        Assert.True(placed.StepsBack > 0, "the outgoing exit should have moved back");
        Assert.True(placed.StartAt < 200);
    }

    [Fact]
    public void Two_voices_is_escaped_even_at_the_cost_of_more_solo_singing()
    {
        // Standing: both sing over the whole changeover. Available: a point where only the outgoing
        // record sings, for the whole of it. More solo singing than some other candidate might have,
        // and still the right answer.
        var placed = VoicePlacement.Choose(
            new(Gridded(300), Sings((100, 260)), 200, Phrase),
            new(Gridded(300), Sings((25, 45)), 30, Phrase),
            Phrase, VoicePlacement.CueIn.Free);

        Assert.False(placed.Voices.Clash);
        Assert.True(placed.Voices.One > 0, "one of them is still singing, which is allowed");
    }

    [Fact]
    public void The_search_never_walks_back_into_the_outgoing_records_intro()
    {
        // Singing everywhere past the intro, so every candidate is bad and the search is pushed as far
        // back as it is allowed to go. It must stop at the intro rather than blending out of it.
        var from = Gridded(300, introEnd: 150);

        var placed = VoicePlacement.Choose(
            new(from, Sings((0, 300)), 200, Phrase),
            new(Gridded(300), Sings((0, 300)), 30, Phrase),
            Phrase, VoicePlacement.CueIn.Free);

        Assert.True(placed.StartAt >= 150 - 1e-6, $"blended out of the intro at {placed.StartAt}");
    }

    [Fact]
    public void The_search_never_cues_past_two_fifths_of_the_record_coming_in()
    {
        var placed = VoicePlacement.Choose(
            new(Gridded(300), Sings((0, 300)), 200, Phrase),
            new(Gridded(300), Sings((0, 300)), 30, Phrase),
            Phrase, VoicePlacement.CueIn.Free);

        Assert.True(placed.CueIn <= 300 * VoicePlacement.MostOfARecord + 1e-6,
            $"cued at {placed.CueIn}, which is past two fifths of the record");
    }

    [Fact]
    public void A_record_with_no_grid_keeps_the_point_it_was_given()
    {
        var ungridded = new TrackAnalysis { FilePath = "/library/x.flac", DurationSeconds = 300 };

        var placed = VoicePlacement.Choose(
            new(ungridded, Sings((190, 260)), 200, 0),
            new(ungridded, Sings((25, 60)), 30, 0),
            Phrase, VoicePlacement.CueIn.Free);

        Assert.True(placed.Unmoved);
    }

    [Fact]
    public void A_changeover_of_no_length_moves_nothing()
    {
        var placed = VoicePlacement.Choose(
            new(Gridded(300), Sings((190, 260)), 200, Phrase),
            new(Gridded(300), Sings((25, 60)), 30, Phrase),
            0, VoicePlacement.CueIn.Free);

        Assert.True(placed.Unmoved);
    }

    [Fact]
    public void The_new_records_voice_is_got_out_of_the_way_before_the_old_ones()
    {
        // Two escapes from the same clash, each one phrase away. Moving the cue in leaves the OLD
        // record singing alone; moving the exit back leaves the NEW one singing alone. They cost the
        // same amount of record, so the tie is broken on whose voice it is, and it should be the
        // record that is already leaving.
        //
        // Phrase lines are every 15s. Standing is (195, 30) and both sing across it.
        var placed = VoicePlacement.Choose(
            new(Gridded(300), Sings((195, 210)), 195, Phrase),
            new(Gridded(300), Sings((30, 45)), 30, Phrase),
            Phrase, VoicePlacement.CueIn.Free);

        Assert.False(placed.Voices.Clash);
        Assert.Equal(0, placed.Voices.Arriving, 1);                 // the new record is not singing
        Assert.True(placed.Voices.Leaving > 0, "the old one is, which is the right way round");
        Assert.Equal(195, placed.StartAt, 3);
        Assert.Equal(45, placed.CueIn, 3);
    }

    [Fact]
    public void A_solo_vocal_on_the_way_out_never_buys_a_phrase_of_record()
    {
        // Nothing to escape: the incoming record is instrumental, so no candidate clashes. The old
        // record sings over the changeover wherever it happens, and that is not a reason to give up a
        // phrase: a voice over the other record's instrumental is what a mix IS.
        var placed = VoicePlacement.Choose(
            new(Gridded(300), Sings((150, 260)), 195, Phrase),
            new(Gridded(300), Instrumental, 30, Phrase),
            Phrase, VoicePlacement.CueIn.Free);

        Assert.True(placed.Unmoved, $"moved to {placed.StartAt}/{placed.CueIn} for {placed.Voices}");
    }

    [Fact]
    public void A_fixed_cue_is_not_moved_however_much_it_would_help()
    {
        // The ordinary changeover's cue is what makes the new record's drop land as the fade finishes.
        // Moving it here would clear the clash and throw the arrival away, so it may not be moved and
        // the exit has to do the whole job.
        var placed = VoicePlacement.Choose(
            new(Gridded(300), Sings((195, 210)), 195, Phrase),
            new(Gridded(300), Sings((30, 45)), 30, Phrase),
            Phrase, VoicePlacement.CueIn.Fixed);

        Assert.Equal(30, placed.CueIn, 3);
        Assert.Equal(0, placed.StepsIn);
        Assert.True(placed.StepsBack > 0, "the exit had to move instead");
        Assert.False(placed.Voices.Clash);
    }

    [Fact]
    public void A_fixed_cue_that_cannot_be_escaped_keeps_the_standing_point()
    {
        // Both records sing everywhere. There is no escape with the cue fixed, and the answer is the
        // point the planner already chose rather than a worse one eight phrases back.
        var placed = VoicePlacement.Choose(
            new(Gridded(300), Sings((0, 300)), 195, Phrase),
            new(Gridded(300), Sings((0, 300)), 30, Phrase),
            Phrase, VoicePlacement.CueIn.Fixed);

        Assert.True(placed.Unmoved, $"moved to {placed.StartAt} for {placed.Voices}");
    }
}
