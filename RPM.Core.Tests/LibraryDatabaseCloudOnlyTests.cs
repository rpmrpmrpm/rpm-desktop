using RPM.Core.Analysis;
using RPM.Core.Cloud;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// Which tracks only in the cloud are handed to the store to measure, with which readings, and how the list
/// is walked.
///
/// <para>These tracks have no file on this disk, so the loudness, sound and fingerprint passes leave them out.
/// The store measures them from its own copy instead. What is pinned down: a track is handed out while it
/// lacks any reading those passes would have taken, it says which ones it lacks, each reading follows its own
/// pass's rule, and the walk reaches the end of the list however many tracks the caller skips.</para>
/// </summary>
public class LibraryDatabaseCloudOnlyTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public LibraryDatabaseCloudOnlyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmcloudonly-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static LibraryTrack CloudTrack(string path, long added = 1, double seconds = 240, string? rpmId = "id") => new()
    {
        RelativePath = path,
        Folder = "",
        FileName = path,
        Extension = ".flac",
        SizeBytes = 100,
        ModifiedUtcTicks = 1,
        DurationSeconds = seconds,
        AddedUtcTicks = added,
        RpmId = rpmId is null ? null : $"{rpmId}-{path}",
        CloudState = CloudState.CloudOnly,
    };

    private static float[] Embedding(int seed)
    {
        var random = new Random(seed);
        var v = new float[SoundEmbedding.Dimensions];
        for (int i = 0; i < v.Length; i++)
            v[i] = (float)(random.NextDouble() * 2 - 1);
        return SoundEmbedding.Average([v])!;
    }

    private static readonly MoodReading SomeMood = new(0.3, 4.5, 7.0);
    private static readonly LoudnessReading SomeLevel = new(-12.5, 0.9);

    private IReadOnlyList<CloudOnlyWork> All(bool fingerprints = true) =>
        _db.QueryCloudOnlyTracksToMeasure(after: null, fingerprints, 100);

    /// <summary>Stores every reading a song can have, so that a test can take one away.</summary>
    private void MeasureFully(string path)
    {
        _db.SaveTrackLoudness(path, 100, 1, SomeLevel);
        _db.SaveTrackMood(path, 100, 1, SomeMood);
        _db.SaveTrackSound(path, 100, 1, Embedding(1));
        _db.SaveTrackFingerprint(path, 100, 1, 240, [1, 2, 3]);
    }

    [Fact]
    public void A_track_only_in_the_cloud_is_handed_out_needing_everything()
    {
        _db.Upsert(CloudTrack("cloud.flac"));
        _db.Upsert(CloudTrack("local.flac") with { CloudState = CloudState.Synced });

        var next = Assert.Single(All());

        Assert.Equal("cloud.flac", next.RelativePath);
        Assert.Equal("id-cloud.flac", next.RpmId);
        Assert.True(next.NeedsLevel);
        Assert.True(next.NeedsMood);
        Assert.True(next.NeedsSound);
        Assert.True(next.NeedsFingerprint);
        Assert.Equal(1, _db.CountCloudOnlyTracksToMeasure(fingerprints: true));
    }

    [Fact]
    public void A_fully_measured_track_is_not_handed_out()
    {
        _db.Upsert(CloudTrack("done.flac"));
        MeasureFully("done.flac");

        Assert.Empty(All());
        Assert.Equal(0, _db.CountCloudOnlyTracksToMeasure(fingerprints: true));
    }

    [Fact]
    public void A_track_says_which_reading_it_still_lacks()
    {
        _db.Upsert(CloudTrack("no-level.flac", added: 1));
        _db.SaveTrackMood("no-level.flac", 100, 1, SomeMood);
        _db.SaveTrackSound("no-level.flac", 100, 1, Embedding(1));
        _db.SaveTrackFingerprint("no-level.flac", 100, 1, 240, [1]);

        _db.Upsert(CloudTrack("no-mood.flac", added: 2));
        _db.SaveTrackLoudness("no-mood.flac", 100, 1, SomeLevel);
        _db.SaveTrackSound("no-mood.flac", 100, 1, Embedding(2));
        _db.SaveTrackFingerprint("no-mood.flac", 100, 1, 240, [1]);

        _db.Upsert(CloudTrack("no-sound.flac", added: 3));
        _db.SaveTrackLoudness("no-sound.flac", 100, 1, SomeLevel);
        _db.SaveTrackMood("no-sound.flac", 100, 1, SomeMood);
        _db.SaveTrackFingerprint("no-sound.flac", 100, 1, 240, [1]);

        _db.Upsert(CloudTrack("no-fp.flac", added: 4));
        _db.SaveTrackLoudness("no-fp.flac", 100, 1, SomeLevel);
        _db.SaveTrackMood("no-fp.flac", 100, 1, SomeMood);
        _db.SaveTrackSound("no-fp.flac", 100, 1, Embedding(3));

        var work = All();

        Assert.Equal(["no-level.flac", "no-mood.flac", "no-sound.flac", "no-fp.flac"], work.Select(w => w.RelativePath));
        Assert.Equal(
            ["L", "M", "S", "F"],
            work.Select(w => (w.NeedsLevel ? "L" : "") + (w.NeedsMood ? "M" : "") + (w.NeedsSound ? "S" : "") +
                             (w.NeedsFingerprint ? "F" : "")));
    }

    [Fact]
    public void Each_reading_follows_its_own_passes_rule()
    {
        _db.Upsert(CloudTrack("interlude.flac", seconds: Moods.LeastSeconds - 1));

        var interlude = Assert.Single(All());

        // A track too short to be a song still gets a level and a fingerprint, and no mood or sound.
        Assert.True(interlude.NeedsLevel);
        Assert.True(interlude.NeedsFingerprint);
        Assert.False(interlude.NeedsMood);
        Assert.False(interlude.NeedsSound);
    }

    [Fact]
    public void Fingerprints_are_wanted_only_while_fingerprinting_is_on()
    {
        _db.Upsert(CloudTrack("a.flac"));
        _db.SaveTrackLoudness("a.flac", 100, 1, SomeLevel);
        _db.SaveTrackMood("a.flac", 100, 1, SomeMood);
        _db.SaveTrackSound("a.flac", 100, 1, Embedding(1));

        Assert.True(Assert.Single(All(fingerprints: true)).NeedsFingerprint);
        Assert.Empty(All(fingerprints: false));
        Assert.Equal(0, _db.CountCloudOnlyTracksToMeasure(fingerprints: false));
    }

    /// <summary>"Cannot be measured", "nothing to describe", "nothing to hear" and "fpcalc could not read
    /// it" are answers, so a track stored with all four is not handed out again.</summary>
    [Fact]
    public void Answers_that_found_nothing_are_answers()
    {
        _db.Upsert(CloudTrack("silent.flac"));
        _db.SaveTrackLoudness("silent.flac", 100, 1, LoudnessReading.Unknown);
        _db.SaveTrackMood("silent.flac", 100, 1, MoodReading.Unknown);
        _db.SaveTrackSound("silent.flac", 100, 1, null);
        _db.SaveTrackFingerprint("silent.flac", 100, 1, 0, []);

        Assert.Empty(All());
    }

    [Fact]
    public void Tracks_without_an_rpm_id_or_lent_rows_are_not_handed_out()
    {
        _db.Upsert(CloudTrack("no-id.flac", rpmId: null));
        _db.Upsert(CloudTrack("song.flac"));

        Assert.Equal("song.flac", Assert.Single(All()).RelativePath);
    }

    /// <summary>The local passes' queries do not hand these tracks out, so the two never ask about the same
    /// track.</summary>
    [Fact]
    public void The_local_queries_still_leave_them_out()
    {
        _db.Upsert(CloudTrack("cloud.flac"));

        Assert.Empty(_db.QueryTracksNeedingSound(10));
        Assert.Empty(_db.QueryTracksNeedingLoudness(10));
        Assert.Empty(_db.QueryTracksNeedingFingerprint(10));
        Assert.Single(All());
    }

    /// <summary>
    /// A caller that skips every track on a page (the store holds no copy of them) still reaches the rest of
    /// the list, because the next page starts after the last row of this one. Tracks added at the same moment
    /// are ordered by path, so none is handed out twice or left out.
    /// </summary>
    [Fact]
    public void Pages_walk_past_skipped_tracks_to_the_end()
    {
        _db.Upsert(CloudTrack("a.flac", added: 1));
        _db.Upsert(CloudTrack("b.flac", added: 2));
        _db.Upsert(CloudTrack("d.flac", added: 2));
        _db.Upsert(CloudTrack("c.flac", added: 2));
        _db.Upsert(CloudTrack("e.flac", added: 3));

        var seen = new List<string>();
        CloudOnlyWork? after = null;
        while (true)
        {
            var page = _db.QueryCloudOnlyTracksToMeasure(after, fingerprints: true, 2);
            if (page.Count == 0)
                break;
            seen.AddRange(page.Select(w => w.RelativePath));
            after = page[^1];
        }

        // Nothing was stored, as when every track is skipped, and the walk still ended.
        Assert.Equal(["a.flac", "b.flac", "c.flac", "d.flac", "e.flac"], seen);
    }

    /// <summary>An answer about a row that changed while the store worked is not taken as current.</summary>
    [Fact]
    public void An_answer_about_a_row_that_changed_is_not_current()
    {
        _db.Upsert(CloudTrack("a.flac"));
        var asked = Assert.Single(All());

        _db.Upsert(CloudTrack("a.flac") with { SizeBytes = 200, ModifiedUtcTicks = 2 });
        _db.SaveTrackLoudness(asked.RelativePath, asked.SizeBytes, asked.ModifiedUtcTicks, SomeLevel);
        _db.SaveTrackMood(asked.RelativePath, asked.SizeBytes, asked.ModifiedUtcTicks, SomeMood);
        _db.SaveTrackSound(asked.RelativePath, asked.SizeBytes, asked.ModifiedUtcTicks, Embedding(1));
        _db.SaveTrackFingerprint(asked.RelativePath, asked.SizeBytes, asked.ModifiedUtcTicks, 240, [1, 2]);

        var again = Assert.Single(All());
        Assert.True(again.NeedsLevel && again.NeedsMood && again.NeedsSound && again.NeedsFingerprint);
        Assert.Equal(200, again.SizeBytes);
    }
}
