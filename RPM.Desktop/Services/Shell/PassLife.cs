namespace RPM.Desktop.Services.Shell;

/// <summary>
/// The background work of one service, kept so that it can be stopped and waited for before the library is
/// closed.
///
/// <para>A service that reads or writes the library from its own workers registers <see cref="CloseAsync"/>
/// with <c>LibraryService.StopBeforeClose</c>, starts its workers through <see cref="Run"/>, and runs them on
/// <see cref="Token"/> (or a token linked to it). The close then refuses new work, cancels the token, and waits
/// for every worker that was started, so none of them is in the middle of a read or a write when the database
/// is closed.</para>
///
/// <para>The refusal and the record of a worker are made under one lock. So a worker is either refused, or
/// recorded before the close takes its list of what to wait for. There is no gap between the two in which a
/// worker could start unseen.</para>
/// </summary>
internal sealed class PassLife
{
    private readonly CancellationTokenSource _closing = new();
    private readonly object _gate = new();
    private readonly HashSet<Task> _running = [];

    /// <summary>Set under <see cref="_gate"/> when the close begins. The token is cancelled after it, outside
    /// the lock, so that nothing the cancellation runs can wait on the lock.</summary>
    private bool _closed;

    /// <summary>Cancelled when the close begins.</summary>
    public CancellationToken Token => _closing.Token;

    /// <summary>Whether the close has begun. Nothing new may start after this is true.</summary>
    public bool IsClosing
    {
        get { lock (_gate) return _closed; }
    }

    /// <summary>A token source cancelled by the close and by <paramref name="other"/>. The caller disposes
    /// it.</summary>
    public CancellationTokenSource Linked(CancellationToken other = default) =>
        CancellationTokenSource.CreateLinkedTokenSource(_closing.Token, other);

    /// <summary>
    /// Starts <paramref name="work"/> on the pool and records it, unless the close has begun.
    /// </summary>
    /// <returns>The work, or null when it was refused because the close has begun.</returns>
    public Task? Run(Func<Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            if (_closed)
                return null;

            var task = Task.Run(work);
            Record(task);
            return task;
        }
    }

    /// <inheritdoc cref="Run(Func{Task})"/>
    public Task<T>? Run<T>(Func<Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            if (_closed)
                return null;

            var task = Task.Run(work);
            Record(task);
            return task;
        }
    }

    /// <summary>Keeps <paramref name="task"/> until it ends. Called under <see cref="_gate"/>.</summary>
    private void Record(Task task)
    {
        _running.Add(task);
        _ = task.ContinueWith(
            done => { lock (_gate) _running.Remove(done); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Refuses new work, cancels <see cref="Token"/>, and waits for every worker started through
    /// <see cref="Run"/>. A worker that fails or is cancelled counts as stopped.
    /// </summary>
    public async Task CloseAsync()
    {
        Task[] running;
        lock (_gate)
        {
            _closed = true;
            running = [.. _running];
        }

        _closing.Cancel();

        try { await Task.WhenAll(running).ConfigureAwait(false); }
        catch { /* a worker's own failure is not the close's business */ }
    }
}
