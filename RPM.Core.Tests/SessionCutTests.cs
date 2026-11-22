using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The session's mood cut and listening profile are built once and held. The startup begins both builds
/// on a worker while the main page is built, and home asks for them a moment later, so a thread that asks
/// during a build must wait for it and not build a second one beside it.
/// </summary>
public class SessionCutTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private sealed class Answer;

    /// <summary>Whether <paramref name="task"/> finishes within <paramref name="within"/>.</summary>
    private static async Task<bool> Finishes(Task task, TimeSpan within) =>
        await Task.WhenAny(task, Task.Delay(within)) == task;

    [Fact]
    public void A_value_once_built_is_not_built_again()
    {
        var cuts = new Gate("test");
        var cut = new SessionCut<Answer>();
        var builds = 0;

        var first = cut.Get(cuts, () => { builds++; return new Answer(); });
        var second = cut.Get(cuts, () => { builds++; return new Answer(); });

        Assert.Same(first, second);
        Assert.Equal(1, builds);
    }

    [Fact]
    public async Task A_thread_that_asks_during_a_build_waits_for_it_and_takes_its_answer()
    {
        var cuts = new Gate("test");
        var cut = new SessionCut<Answer>();
        var builds = 0;
        using var building = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var early = Task.Run(() => cut.Get(cuts, () =>
        {
            Interlocked.Increment(ref builds);
            building.Set();
            release.Wait(Patience);
            return new Answer();
        }));
        Assert.True(building.Wait(Patience), "the first build did not start");

        var late = Task.Run(() => cut.Get(cuts, () =>
        {
            Interlocked.Increment(ref builds);
            return new Answer();
        }));

        // The second asker is waiting, not building: nothing it could return exists yet.
        Assert.False(await Finishes(late, TimeSpan.FromMilliseconds(200)), "the second asker did not wait");

        release.Set();
        Assert.Same(await early.WaitAsync(Patience), await late.WaitAsync(Patience));
        Assert.Equal(1, builds);
    }

    [Fact]
    public async Task A_failed_build_is_not_kept_and_a_waiting_thread_builds_its_own()
    {
        var cuts = new Gate("test");
        var cut = new SessionCut<Answer>();
        using var building = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var failing = Task.Run(() => cut.Get(cuts, () =>
        {
            building.Set();
            release.Wait(Patience);
            throw new InvalidOperationException("the read failed");
        }));
        Assert.True(building.Wait(Patience), "the first build did not start");

        var own = new Answer();
        var waiting = Task.Run(() => cut.Get(cuts, () => own));
        Assert.False(await Finishes(waiting, TimeSpan.FromMilliseconds(200)), "the second asker did not wait");

        release.Set();
        await Assert.ThrowsAsync<InvalidOperationException>(() => failing.WaitAsync(Patience));

        Assert.Same(own, await waiting.WaitAsync(Patience));
        Assert.Same(own, cut.Get(cuts, () => new Answer()));
    }

    [Fact]
    public void The_early_build_is_the_cut_the_session_reads()
    {
        var root = Path.Combine(Path.GetTempPath(), $"rpmcut-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            using var db = new LibraryDatabase(MirrorLocation.InFolder(root));
            var profile = db.SessionProfile;

            db.BuildSessionCuts();

            Assert.Same(profile, db.SessionProfile);
            Assert.Same(db.SessionMoods, db.SessionMoods);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
