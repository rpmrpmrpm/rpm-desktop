using Microsoft.Data.Sqlite;
using RPM.Desktop.Services.Platform;

namespace RPM.Desktop.Tests;

/// <summary>
/// The WinUI head's interface-thread context, which stops a database refusal already on screen before
/// it reaches the dispatcher queue. On WinUI, an exception rethrown there ends the process with a stowed
/// exception, and no managed handler runs.
/// </summary>
public class RefusalGuardContextTests
{
    /// <summary>Runs every callback where it is posted, the way a single interface thread does.</summary>
    private sealed class InlineContext : SynchronizationContext
    {
        public int Started;
        public int Completed;

        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
        public override void OperationStarted() => Started++;
        public override void OperationCompleted() => Completed++;
    }

    private static readonly SqliteException OnScreen = new("no such table: pl_node", 1);

    private readonly List<Exception> _crashes = [];

    private RefusalGuardContext Guard(InlineContext inner) =>
        new(inner, error => ReferenceEquals(error, OnScreen), _crashes.Add);

    [Fact]
    public void A_refusal_already_on_screen_is_stopped_at_the_context()
    {
        var guard = Guard(new InlineContext());

        guard.Post(_ => throw OnScreen, null);

        Assert.Empty(_crashes);
    }

    [Fact]
    public void Anything_else_is_recorded_and_still_thrown()
    {
        var guard = Guard(new InlineContext());
        var defect = new InvalidOperationException("a defect");

        var thrown = Assert.Throws<InvalidOperationException>(() => guard.Post(_ => throw defect, null));

        Assert.Same(defect, thrown);
        Assert.Equal([defect], _crashes);
    }

    /// <summary>The path the Windows run took: an async void handler that throws after an await. Its
    /// builder posts the exception back to the context it started on, and that post is what reaches the
    /// dispatcher queue on WinUI. Under the guard it stops there.</summary>
    [Fact]
    public void An_async_void_handler_that_throws_a_refusal_after_an_await_is_stopped()
    {
        var inner = new InlineContext();
        var guard = Guard(inner);
        var before = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(guard);
            Handler();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(before);
        }

        Assert.Empty(_crashes);
        Assert.Equal(1, inner.Started);
        Assert.Equal(1, inner.Completed);

        static async void Handler()
        {
            await Task.Yield();
            throw OnScreen;
        }
    }

    [Fact]
    public void A_copy_guards_the_same_way()
    {
        var copy = Guard(new InlineContext()).CreateCopy();

        copy.Post(_ => throw OnScreen, null);

        Assert.Empty(_crashes);
    }
}
