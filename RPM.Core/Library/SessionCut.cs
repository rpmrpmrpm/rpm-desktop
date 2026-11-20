namespace RPM.Core.Library;

/// <summary>
/// A value held for the life of the session, built once and then handed to every thread that asks.
/// <see cref="LibraryDatabase.SessionMoods"/> and <see cref="LibraryDatabase.SessionProfile"/> are kept in
/// one each.
/// </summary>
/// <remarks>
/// <para><b>Built outside the lock.</b> A build is several whole-table reads. The lock that is passed in
/// guards only the fields, and nothing under it may touch a connection.</para>
///
/// <para><b>One build at a time.</b> A thread that asks while another thread is building waits for that
/// build and takes its answer. The startup begins the builds before home asks (see
/// <see cref="LibraryDatabase.BuildSessionCuts"/>). A second build beside the first would double the reads
/// on the read gate, which is what the early start exists to save. The answer kept is the first one
/// finished, so two lists can never be drawn from two cuts.</para>
///
/// <para><b>A failed build is not kept.</b> A thread that was waiting for it builds its own, and so does
/// the next thread that asks. A failure says nothing about the collection.</para>
///
/// <para><b>Never ask for the value while holding a connection's gate.</b> The build takes the read gate. A
/// thread that held that gate and waited here would block the build it is waiting for. Every caller in
/// <see cref="LibraryDatabase"/> composes a list before it takes a gate (see the notes in <c>QueryRun</c>
/// and <c>Totals</c>).</para>
/// </remarks>
internal sealed class SessionCut<T> where T : class
{
    private T? _value;

    /// <summary>The build under way, or null. It is set and cleared under the lock.</summary>
    private Flight? _flight;

    /// <summary>The value, built by this thread if nobody has built it and nobody is building it.</summary>
    /// <param name="cuts">The lock that guards the fields. It is held only for a moment, never across
    /// <paramref name="build"/>.</param>
    /// <param name="build">Makes the value. It may throw, and the exception reaches the caller.</param>
    public T Get(Gate cuts, Func<T> build)
    {
        Flight? mine = null;
        Flight? theirs;
        using (cuts.Take())
        {
            if (_value is not null)
                return _value;

            theirs = _flight;
            if (theirs is null)
                _flight = mine = new Flight();
        }

        if (theirs is not null)
        {
            theirs.Done.Wait();
            if (theirs.Value is { } answered)
                return answered;
        }

        // This thread builds: it started the flight, or the flight it waited for failed.
        T? kept = null;
        try
        {
            var built = build();
            using (cuts.Take())
                kept = _value ??= built;
            return kept;
        }
        finally
        {
            if (mine is not null)
            {
                // The waiters are let go first, so nothing below can leave them waiting. Value is null when
                // the build threw, and a waiter then builds for itself. A thread that finds this finished
                // flight before it is cleared reads the same answer.
                mine.Value = kept;
                mine.Done.Set();

                using (cuts.Take())
                    if (ReferenceEquals(_flight, mine))
                        _flight = null;
            }
        }
    }

    /// <summary>One build under way, and what it made once it has finished.</summary>
    private sealed class Flight
    {
        public readonly ManualResetEventSlim Done = new();
        public T? Value;
    }
}
