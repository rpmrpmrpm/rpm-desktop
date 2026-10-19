using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RPM.Core.Library;
using RPM.Desktop.Presentation.Main.Genres;
using RPM.Desktop.Presentation.Main.Genres.Models;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// The loader that fills the genre wall. It must use no more than its share of the drawing thread, and it
/// must never ask for the same card twice.
///
/// <para>These tests check the ORDER and the BOUND, which are the two halves of a smooth wall. A fetch must
/// not start straight from the realize callback for each card. If it did, a whole screen of cards would
/// queue decodes at once, and the UI thread could stall for half a second. Timing cannot be checked
/// reliably on a build machine. So these tests check that two workers run, that a card is taken once, that
/// the cards on screen are filled first, and that letting the view go stops the workers without losing the
/// card they hold.</para>
///
/// <para>There is no dispatcher. The loader accepts one that can be null, because the pacing has nothing to
/// pace against when there is no window. The tests below check what the loader does, not when it does
/// it.</para>
/// </summary>
public class GenreFaceStreamTests
{
    private static GenreWallItem Shelf(string name) => new(name, [], []);

    private static GenreTile Card(string genre) =>
        new(Shelf(genre), new GenreCardMetrics(), flushRight: false);

    /// <summary>Waits until the condition is true, or gives up. The workers run on the thread pool and
    /// there is no dispatcher to pump, so the only thing to wait for is the effect.</summary>
    private static async Task<bool> Until(Func<bool> settled, int ms = 5000)
    {
        for (var waited = 0; waited < ms; waited += 10)
        {
            if (settled())
                return true;
            await Task.Delay(10);
        }
        return settled();
    }

    /// <summary>A card is taken once, however many times the wall is woken. A fast scroll wakes the loader
    /// on every realize. Two fetches for one picture would mean two downloads of it.</summary>
    [Fact]
    public async Task A_card_is_only_ever_taken_once()
    {
        var cards = new[] { Card("Rock"), Card("Pop"), Card("Jazz") };
        var reads = new Dictionary<string, int>();

        var stream = new GenreFaceStream(null, new GenreFaceStream.Wall(
            OnScreen: () => cards,
            Everything: () => cards,
            Ready: () => true,
            Read: (card, _) =>
            {
                lock (reads)
                    reads[card.Name] = reads.TryGetValue(card.Name, out var n) ? n + 1 : 1;
                return Task.FromResult<byte[]?>([1]);
            },
            Colour: (_, _) => Task.CompletedTask,
            Show: (_, _) => Task.CompletedTask));

        for (var i = 0; i < 20; i++)
            stream.Wake();

        Assert.True(await Until(() => cards.All(c => c.Asked)));
        await Task.Delay(50);   // nothing else arrives after this

        lock (reads)
        {
            Assert.Equal(3, reads.Count);
            Assert.All(reads.Values, n => Assert.Equal(1, n));
        }
    }

    /// <summary>Never more than two cards at once. Two is the limit because the gate below resolves two
    /// genres at a time. A third worker would only wait in a queue.</summary>
    [Fact]
    public async Task No_more_than_two_cards_are_filled_at_once()
    {
        var cards = Enumerable.Range(0, 12).Select(i => Card($"G{i}")).ToArray();
        var inFlight = 0;
        var most = 0;

        var stream = new GenreFaceStream(null, new GenreFaceStream.Wall(
            OnScreen: () => cards,
            Everything: () => cards,
            Ready: () => true,
            Read: async (_, _) =>
            {
                var now = Interlocked.Increment(ref inFlight);
                InterlockedMax(ref most, now);
                await Task.Delay(20);
                Interlocked.Decrement(ref inFlight);
                return (byte[]?)[1];
            },
            Colour: (_, _) => Task.CompletedTask,
            Show: (_, _) => Task.CompletedTask));

        stream.Wake();

        Assert.True(await Until(() => cards.All(c => c.Asked)));
        Assert.InRange(most, 1, 2);
    }

    /// <summary>The cards on the reader's screen are filled first. Everything on the wall is filled in the
    /// end, so a fast scroll lands on pictures and not on plain colours. But the cards the reader is
    /// looking at do not wait behind the cards they are not looking at.</summary>
    [Fact]
    public async Task The_cards_on_screen_are_filled_first()
    {
        var all = Enumerable.Range(0, 8).Select(i => Card($"G{i}")).ToArray();
        var onScreen = new[] { all[6], all[7] };
        var order = new List<string>();

        var stream = new GenreFaceStream(null, new GenreFaceStream.Wall(
            OnScreen: () => onScreen,
            Everything: () => all,
            Ready: () => true,
            Read: (card, _) =>
            {
                lock (order)
                    order.Add(card.Name);
                return Task.FromResult<byte[]?>([1]);
            },
            Colour: (_, _) => Task.CompletedTask,
            Show: (_, _) => Task.CompletedTask));

        stream.Wake();
        Assert.True(await Until(() => all.All(c => c.Asked)));

        lock (order)
        {
            Assert.Equal(8, order.Count);

            // The two on screen are taken by the two workers before anything else is looked at.
            Assert.Equal(["G6", "G7"], order.Take(2).OrderBy(n => n).ToArray());
            Assert.Equal(8, order.Distinct().Count());
        }
    }

