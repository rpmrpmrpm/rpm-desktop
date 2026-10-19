using RPM.Core.Library;
using RPM.Desktop.Presentation.Import;

namespace RPM.Desktop.Tests;

/// <summary>
/// What the foot of the import pane says about a queue.
///
/// <para>The facts and their order are what is held here, not the words. The words are in
/// <c>strings.json</c>, in five languages. The question this answers, "does a clean queue say anything
/// about duplicates", has the same answer in all five.</para>
/// </summary>
public class QueueStatusTests
{
    private static QueueChipCounts Counts(
        int total = 0, int ticked = 0, int needsRecognition = 0, int duplicates = 0,
        int unreadable = 0, int inCloud = 0, long bytes = 0, double seconds = 0)
        => new(total, ticked, needsRecognition, duplicates, unreadable,
               SplitCandidates: 0, NoData: 0, inCloud, bytes, seconds);

    [Fact]
    public void An_empty_queue_says_nothing()
    {
        Assert.Empty(QueueStatus.Facts(Counts()));
    }

    [Fact]
    public void A_clean_queue_states_what_is_there_and_stops()
    {
        var facts = QueueStatus.Facts(Counts(total: 142, bytes: 3_200_000_000, seconds: 33_153));

        Assert.Equal(3, facts.Count);
        Assert.Equal("Queue_StatusFiles", facts[0].StringKey);
        Assert.Equal(142, facts[0].Count);
        Assert.Equal("3 GB", facts[1].Literal);
        Assert.Equal("9:12:33", facts[2].Literal);
    }

    [Fact]
    public void Each_problem_is_stated_on_its_own()
    {
        var facts = QueueStatus.Facts(
            Counts(total: 40, needsRecognition: 12, duplicates: 3, unreadable: 1));

        Assert.Equal(
            ["Queue_StatusFiles", "Queue_StatusNotIdentified", "Queue_StatusDuplicates",
             "Queue_StatusUnreadableOne"],
            facts.Select(f => f.StringKey));
    }

    [Fact]
    public void One_of_a_thing_gets_the_singular_key()
    {
        var facts = QueueStatus.Facts(Counts(total: 1, duplicates: 1, unreadable: 1));

        Assert.Equal(
            ["Queue_StatusFileOne", "Queue_StatusDuplicateOne", "Queue_StatusUnreadableOne"],
            facts.Select(f => f.StringKey));
    }

    [Fact]
    public void A_queue_with_no_size_or_length_yet_leaves_both_out()
    {
        // A scan that has found rows but not yet read their streams: stating "0 B, 0:00" would be
        // reporting on work that has not happened.
        var facts = QueueStatus.Facts(Counts(total: 8));

        Assert.Single(facts);
    }

    [Theory]
    [InlineData(0.4, "0:00")]
    [InlineData(59, "0:59")]
    [InlineData(212, "3:32")]
    [InlineData(3600, "1:00:00")]
    [InlineData(33_153, "9:12:33")]
    public void Running_time_reads_as_a_clock(double seconds, string expected)
    {
        Assert.Equal(expected, QueueStatus.Duration(seconds));
    }
}
