using RPM.Core.Library;

namespace RPM.Core.Tests;

public class MoodClockTests
{
    [Theory]
    [InlineData(0, SmartPlaylistKind.Sleep)]
    [InlineData(3, SmartPlaylistKind.Sleep)]
    [InlineData(4, SmartPlaylistKind.Sleep)]
    [InlineData(5, SmartPlaylistKind.Calm)]
    [InlineData(8, SmartPlaylistKind.Calm)]
    [InlineData(9, SmartPlaylistKind.Calm)]
    [InlineData(10, SmartPlaylistKind.Focus)]
    [InlineData(16, SmartPlaylistKind.Focus)]
    [InlineData(17, SmartPlaylistKind.Energetic)]
    [InlineData(19, SmartPlaylistKind.Energetic)]
    [InlineData(20, SmartPlaylistKind.Danceable)]
    [InlineData(21, SmartPlaylistKind.Danceable)]
    [InlineData(22, SmartPlaylistKind.Sleep)]
    [InlineData(23, SmartPlaylistKind.Sleep)]
    public void Each_hour_asks_for_its_own_mood(int hour, SmartPlaylistKind want)
        => Assert.Equal(want, MoodClock.Best(hour));

    [Fact]
    public void Night_into_morning_is_the_chill_one()
    {
        // Five in the morning to ten is Calm, which the shelf calls Chill.
        for (var hour = 5; hour <= 9; hour++)
            Assert.Equal(SmartPlaylistKind.Calm, MoodClock.Best(hour));
    }

    [Fact]
    public void The_night_runs_on_the_night_list()
    {
        // Ten at night to five in the morning, across midnight, is Lights out.
        foreach (var hour in new[] { 22, 23, 0, 1, 2, 3, 4 })
            Assert.Equal(SmartPlaylistKind.Sleep, MoodClock.Best(hour));
    }

    [Fact]
    public void Every_band_names_every_mood()
    {
        // A band that named only one mood would give nothing on a shelf that cannot fill that list.
        // Each band names all five, in its own order, so the caller always has a fallback.
        SmartPlaylistKind[] moods =
        [
            SmartPlaylistKind.Sleep, SmartPlaylistKind.Calm,
            SmartPlaylistKind.Focus, SmartPlaylistKind.Energetic, SmartPlaylistKind.Danceable,
        ];

        for (var hour = 0; hour < 24; hour++)
        {
            var order = MoodClock.Order(hour);
            Assert.Equal(moods.Length, order.Count);
            Assert.Equal(moods.Length, order.Distinct().Count());
            foreach (var mood in moods)
                Assert.Contains(mood, order);
        }
    }

    [Fact]
    public void An_hour_off_the_clock_is_wrapped_rather_than_refused()
    {
        // A caller doing arithmetic on the hour (an hour back, an hour forward) must not be able to
        // pass a value that makes this throw.
        Assert.Equal(MoodClock.Best(1), MoodClock.Best(25));
        Assert.Equal(MoodClock.Best(23), MoodClock.Best(-1));
        Assert.Equal(MoodClock.Best(0), MoodClock.Best(24));
    }
}
