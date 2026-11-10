using RPM.Core.Migration;

namespace RPM.Core.Tests;

/// <summary>The summary's estimate of what rpm already has, and the album batches an import works in.</summary>
public class MigrationEstimateTests
{
    private static SourceTrack Track(string id, string artist, string album, string title, double seconds, long size = 1000, string albumArtist = "") =>
        new(id, new LocalFile("/m/" + id), title, artist, album, albumArtist, 1, 1, seconds, size, 0, false, 0, null, null);

    private static SourceLibrary Library(params SourceTrack[] tracks) => new(SourceKind.ITunes, "x", "iTunes", tracks, [], []);

    [Fact]
    public void Estimate_counts_matches_within_two_seconds()
    {
        var library = Library(
            Track("a", "Band", "Record", "One", 200, 10),
            Track("b", "band ", "RECORD", "one", 300, 20),
            Track("c", "Band", "Record", "Two", 181.5, 30),
            Track("d", "Other", "Record", "Two", 180, 40));
        KnownSong[] known = [new("Band", "Record", "One", 201.9), new("Band", "Record", "Two", 180)];

        var estimate = MigrationEstimate.Of(library, known);

        Assert.Equal(2, estimate.AboutAlreadyInRpm);
        Assert.Equal(20 + 40, estimate.AboutBytesNeeded);
        Assert.Equal(100, estimate.TotalBytes);
    }

    [Fact]
    public void Same_song_needs_the_same_artist_album_and_title() =>
        Assert.True(MigrationEstimate.SameSong("Band", "Record", "One", 200, new KnownSong(" band", "record ", "ONE", 198.5)));

    [Fact]
    public void Albums_are_grouped_by_album_artist_and_album_in_first_seen_order()
    {
        var batches = AlbumBatch.Group([
            Track("1", "Band", "B", "x", 1),
            Track("2", "Guest", "A", "x", 1, albumArtist: "Band"),
            Track("3", "Band", "B", "y", 1),
            Track("4", "Band", "A", "y", 1),
        ]);

        Assert.Equal([["1", "3"], ["2", "4"]], batches.Select(b => b.Select(t => t.SourceId).ToArray()));
    }
}
