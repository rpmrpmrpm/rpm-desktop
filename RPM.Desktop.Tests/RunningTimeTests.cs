using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Tests;

/// <summary>
/// A running time stated in minutes. The library pane's subtitle and the record summary must agree. Both
/// take the number from <see cref="PaneStats.WholeMinutes"/>, which drops the seconds, as the phone does.
/// Rounding would show 6 minutes for a 5:47 song where the other view shows 5.
/// </summary>
public class RunningTimeTests
{
    [Fact]
    public void A_song_of_five_minutes_forty_seven_is_five_minutes()
        => Assert.Equal(5, PaneStats.WholeMinutes(5 * 60 + 47));

    [Fact]
    public void Seconds_round_to_the_nearest_second_before_the_minute_is_taken()
        // 59.6 s reads as 1:00 on a track line, so it is one minute here too.
        => Assert.Equal(1, PaneStats.WholeMinutes(59.6));

    [Fact]
    public void A_negative_length_is_no_minutes()
        => Assert.Equal(0, PaneStats.WholeMinutes(-12));
}
