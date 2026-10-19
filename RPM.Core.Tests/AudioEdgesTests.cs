using RPM.Core.Analysis;

namespace RPM.Core.Tests;

/// <summary>
/// Where the record is, as opposed to where the file is. This measurement keeps mixes from starting
/// too early or too late: a changeover timed against a length that is partly silence arrives after the
/// music has stopped, and a track started at zero on a file with silence in front of it comes in
/// before the music has started.
/// </summary>
public class AudioEdgesTests
{
    private const int Rate = 22050;

    /// <summary>A tone at a level well clear of the floor.</summary>
    private static void Fill(float[] into, int from, int count, double amplitude = 0.5)
    {
        for (int i = 0; i < count && from + i < into.Length; i++)
            into[from + i] = (float)(amplitude * Math.Sin(2 * Math.PI * 440 * i / Rate));
    }

    [Fact]
    public void Silence_in_front_of_a_record_is_not_the_record()
    {
        // One second of nothing, then two seconds of music.
        var mono = new float[Rate * 3];
        Fill(mono, Rate, Rate * 2);

        var edges = AudioEdges.Find(mono, Rate);

        Assert.True(edges.IsKnown);
        Assert.Equal(1.0, edges.StartSeconds, 1);
        Assert.Equal(3.0, edges.EndSeconds, 1);
    }

    [Fact]
    public void A_tail_of_nothing_is_not_the_record_either()
    {
        // Two seconds of music, then a second of the silence a master leaves behind. A mix-out timed
        // against the file's length starts a second late.
        var mono = new float[Rate * 3];
        Fill(mono, 0, Rate * 2);

        var edges = AudioEdges.Find(mono, Rate);

        Assert.Equal(0, edges.StartSeconds, 1);
        Assert.Equal(2.0, edges.EndSeconds, 1);
    }

    [Fact]
    public void A_record_that_runs_to_its_last_sample_ends_at_its_last_sample()
    {
        var mono = new float[Rate * 2];
        Fill(mono, 0, mono.Length);

        var edges = AudioEdges.Find(mono, Rate);

        Assert.Equal(0, edges.StartSeconds, 2);
        Assert.Equal(2.0, edges.EndSeconds, 1);
    }

    [Fact]
    public void The_floor_is_absolute_and_does_not_move_with_the_mastering()
    {
        // The same record, mastered twelve decibels quieter. A relative threshold (which is what the
        // cue reading uses) would answer differently for the two; this must not.
        var loud = new float[Rate * 3];
        Fill(loud, Rate, Rate * 2, amplitude: 0.8);

        var quiet = new float[Rate * 3];
        Fill(quiet, Rate, Rate * 2, amplitude: 0.2);

        Assert.Equal(AudioEdges.Find(loud, Rate), AudioEdges.Find(quiet, Rate));
    }

    [Fact]
    public void A_single_click_is_not_the_start_of_the_music()
    {
        var mono = new float[Rate * 3];
        mono[100] = 0.9f;                  // one sample of nothing in particular
        Fill(mono, Rate, Rate * 2);

        Assert.Equal(1.0, AudioEdges.Find(mono, Rate).StartSeconds, 1);
    }

    [Fact]
    public void A_quiet_fade_in_still_counts_as_the_record()
    {
        // Well below anything a cue point would call the body of the track, and well above silence.
        var mono = new float[Rate * 3];
        Fill(mono, Rate, Rate * 2, amplitude: 0.01);       // about -40 dBFS

        Assert.Equal(1.0, AudioEdges.Find(mono, Rate).StartSeconds, 1);
    }

    [Fact]
    public void A_silent_track_has_no_edges_rather_than_wrong_ones()
    {
        Assert.False(AudioEdges.Find(new float[Rate * 2], Rate).IsKnown);
        Assert.False(AudioEdges.Find([], Rate).IsKnown);
        Assert.False(AudioEdges.Find(new float[Rate], 0).IsKnown);
    }
}

