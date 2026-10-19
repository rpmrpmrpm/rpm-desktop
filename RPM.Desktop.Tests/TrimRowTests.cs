using RPM.Desktop.Presentation.Controls;

namespace RPM.Desktop.Tests;

/// <summary>
/// The width a track title may measure against on its line. A title measured against unlimited width
/// (what a horizontal StackPanel gives it) never trims and is cut off mid-letter by the cell around it;
/// these hold the leftover width to what the line actually has.
/// </summary>
public class TrimRowTests
{
    [Fact]
    public void The_title_gets_what_the_other_pieces_and_the_gaps_leave()
        // 140 wide, a 20-wide mark and a 13-wide badge beside the title, three visible pieces so two gaps
        // of 6: 140 - 33 - 12.
        => Assert.Equal(95, TrimRow.Room(available: 140, othersWidth: 33, visibleCount: 3, spacing: 6));

    [Fact]
    public void A_title_alone_on_its_line_gets_the_whole_width()
        => Assert.Equal(140, TrimRow.Room(available: 140, othersWidth: 0, visibleCount: 1, spacing: 6));

    [Fact]
    public void A_line_narrower_than_its_other_pieces_gives_the_title_nothing_rather_than_less()
        => Assert.Equal(0, TrimRow.Room(available: 20, othersWidth: 33, visibleCount: 3, spacing: 6));

    /// <summary>Inside a parent that does not limit the width, the row behaves as the StackPanel it
    /// replaced: the title measures at its full length.</summary>
    [Fact]
    public void An_unlimited_line_stays_unlimited()
        => Assert.True(double.IsPositiveInfinity(
            TrimRow.Room(available: double.PositiveInfinity, othersWidth: 33, visibleCount: 3, spacing: 6)));
}
