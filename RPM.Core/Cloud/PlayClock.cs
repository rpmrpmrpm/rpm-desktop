namespace RPM.Core.Cloud;

/// <summary>Converts a play time between the two clocks: the mirror's UTC ticks and the cloud's unix
/// seconds.
///
/// <para>This is done here, not inline at the two call sites in the sync code. Zero means "never
/// played" on both sides, but zero is not the same instant on both sides. Zero ticks is year one, which
/// converts to a large negative number of unix seconds. A row with that value would sort as the oldest
/// listening in the account, not as no listening at all. Recently listened is ordered on this
/// value.</para>
///
/// <para>The wire uses seconds because every other timestamp in this API does. The precision lost is
/// less than a second, which does not matter for ordering records by when they were last played.</para>
/// </summary>
public static class PlayClock
{
    /// <summary>The mirror's UTC ticks as the cloud's unix seconds. Zero for never played.</summary>
    public static long Seconds(long utcTicks) =>
        utcTicks <= 0 ? 0 : new DateTimeOffset(utcTicks, TimeSpan.Zero).ToUnixTimeSeconds();

    /// <summary>The cloud's unix seconds as the mirror's UTC ticks. Zero for never played.</summary>
    public static long Ticks(long unixSeconds) =>
        unixSeconds <= 0 ? 0 : DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcTicks;
}
