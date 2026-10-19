using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>
/// The sheet of everything known about one track. The rule the whole thing turns on is that an
/// unknown fact gets no line. A sheet that lists "Conductor: unknown" and "ISRC: unknown" for the
/// ninety-nine records in a hundred that have neither is a sheet nobody reads to the end, and the
/// one record that DOES carry a conductor is exactly where the line matters.
/// </summary>
public class TrackFactsTests
{
    private static readonly TrackFactFormats Formats = new(
        Duration: s => $"{(int)s / 60}:{(int)s % 60:00}",
        Size: b => $"{b} B",
        Timestamp: ticks => new DateTime(ticks, DateTimeKind.Utc).ToString("yyyy-MM-dd"),
        OfCount: (position, count) => count > 0 ? $"{position} of {count}" : position.ToString());

    /// <summary>An ordinary album track. Each test says "with" and the two or three fields it is
    /// actually about.</summary>
    private static readonly TrackInfo Tags = new()
    {
        FilePath = "/library/Fleetwood Mac/Rumours/02 Dreams.flac",
        Extension = ".flac",
        Title = "Dreams",
        Artist = "Fleetwood Mac",
        AlbumArtist = "Fleetwood Mac",
        Album = "Rumours",
    };

    private static LibraryTrack Row() => new()
    {
        RelativePath = "Fleetwood Mac/Rumours/02 Dreams.flac",
        Folder = "Fleetwood Mac/Rumours",
        FileName = "02 Dreams.flac",
        Extension = ".flac",
    };

    private static string? Value(IReadOnlyList<TrackFact> facts, string key) =>
        facts.FirstOrDefault(f => f.LabelKey == key)?.Value;

    private static bool Has(IReadOnlyList<TrackFact> facts, string key) =>
        facts.Any(f => f.LabelKey == key);

    [Fact]
    public void Nothing_known_is_an_empty_sheet() =>
        Assert.Empty(TrackFacts.Build(null, null, Formats));

    [Fact]
    public void A_field_the_file_does_not_carry_gets_no_line()
    {
        var facts = TrackFacts.Build(Tags, Row(), Formats);

        Assert.False(Has(facts, "Details_Conductor"));
        Assert.False(Has(facts, "Details_Isrc"));
        Assert.False(Has(facts, "Details_Bpm"));
    }

    [Fact]
    public void A_field_that_is_only_whitespace_is_no_field() =>
        Assert.False(Has(TrackFacts.Build((Tags with { Composers = "   " }), Row(), Formats),
                         "Details_Composers"));

    /// <summary>On a single-artist record the album artist is the track artist said twice. The line is
    /// only useful on compilations, where the two differ.</summary>
    [Fact]
    public void The_album_artist_is_named_only_where_it_differs()
    {
        Assert.False(Has(TrackFacts.Build(Tags, Row(), Formats), "Details_AlbumArtist"));

        var compilation = TrackFacts.Build(
            (Tags with { Artist = "Stevie Nicks", AlbumArtist = "Various Artists" }),
            Row(), Formats);
        Assert.Equal("Various Artists", Value(compilation, "Details_AlbumArtist"));
    }

    [Fact]
    public void A_track_number_says_how_long_the_record_is_when_it_knows()
    {
        Assert.Equal("2 of 11", Value(
            TrackFacts.Build((Tags with { TrackNumber = 2, TrackCount = 11 }), Row(), Formats),
            "Details_TrackNo"));

        Assert.Equal("2", Value(
            TrackFacts.Build((Tags with { TrackNumber = 2 }), Row(), Formats),
            "Details_TrackNo"));
    }

    /// <summary>"Disc 1 of 1" is a line about nothing. The disc only matters once there is another one
    /// it could have been.</summary>
    [Fact]
    public void A_disc_is_named_only_on_a_set_with_more_than_one()
    {
        Assert.False(Has(
            TrackFacts.Build((Tags with { DiscNumber = 1, DiscCount = 1 }), Row(), Formats),
            "Details_Disc"));

        Assert.Equal("2 of 3", Value(
            TrackFacts.Build((Tags with { DiscNumber = 2, DiscCount = 3 }), Row(), Formats),
            "Details_Disc"));
    }