    /// <summary>A card whose picture is refused is not asked about again. Otherwise a wall with no network
    /// would keep asking for the same card while it is shown.</summary>
    [Fact]
    public async Task A_genre_nobody_has_a_photograph_of_is_asked_once()
    {
        var cards = new[] { Card("Rock") };
        var reads = 0;

        var stream = new GenreFaceStream(null, new GenreFaceStream.Wall(
            OnScreen: () => cards,
            Everything: () => cards,
            Ready: () => true,
            Read: (_, _) =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult<byte[]?>(null);
            },
            Colour: (_, _) => Task.CompletedTask,
            Show: (_, _) => throw new InvalidOperationException("nothing to show")));

        stream.Wake();
        Assert.True(await Until(() => cards[0].Asked));
        await Task.Delay(50);

        Assert.Equal(1, reads);
    }

    /// <summary>A picture that cannot be decoded leaves that card without one, and the wall goes on. A
    /// failed picture must not stop the loader.</summary>
    [Fact]
    public async Task A_card_that_throws_does_not_stop_the_rest()
    {
        var cards = new[] { Card("Rock"), Card("Pop"), Card("Jazz") };

        var stream = new GenreFaceStream(null, new GenreFaceStream.Wall(
            OnScreen: () => cards,
            Everything: () => cards,
            Ready: () => true,
            Read: (_, _) => Task.FromResult<byte[]?>([1]),
            Colour: (_, _) => Task.CompletedTask,
            Show: (card, _) => card.Name == "Pop"
                ? Task.FromException(new InvalidOperationException("not a picture"))
                : Task.CompletedTask));

        stream.Wake();

        Assert.True(await Until(() => cards.All(c => c.Asked)));
    }

    /// <summary>The view is released while a card is held. The workers stop, and the card is put back. So
    /// when the wall is shown again, it asks for that card, and the answer comes from the cache, because
    /// the work itself was never cancelled.</summary>
    [Fact]
    public async Task Letting_the_view_go_puts_the_card_in_hand_back()
    {
        var cards = new[] { Card("Rock") };
        var taken = new TaskCompletionSource();

        var stream = new GenreFaceStream(null, new GenreFaceStream.Wall(
            OnScreen: () => cards,
            Everything: () => cards,
            Ready: () => true,
            Read: async (_, ct) =>
            {
                taken.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return null;
            },
            Colour: (_, _) => Task.CompletedTask,
            Show: (_, _) => Task.CompletedTask));

        stream.Wake();
        await taken.Task;
        Assert.True(cards[0].Asked);

        stream.Stop();

        Assert.True(await Until(() => !cards[0].Asked));
    }

    /// <summary>A wall that is shown again works too. The cancellation token that stopped the earlier
    /// workers does not refuse the fetches made later.</summary>
    [Fact]
    public async Task A_stopped_wall_fills_again_when_it_is_woken()
    {
        var cards = new[] { Card("Rock") };
        var reads = 0;

        var stream = new GenreFaceStream(null, new GenreFaceStream.Wall(
            OnScreen: () => cards,
            Everything: () => cards,
            Ready: () => true,
            Read: (_, _) =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult<byte[]?>([1]);
            },
            Colour: (_, _) => Task.CompletedTask,
            Show: (_, _) => Task.CompletedTask));

        stream.Stop();
        stream.Wake();

        Assert.True(await Until(() => cards[0].Asked && Volatile.Read(ref reads) == 1));
    }

    /// <summary>The loader takes no card until there is something to read with. The wall is laid out before
    /// the page gives it the read delegate. A worker that ran in that gap would mark every card as asked
    /// and answer none of them.</summary>
    [Fact]
    public async Task Nothing_is_taken_before_the_wall_can_read()
    {
        var cards = new[] { Card("Rock"), Card("Pop") };
        var ready = false;
        var reads = 0;

        var stream = new GenreFaceStream(null, new GenreFaceStream.Wall(
            OnScreen: () => cards,
            Everything: () => cards,
            Ready: () => ready,
            Read: (_, _) =>
            {
                Interlocked.Increment(ref reads);
                return Task.FromResult<byte[]?>([1]);
            },
            Colour: (_, _) => Task.CompletedTask,
            Show: (_, _) => Task.CompletedTask));

        stream.Wake();
        await Task.Delay(60);

        Assert.All(cards, c => Assert.False(c.Asked));
        Assert.Equal(0, Volatile.Read(ref reads));

        // …and once there is, a wake fills the wall in.
        ready = true;
        stream.Wake();
        Assert.True(await Until(() => cards.All(c => c.Asked)));
    }

    private static void InterlockedMax(ref int at, int value)
    {
        int was;
        do
        {
            was = Volatile.Read(ref at);
            if (value <= was)
                return;
        }
        while (Interlocked.CompareExchange(ref at, value, was) != was);
    }
}
