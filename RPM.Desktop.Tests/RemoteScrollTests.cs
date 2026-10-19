using RPM.Desktop.Presentation.Pegasus;

namespace RPM.Desktop.Tests;

/// <summary>
/// Where a scroller is moved to show what the TV remote's focus has moved onto in Pegasus
/// (RemoteScroll.Centred and Revealed). All in the viewport's points, at the scroller's present offset.
/// </summary>
public class RemoteScrollTests
{
    [Fact]
    public void A_move_to_another_row_puts_the_element_in_the_middle()
        // At 600 of an 800 high viewport, 60 tall: its middle at 630 goes to 400.
        => Assert.Equal(230, RemoteScroll.Centred(offset: 0, start: 600, length: 60, viewport: 800, scrollable: 2000));

    [Fact]
    public void Centring_stops_at_the_ends_of_the_list()
    {
        Assert.Equal(0, RemoteScroll.Centred(offset: 0, start: 100, length: 60, viewport: 800, scrollable: 2000));
        Assert.Equal(2000, RemoteScroll.Centred(offset: 1900, start: 700, length: 60, viewport: 800, scrollable: 2000));
    }

    [Fact]
    public void A_move_along_the_row_leaves_the_list_where_it_is_when_the_element_shows()
        => Assert.Equal(300, RemoteScroll.Revealed(offset: 300, start: 200, length: 60, viewport: 800, scrollable: 2000));

    /// <summary>The long track list on a record page: the line under the foot of the list is shown with
    /// some air under it.</summary>
    [Fact]
    public void An_element_cut_off_at_the_foot_is_brought_up_with_air_under_it()
        => Assert.Equal(84, RemoteScroll.Revealed(offset: 0, start: 780, length: 80, viewport: 800, scrollable: 2000));

    [Fact]
    public void An_element_cut_off_at_the_head_is_brought_down_with_air_over_it()
        => Assert.Equal(276, RemoteScroll.Revealed(offset: 300, start: 0, length: 80, viewport: 800, scrollable: 2000));
}
