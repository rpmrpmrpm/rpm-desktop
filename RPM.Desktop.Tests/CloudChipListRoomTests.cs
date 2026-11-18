using RPM.Desktop.Presentation.Main.Transport.Controls;

namespace RPM.Desktop.Tests;

/// <summary>
/// The cloud chip's flyout keeps its height while a run is going.
///
/// <para>The in-progress list loses a row when a track finishes and gains one when the next track
/// starts. Between two waves it is empty for a moment. The flyout opens upwards from the chip, so a
/// shorter list moves its top edge down and the page behind it shows through until the list grows
/// again.</para>
/// </summary>
public class CloudChipListRoomTests
{
    private const double OneRow = 51;
    private const double ThreeRows = 3 * OneRow;

    [Fact]
    public void A_running_list_keeps_its_height_when_a_row_leaves()
    {
        Assert.Equal(ThreeRows, CloudChip.ListRoom(held: ThreeRows, measured: 2 * OneRow, running: true));
    }

    [Fact]
    public void A_running_list_keeps_its_height_when_it_is_empty_between_waves()
    {
        Assert.Equal(ThreeRows, CloudChip.ListRoom(held: ThreeRows, measured: 0, running: true));
    }

    [Fact]
    public void A_running_list_grows_with_its_rows()
    {
        Assert.Equal(ThreeRows, CloudChip.ListRoom(held: OneRow, measured: ThreeRows, running: true));
    }

    [Fact]
    public void A_stopped_run_gives_the_height_back()
    {
        Assert.Equal(0, CloudChip.ListRoom(held: ThreeRows, measured: ThreeRows, running: false));
    }

    [Fact]
    public void The_list_stays_on_screen_while_it_is_empty_between_waves()
    {
        Assert.True(CloudChip.ShowsList(running: true, rows: 0, room: ThreeRows));
    }

    [Fact]
    public void The_list_is_hidden_before_the_first_track_starts()
    {
        Assert.False(CloudChip.ShowsList(running: true, rows: 0, room: 0));
    }

    [Fact]
    public void The_list_is_hidden_once_the_run_stops()
    {
        Assert.False(CloudChip.ShowsList(running: false, rows: 2, room: ThreeRows));
    }
}
