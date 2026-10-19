using System.Runtime.CompilerServices;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Shell;

/// <summary>How a service starts work nobody is waiting for, and the only way it should.
/// <para><b>What this is for.</b> <c>_ = SomethingAsync();</c> looks like "do that in the background",
/// but it is not. An async method runs on the calling thread until its first await. Then, because that
/// thread has a synchronization context, every resumption after every await returns to it. When the
/// call starts from a <c>Changed</c> handler, a <c>Loaded</c> handler, or anything else that a user
/// action reached, "the background" is the drawing thread. The whole chain runs there: the JSON from
/// each response, the disk write in the middle, and whatever that chain starts in turn.</para>
/// <para><b>What it cost.</b> On a cold start with a borrowed shelf, the app froze for about 3.5
/// seconds while it drew the genres wall. During that time the UI thread was blocked. The process used
/// less than one core, because the work was all queued on one thread. Forty-one requests were sent in
/// that window, and each continuation waited for the same thread that the wall needed. The line near
/// the one that caused this uses the right form: <c>_ = Task.Run(() =&gt;
/// CatalogLoopAsync(token));</c>.</para>
/// <para><b>Why this and not <c>ConfigureAwait(false)</c> everywhere.</b> Adding it everywhere would
/// need the same fix in many places, and each one would have to be right. It also treats the symptom,
/// which is where the chain resumes. The cause is where it starts. Work begun on the pool has no
/// synchronization context to capture. So every await in it, and in everything it calls, resumes on the
/// pool, whether or not anybody remembered to ask. There is one entry point, and the compiler shows
/// every place that uses it.</para>
/// <para>Faults are observed here. A fault that is left alone is raised later by the finalizer thread,
/// and a bare <c>_ = SomethingAsync()</c> has that problem.</para>
/// </summary>
internal static class Background
{
    /// <summary>
    /// Starts <paramref name="work"/> on the thread pool and does not wait for it.
    /// </summary>
    /// <param name="work">The work. A lambda rather than a task, deliberately: handed a
    /// <c>Task</c> the caller would already have started it on their own thread, which is the whole
    /// thing being avoided.</param>
    /// <param name="from">Filled in by the compiler, so a failure says which service's work it was
    /// without anybody having to remember to say.</param>
    public static void Run(Func<Task> work, [CallerMemberName] string? from = null)
    {
        ArgumentNullException.ThrowIfNull(work);

        _ = Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The app is closing, or whoever asked has gone. Not news.
            }
            catch (Exception ex) when (RPM.Core.Net.TransientHttp.LooksLikeTheLine(ex))
            {
                // This is a network that is down, and it is not logged here. A laptop that wakes with
                // Wi-Fi off fails every one of these calls at once, and a line per failure would be
                // noise. NetworkReach decides whether there is a network, and it is the one place that
                // keeps that answer.
            }
            catch (Exception ex)
            {
                // Anything else is a real fault, not a network problem. It is logged once, here. A bare
                // `_ = SomethingAsync()` would raise it later, on the finalizer thread. It goes on the
                // ring, so a Release build keeps it too.
                Faults.Note("background", from ?? "background work", ex);
            }
        });
    }
}
