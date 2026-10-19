namespace RPM.Core.Cloud;

/// <summary>
/// How much space the account has left in the cloud, and what that means for the uploader.
///
/// <para>
/// The store is the one part of rpm that can run out. Free accounts get 100 GB and supporters have
/// no limit, so every question about whether something may go up is asked here and is not worked
/// out again at each call site: what is left, whether the next file fits, and whether the user is
/// close enough to the limit to be told.
/// </para>
///
/// <para>
/// <see cref="Known"/> is the important one. A quota that has never been read (signed out, offline,
/// or a failed request) is zero, and treating zero as "no space" would stop a working library from
/// syncing because one HTTP call did not return. So an unknown quota says yes to everything and
/// claims nothing: the server refuses what will not fit, and this check only saves the attempt.
/// </para>
/// </summary>
public readonly record struct CloudSpace(long UsedBytes, long QuotaBytes, bool Unlimited = false)
{
    /// <summary>
    /// Less space than this and the account is "almost full". This is the point at which the app says
    /// so, and does not wait for the first upload to be refused.
    /// </summary>
    public const long AlmostFullBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Nothing has been read back from the store yet. Refuses nothing; see the class remarks.</summary>
    public static readonly CloudSpace Unknown = new(0, 0);

    /// <summary>Whether the limit is actually known. False before the first quota read.</summary>
    public bool Known => Unlimited || QuotaBytes > 0;

    /// <summary>What is left, never negative. For a supporter the space is not a number worth showing.</summary>
    public long FreeBytes => Unlimited ? long.MaxValue : Known ? Math.Max(0, QuotaBytes - UsedBytes) : 0;

    /// <summary>Nothing more fits. This is where the uploader stops.</summary>
    public bool IsFull => Known && !Unlimited && UsedBytes >= QuotaBytes;

    /// <summary>Within <see cref="AlmostFullBytes"/> of the limit. Full counts as almost full.</summary>
    public bool IsAlmostFull => Known && !Unlimited && QuotaBytes - UsedBytes < AlmostFullBytes;

    /// <summary>Close to the limit but not over it, which is the only state that needs its own wording.</summary>
    public bool IsNearlyFull => IsAlmostFull && !IsFull;

    /// <summary>Whether a file of this size may be sent. Unknown space and unlimited space both say yes.</summary>
    public bool Fits(long bytes) => !Known || Unlimited || bytes <= QuotaBytes - UsedBytes;

    /// <summary>How full, from 0 to 1, for a bar to draw. Unlimited and unknown are both zero: a limit
    /// that does not exist has no fraction.</summary>
    public double UsedFraction =>
        Unlimited || QuotaBytes <= 0 ? 0 : Math.Clamp((double)UsedBytes / QuotaBytes, 0, 1);
}

/// <summary>
/// What running out of space does to the sync mode.
///
/// <para>
/// "All my music" is a standing instruction to keep uploading, and a full store turns it into a
/// request that the app can only fail: a whole library of uploads that cannot land, retried for
/// ever. So the mode drops to the user's own selection, which is the largest thing that can still
/// be done, and the fact that it was dropped is remembered. Nothing already in the cloud is touched.
/// </para>
///
/// <para>
/// The way back is buying space, not freeing it: a few gigabytes freed by removing an album would
/// put "all my music" straight back into the same limit. Selected tracks resume as soon as they
/// fit, because the uploader asks for each file.
/// </para>
/// </summary>
public static class CloudFullPolicy
{
    /// <summary>The mode to fall back to when the store is full.</summary>
    public static CloudSyncMode WhenFull(CloudSyncMode mode) =>
        mode == CloudSyncMode.All ? CloudSyncMode.Selected : mode;

    /// <summary>Whether the fall-back should be undone, which only happens on unlimited space.</summary>
    /// <param name="pausedByFull">Whether "all my music" was on when the store filled up.</param>
    /// <param name="unlimited">Whether the account now has no ceiling.</param>
    /// <param name="mode">The mode in force now. Anything the user has chosen since stands, so only
    /// the mode that the fall-back left behind is put back.</param>
    public static bool ShouldRestoreAll(bool pausedByFull, bool unlimited, CloudSyncMode mode) =>
        pausedByFull && unlimited && mode == CloudSyncMode.Selected;
}

/// <summary>
/// How often the app may bring up the "your storage is full" window.
///
/// <para>Once a week. The state it reports lasts until the user does something about it, and a
/// window that came back on every sync pass would be nagging and not informing.</para>
/// </summary>
public static class CloudFullNotice
{
    public static readonly TimeSpan Every = TimeSpan.FromDays(7);

    /// <summary>
    /// How many showings before the user is offered a way to stop them.
    ///
    /// <para>Two. The first time, the window is news, and a box saying "never show this again" would
    /// be a switch to dismiss a message that nobody has finished reading. The second time (a week
    /// later, with the store still full) it has become a repeat, and a repeat must be refusable.</para>
    /// </summary>
    public const int ShowingsBeforeOptOut = 2;

    /// <param name="lastShownUtc">When it was last shown, or null for never.</param>
    /// <param name="utcNow">Now.</param>
    /// <param name="suppressed">Whether the user has asked never to see it again, which is final.</param>
    /// <returns>Whether it may be shown now. A stamp in the future (a clock that has been put back, or
    /// a file copied from another machine) counts as never, and does not silence the window until the
    /// date catches up.</returns>
    public static bool Due(DateTime? lastShownUtc, DateTime utcNow, bool suppressed = false) =>
        !suppressed && (lastShownUtc is not { } last || utcNow < last || utcNow - last >= Every);

    /// <summary>Whether this showing carries the opt-out box.</summary>
    /// <param name="shownBefore">How many times it has been shown before this one.</param>
    public static bool MayOptOut(int shownBefore) => shownBefore >= ShowingsBeforeOptOut - 1;
}
