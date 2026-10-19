namespace RPM.Core.Cloud;

/// <summary>
/// When a track that would not go up is tried again.
///
/// <para>
/// The queue is the library itself: a row's state is its place in the queue, so a track that fails
/// is still in the queue and would be picked up by the very next pass. Without a wait and a limit,
/// one unreadable file would spin the queue (the run finds it, fails, finds it again), and the log
/// would fill with the same error while the rest of the collection waits behind it.
/// </para>
/// </summary>
public static class CloudRetrySchedule
{
    /// <summary>After this many failures a track is left alone until the user does something about it.
    /// Five attempts spread over the backoff below take most of an hour, which is long enough for a
    /// network that is only down for a while to come back.</summary>
    public const int MaxAttempts = 5;

    /// <summary>The longest wait between attempts. The doubling stops here, so a track that has failed
    /// four times is tried every sixteen minutes and not every eight hours.</summary>
    public const int MaxBackoffMinutes = 16;

    /// <summary>Whether a track with this many failures behind it is still in the queue.</summary>
    public static bool MayTryAgain(int attempts) => attempts < MaxAttempts;

    /// <summary>Exponential backoff: 1, 2, 4, 8, 16 minutes, and 16 from then on.</summary>
    public static TimeSpan Backoff(int attempts) =>
        TimeSpan.FromMinutes(Math.Min(Math.Pow(2, Math.Max(0, attempts)), MaxBackoffMinutes));

    /// <summary>When the queue may look at this track again, in UTC ticks.</summary>
    public static long NextAttemptTicks(int attempts, DateTime nowUtc) =>
        (nowUtc + Backoff(attempts)).Ticks;

    /// <summary>
    /// Never, expressed as a time.
    ///
    /// <para>For the failure that waiting will not fix: a file whose format cannot carry an rpm id has
    /// nothing to fix itself into, and retrying it every sixteen minutes for the life of the
    /// installation would be a permanent error in the activity log. It is a date and not a state of its
    /// own, so the row still reads as "failed, with a reason" everywhere that shows it.</para>
    /// </summary>
    public static long NeverAgainTicks(DateTime nowUtc) => nowUtc.AddYears(100).Ticks;

    /// <summary>
    /// When a track whose upload just failed is tried again.
    ///
    /// <para>A permanent failure is final only when the attempt before this one ended with the same
    /// sentence. One refusal on its own is never trusted. A file that the import, a tag write or
    /// another program is still writing looks like a damaged file to ffmpeg, and without this rule
    /// every track of a freshly imported album would be refused for good seconds after it arrived. A
    /// file that really cannot be encoded costs one more attempt.</para>
    ///
    /// <para>The row's error is cleared when the file's size or date changes (see
    /// <c>LibraryDatabase.Upsert</c>), so a match means the same refusal on the same bytes.</para>
    /// </summary>
    /// <param name="sentence">What the user is told about this failure.</param>
    /// <param name="previousError">The row's error as it was before this attempt, or null when the
    /// previous attempt did not fail.</param>
    /// <param name="attempts">Failures before this one.</param>
    public static long AfterFailure(
        CloudFailureKind failure, string sentence, string? previousError, int attempts, DateTime nowUtc) =>
        failure.Permanent && string.Equals(sentence, previousError, StringComparison.Ordinal)
            ? NeverAgainTicks(nowUtc)
            : NextAttemptTicks(attempts, nowUtc);
}
