namespace RPM.Fingerprint;

/// <summary>
/// Where things live in the store.
///
/// <para>
/// Here because two functions have to agree on it exactly and cannot share much else: the API, which
/// signs URLs for these keys, and the transcoder, which writes them. A second copy of
/// <c>rate/128/&lt;sha&gt;.m4a</c> in the other one would be a rendition written where nothing looks
/// for it, and nothing would fail. The store would simply appear to have made nothing, for ever.
/// </para>
/// </summary>
public static class CloudLayout
{
    public const string Bucket = "rpm-user-audio";
    public const string BlobsTable = "RpmBlobs";

    /// <summary>Which names stand for the same record. Here for the same reason everything else is:
    /// the API resolves these and the transcoder writes them, and a second spelling in one of them
    /// would be rows nothing ever looks up.</summary>
    public const string RecordingKeysTable = "RpmRecordingKeys";

    /// <summary>What is known about a record, a fact at a time.</summary>
    public const string RecordingFactsTable = "RpmRecordingFacts";

    /// <summary>The timing of a recording's lyric lines: when each line is sung, as the transcoder measured it
    /// from this exact encoding. There is one object per recording, shared by every account that holds
    /// it. Lines are kept by their text and time, so measurements against different lyric sheets can
    /// sit side by side.</summary>
    public static string LyricTimingKey(string sha256) => $"lyrictiming/{sha256}.json";

    /// <summary>A measurement asked for and not finished, so the same recording is not sent to the transcoder twice
    /// while it works. Its age is the object's own LastModified.</summary>
    public static string LyricTimingPendingKey(string sha256) => $"lyrictiming/{sha256}.pending";

    /// <summary>The canonical original: one object per unique encoding, keyed by its own hash.</summary>
    public static string BlobKey(string sha256) => $"blobs/{sha256}";

    /// <summary>
    /// The prefix on every row of <see cref="BlobsTable"/> that is not a blob.
    ///
    /// <para>A sha256 is 64 lowercase hex characters, so a key containing a slash cannot be one and
    /// cannot collide with one. Anything that walks the table looking for blobs must skip these rows.
    /// A sweep that did not skip them would put the lease row on the want list as a want for a blob
    /// that does not exist.</para>
    /// </summary>
    public const string ReservedKeyPrefix = "rpm/";

    /// <summary>The row the transcoder's drain lease lives on. See <c>DrainLease</c>.</summary>
    public const string DrainLeaseKey = ReservedKeyPrefix + "transcode-drain-lease";

    /// <summary>Until when the holder of the drain lease holds it, in unix seconds.</summary>
    public const string LeaseUntilAttribute = "leaseUntil";

    /// <summary>Which invocation holds the drain lease. A holder only releases its own lease: a run
    /// that overran and lost it must not release the one that took over.</summary>
    public const string LeaseHolderAttribute = "leaseBy";

    /// <summary>A rendition, or the head clip that is a track's first seconds.</summary>
    public static string RenditionKey(string sha256, int kilobits) =>
        kilobits == HeadClip ? $"head/{sha256}.m4a" : $"rate/{kilobits}/{sha256}.m4a";

    /// <summary>The head clip's stand-in rate. It is not on the ladder, because it is three seconds long
    /// and not a rendition. It lives in the same place and is made by the same pass.</summary>
    public const int HeadClip = -1;

    /// <summary>
    /// The stand-in rate for "measure how loud this recording is".
    ///
    /// <para>This is not a rendition. It makes no object and puts nothing in the bucket. It writes two
    /// numbers onto the blob's own row. It still uses the want list, because the want list is the queue
    /// for "something has to read this blob, and no desktop will do it". The case it is for is a track
    /// that nobody's computer will measure, such as one from a machine that has since been replaced.</para>
    /// </summary>
    public const int Loudness = -2;

    /// <summary>
    /// The stand-in rate for "work out how this record is put together": its tempo, its beat grid,
    /// its key, and where to mix in and out.
    ///
    /// <para>This makes no object either. It writes what it finds into the shared pool, where anybody
    /// holding this encoding can read it. It exists for a record that no desktop will analyse. That is
    /// not the same set as a record that nobody will measure. An album ripped and uploaded today has
    /// not been near a desktop. A set started on a phone has nothing to beat-match against, and no way
    /// to work that out for itself.</para>
    /// </summary>
    public const int Analysis = -3;

    /// <summary>Everything a rendition may be, so neither side can invent a path.</summary>
    public static bool IsRenditionRate(int kilobits) =>
        kilobits == HeadClip || AudioLadder.IsOnTheLadder(kilobits);

    /// <summary>Everything the want list may hold: the renditions, and the two passes that are not
    /// renditions at all.</summary>
    public static bool IsWantable(int kilobits) =>
        kilobits == Loudness || kilobits == Analysis || IsRenditionRate(kilobits);

    // ── the queue's index ─────────────────────────────────────────────────────────────────────
    //
    // The want list is a set on the blob's own row. A set cannot be queried, so the list cannot be read
    // directly. Draining it by scanning the table has a limit: the scan stops after a fixed number of
    // pages and starts again at the beginning on every tick. Once the table is larger than that limit,
    // the queued rows lie beyond it, and the worker never reaches them. A tick that finds nothing does
    // not log, so this failure is silent.
    //
    // These two attributes make the set reachable through an index. They are written beside the set and
    // removed with it. A row that wants nothing carries neither attribute, so the index holds the queue
    // and nothing else, however large the table grows.

    /// <summary>The sparse index the worker drains, over <see cref="BlobsTable"/>.</summary>
    public const string WantedIndex = "wanted-index";

    /// <summary>Which shard of the index a row's want sits in: the index's partition key. A single
    /// partition would take every write the queue makes, so the rows are spread by their own hash,
    /// which is a number each side can work out and neither has to store.</summary>
    public const string WantedShardAttribute = "wantedShard";

    /// <summary>When the row first wanted anything, in unix seconds: the index's sort key, so a
    /// query comes back oldest first and the queue is drained in the order it was filled.</summary>
    public const string WantedAtAttribute = "wantedAt";

    /// <summary>How many shards the queue is spread over. Small enough that a worker can read all of
    /// them in one tick, large enough that no single partition takes every write.</summary>
    public const int WantedShards = 8;

    /// <summary>
    /// Which shard a blob's want belongs in, from the blob's own hash.
    /// </summary>
    /// <param name="sha256">The blob's hash, as the table keys it: lowercase hex.</param>
    public static int ShardFor(string sha256) =>
        string.IsNullOrEmpty(sha256) ? 0 : HexValue(sha256[0]) % WantedShards;

    private static int HexValue(char digit) => digit switch
    {
        >= '0' and <= '9' => digit - '0',
        >= 'a' and <= 'f' => digit - 'a' + 10,
        >= 'A' and <= 'F' => digit - 'A' + 10,
        _ => 0,
    };
}
