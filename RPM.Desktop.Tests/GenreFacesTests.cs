using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RPM.Core.Library;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Services.Art;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// The gate every genre's photograph comes through.
///
/// <para>These tests check concurrency, not pictures. The wall realizes a screen of cards at once. Each
/// card needs a chain of network lookups and two neural nets. Without a gate, many model runs would compete
/// for one session lock, and they would all be cancelled when the reader opened a genre. So each genre is
/// asked once, only a couple run at a time, and a caller that gives up does not cancel the work.</para>
/// </summary>
[Collection("GenreFaces")]
public class GenreFacesTests : IDisposable
{
    private static GenreWallItem Shelf(string name) => new(name, [], []);

    private static byte[] Answer(params byte[] bytes) => bytes;

    /// <summary>
    /// A cache folder for this class only, never the real one.
    /// </summary>
    /// <remarks>
    /// <b>This is required. Without it, these tests fail, and they fail by reading the machine's own
    /// pictures.</b> <c>GenreFaces</c> answers from disk before it resolves anything (see
    /// <c>GenreFaceCache</c>). If the suite used the real folder, its "was this resolved once?" checks
    /// would be answered by whatever the person running it had already looked at. The runs would also write
    /// into that folder. With one folder per instance, the cache starts empty in every case, and the real
    /// disk is not touched.
    ///
    /// <para><see cref="GenreFaceCache.RootOverride"/> is global to the process. This class and
    /// <see cref="GenreFaceCacheTests"/> are in one xunit collection for that reason. Two classes using it
    /// at once would each delete the other's folder.</para>
    /// </remarks>
    private readonly string _cache =
        Path.Combine(Path.GetTempPath(), "rpm-faces-" + Guid.NewGuid().ToString("N"));

    public GenreFacesTests()
    {
        Directory.CreateDirectory(_cache);
        GenreFaceCache.RootOverride = _cache;
        GenreFaces.Forget();
    }

