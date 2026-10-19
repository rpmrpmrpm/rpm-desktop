using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// Making the cloud agree with the library again: the push half, and the silence that has to go with
/// it. A comparison that reported false differences would send every track on every sync run for ever.
///
/// <para>Which side wins when the two have both moved is <see cref="CloudSyncPlanTests"/>.</para>
/// </summary>
public class CatalogReconcilerTests
{
    private const string Id = "11111111-2222-3333-4444-555555555555";

    private static LibraryTrack Mine(
        string? title = "Brimful of Asha",
        string? artist = "Cornershop",
        string? album = "When I Was Born for the 7th Time",
        string relativePath = "Cornershop/When I Was Born for the 7th Time/02 - Brimful of Asha.flac",
        double duration = 318,
        uint track = 2,
        long modified = 100,
        long synced = 100) => new()
        {
            Id = 1,
            RelativePath = relativePath,
            Folder = "Cornershop/When I Was Born for the 7th Time",
            FileName = "02 - Brimful of Asha.flac",
            Extension = ".flac",
            RpmId = Id,
            Title = title,
            Artist = artist,
            Album = album,
            AlbumArtist = "Cornershop",
            DurationSeconds = duration,
            TrackNumber = track,
            DiscNumber = 1,
            Year = 1997,
            Channels = 2,
            SampleRate = 44100,
            BitsPerSample = 16,
            CloudState = CloudState.Synced,
            ModifiedUtcTicks = modified,
            CloudSyncedUtcTicks = synced,
        };

    private static CloudTrack Theirs(
        string? title = "Brimful of Asha",
        string? artist = "Cornershop",
        string? album = "When I Was Born for the 7th Time",
        string? relativePath = "Cornershop/When I Was Born for the 7th Time/02 - Brimful of Asha.flac",
        double duration = 318,
        int track = 2) => new(
            Id, "sha", relativePath, 1000, title, artist, album, 0,
            new CloudTrackTags(
                duration, track, 0, 1, 0, 1997, null, "Cornershop",
                ".flac", 2, 44100, 16));

    // ── nothing to say ──

    [Fact]
    public void A_library_that_agrees_with_the_cloud_sends_nothing()
    {
        var work = CloudSyncPlan.Make([Mine()], [Theirs()]);

        Assert.Empty(work.Push);
        Assert.Empty(work.Sidecars);
    }

    [Fact]
    public void A_duration_read_by_two_different_readers_is_the_same_duration()
    {
        // Floating point, two decoders. A hundredth of a second apart is not an edit, and treating it
        // as one would push the whole library on every run.
        var work = CloudSyncPlan.Make([Mine(duration: 318.004)], [Theirs(duration: 318)]);

        Assert.Empty(work.Push);
    }

    [Fact]
    public void A_track_the_cloud_has_never_heard_of_is_not_this_jobs_problem()
    {
        // Uploading is what puts a track in the cloud. This only ever corrects what is already there.
        var work = CloudSyncPlan.Make([Mine()], []);

        Assert.Empty(work.Push);
        Assert.Empty(work.Sidecars);
    }

    [Fact]
    public void A_row_with_no_rpm_id_is_skipped()
    {
        var track = Mine() with { RpmId = null };

        Assert.Empty(CloudSyncPlan.Make([track], [Theirs()]).Push);
    }

    // ── the edits that have to be pushed ──

    [Fact]
    public void A_corrected_artist_travels()
    {
        var work = CloudSyncPlan.Make([Mine(artist: "Cornershop")], [Theirs(artist: "Cornershp")]);

        var push = Assert.Single(work.Push);
        Assert.Equal("Cornershop", push.Artist);
        // And nothing else is claimed to have changed.
        Assert.Null(push.Title);
        Assert.Null(push.Album);
        Assert.Null(push.RelativePath);
    }

    [Fact]
    public void A_retitled_track_travels()
    {
        var push = Assert.Single(CloudSyncPlan.Make(
            [Mine(title: "Brimful of Asha")], [Theirs(title: "Track 02")]).Push);

        Assert.Equal("Brimful of Asha", push.Title);
    }

