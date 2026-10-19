using RPM.Core.Library;
using RPM.Desktop.Services.Art;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The photograph a genre is drawn on. It is asked for once, and the answer is kept after that.
///
/// <para>Everything that stands for a genre draws the same picture of the same artist. That covers the
/// card on the wall and the head of the genre's own page. Getting the picture costs two network lookups
/// and two neural nets. This class is the only way to get it. The work is done once per genre, with at
/// most a couple of genres at a time. It never runs on the thread that draws the wall.</para>
///
/// <para><b>What went wrong without it.</b> The wall creates a screenful of cards at once, and each one
/// asked for its own picture. Each one walked up to six artists and ran the saliency and face models on
/// each. That made a hundred or more model runs, all waiting for one session's semaphore. Every image was
/// decoded and re-encoded on the UI thread. Then every one of them was cancelled when the reader opened a
/// genre. The visible result was a wall that stalled, and a console full of cancellations.</para>
///
/// <para><b>Once per MACHINE, not once per launch.</b> The table below is held in memory, so it is empty when
/// the process starts. Without a disk copy, each launch would repeat the whole walk for every shelf. Each
/// answer is therefore also written to disk. See <see cref="GenreFaceCache"/>. The cache is read back before
/// any lookup starts.</para>
///
/// <para><b>What can happen out of order here, and why each case is safe.</b></para>
/// <list type="bullet">
/// <item>Two cards ask for the same genre at once. That happens when the wall's two sides re-pair, or when a
/// card and the page ask together. They get the SAME task, and the second caller starts no work.</item>
/// <item>A caller stops waiting while the answer is still coming. Its await is cancelled, but the WORK is
/// not. The shared task runs on <see cref="CancellationToken.None"/>. So the next asker, usually the same
/// card scrolled back on screen, gets the answer at once instead of starting again.</item>
/// <item>The library changes during a fetch. The answer is filed under the acts that were considered. So a
/// genre whose artists changed is a new question, and it is asked again. A genre whose artists did not change
/// keeps its picture.</item>
/// <item>The answer arrives after the card that wanted it has been recycled. That is the caller's problem,
/// and it is already handled. The picture is put on the card, and the card asks on realize whether it has
/// one (see GenreWallView).</item>
/// <item>Two callers race to be the first to file a genre. The dictionary is locked for the check and the
/// insert together. So the loser gets the winner's task, and does not start a second one.</item>
/// </list>
/// </summary>
internal static class GenreFaces
{
    /// <summary>
    /// How many genres are resolved at once.
    ///
    /// <para>Two. Each resolve is a chain of lookups and model runs. Each model has its own semaphore, and
    /// it is held for one session at a time. So a wall that asked for eighteen at once would not go
    /// eighteen times faster. It would queue eighteen deep on a lock, and hold eighteen decoded photographs
    /// in memory while it waited. Two keeps the pipeline full and the queue short.</para>
    /// </summary>
    private static readonly SemaphoreSlim Gate = new(2, 2);

    /// <summary>Every genre asked about, by what was asked. It holds the TASK rather than the answer. So two
    /// askers that arrive together share one piece of work, and do not start two.</summary>
    private static readonly Dictionary<string, Task<byte[]?>> Asked = new(StringComparer.Ordinal);

    /// <summary>
    /// The picture for a genre: one of its artists, framed for the shape asked for. It is null where none of
    /// them has been photographed.
    /// </summary>
    /// <param name="genre">The shelf, and the acts on it. The acts are half of what the answer is filed
    /// under.</param>
    /// <param name="candidates">The acts to try, in the order to try them. The caller builds this list, so
    /// the same collection asks the same question on every machine.</param>
    /// <param name="shape">How wide the picture should be, for every unit of its height.</param>
    /// <param name="resolve">How to turn one artist's name into a framed cut. It is passed in rather than
    /// looked up, because the lookups and the models belong to the page. This class belongs to
    /// nobody.</param>
    public static async Task<byte[]?> ForAsync(
        GenreWallItem genre,
        IReadOnlyList<string> candidates,
        double shape,
        Func<IReadOnlyList<string>, double, CancellationToken, Task<Asked<byte[]?>>> resolve,
        CancellationToken ct)
    {
        var key = $"{genre.Name}␟{shape:F3}␟{string.Join('|', candidates)}";

        Task<byte[]?> shared;
        lock (Asked)
        {
            if (!Asked.TryGetValue(key, out shared!))
                Asked[key] = shared = Run(key, candidates, shape, resolve);
        }

        // The caller's token cancels the AWAIT, not the work. A reader who opens a genre while the wall is
        // still filling in has not made the wall's remaining pictures worthless. They have only made them not
        // worth waiting for. The difference is a cache hit when they come back.
        return await shared.WaitAsync(ct);
    }

    private static Task<byte[]?> Run(
        string key,
        IReadOnlyList<string> candidates,
        double shape,
        Func<IReadOnlyList<string>, double, CancellationToken, Task<Asked<byte[]?>>> resolve)
        // Task.Run is used, not a bare async method, and not Task.Yield. This is started from the UI
        // thread, for example when a card is realized or a page opens. An async method started there runs
        // on that thread until its first await. Task.Yield hands the continuation straight back to the same
        // thread that it was trying to leave. Task.Run moves the whole chain to the pool from its first
        // instruction. Anything that decodes a photograph belongs on the pool.
        => Task.Run(async () =>
        {
            try
            {
                // What this machine answered last time. It is checked before the gate and before any
                // lookup. The table above holds every answer for the life of the process. That is what
                // makes scrolling a wall back free. The table is empty at launch. Without the cache,
                // eighteen shelves each walking six artists through two lookups and two models is what a
                // reader sees as cards arriving one or two a second. See GenreFaceCache. An entry of length
                // zero is the ANSWER "nobody on this shelf has a photograph", not the absence of an answer.
                // It is also the most expensive answer to work out, so it is the last one that should be
                // paid for twice.
                if (GenreFaceCache.Known(key) is { } remembered)
                    return remembered.Length > 0 ? remembered : null;

                await Gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var answer = await resolve(candidates, shape, CancellationToken.None)
                        .ConfigureAwait(false);

                    // Not answered: a candidate's photograph could not be fetched. This is neither written
                    // to disk nor kept in the table. So the next time this genre is asked for, it is walked
                    // again. Otherwise a launch with no network would record "nobody on this shelf has a
                    // photograph" for every shelf.
                    if (!answer.Answers(out var face, out _))
                    {
                        lock (Asked)
                            Asked.Remove(key);
                        return null;
                    }

                    // The answer is kept before it is returned. So a reader who closes the wall as soon as
                    // it fills does not lose the work. The caller's token cancels the await and never the
                    // resolve. This line is on the resolve's side of that line.
                    GenreFaceCache.Keep(key, face);
                    return face;
                }
                finally
                {
                    Gate.Release();
                }
            }
            catch
            {
                // A lookup that failed is not an answer, so it is not kept. The next time this genre comes
                // on screen, it is asked again. "Nobody on this genre has been photographed" IS an answer.
                // It comes back as a null that stays filed.
                lock (Asked)
                    Asked.Remove(key);
                return null;
            }
        });

    /// <summary>Drops everything asked so far. This is for the tests, and for a library that has been
    /// swapped out under the app. A shelf borrowed from a peer is a different collection, even if it has
    /// the same genre names.</summary>
    internal static void Forget()
    {
        lock (Asked)
            Asked.Clear();
    }
}
