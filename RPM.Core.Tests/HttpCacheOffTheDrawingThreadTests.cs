using RPM.Core.Net;

namespace RPM.Core.Tests;

/// <summary>
/// The picture cache never runs on the thread that draws.
///
/// <para><b>Why this is a test and not a comment.</b> <c>HttpCacheHandler.SendAsync</c> is an async
/// method, and an async method stays on the caller's thread until its first await: everything
/// before that await runs on the caller's thread. The cache lookup is a SQLite query and a blob read
/// from disk, behind a lock shared with every other caller, including the ones writing blobs. If it
/// ran before the first await, a picture requested from the drawing thread would do its lookup
/// there, and could wait there behind another caller's disk write.</para>
///
/// <para>When that happens the drawing thread can stall for seconds (4,280 ms has been seen), with
/// <c>HttpCacheHandler.SendAsync</c> → <c>WebCacheStore.Read</c> on its stack. Nothing in the source
/// shows the problem: moving one line above the first await brings it back, so this test catches
/// it.</para>
/// </summary>
public class HttpCacheOffTheDrawingThreadTests
{
    /// <summary>
    /// A thread with a synchronization context, like the drawing thread.
    ///
    /// <para>The handler decides whether it must leave the current thread by checking for a
    /// synchronization context, because in this app only the drawing thread has one. So the test
    /// sets one.</para>
    ///
    /// <para>Work posted to it runs with this context current, as work posted to the drawing thread
    /// does. A continuation that comes back to the caller's context is then still "on the drawing
    /// thread", and a store call made from it is seen.</para>
    /// </summary>
    private sealed class OwnedThread : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) =>
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var was = Current;
                SetSynchronizationContext(this);
                try { d(state); }
                finally { SetSynchronizationContext(was); }
            });
    }

    /// <summary>
    /// A store that records a synchronization context that was current during any read, renew or
    /// write. Writes count as much as reads: a picture write is a SQLite write and a blob write.
    ///
    /// <para><b>The context, not the thread id.</b> Comparing thread ids does not work: a thread
    /// that queues work to the pool and then awaits returns to the pool, and can run its own work
    /// item. The read can then have the same id correctly, and the test would fail about a third of
    /// the time under load. The test checks "not a thread with a context", and a pool thread has no
    /// context.</para>
    /// </summary>
    private sealed class WatchfulStore : IWebCacheStore
    {
        public bool Read_At_All { get; private set; }
        public SynchronizationContext? ContextAtRead { get; private set; }

        public bool Written { get; private set; }

        public WebCacheEntry? Read(string key)
        {
            Read_At_All = true;
            Watch();
            return null;
        }

        public void Write(WebCacheEntry entry)
        {
            Written = true;
            Watch();
        }

        public void Renew(string key, long fetchedUtcTicks, long expiresUtcTicks) => Watch();

        private void Watch() => ContextAtRead ??= SynchronizationContext.Current;
        public void Clear() { }
        public long BytesOnDisk() => 0;
    }

    /// <summary>Answers every request at once, so the test is about threads and not a network.</summary>
    private sealed class Answering : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3]),
                RequestMessage = request,
            });
    }

    [Fact]
    public async Task The_cache_is_not_read_on_a_thread_that_owns_itself()
    {
        var store = new WatchfulStore();
        using var handler = new HttpCacheHandler(
            store,
            new WebCachePolicy("test", TimeSpan.FromHours(1), TimeSpan.FromMinutes(5)),
            new Answering());
        using var client = new HttpClient(handler);

        var was = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new OwnedThread());

        try
        {
            await client.GetAsync("https://example.invalid/a-picture.jpg");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(was);
        }

        Assert.True(store.Read_At_All, "the cache was never consulted, so this proves nothing");
        Assert.True(store.Written, "the answer was never filed, so the write was not checked");
        Assert.True(
            store.ContextAtRead is null,
            "the cache was read on a thread that belongs to somebody. On the drawing thread that is " +
            "a SQLite query and a blob read off disk, behind a lock shared with whoever is writing " +
            "blobs — a 4,280ms stall, measured. Something has moved above the first await in " +
            "SendAsync, or the hop has been taken out.");
    }
}
