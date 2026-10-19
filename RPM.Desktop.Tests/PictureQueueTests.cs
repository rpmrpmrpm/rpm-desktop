using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using RPM.Desktop.Presentation.Pegasus;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// Pictures are started in the order the cards stand on the page, not the order they were queued. A row
/// filled after the build, such as a genre's popular songs, stands above the rows queued at the build. In
/// arrival order its pictures would wait behind every record on the page. In a large genre (about 280
/// albums) that can leave the popular songs grey for about nine seconds.
/// </summary>
public class PictureQueueTests
{
    [Fact]
    public async Task Cards_start_top_row_first_and_along_each_row_whatever_order_they_were_queued_in()
    {
        var queue = new PictureQueue(1);
        var started = new List<string>();
        var first = new TaskCompletionSource();

        // Holds the one slot while the rest are queued, as the page's first pictures do while the
        // collection is read.
        queue.Add(new CardPlace(9, 0), async () =>
        {
            started.Add("holder");
            await first.Task;
        });

        queue.Add(new CardPlace(6, 0), Record("decade"));
        queue.Add(new CardPlace(4, 1), Record("recent, second"));
        queue.Add(new CardPlace(4, 0), Record("recent, first"));
        queue.Add(new CardPlace(2, 0), Record("popular, first"));
        queue.Add(new CardPlace(2, 1), Record("popular, second"));

        first.SetResult();
        await Task.Delay(50);

        Assert.Equal(
            ["holder", "popular, first", "popular, second", "recent, first", "recent, second", "decade"],
            started);

        Func<Task> Record(string name) => () =>
        {
            started.Add(name);
            return Task.CompletedTask;
        };
    }

    [Fact]
    public async Task Cards_at_one_place_start_in_the_order_they_were_added()
    {
        var queue = new PictureQueue(1);
        var started = new List<string>();
        var first = new TaskCompletionSource();

        queue.Add(new CardPlace(0, 0), async () => await first.Task);
        queue.Add(new CardPlace(3, 2), () => { started.Add("a"); return Task.CompletedTask; });
        queue.Add(new CardPlace(3, 2), () => { started.Add("b"); return Task.CompletedTask; });

        first.SetResult();
        await Task.Delay(50);

        Assert.Equal(["a", "b"], started);
    }
}
