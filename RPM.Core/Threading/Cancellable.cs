namespace RPM.Core.Threading;

/// <summary>
/// Waiting that ends by returning rather than by throwing.
///
/// <para>The framework's cancellable waits (<c>Task.Delay(t, ct)</c>, <c>SemaphoreSlim.WaitAsync(ct)</c>
/// and <c>PeriodicTimer.WaitForNextTickAsync(ct)</c>) all report a cancelled wait as an
/// <see cref="OperationCanceledException"/>. That is a good contract for a rip that must abort. It is a
/// poor one for the many background loops here, which only need to stop looping. Those loops catch the
/// exception, so nothing breaks. But a caught exception is still a thrown exception, and the debugger
/// reports each one. A pass over a library causes thousands of reports. The output window then becomes
/// unreadable when you most need to read it.</para>
///
/// <para>So this class gives the same waits as a bool. True means the wait finished normally: the
/// delay ended, the gate opened, or the tick arrived. False means cancellation, and the caller returns.
/// Nothing is thrown, so nothing is reported. The output window then holds only what the code writes
/// on purpose.</para>
///
/// <para>Use this for waits that the caller will abandon quietly. Where cancellation must unwind a deep
/// call stack, such as a rip or a split, <c>ThrowIfCancellationRequested</c> is still the right tool.
/// The exception it throws does real work there.</para>
/// </summary>
public static class Cancellable
{
    /// <summary>
    /// What a run came back with. <c>Ran</c> false means the work never started, because the token had
    /// already fired. In that case <c>Value</c> is not an answer about anything. <c>Ran</c> is a separate
    /// flag, not a null, because "nobody ran this" and "this ran and found nothing" are different facts.
    /// Many callers act on the second one.
    /// </summary>
    public readonly record struct Done<T>(bool Ran, T? Value);

    /// <summary>
    /// Runs work on a worker thread, unless the token fires before it starts.
    ///
    /// <para>This is <c>Task.Run(work, ct)</c> written as a result. That overload's token drops work that
    /// is still waiting in the pool's queue. It reports the drop by cancelling the task, so the await
    /// throws. A page closed over four loads gives four throws, and nobody wanted to hear about any of
    /// them. Here the same drop comes back as <c>Ran: false</c>.</para>
    ///
    /// <para>The token does not stop work that is already running. A delegate that has started runs to
    /// its end, as in the framework's overload. So a caller that cares whether the answer is still
    /// wanted must check the token again afterwards. The call sites already do this, because it is the
    /// only check that means anything.</para>
    /// </summary>
    public static async Task<Done<T>> RunAsync<T>(Func<T> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (ct.IsCancellationRequested)
            return default;

        // The second check is inside the delegate, on the worker. Between the check above and the moment
        // the pool runs this, the caller has had as long as the queue takes to change their mind.
        return await Task.Run(
            () => ct.IsCancellationRequested ? default : new Done<T>(true, work())).ConfigureAwait(false);
    }

    /// <summary>Waits out the delay. False when the token cancelled first.</summary>
    public static Task<bool> DelayAsync(int milliseconds, CancellationToken ct)
        => DelayAsync(TimeSpan.FromMilliseconds(milliseconds), ct);

    /// <summary>Waits out the delay. False when the token cancelled first.</summary>
    public static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return false;

        if (!ct.CanBeCanceled)
        {
            await Task.Delay(delay).ConfigureAwait(false);
            return true;
        }

        // Deliberately not linked to ct. A linked source would cancel the delay at the same instant the
        // token fires. The race between "the delay ended" and "the token fired" would then be
        // random, and a cancelled wait could report success. The timer is stopped below instead, after
        // the race has been decided.
        using var stop = new CancellationTokenSource();
        var elapsed = Task.Delay(delay, stop.Token);

        if (await Finished(elapsed, ct).ConfigureAwait(false))
            return true;

        // Cancelling a task is not the same as throwing from one. The task is left in the cancelled
        // state. Nobody awaits it, so no exception is built to describe that state.
        stop.Cancel();
        return false;
    }

    /// <summary>Takes the gate. False when the token cancelled first. In that case nothing was taken, and
    /// the caller must not release.</summary>
    public static async Task<bool> WaitAsync(SemaphoreSlim gate, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return false;

        // An uncontended gate is the common case, and it needs no extra machinery.
        if (gate.Wait(0))
            return true;

        if (!ct.CanBeCanceled)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            return true;
        }

        var entered = gate.WaitAsync();

        if (await Finished(entered, ct).ConfigureAwait(false))
            return true;

        // The wait has stopped, but the queue still holds our place. When it reaches us, hand the slot
        // straight back. A semaphore that quietly loses a count causes a deadlock several minutes later,
        // in a part of the app that did nothing wrong.
        _ = entered.ContinueWith(
            static (t, state) =>
            {
                if (!t.IsCompletedSuccessfully)
                    return;
                try
                {
                    ((SemaphoreSlim)state!).Release();
                }
                catch (ObjectDisposedException)
                {
                    // Shutdown got there first. A disposed gate has no count left to protect.
                }
            },
            gate,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return false;
    }

    /// <summary>Takes the gate on the calling thread. False when the token cancelled first. In that case
    /// nothing was taken, and the caller must not release. This is for work already on a worker thread,
    /// where an await is not available and <c>SemaphoreSlim.Wait(ct)</c> would throw.</summary>
    public static bool Enter(SemaphoreSlim gate, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gate);

        // Short enough that a cancelled caller releases its thread quickly. Long enough that waiting for
        // a gate held for a minute does not wake the thread many times a second.
        while (!ct.IsCancellationRequested)
            if (gate.Wait(50))
                return true;

        return false;
    }

    /// <summary>Waits for the next tick. False when the token cancelled first, or the timer was
    /// disposed.</summary>
    public static async Task<bool> TickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
            return false;

        try
        {
            var tick = timer.WaitForNextTickAsync().AsTask();

            if (!await Finished(tick, ct).ConfigureAwait(false))
                return false;

            // The task is already complete, so this await waits for nothing. It only unwraps the result,
            // without the AggregateException that reading Result directly would wrap a disposal in.
            return await tick.ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Stopped by disposal, not by the token. The answer is the same either way.
            return false;
        }
    }

    /// <summary>Whether the work won the race against the token. It never throws, whichever one wins. The
    /// token side gives a result, not an exception. The work side is only observed on success.</summary>
    private static async Task<bool> Finished(Task work, CancellationToken ct)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = ct.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(), cancelled);

        return await Task.WhenAny(work, cancelled.Task).ConfigureAwait(false) == work;
    }
}
