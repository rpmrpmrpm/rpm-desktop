using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// The one place a play's clock is converted, between the mirror's UTC ticks and the cloud's unix
/// seconds.
///
/// <para>Lifted out of the sync leg because "never played" is nought on both sides and is not the
/// same instant on both sides: year one in ticks is a large negative number of unix seconds, and a
/// row carrying that would sort as the oldest listening this account has ever had rather than as no
/// listening at all. Recently listened is ordered on this value.</para>
/// </summary>
public class PlayClockTests
{
    [Fact]
    public void Never_played_stays_never_played_in_both_directions()
    {
        Assert.Equal(0, PlayClock.Seconds(0));
        Assert.Equal(0, PlayClock.Ticks(0));
    }

    [Fact]
    public void A_negative_reading_is_never_played_rather_than_a_date()
    {
        Assert.Equal(0, PlayClock.Seconds(-1));
        Assert.Equal(0, PlayClock.Ticks(-1));
    }

    [Fact]
    public void An_instant_survives_the_round_trip_to_the_second()
    {
        var when = new DateTimeOffset(2026, 9, 22, 21, 30, 15, TimeSpan.Zero);

        var seconds = PlayClock.Seconds(when.UtcTicks);

        Assert.Equal(when.ToUnixTimeSeconds(), seconds);
        Assert.Equal(when.UtcTicks, PlayClock.Ticks(seconds));
    }

    [Fact]
    public void The_sub_second_part_is_dropped_rather_than_rounded_up()
    {
        // Rounding up would put a play a fraction of a second into the future of the moment it was
        // counted, and two heads comparing stamps would disagree about which came first.
        var when = new DateTimeOffset(2026, 9, 22, 21, 30, 15, 900, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 9, 22, 21, 30, 15, TimeSpan.Zero).ToUnixTimeSeconds(),
            PlayClock.Seconds(when.UtcTicks));
    }
}