/// <summary>
/// What the measured edges change about a mix. The grid is one straight line carried to both ends of
/// the file on purpose, and past the last note it goes on naming bar lines in silence, and a
/// changeover will wait for them.
/// </summary>
public class GridEdgeTests
{
    private static TrackAnalysis Record(double audioEnd = 0, double duration = 200) => new()
    {
        FilePath = "/music/a.flac",
        DurationSeconds = duration,
        Bpm = 120,
        BpmConfidence = 0.9,
        // A grid every half second from zero, running the whole 200 seconds.
        BeatTimes = [.. Enumerable.Range(0, 400).Select(i => i * 0.5)],
        FirstDownbeat = 0,
        BeatsPerBar = 4,
        AudioStartSeconds = audioEnd > 0 ? 0.5 : 0,
        AudioEndSeconds = audioEnd,
        BeatLastSeconds = audioEnd > 0 ? audioEnd - 10 : 0,
    };

    [Fact]
    public void A_changeover_is_not_timed_to_a_bar_line_in_dead_air()
    {
        // The music stops at 180; the file runs to 200. Every bar line the grid names after that is a
        // downbeat nobody will ever hear, and waiting for one is a mix arriving late.
        var record = Record(audioEnd: 180);

        Assert.Null(record.NextDownbeatAfter(180));
        Assert.Null(record.NextDownbeatAfter(190));
        Assert.NotNull(record.NextDownbeatAfter(100));

        // A line falling exactly on the last audible moment is inside the music, not after it.
        Assert.Equal(180, record.NextDownbeatAfter(179.9));
    }

    [Fact]
    public void The_last_bar_line_inside_the_music_is_still_offered()
    {
        var record = Record(audioEnd: 180);

        var line = record.NextDownbeatAfter(177);

        Assert.NotNull(line);
        Assert.True(line <= 180);
    }

    [Fact]
    public void A_record_whose_edges_were_never_measured_behaves_exactly_as_it_always_did()
    {
        // A row with no measured edges. Nothing may change how it is read.
        var old = Record();

        Assert.False(old.HasEdges);
        Assert.NotNull(old.NextDownbeatAfter(199));
        Assert.Equal(200, old.AudibleEndSeconds, 3);
        Assert.False(old.IsExtrapolatedAt(199));
    }

    [Fact]
    public void The_grid_says_which_part_of_itself_anybody_heard()
    {
        // This does not refuse anything: the line past the last tracked beat is usually right, and a
        // record's outro is exactly where a changeover needs a bar line. It only reports the fact.
        var record = Record(audioEnd: 180);

        Assert.False(record.IsExtrapolatedAt(100));
        Assert.True(record.IsExtrapolatedAt(175));
    }

    [Fact]
    public void Only_a_grid_carried_over_music_that_is_still_playing_is_worth_warning_about()
    {
        // The tracker gave up at 170; the music runs to 180 and the file to 200. The ten seconds
        // between 170 and 180 are bars counted over drums the tracker did not read, which is the case
        // worth reporting. Everything after 180 is the tail of the record, extrapolated on every
        // record, and a red warning on all of them would make the warning useless.
        var record = Record(audioEnd: 180);

        Assert.False(record.IsExtrapolatedOverMusicAt(100));
        Assert.True(record.IsExtrapolatedOverMusicAt(175));

        Assert.True(record.IsExtrapolatedAt(190));
        Assert.False(record.IsExtrapolatedOverMusicAt(190));
    }

    [Fact]
    public void A_record_whose_edges_were_never_measured_keeps_flagging()
    {
        // With no measured end the whole file counts as music, so nothing changes for a row that has
        // no measured edges.
        var old = Record();
        old = old with { BeatLastSeconds = 150 };

        Assert.False(old.HasEdges);
        Assert.True(old.IsExtrapolatedOverMusicAt(175));
    }

    [Fact]
    public void The_audible_end_is_the_measured_one_where_there_is_one()
    {
        Assert.Equal(180, Record(audioEnd: 180).AudibleEndSeconds, 3);
        Assert.Equal(200, Record().AudibleEndSeconds, 3);
    }
}
