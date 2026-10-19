namespace RPM.Fingerprint.Tests;

/// <summary>
/// Where things live in the store, pinned.
///
/// <para>These strings are not an implementation detail: objects are already sitting in the bucket
/// under them. Changing one does not break a build or fail a request. The API signs a URL for a key
/// nothing is ever written to, the transcoder writes to a key nothing looks at, and the store simply
/// appears to have stopped making renditions. So the keys are written down here, by hand, and a
/// change to the format has to be a change to this test too.</para>
/// </summary>
public class CloudLayoutTests
{
    private const string Sha = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void The_original_lives_under_its_own_hash()
    {
        Assert.Equal($"blobs/{Sha}", CloudLayout.BlobKey(Sha));
    }

    [Fact]
    public void A_rendition_lives_under_the_rate_it_was_made_at()
    {
        Assert.Equal($"rate/128/{Sha}.m4a", CloudLayout.RenditionKey(Sha, 128));
        Assert.Equal($"rate/320/{Sha}.m4a", CloudLayout.RenditionKey(Sha, 320));
    }

    /// <summary>The head clip is not a rendition. It is three seconds long, so it does not sit on the
    /// ladder's shelf, where a rate of -1 would read as a folder called "rate/-1".</summary>
    [Fact]
    public void The_head_clip_lives_apart_from_the_rates()
    {
        Assert.Equal($"head/{Sha}.m4a", CloudLayout.RenditionKey(Sha, CloudLayout.HeadClip));
    }

    [Fact]
    public void Every_key_a_track_has_is_a_different_key()
    {
        var keys = AudioLadder.Rates
            .Select(rate => CloudLayout.RenditionKey(Sha, rate))
            .Append(CloudLayout.RenditionKey(Sha, CloudLayout.HeadClip))
            .Append(CloudLayout.BlobKey(Sha))
            .ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void A_rendition_is_a_rung_of_the_ladder_or_the_head_clip_and_nothing_else()
    {
        foreach (var rate in AudioLadder.Rates)
            Assert.True(CloudLayout.IsRenditionRate(rate), $"{rate} is on the ladder but not a rendition");

        Assert.True(CloudLayout.IsRenditionRate(CloudLayout.HeadClip));

        Assert.False(CloudLayout.IsRenditionRate(160));                     // a rate nobody makes
        Assert.False(CloudLayout.IsRenditionRate(AudioLadder.Original));    // the file itself is not made
        Assert.False(CloudLayout.IsRenditionRate(CloudLayout.Loudness));    // measuring writes no object
    }

    /// <summary>
    /// The measuring pass makes no object at all. It writes two numbers onto the blob's row, but it
    /// rides the same want list, because the want list is already the queue for "somebody's server has
    /// to read this blob".
    /// </summary>
    [Fact]
    public void The_want_list_holds_the_renditions_and_the_measuring_pass_that_is_not_one()
    {
        Assert.True(CloudLayout.IsWantable(CloudLayout.Loudness));
        Assert.True(CloudLayout.IsWantable(CloudLayout.HeadClip));
        foreach (var rate in AudioLadder.Rates)
            Assert.True(CloudLayout.IsWantable(rate));

        Assert.False(CloudLayout.IsWantable(AudioLadder.Original));
        Assert.False(CloudLayout.IsWantable(160));
    }

    /// <summary>The two stand-in rates are negative so they can never collide with a real one, and
    /// distinct from each other so a want list cannot mistake one job for the other.</summary>
    [Fact]
    public void The_stand_in_rates_are_not_rates()
    {
        Assert.True(CloudLayout.HeadClip < 0);
        Assert.True(CloudLayout.Loudness < 0);
        Assert.NotEqual(CloudLayout.HeadClip, CloudLayout.Loudness);
        Assert.DoesNotContain(CloudLayout.HeadClip, AudioLadder.Rates);
        Assert.DoesNotContain(CloudLayout.Loudness, AudioLadder.Rates);
    }

    [Fact]
    public void The_bucket_and_table_are_the_ones_that_exist()
    {
        // Named here rather than derived: both are live AWS resources, and a rename is a migration,
        // not an edit.
        Assert.Equal("rpm-user-audio", CloudLayout.Bucket);
        Assert.Equal("RpmBlobs", CloudLayout.BlobsTable);
    }

    /// <summary>The head clip is made at the bottom of the ladder, so the join into the real stream is
    /// a rung the player already knows how to ask for.</summary>
    [Fact]
    public void The_head_clip_is_encoded_at_a_rate_the_ladder_has()
    {
        Assert.Contains(AudioLadder.HeadKilobits, AudioLadder.Rates);
        Assert.Equal(AudioLadder.HeadKilobits, AudioLadder.Rates.Min());
    }

    // ── the queue's index ─────────────────────────────────────────────────────────────────────────

    /// <summary>The shard is worked out from the hash by both sides: the API writing a want and the
    /// worker reading one. It is never stored anywhere else, so the two must agree for ever.</summary>
    [Fact]
    public void A_blob_always_lands_in_the_same_shard()
    {
        Assert.Equal(CloudLayout.ShardFor(Sha), CloudLayout.ShardFor(Sha));
        Assert.Equal(CloudLayout.ShardFor("0abc"), CloudLayout.ShardFor("0def"));
    }

    /// <summary>Every shard is a partition of the index, and a shard nothing lands in is a query per
    /// tick that can never return anything.</summary>
    [Fact]
    public void Every_shard_is_reachable_and_no_hash_falls_outside_them()
    {
        var landed = new HashSet<int>();
        foreach (var digit in "0123456789abcdef")
            landed.Add(CloudLayout.ShardFor($"{digit}0000000"));

        Assert.Equal(CloudLayout.WantedShards, landed.Count);
        Assert.All(landed, shard => Assert.InRange(shard, 0, CloudLayout.WantedShards - 1));
    }

    /// <summary>Upper case because a hash read back off a row is whatever put it there, and a hash
    /// that sharded differently by case would be queued in one shard and looked for in another.</summary>
    [Fact]
    public void Case_does_not_move_a_blob_between_shards()
    {
        Assert.Equal(CloudLayout.ShardFor("abc123"), CloudLayout.ShardFor("ABC123"));
        Assert.Equal(CloudLayout.ShardFor("f00"), CloudLayout.ShardFor("F00"));
    }

    /// <summary>Nothing to shard on is still a shard: a want with no hash cannot be made, but it must
    /// not throw on the way to finding that out.</summary>
    [Fact]
    public void An_empty_hash_does_not_throw() =>
        Assert.InRange(CloudLayout.ShardFor(""), 0, CloudLayout.WantedShards - 1);
}