    /// <summary>
    /// A library re-filed under a new naming template does NOT travel on its own.
    ///
    /// <para>It is the one case that changes no tags at all: the files move and the mirror's path
    /// moves with them. The path is not something two machines can agree on. Each has its own naming
    /// template and its own answer, nothing at the other end can act on the answer, and every push
    /// moves the row's stamp, so a push made for a path alone would be a write per track per run with
    /// no end state.</para>
    /// </summary>
    [Fact]
    public void A_library_re_filed_under_a_new_naming_template_does_not_travel_on_its_own()
    {
        var plan = CloudSyncPlan.Make(
            [Mine(relativePath: "Cornershop/1997 - When I Was Born/02 Brimful of Asha.flac")],
            [Theirs()]);

        Assert.Empty(plan.Push);
        Assert.Empty(plan.Pull);
    }

    /// <summary>But it is sent together with a correction that IS worth sending, so the store's idea of
    /// where a file lives stays current without ever being a reason to write. That path is what an
    /// adopted row sits at and what a restore writes to.</summary>
    [Fact]
    public void A_re_file_rides_along_with_a_correction()
    {
        var push = Assert.Single(CloudSyncPlan.Make(
            [Mine(title: "Brimful of Asha",
                  relativePath: "Cornershop/1997 - When I Was Born/02 Brimful of Asha.flac")],
            [Theirs(title: "Track 02")]).Push);

        Assert.Equal("Brimful of Asha", push.Title);
        Assert.Equal("Cornershop/1997 - When I Was Born/02 Brimful of Asha.flac", push.RelativePath);
    }

    [Fact]
    public void A_corrected_track_number_travels()
    {
        var push = Assert.Single(CloudSyncPlan.Make([Mine(track: 2)], [Theirs(track: 9)]).Push);

        Assert.NotNull(push.Tags);
        Assert.Equal(2, push.Tags!.TrackNumber);
    }

    [Fact]
    public void A_row_the_cloud_has_no_catalog_for_gets_one()
    {
        var theirs = Theirs() with { Tags = null };

        var push = Assert.Single(CloudSyncPlan.Make([Mine()], [theirs]).Push);
        Assert.NotNull(push.Tags);
    }

    // ── what must never travel ──

    [Fact]
    public void A_field_the_library_does_not_know_never_clears_the_one_the_cloud_has()
    {
        // The mirror not knowing an album artist is not a reason to take the cloud's away. Blank means
        // "no opinion" on both sides of this exchange.
        var mine = Mine(title: null, artist: "   ") with { AlbumArtist = null, Genre = null };

        var work = CloudSyncPlan.Make([mine], [Theirs()]);

        Assert.Empty(work.Push);
    }

    [Fact]
    public void An_unread_stream_does_not_claim_the_file_has_no_format()
    {
        // Null in the mirror means "never read", not "zero channels". Pushing zeroes would blank the
        // format line of a row that had a good one.
        var mine = Mine() with { Channels = null, SampleRate = null, BitsPerSample = null };

        Assert.Empty(CloudSyncPlan.Make([mine], [Theirs()]).Push);
    }

    // ── the private tag blob ──

    [Fact]
    public void A_file_written_since_it_was_uploaded_has_its_sidecar_refreshed()
    {
        // Retagging changes the file and not the canonical audio, so nothing re-uploads and the sidecar
        // a restore rebuilds from would otherwise keep the tags it had at upload.
        var work = CloudSyncPlan.Make([Mine(modified: 500, synced: 100)], [Theirs()]);

        Assert.Equal(Id, Assert.Single(work.Sidecars));
    }

    [Fact]
    public void A_file_untouched_since_it_was_uploaded_does_not()
    {
        Assert.Empty(CloudSyncPlan.Make([Mine(modified: 100, synced: 500)], [Theirs()]).Sidecars);
    }

    // ── how much goes at once ──

    [Fact]
    public void One_run_sends_no_more_than_it_is_allowed_to()
    {
        // A library that drifted for a year must not become one enormous burst. The rest is found
        // again next run, because the work is read off the server rather than remembered.
        var mine = Enumerable.Range(0, 50)
            .Select(i => Mine(title: $"Changed {i}", modified: 500, synced: 100) with
            {
                RpmId = $"id-{i}",
            })
            .ToList();

        var theirs = Enumerable.Range(0, 50)
            .Select(i => Theirs(title: "Old") with { RpmId = $"id-{i}" })
            .ToList();

        var work = CloudSyncPlan.Make(mine, theirs, limit: 10);

        Assert.Equal(10, work.Push.Count);
        Assert.Equal(10, work.Sidecars.Count);
    }
}
