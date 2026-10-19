namespace RPM.Core.Cloud;

/// <summary>What was done to a track's cloud copy.</summary>
public enum CloudOp
{
    /// <summary>Put in the cloud: transcoded to the canonical form, then sent.</summary>
    Upload,

    /// <summary>Taken out of the cloud, server copy deleted.</summary>
    Remove,

    /// <summary>Fetched back down to this machine.</summary>
    Restore,

    /// <summary>
    /// The local copy deleted and the cloud copy kept, to free disk space.
    ///
    /// <para>Not <see cref="Remove"/>, which is the opposite: that one deletes the cloud copy and
    /// leaves the file. If the log used one word for both, it could not answer the one question it
    /// exists for: which copy of my music did this machine delete.</para>
    /// </summary>
    Offload,
}

/// <summary>How it ended.</summary>
public enum CloudOutcome
{
    Done,
    Failed,
}

/// <summary>
/// One finished cloud operation, kept so the user can see what happened to their music afterwards.
/// Written when the work ends, never while it runs. The live view belongs to
/// <c>CloudJobService</c>, and a row here is a fact, not a status.
/// </summary>
/// <param name="Detail">The sentence the user is shown.</param>
/// <param name="FullError">Everything known about a failure (<see cref="CloudFailure.InFull(Exception)"/>),
/// for a report. Null for a success, for a failure that threw nothing, and for rows written by an
/// older build.</param>
public sealed record CloudActivityEntry(
    long Id,
    CloudOp Op,
    string Title,
    string? Subtitle,
    CloudOutcome Outcome,
    string? Detail,
    string? FullError,
    long Bytes,
    long AtUtcTicks);

/// <summary>
/// How much history is kept.
///
/// <para>Thirty days is the length the log needs: "did last week's upload finish?" is worth asking,
/// "what did I sync in March?" is not. The row cap belongs to the same limit. A library that uploads
/// a hundred thousand tracks would otherwise put a hundred thousand rows in the mirror in one day,
/// all saying the same thing.</para>
/// </summary>
public static class CloudActivityRetention
{
    public const int Days = 30;
    public const int MaxRows = 500;

    /// <summary>The oldest tick still worth keeping, given a "now".</summary>
    public static long CutoffTicks(long nowUtcTicks) =>
        nowUtcTicks - TimeSpan.FromDays(Days).Ticks;
}

/// <summary>
/// The failures in the log that still stand, for the settings page's Errors card.
///
/// <para>The newest entry for a track and an operation decides. If it is a success, the earlier
/// failures are history and are not shown. If it is a failure, it is shown once, however many times
/// the same track failed before. Without this, a track that failed once and uploaded a minute later
/// would stay in the error list for the thirty days the log keeps it.</para>
///
/// <para>A track is identified by title and album, because that is all a log row records. Two tracks
/// with the same title on the same album count as one.</para>
/// </summary>
public static class CloudActivityFailures
{
    public static IReadOnlyList<CloudActivityEntry> Outstanding(IEnumerable<CloudActivityEntry> log)
    {
        var decided = new HashSet<(CloudOp, string, string?)>();
        var outstanding = new List<CloudActivityEntry>();

        // Sorted here, not trusted from the query, so the answer does not depend on the order in which
        // the caller read the rows.
        foreach (var entry in log.OrderByDescending(e => e.AtUtcTicks).ThenByDescending(e => e.Id))
        {
            if (!decided.Add((entry.Op, entry.Title, entry.Subtitle)))
                continue;

            if (entry.Outcome == CloudOutcome.Failed)
                outstanding.Add(entry);
        }

        return outstanding;
    }
}

/// <summary>The unit in which a log line's age is stated.</summary>
public enum AgeUnit
{
    JustNow,
    Minutes,
    Hours,
    Days,
}

/// <summary>How long ago something happened, in the unit that suits it.</summary>
public readonly record struct RelativeAge(AgeUnit Unit, int Value);

/// <summary>
/// Turns a timestamp into "how long ago", with one unit of precision.
///
/// <para>Kept out of the page because the rounding is where mistakes happen: 90 seconds is "1 min
/// ago", not "1.5 min" and not "just now". 23 hours is hours and 25 hours is a day. A user who
/// checks the log to see whether last night's sync finished expects it to agree with their own
/// sense of last night.</para>
/// </summary>
public static class CloudActivityAge
{
    /// <summary>Under a minute is not worth showing as a number.</summary>
    private const int JustNowSeconds = 60;

    public static RelativeAge Of(long atUtcTicks, long nowUtcTicks)
    {
        // A clock that went backwards (a time correction, or a row written on another machine) reads as
        // "just now", not as a negative age.
        var span = TimeSpan.FromTicks(Math.Max(0, nowUtcTicks - atUtcTicks));

        if (span.TotalSeconds < JustNowSeconds)
            return new RelativeAge(AgeUnit.JustNow, 0);
        if (span.TotalMinutes < 60)
            return new RelativeAge(AgeUnit.Minutes, (int)span.TotalMinutes);
        if (span.TotalHours < 24)
            return new RelativeAge(AgeUnit.Hours, (int)span.TotalHours);
        return new RelativeAge(AgeUnit.Days, (int)span.TotalDays);
    }
}
