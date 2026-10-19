using System.Net;

namespace RPM.Core.Net;

/// <summary>
/// The gate, in the shape of an <see cref="HttpClient"/> handler: nothing goes out while
/// <see cref="NetworkReach"/> says there is nothing out there.
///
/// <para>It is the outermost handler, in front of the retries. A refusal is not a transient error to retry
/// after a wait. It is the app declining to open the socket, so no name lookup, connection or exception
/// happens.</para>
///
/// <para>A refused request comes back as a real response rather than a throw, so that the ordinary
/// "did that work?" branch every caller already has is the branch that runs. Callers that want to tell this
/// apart from a service that is genuinely unwell ask <see cref="WasRefused"/>.</para>
///
/// <para>The gate also works the other way. A request that fails like a lost line tells the gate to measure
/// the line again. Then the next caller measures instead of hitting the same failure.</para>
/// </summary>
public sealed class OnlineGateHandler : DelegatingHandler
{
    /// <summary>Marks the response this handler makes up. Present only on those.</summary>
    public const string RefusedHeader = "X-RPM-Offline";

    private readonly Func<NetworkReach> _reach;
    private readonly TimeSpan _afterAdmission;

    public OnlineGateHandler(NetworkReach reach, HttpMessageHandler inner)
        : this(() => reach, inner) { }

    /// <param name="reach">Asked per request rather than held, so that a client built before the app's gate
    /// exists still asks the one that ends up there. The alternative is a client holding a gate that was
    /// replaced, which is a gate that answers for nobody.</param>
    /// <param name="afterAdmission">How long the request itself may take, timed from the moment it is let
    /// through, not from the moment it was asked for. <see cref="Timeout.InfiniteTimeSpan"/> leaves the
    /// timing to the client's own timeout.</param>
    public OnlineGateHandler(
        Func<NetworkReach> reach, HttpMessageHandler inner, TimeSpan? afterAdmission = null)
        : base(inner)
    {
        _reach = reach;
        _afterAdmission = afterAdmission ?? Timeout.InfiniteTimeSpan;
    }

    /// <summary>True when this response is the gate's own, and no request was ever sent.</summary>
    public static bool WasRefused(HttpResponseMessage response) =>
        response.Headers.Contains(RefusedHeader);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var reach = _reach();

        // THE WAIT FOR ADMISSION IS BOUNDED TOO, and it ends in a refusal rather than a throw.
        //
        // Bounding only the request is not enough. Without a limit here, requests that are still queued are
        // not cancelled, so many quick cancellations turn into a few very long waits. That is worse,
        // because a caller that is told "not now" can do something else, but a caller that gets no answer
        // cannot.
        //
        // A refusal is the shape this handler uses for "the app declined to open the socket": a real
        // response that every caller's ordinary "did that work?" branch reads, with no exception. So an
        // admission that does not come in time counts as offline.
        if (!await AdmittedAsync(reach, ct).ConfigureAwait(false))
            return Refused(request);

        // THE CLOCK STARTS HERE, not when the caller asked.
        //
        // HttpClient.Timeout runs from the moment SendAsync is entered. That is before this gate has
        // measured the line, and before the request has waited for one of the per-host connections. So a
        // request can use up its whole timeout in a queue and be cancelled before a byte is sent. At
        // start-up, many requests ask at once while the first measurement is still running, so this happens
        // often.
        //
        // The timeout used here is measured from admission, so it limits only the time the service takes to
        // answer. Measuring the queue as well would make the limit depend on how busy the app is.
        using var timing = _afterAdmission == Timeout.InfiniteTimeSpan
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(ct);

        timing?.CancelAfter(_afterAdmission);

        try
        {
            return await base.SendAsync(request, timing?.Token ?? ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timing is { IsCancellationRequested: true } &&
                                                 !ct.IsCancellationRequested)
        {
            // This timeout is ours, not the caller's. It is reported the way HttpClient reports its own
            // timeout, so TransientHttp.LooksLikeTheLine and the callers that read that shape treat it the
            // same way.
            throw new TaskCanceledException(
                $"The request was canceled after {_afterAdmission.TotalSeconds:0.#}s.",
                new TimeoutException());
        }
        // Only failures of the line itself count here, not failures of the service on it. A 502 arrives
        // over a working connection, so it says nothing about whether this machine can reach the internet.
        // Doubting the line on those errors would make one unwell server look like no internet on every
        // machine.
        catch (Exception ex) when (TransientHttp.LooksLikeTheLine(ex) && !ct.IsCancellationRequested)
        {
            reach.Doubt();
            throw;
        }
    }

    /// <summary>
    /// Whether the gate let this one through within its allowance.
    /// </summary>
    /// <returns>False both when the gate says the line is down and when it did not answer in time. The two
    /// mean the same thing to a caller: nothing is going out. Neither is an exception.</returns>
    private async Task<bool> AdmittedAsync(NetworkReach reach, CancellationToken ct)
    {
        if (_afterAdmission == Timeout.InfiniteTimeSpan)
            return await reach.AllowAsync(ct).ConfigureAwait(false);

        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waiting.CancelAfter(_afterAdmission);

        try
        {
            return await reach.AllowAsync(waiting.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;   // ours: the measurement is taking too long, so treat it as offline
        }
    }

    private static HttpResponseMessage Refused(HttpRequestMessage request)
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            ReasonPhrase = "Offline",
            RequestMessage = request,
            Content = new ByteArrayContent([]),
        };
        response.Headers.TryAddWithoutValidation(RefusedHeader, "1");
        return response;
    }
}
