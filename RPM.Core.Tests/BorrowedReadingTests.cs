using RPM.Core.Analysis;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// What a record does when the reading it is carrying turns out to be a reading of a different
/// record.
///
/// <para>Not a hypothetical. The shared pool is asked under a bundle of names, and one of those
/// names (the song id the identification service returns) is matched by ear rather than by
/// equality. Where it gives two different recordings the same number, the pool fuses them, and
/// afterwards either one's reading is served for the other: a complete, confident analysis of a
/// seven-minute record stored against a three-minute file. A mix lab drawing that gets a timeline
/// longer than the file, bar lines counted over audio nobody has, and a changeover timed a minute
/// past the end of the music.</para>
/// </summary>
public class BorrowedReadingTests
{
    private static TrackAnalysis Reading(double duration) => new()
    {
        FilePath = "/music/a.mp3",
        DurationSeconds = duration,
        Bpm = 122,
        BpmConfidence = 0.9,
        BeatTimes = [.. Enumerable.Range(0, 64).Select(i => i * 0.5)],
        BeatsPerBar = 4,
    };

    private static MixTrack Track(TrackAnalysis? analysis, double tagged) => new()
    {
        Path = "/music/a.mp3",
        Source = "/music/a.mp3",
        Title = "a",
        TaggedDuration = tagged,
        Analysis = analysis,
    };

    [Fact]
    public void A_reading_of_a_longer_record_does_not_stretch_this_one()
    {
        // The file plays 180.9s. The reading came back describing 255.6s of audio, which is not this
        // record whatever the pool called it.
        var track = Track(Reading(255.6), tagged: 180.9);

        Assert.Equal(180.9, track.DurationSeconds, 3);
    }

    [Fact]
    public void A_reading_of_a_shorter_record_does_not_cut_this_one_short()
    {
        var track = Track(Reading(180.9), tagged: 255.6);

        Assert.Equal(255.6, track.DurationSeconds, 3);
    }

    [Fact]
    public void The_measured_length_is_still_preferred_where_the_two_agree()
    {
        // Encoder padding and a header's rounding put the two a fraction apart on every record there
        // is. Inside that, the analysis is the better number: it was counted off the samples.
        var track = Track(Reading(181.028), tagged: 180.873);

        Assert.Equal(181.028, track.DurationSeconds, 3);
    }

    [Fact]
    public void A_record_nobody_has_read_still_answers_from_its_tags()
    {
        Assert.Equal(180.9, Track(null, tagged: 180.9).DurationSeconds, 3);
    }

    [Fact]
    public void An_untagged_record_still_answers_from_its_reading()
    {
        // A row adopted before the file was scanned has no tagged length to check against, and half
        // an answer is better than none.
        Assert.Equal(255.6, Track(Reading(255.6), tagged: 0).DurationSeconds, 3);
    }

    [Fact]
    public void A_borrowed_length_cannot_time_a_changeover_past_the_end_of_the_file()
    {
        // What the fault actually costs. AudibleEndSeconds is what a changeover may not be timed
        // past, and it is measured from the duration, so a duration 75 seconds too long gives the
        // planner 75 seconds of record that does not exist.
        var track = Track(Reading(255.6), tagged: 180.9);

        Assert.True(track.AudibleEndSeconds <= 180.9 + TrackAnalysis.SameRecordSeconds);
    }
}