    public void Dispose()
    {
        GenreFaceCache.RootOverride = null;
        try { Directory.Delete(_cache, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>Two cards that ask for one genre at the same time, for example the two sides of the wall
    /// after a re-pair, or a card and the page, share one piece of work.</summary>
    [Fact]
    public async Task One_genre_is_only_ever_resolved_once()
    {
        var runs = 0;
        var release = new TaskCompletionSource();

        Task<Asked<byte[]?>> Resolve(IReadOnlyList<string> acts, double shape, CancellationToken ct)
        {
            Interlocked.Increment(ref runs);
            return release.Task.ContinueWith(_ => Asked.Answer<byte[]?>(Answer(7)), TaskScheduler.Default);
        }

        var a = GenreFaces.ForAsync(Shelf("Rock"), ["Nick"], 1.0, Resolve, default);
        var b = GenreFaces.ForAsync(Shelf("Rock"), ["Nick"], 1.0, Resolve, default);

        release.SetResult();

        Assert.Equal(Answer(7), await a);
        Assert.Equal(Answer(7), await b);
        Assert.Equal(1, runs);

        // …and once it is answered, a third asker starts nothing at all.
        Assert.Equal(Answer(7), await GenreFaces.ForAsync(Shelf("Rock"), ["Nick"], 1.0, Resolve, default));
        Assert.Equal(1, runs);
    }

    /// <summary>
    /// A caller that gives up cancels its own wait, not the work.
    ///
    /// <para>A reader who opens a genre while the wall is filling must not cost the pictures the wall has
    /// started. The next asker gets its answer from the same task.</para>
    /// </summary>
    [Fact]
    public async Task A_caller_that_gives_up_does_not_take_the_work_with_it()
    {
        var runs = 0;
        var release = new TaskCompletionSource();

        Task<Asked<byte[]?>> Resolve(IReadOnlyList<string> acts, double shape, CancellationToken ct)
        {
            Interlocked.Increment(ref runs);
            return release.Task.ContinueWith(_ => Asked.Answer<byte[]?>(Answer(9)), TaskScheduler.Default);
        }

        using var giveUp = new CancellationTokenSource();
        var abandoned = GenreFaces.ForAsync(Shelf("Jazz"), ["Miles"], 1.0, Resolve, giveUp.Token);

        giveUp.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);

        release.SetResult();

        Assert.Equal(Answer(9), await GenreFaces.ForAsync(Shelf("Jazz"), ["Miles"], 1.0, Resolve, default));
        Assert.Equal(1, runs);
    }

    /// <summary>The answer is filed under the acts that were considered. So a genre whose artists changed
    /// is a different question. A library change that adds a record must be able to change the face on the
    /// card. A change that does not affect the acts must not cost a second lookup.</summary>
    [Fact]
    public async Task A_genre_whose_acts_changed_is_asked_again()
    {
        var runs = 0;

        Task<Asked<byte[]?>> Resolve(IReadOnlyList<string> acts, double shape, CancellationToken ct)
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult(Asked.Answer<byte[]?>(Answer((byte)acts.Count)));
        }

        await GenreFaces.ForAsync(Shelf("Folk"), ["Nick"], 1.0, Resolve, default);
        await GenreFaces.ForAsync(Shelf("Folk"), ["Nick"], 1.0, Resolve, default);
        Assert.Equal(1, runs);

        await GenreFaces.ForAsync(Shelf("Folk"), ["Nick", "Sandy"], 1.0, Resolve, default);
        Assert.Equal(2, runs);

        // The shape is part of the question too. A card and a page that want different crops of one
        // photograph would otherwise get each other's crop.
        await GenreFaces.ForAsync(Shelf("Folk"), ["Nick"], 1.6, Resolve, default);
        Assert.Equal(3, runs);
    }

    /// <summary>"Nobody on this genre has been photographed" is an answer, and it is stored. Asking the
    /// network again each time such a card is scrolled past would be the cost this cache exists to
    /// avoid.</summary>
    [Fact]
    public async Task Nobody_photographed_is_an_answer_and_is_kept()
    {
        var runs = 0;

        Task<Asked<byte[]?>> Resolve(IReadOnlyList<string> acts, double shape, CancellationToken ct)
        {
            Interlocked.Increment(ref runs);
            return Task.FromResult(Asked.Answer<byte[]?>(null));
        }

        Assert.Null(await GenreFaces.ForAsync(Shelf("Noise"), ["Merzbow"], 1.0, Resolve, default));
        Assert.Null(await GenreFaces.ForAsync(Shelf("Noise"), ["Merzbow"], 1.0, Resolve, default));

        Assert.Equal(1, runs);
    }

    /// <summary>A lookup that failed is not an answer, so it is not kept. The next time the genre comes on
    /// screen, it is asked again. A short loss of network, for example in a train tunnel, must not cost a
    /// genre its picture for the rest of the session.</summary>
    [Fact]
    public async Task A_failed_lookup_is_asked_again()
    {
        var runs = 0;

        Task<Asked<byte[]?>> Resolve(IReadOnlyList<string> acts, double shape, CancellationToken ct)
        {
            if (Interlocked.Increment(ref runs) == 1)
                throw new InvalidOperationException("no network");
            return Task.FromResult(Asked.Answer<byte[]?>(Answer(3)));
        }

        Assert.Null(await GenreFaces.ForAsync(Shelf("World"), ["Ali"], 1.0, Resolve, default));
        Assert.Equal(Answer(3), await GenreFaces.ForAsync(Shelf("World"), ["Ali"], 1.0, Resolve, default));

        Assert.Equal(2, runs);
    }

    /// <summary>
    /// A walk that comes back unanswered (a candidate's photograph could not be fetched) is not kept in the
    /// table, and it is not written to disk. The next ask walks again.
    ///
    /// <para>A null from this case would be written to disk as "nobody on this shelf has a photograph". A
    /// launch with no network would then record that for every shelf, permanently.</para>
    /// </summary>
    [Fact]
    public async Task An_unanswered_walk_is_not_written_down()
    {
        var runs = 0;

        Task<Asked<byte[]?>> Resolve(IReadOnlyList<string> acts, double shape, CancellationToken ct)
            => Task.FromResult(Interlocked.Increment(ref runs) == 1
                ? Asked.Failed<byte[]?>("HTTP 503")
                : Asked.Answer<byte[]?>(Answer(5)));

        Assert.Null(await GenreFaces.ForAsync(Shelf("Soul"), ["Aretha"], 1.0, Resolve, default));

        // Nothing on disk for it: a fresh process would ask again rather than read "nobody".
        GenreFaces.Forget();
        Assert.Equal(Answer(5), await GenreFaces.ForAsync(Shelf("Soul"), ["Aretha"], 1.0, Resolve, default));
        Assert.Equal(2, runs);
    }

    /// <summary>
    /// However many genres are on screen, only a couple are resolved at once.
    ///
    /// <para>Each model has one semaphore in the session. Eighteen cards asking together would not run
    /// eighteen times faster. They would queue on the lock, and each would hold a decoded photograph while
    /// it waits. The gate prevents this.</para>
    /// </summary>
    [Fact]
    public async Task Only_a_couple_of_genres_are_resolved_at_once()
    {
        var running = 0;
        var highest = 0;
        var release = new TaskCompletionSource();

        async Task<Asked<byte[]?>> Resolve(IReadOnlyList<string> acts, double shape, CancellationToken ct)
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref highest, now);
            await release.Task;
            Interlocked.Decrement(ref running);
            return Asked.Answer<byte[]?>(Answer(1));
        }

        var asked = Enumerable.Range(0, 12)
            .Select(i => GenreFaces.ForAsync(Shelf($"G{i}"), [$"A{i}"], 1.0, Resolve, default))
            .ToArray();

        // Give the gate time to let through everything it is going to.
        for (var spin = 0; spin < 50 && Volatile.Read(ref highest) == 0; spin++)
            await Task.Delay(10);

        release.SetResult();
        await Task.WhenAll(asked);

        Assert.InRange(highest, 1, 2);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int was;
        do
        {
            was = Volatile.Read(ref target);
            if (value <= was)
                return;
        }
        while (Interlocked.CompareExchange(ref target, value, was) != was);
    }
}
