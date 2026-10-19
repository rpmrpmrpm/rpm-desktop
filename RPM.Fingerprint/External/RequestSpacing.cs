namespace RPM.Fingerprint.External;

/// <summary>
/// When the next request to a rate-limited service may be sent.
///
/// <para>MusicBrainz allows one request per second from an address. The limit is on how often requests
/// start, not on how many are open, so a turn is a time to send, not a lock held for the length of the
/// call. If a lock were held for the call, one slow answer (about 16 s) would delay every other lookup
/// in the same container by that long.</para>
/// </summary>
public interface IRequestSpacing
{
    /// <summary>Reserves the next free time to send and waits until that time. A caller that is
    /// cancelled while waiting loses its turn. The turn is not given back, so the service is called
    /// less often than the limit, never more often.</summary>
    Task TakeTurnAsync(CancellationToken ct);
}

/// <summary>
/// Spacing within one process.
///
/// <para>Correct only when this process is the only one asking. The API runs in many Lambda containers
/// that may share an outbound address, so it uses <c>SharedSpacing</c> instead and falls back to this
/// one only when the shared record cannot be reached.</para>
/// </summary>
public sealed class LocalSpacing(TimeSpan interval, TimeProvider clock) : IRequestSpacing
{
    private readonly Lock _gate = new();
    private DateTimeOffset _next = DateTimeOffset.MinValue;

    public Task TakeTurnAsync(CancellationToken ct)
    {
        DateTimeOffset turn;
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            turn = _next > now ? _next : now;
            _next = turn + interval;
        }

        var wait = turn - clock.GetUtcNow();
        return wait > TimeSpan.Zero ? Task.Delay(wait, clock, ct) : Task.CompletedTask;
    }
}
