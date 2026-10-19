using RPM.Core.Splitting;

namespace RPM.Core.Tests;

public class CueSheetParserTests
{
    private const string Classic = """
        REM GENRE Electronica
        PERFORMER "Daft Punk"
        TITLE "Discovery"
        FILE "discovery.flac" WAVE
          TRACK 01 AUDIO
            TITLE "One More Time"
            INDEX 01 00:00:00
          TRACK 02 AUDIO
            TITLE "Aerodynamic"
            PERFORMER "Daft Punk feat. Nobody"
            INDEX 00 05:15:30
            INDEX 01 05:20:37
          TRACK 03 AUDIO
            TITLE "Digital Love"
            INDEX 01 08:47:74
        """;

    [Fact]
    public void Parses_album_and_track_fields()
    {
        var cue = CueSheetParser.Parse(Classic)!;

        Assert.Equal("Daft Punk", cue.Performer);
        Assert.Equal("Discovery", cue.Title);
        Assert.Equal("discovery.flac", cue.AudioFileName);
        Assert.Equal(3, cue.Tracks.Count);
        Assert.Equal("One More Time", cue.Tracks[0].Title);
        Assert.Equal("Daft Punk feat. Nobody", cue.Tracks[1].Performer);
    }

    [Fact]
    public void Index01_wins_over_index00_and_frames_are_75ths()
    {
        var cue = CueSheetParser.Parse(Classic)!;

        Assert.Equal(0.0, cue.Tracks[0].Index01Seconds, 3);
        Assert.Equal(5 * 60 + 20 + 37 / 75.0, cue.Tracks[1].Index01Seconds, 3);
        Assert.Equal(8 * 60 + 47 + 74 / 75.0, cue.Tracks[2].Index01Seconds, 3);
    }

    [Fact]
    public void Index00_used_when_no_index01()
    {
        var cue = CueSheetParser.Parse("""
            FILE "x.mp3" MP3
              TRACK 01 AUDIO
                INDEX 00 00:10:00
            """)!;

        Assert.Equal(10.0, cue.Tracks[0].Index01Seconds, 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("REM nothing here")]
    [InlineData("TITLE \"album only, no tracks\"")]
    [InlineData("TRACK 01 AUDIO\n  TITLE \"no index\"")]
    public void Unusable_sheets_return_null(string text) =>
        Assert.Null(CueSheetParser.Parse(text));

    [Fact]
    public void Invalid_msf_values_are_rejected()
    {
        // 99 frames and 75 seconds are out of range; the track has no valid index.
        Assert.Null(CueSheetParser.Parse("""
            TRACK 01 AUDIO
              INDEX 01 00:75:00
            """));
        Assert.Null(CueSheetParser.Parse("""
            TRACK 01 AUDIO
              INDEX 01 00:00:99
            """));
    }

    [Fact]
    public void ToPlan_sets_ends_from_next_start_and_total()
    {
        var cue = CueSheetParser.Parse(Classic)!;
        var plan = CueSheetParser.ToPlan(cue, "/x/discovery.flac", ".flac", totalSeconds: 700);

        Assert.Equal(3, plan.Tracks.Count);
        Assert.Equal(plan.Tracks[1].StartSeconds, plan.Tracks[0].EndSeconds, 3);
        Assert.Equal(plan.Tracks[2].StartSeconds, plan.Tracks[1].EndSeconds, 3);
        Assert.Equal(700, plan.Tracks[2].EndSeconds, 3);
        Assert.All(plan.Tracks, t => Assert.Equal(BoundarySource.CueSheet, t.Source));
        Assert.Equal("Discovery", plan.Album);
        Assert.Equal("Daft Punk", plan.AlbumArtist);
        // Album performer flows to tracks without their own.
        Assert.Equal("Daft Punk", plan.Tracks[0].Artist);
        Assert.Equal("Daft Punk feat. Nobody", plan.Tracks[1].Artist);
    }

    [Fact]
    public void ToPlan_drops_zero_length_entries()
    {
        var cue = CueSheetParser.Parse("""
            FILE "x.mp3" MP3
              TRACK 01 AUDIO
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                INDEX 01 01:00:00
              TRACK 03 AUDIO
                INDEX 01 01:00:00
            """)!;
        var plan = CueSheetParser.ToPlan(cue, "/x/x.mp3", ".mp3", totalSeconds: 120);

        // Track 2 ends exactly at track 3's identical start → the zero-length entry is junk.
        Assert.Equal(2, plan.Tracks.Count);
        Assert.Equal(60, plan.Tracks[1].StartSeconds, 3);
        Assert.Equal(120, plan.Tracks[1].EndSeconds, 3);
    }
}
