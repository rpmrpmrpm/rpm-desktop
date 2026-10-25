using RPM.Core.Playback;

namespace RPM.Core.Tests;

/// <summary>
/// The queue drawer's headings over the songs the listener added (<see cref="QueueHeadings"/>). In a queue
/// written as a string, "o" is a song of the list that was played and "+" one the listener added.
/// </summary>
public class QueueHeadingsTests
{
    private const QueueHeading None = QueueHeading.None;
    private const QueueHeading UpNext = QueueHeading.UpNext;
    private const QueueHeading After = QueueHeading.AfterThat;

    private static IReadOnlyList<QueueHeading> Over(string queue) => QueueHeadings.Over([.. queue.Select(c => c == '+')]);

    [Fact]
    public void A_queue_with_nothing_added_has_no_headings()
    {
        Assert.Equal([None, None, None], Over("ooo"));
    }

    [Fact]
    public void An_empty_queue_has_no_headings()
    {
        Assert.Empty(Over(""));
    }

    [Fact]
    public void Added_songs_in_the_middle_get_up_next_and_the_list_goes_on_under_after_that()
    {
        Assert.Equal([None, None, UpNext, None, After, None], Over("oo++oo"));
    }

    [Fact]
    public void A_run_at_the_end_has_no_after_that()
    {
        Assert.Equal([None, UpNext, None], Over("o++"));
    }

    [Fact]
    public void A_run_at_the_top_still_gets_its_heading()
    {
        Assert.Equal([UpNext, After], Over("+o"));
    }

    [Fact]
    public void Every_run_gets_its_own_heading()
    {
        Assert.Equal([None, UpNext, After, UpNext], Over("o+o+"));
    }

    [Fact]
    public void A_queue_of_added_songs_only_has_one_heading_at_the_top()
    {
        Assert.Equal([UpNext, None, None], Over("+++"));
    }
}
