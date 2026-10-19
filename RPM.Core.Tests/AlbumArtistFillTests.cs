using RPM.Core.Analysis;
using RPM.Core.Models;

namespace RPM.Core.Tests;

public class AlbumArtistFillTests
{
    private static TrackInfo Track(string path, string? artist, string? album, string? albumArtist = null)
        => new()
        {
            FilePath = path,
            Extension = ".mp3",
            Artist = artist,
            Album = album,
            Title = System.IO.Path.GetFileNameWithoutExtension(path),
            AlbumArtist = albumArtist,
        };

    private static List<string?> Fill(params TrackInfo[] tracks)
        => AlbumArtistFill.Fill(tracks).Select(t => t.AlbumArtist).ToList();

    private static List<string?> Expect(params string?[] artists) => artists.ToList();

    [Fact]
    public void One_artist_across_the_record_is_the_records_artist()
    {
        Assert.Equal(
            Expect("Muse", "Muse"),
            Fill(Track("/m/Muse/1.mp3", "Muse", "Absolution"),
                 Track("/m/Muse/2.mp3", "Muse", "Absolution")));
    }

    [Fact]
    public void Several_artists_across_the_record_make_it_a_compilation()
    {
        Assert.Equal(
            Expect(AlbumResolver.VariousArtists, AlbumResolver.VariousArtists),
            Fill(Track("/m/Now/1.mp3", "Snap!", "Now 22"),
                 Track("/m/Now/2.mp3", "The Shamen", "Now 22")));
    }

    [Fact]
    public void A_guest_credit_is_the_tracks_and_not_the_records()
    {
        // Without this a single featured singer turns a solo album into a compilation.
        Assert.Equal(
            Expect("Beyoncé", "Beyoncé"),
            Fill(Track("/m/B/1.mp3", "Beyoncé", "Lemonade"),
                 Track("/m/B/2.mp3", "Beyoncé feat. Jack White", "Lemonade")));
    }

    [Fact]
    public void An_album_artist_already_there_is_never_overwritten_and_answers_for_the_rest()
    {
        var filled = AlbumArtistFill.Fill([
            Track("/m/va/1.mp3", "Snap!", "Now 22", albumArtist: "Various Artists"),
            Track("/m/va/2.mp3", "The Shamen", "Now 22"),
        ]);

        Assert.Equal(["Various Artists", "Various Artists"], filled.Select(t => t.AlbumArtist));
    }

    [Fact]
    public void Two_records_of_the_same_name_in_two_folders_stay_two_records()
    {
        // The one case a name-only key gets catastrophically wrong: every "Greatest Hits" in a bulk
        // import becoming one enormous compilation.
        Assert.Equal(
            Expect("Queen", "Abba"),
            Fill(Track("/m/Queen/1.mp3", "Queen", "Greatest Hits"),
                 Track("/m/Abba/1.mp3", "Abba", "Greatest Hits")));
    }

    [Fact]
    public void A_two_disc_set_is_one_record()
    {
        Assert.Equal(
            Expect(AlbumResolver.VariousArtists, AlbumResolver.VariousArtists),
            Fill(Track("/m/Now 64/CD 1/1.mp3", "Snap!", "Now 64"),
                 Track("/m/Now 64/CD 2/1.mp3", "The Shamen", "Now 64")));
    }

    [Fact]
    public void A_filled_track_is_marked_edited_so_the_import_writes_it()
    {
        // Without the mark the import moves a well-tagged compilation without writing to it at all:
        // the deduced name reaches the FOLDER and never the file, and the mirror re-reads the file.
        var filled = AlbumArtistFill.Fill([
            Track("/m/Now/1.mp3", "Snap!", "Now 22"),
            Track("/m/Now/2.mp3", "The Shamen", "Now 22"),
        ]);

        Assert.All(filled, t => Assert.True(t.TagsEdited));
    }

    [Fact]
    public void A_track_that_already_had_one_is_not_marked_edited()
    {
        var filled = AlbumArtistFill.Fill([
            Track("/m/va/1.mp3", "Snap!", "Now 22", albumArtist: "Various Artists"),
            Track("/m/va/2.mp3", "The Shamen", "Now 22"),
        ]);

        Assert.False(filled[0].TagsEdited);   // nothing was deduced for it
        Assert.True(filled[1].TagsEdited);
    }

    /// <summary>Asking the same question of a record already in the library, which is what a tracklist
    /// needs to know before it decides whether the line under each title is worth its height.</summary>
    [Fact]
    public void A_record_knows_whether_it_is_various_artists()
    {
        Assert.True(AlbumArtistFill.IsVariousArtists([
            Track("/m/Now/1.mp3", "Snap!", "Now 22"),
            Track("/m/Now/2.mp3", "The Shamen", "Now 22")]));

        // The tagged field wins outright, on a record whose track artists were never filled in.
        Assert.True(AlbumArtistFill.IsVariousArtists([
            Track("/m/ost/1.mp3", null, "Trainspotting", albumArtist: "Various Artists"),
            Track("/m/ost/2.mp3", null, "Trainspotting", albumArtist: "Various Artists")]));

        Assert.False(AlbumArtistFill.IsVariousArtists([
            Track("/m/Muse/1.mp3", "Muse", "Absolution"),
            Track("/m/Muse/2.mp3", "Muse", "Absolution")]));

        // A guest on one track is the TRACK's artist, not a second artist on the record.
        Assert.False(AlbumArtistFill.IsVariousArtists([
            Track("/m/b/1.mp3", "Beyoncé feat. Jay-Z", "Dangerously in Love"),
            Track("/m/b/2.mp3", "Beyoncé", "Dangerously in Love")]));

        Assert.False(AlbumArtistFill.IsVariousArtists([]));
    }

    [Fact]
    public void Nothing_to_go_on_is_left_alone()
    {
        // No album is not a record; no artist anywhere is not an answer. Neither gets invented.
        Assert.Equal(
            Expect(null, null),
            Fill(Track("/m/loose/1.mp3", "Muse", album: null),
                 Track("/m/x/1.mp3", artist: null, album: "Unknown Record")));
    }
}
