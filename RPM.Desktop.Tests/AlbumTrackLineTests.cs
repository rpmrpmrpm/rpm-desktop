using RPM.Core.Models;
using RPM.Desktop.Presentation.Main.Library.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// What the small line under a track title shows, on the three kinds of record that need different
/// answers.
///
/// <para>The line is expensive: it doubles the height of every row in the drawer, the band and the
/// album page. So it is shown only where it is useful: the composer on a classical record, where the
/// composer is central to the piece; the track's own artist on a various-artists record, where the
/// heading can only say "Various Artists"; and nothing at all on a record by one artist, where the
/// heading already names the artist once for all tracks.</para>
/// </summary>
public class AlbumTrackLineTests
{
    private static TrackInfo Track(
        string title,
        string? artist = null,
        string? albumArtist = null,
        string? composer = null,
        string? work = null,
        string? genre = null,
        uint number = 0)
        => new()
        {
            FilePath = $"/music/{albumArtist ?? artist}/Record/{title}.flac",
            Extension = ".flac",
            Title = title,
            Artist = artist,
            AlbumArtist = albumArtist,
            Album = "Record",
            Composers = composer,
            Work = work,
            Genre = genre,
            TrackNumber = number,
        };

    private static List<string> Subtitles(IReadOnlyList<TrackInfo> tracks, bool classical = false)
        => [.. AlbumTrackItem.Build(tracks, classical).Select(l => l.Subtitle)];

    /// <summary>A rock album that credits its songwriters is not marked classical, and a songwriter
    /// under every title would be information nobody asked for.</summary>
    [Fact]
    public void A_record_by_one_artist_says_nothing_under_its_titles()
    {
        var tracks = new[]
        {
            Track("Come Together", artist: "The Beatles", albumArtist: "The Beatles",
                  composer: "Lennon, McCartney", genre: "Rock", number: 1),
            Track("Something", artist: "The Beatles", albumArtist: "The Beatles",
                  composer: "Harrison", genre: "Rock", number: 2),
        };

        Assert.All(Subtitles(tracks), s => Assert.Equal("", s));
    }

    /// <summary>A compilation's heading says "Various Artists" and nothing else, so the line under each
    /// title names who played THAT track, which the heading cannot show.</summary>
    [Fact]
    public void A_various_artists_record_puts_the_track_artist_there()
    {
        var tracks = new[]
        {
            Track("Praise You", artist: "Fatboy Slim", albumArtist: "Various Artists", number: 1),
            Track("Right Here, Right Now", artist: "Jesus Jones", albumArtist: "Various Artists", number: 2),
        };

        Assert.Equal(["Fatboy Slim", "Jesus Jones"], Subtitles(tracks));
    }

    /// <summary>Many records have no album artist tag at all. A record whose tracks are by three
    /// different artists is a compilation whether or not the field is set.</summary>
    [Fact]
    public void An_untagged_compilation_is_read_off_its_track_artists()
    {
        var tracks = new[]
        {
            Track("One", artist: "Aphex Twin", number: 1),
            Track("Two", artist: "Autechre", number: 2),
            Track("Three", artist: "Boards of Canada", number: 3),
        };

        Assert.Equal(["Aphex Twin", "Autechre", "Boards of Canada"], Subtitles(tracks));
    }

    /// <summary>Guest credits are the track's, not the record's: without that, one featured singer
    /// would turn a solo album into a compilation and print the artist under every song.</summary>
    [Fact]
    public void A_guest_credit_does_not_make_a_record_various_artists()
    {
        var tracks = new[]
        {
            Track("Crazy in Love", artist: "Beyoncé feat. Jay-Z", number: 1),
            Track("Naughty Girl", artist: "Beyoncé", number: 2),
        };

        Assert.All(Subtitles(tracks), s => Assert.Equal("", s));
    }

    /// <summary>A record marked classical is the main use of the line: the work heading shows the
    /// composer of the group of tracks, and a piece that is a work by itself shows its own.</summary>
    [Fact]
    public void A_classical_record_puts_the_composer_there()
    {
        var tracks = new[]
        {
            Track("Sonata In C Major, KV. 330: Allegro moderato", artist: "Mitsuko Uchida",
                  composer: "Mozart", work: "Sonata In C Major, KV. 330", genre: "Classical", number: 1),
            Track("Sonata In C Major, KV. 330: Andante cantabile", artist: "Mitsuko Uchida",
                  composer: "Mozart", work: "Sonata In C Major, KV. 330", genre: "Classical", number: 2),
            Track("Fantasia In D Minor, KV. 397", artist: "Mitsuko Uchida",
                  composer: "Mozart", genre: "Classical", number: 3),
        };

        var lines = AlbumTrackItem.Build(tracks, classical: true);

        // The work heading, then its two movements (with no line, because the heading shows it), then
        // the Fantasy on its own with its own composer under it.
        Assert.Equal(["Mozart", "", "", "Mozart"], lines.Select(l => l.Subtitle));
    }

    /// <summary>A record is never treated as classical because of its tags. Unmarked symphonies get a
    /// plain tracklist, and a plain tracklist by one artist shows no line.</summary>
    [Fact]
    public void A_record_nobody_has_marked_classical_is_a_plain_tracklist()
    {
        var tracks = new[]
        {
            Track("Symphony No. 5 in C minor, Op. 67: I. Allegro con brio", artist: "Karajan",
                  albumArtist: "Karajan", composer: "Beethoven",
                  work: "Symphony No. 5 in C minor, Op. 67", number: 1),
            Track("Symphony No. 5 in C minor, Op. 67: II. Andante con moto", artist: "Karajan",
                  albumArtist: "Karajan", composer: "Beethoven",
                  work: "Symphony No. 5 in C minor, Op. 67", number: 2),
        };

        // Two lines, no work heading, nothing under either title.
        Assert.Equal(2, Subtitles(tracks).Count);
        Assert.All(Subtitles(tracks), s => Assert.Equal("", s));

        // And the same record, marked: a work heading with its composer over its two movements.
        Assert.Equal("Beethoven", Subtitles(tracks, classical: true)[0]);
    }

    /// <summary>A rock album linked to works by MusicBrainz carries a WORK tag and a songwriter on
    /// every track, and is still a rock album: nobody wants Pete Townshend under every title.</summary>
    [Fact]
    public void A_pop_record_tagged_with_works_says_nothing_under_its_titles()
    {
        var tracks = new[]
        {
            Track("Baba O'Riley", artist: "The Who", albumArtist: "The Who",
                  composer: "Townshend", work: "Baba O'Riley", genre: "Rock", number: 1),
            Track("Bargain", artist: "The Who", albumArtist: "The Who",
                  composer: "Townshend", work: "Bargain", genre: "Rock", number: 2),
        };

        Assert.All(Subtitles(tracks), s => Assert.Equal("", s));
    }

    /// <summary>A various-artists record that is not classical shows the track artist where the
    /// composer would be. The line is still used, for what is useful on that record.</summary>
    [Fact]
    public void A_various_artists_record_that_is_not_classical_shows_its_artists()
    {
        var tracks = new[]
        {
            Track("Adagio", artist: "Karajan", albumArtist: "Various Artists",
                  composer: "Albinoni", work: "Adagio in G Minor", number: 1),
            Track("Air", artist: "Marriner", albumArtist: "Various Artists",
                  composer: "Bach", work: "Orchestral Suite No. 3", number: 2),
        };

        Assert.Equal(["Karajan", "Marriner"], Subtitles(tracks));
    }
}