    /// <summary>The mirror holds what a grid needs and has no column for a conductor or a publisher,
    /// which is half the reason to open this pane, so the file's own tags answer where both can.</summary>
    [Fact]
    public void The_files_own_tags_answer_over_the_mirrors_row()
    {
        var facts = TrackFacts.Build(
            (Tags with { Title = "Dreams" }),
            Row() with { Title = "02 Dreams" },
            Formats);

        Assert.Equal("Dreams", Value(facts, "Review_ColTitle"));
    }

    /// <summary>An unreadable file is not an empty pane: the mirror still knows what the grid shows.</summary>
    [Fact]
    public void The_mirror_answers_alone_when_the_file_cannot_be_read()
    {
        var facts = TrackFacts.Build(null, Row() with { Title = "Dreams", Album = "Rumours" }, Formats);

        Assert.Equal("Dreams", Value(facts, "Review_ColTitle"));
        Assert.Equal("Rumours", Value(facts, "Review_ColAlbum"));
    }

    [Fact]
    public void What_the_file_is_gets_said_the_way_a_sleeve_would()
    {
        var facts = TrackFacts.Build(
            (Tags with { BitsPerSample = 24, SampleRate = 96000, Channels = 6 }),
            Row(), Formats);

        Assert.Equal("FLAC  ·  24/96  ·  5.1", Value(facts, "TrackInfo_Encoding"));
        Assert.Equal("96 kHz", Value(facts, "TrackInfo_SampleRate"));
        Assert.Equal("6", Value(facts, "TrackInfo_ChannelCount"));
    }

    /// <summary>When the track joined the library and how often it has been listened to are the two
    /// facts no file carries. They are the library's own, and the reason the mirror is consulted as
    /// well as the tags.</summary>
    [Fact]
    public void The_library_answers_for_what_no_file_can()
    {
        var facts = TrackFacts.Build(Tags, Row() with
        {
            AddedUtcTicks = new DateTime(2024, 3, 9, 0, 0, 0, DateTimeKind.Utc).Ticks,
            LastPlayedUtcTicks = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc).Ticks,
            Plays = 37,
            SizeBytes = 4096,
        }, Formats);

        Assert.Equal("2024-03-09", Value(facts, "TrackInfo_Added"));
        Assert.Equal("2026-01-02", Value(facts, "TrackInfo_LastPlayed"));
        Assert.Equal("37", Value(facts, "TrackInfo_Plays"));
        Assert.Equal("4096 B", Value(facts, "TrackInfo_FileSize"));
        Assert.All(facts.Where(f => f.LabelKey.StartsWith("TrackInfo_Last", StringComparison.Ordinal)),
                   f => Assert.Equal(TrackFactGroup.Library, f.Group));
    }

    /// <summary>Never played is not a date, and a zero tick count would print as the year one.</summary>
    [Fact]
    public void A_track_never_played_gets_no_last_played_line() =>
        Assert.False(Has(TrackFacts.Build(Tags, Row(), Formats), "TrackInfo_LastPlayed"));

    /// <summary>A file that cannot hold an id is marked with a blank one, which is the same as having
    /// none. It is a note about the container, not a fact about the record.</summary>
    [Fact]
    public void A_file_that_cannot_carry_an_id_shows_no_id() =>
        Assert.False(Has(TrackFacts.Build(Tags, Row() with { RpmId = RpmTag.NoCarrier }, Formats),
                         "TrackInfo_RpmId"));

    /// <summary>The sheet is read top to bottom as four blocks, so the facts arrive grouped, not
    /// interleaved. The view puts a heading between them and never re-sorts.</summary>
    [Fact]
    public void The_sheet_arrives_grouped_in_reading_order()
    {
        var facts = TrackFacts.Build(
            (Tags with { Composers = "Stevie Nicks", DurationSeconds = 257 }),
            Row() with { Plays = 1 }, Formats);

        var groups = facts.Select(f => f.Group).Distinct();
        Assert.Equal([TrackFactGroup.Song, TrackFactGroup.Credits, TrackFactGroup.File, TrackFactGroup.Library],
                     groups);
    }
}
