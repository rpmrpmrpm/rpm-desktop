using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using RPM.Core.Net;

namespace RPM.Core.Tests;

/// <summary>
/// The three questions a failed request raises: should it be sent again, how long to wait, and
/// what to tell the person.
///
/// <para>What is pinned above everything else: <b>a POST is not sent again because the service
/// failed.</b> A 500 means the service received the request and failed afterwards, and rpm's own
/// POSTs are a comment under an album and a play counter. The rest of this file is the boundary
/// around that rule: which statuses mean the edge never delivered the request, which failures mean
/// nothing left this machine, and which of them are the network rather than the service.</para>
/// </summary>
public class TransientHttpTests
{
    private static TransientHttp.Replay Get => TransientHttp.Replay.Of(HttpMethod.Get, bodyInHand: true);
    private static TransientHttp.Replay Post => TransientHttp.Replay.Of(HttpMethod.Post, bodyInHand: true);
    private static TransientHttp.Replay Put => TransientHttp.Replay.Of(HttpMethod.Put, bodyInHand: true);

    // ---- what came back ----------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    public void TheEdgeSayingItNeverGotThereIsWorthAnotherGoWhateverTheMethodWas(HttpStatusCode status)
    {
        Assert.True(Get.After(status));
        Assert.True(Post.After(status));
        Assert.True(Put.After(status));
    }

    [Fact]
    public void AServiceThatThrewIsAskedAgainOnlyForARequestThatMayBeSentTwice()
    {
        Assert.True(Get.After(HttpStatusCode.InternalServerError));
        Assert.True(TransientHttp.Replay.Of(HttpMethod.Head, bodyInHand: true)
            .After(HttpStatusCode.InternalServerError));

        // The whole point of this file. A 500 on POST albums/{id}/comments is the service saying it
        // has the comment; sending it again posts a second one.
        Assert.False(Post.After(HttpStatusCode.InternalServerError));
        Assert.False(Put.After(HttpStatusCode.InternalServerError));
        Assert.False(TransientHttp.Replay.Of(HttpMethod.Delete, bodyInHand: true)
            .After(HttpStatusCode.InternalServerError));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotModified)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public void AnAnswerTheServerDecidedIsNews(HttpStatusCode status)
    {
        Assert.False(Get.After(status));
        Assert.False(Post.After(status));
    }

    [Fact]
    public void ABodyTooLargeToHoldIsSentTheOnce()
    {
        var oversize = TransientHttp.Replay.Of(HttpMethod.Get, bodyInHand: false);

        Assert.False(oversize.After(HttpStatusCode.ServiceUnavailable));
        Assert.False(oversize.After(new HttpRequestException("gone")));

        Assert.False(TransientHttp.Replay.Never.After(HttpStatusCode.ServiceUnavailable));
        Assert.False(TransientHttp.Replay.Never.After(new SocketException()));
    }

    // ---- what failed -------------------------------------------------------------------------

    [Fact]
    public void ADroppedSocketIsWorthAnotherGoForAReadAndNotForAWrite()
    {
        // Nothing here says whether the service got the request: as far as anyone on this side can tell,
        // the connection went away after it was written.
        foreach (var failure in Failures())
        {
            Assert.True(Get.After(failure));
            Assert.False(Post.After(failure));
        }

        static IEnumerable<Exception> Failures()
        {
            yield return new SocketException((int)SocketError.ConnectionReset);
            yield return new IOException("the connection was closed");
            yield return new HttpRequestException("no answer");
        }
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError)]
    [InlineData(HttpRequestError.ConnectionError)]
    [InlineData(HttpRequestError.SecureConnectionError)]
    [InlineData(HttpRequestError.ProxyTunnelError)]
    public void AFailureThatNeverLeftThisMachineIsSafeForAnything(HttpRequestError error)
    {
        var failure = new HttpRequestException(error, "never left");

        Assert.True(Get.After(failure));
        // A name that did not resolve is a request the service cannot possibly have seen, so even a
        // comment may be offered again.
        Assert.True(Post.After(failure));
    }

    [Fact]
    public void AFailureAfterTheRequestWentOutIsNotSafeForAWrite()
    {
        // The framework knows the response ended early, which means the request was sent.
        var failure = new HttpRequestException(HttpRequestError.ResponseEnded, "cut off");

        Assert.True(Get.After(failure));
        Assert.False(Post.After(failure));
    }

    [Fact]
    public void ATimeoutIsNotAskedAgain()
    {
        // It has already spent the whole limit finding out, and a second attempt costs the same again for
        // the same answer. What a timeout means is handled by NetworkReach, not by waiting.
        var clientTimeout = new TaskCanceledException("timed out", new TimeoutException());

        Assert.False(Get.After(clientTimeout));
        Assert.False(Get.After(new TimeoutException()));
    }

    [Fact]
    public void CancellationIsNeverRetried()
    {
        Assert.False(Get.After(new OperationCanceledException()));
        Assert.False(Get.After(new TaskCanceledException()));
    }

    [Fact]
    public void AnAnswerWithAStatusOnItIsJudgedOnThatStatus()
    {
        // EnsureSuccessStatusCode puts the status on the exception.
        Assert.True(Get.After(new HttpRequestException(HttpRequestError.Unknown, "502", null, HttpStatusCode.BadGateway)));
        Assert.True(Post.After(new HttpRequestException(HttpRequestError.Unknown, "503", null, HttpStatusCode.ServiceUnavailable)));
        Assert.False(Post.After(new HttpRequestException(HttpRequestError.Unknown, "500", null, HttpStatusCode.InternalServerError)));
        Assert.False(Get.After(new HttpRequestException(HttpRequestError.Unknown, "401", null, HttpStatusCode.Unauthorized)));
    }

    [Fact]
    public void SomethingThatIsNotANetworkFailureAtAllIsNotRetried()
    {
        Assert.False(Get.After(new InvalidOperationException("a bug")));
        Assert.False(Get.After(new System.Text.Json.JsonException("bad body")));
    }

    // ---- the line, as against the service on it ----------------------------------------------

    [Fact]
    public void TheLineIsToldApartFromTheServiceOnIt()
    {
        // Carried perfectly well: something answered.
        Assert.False(TransientHttp.LooksLikeTheLine(
            new HttpRequestException(HttpRequestError.Unknown, "502", null, HttpStatusCode.BadGateway)));
        Assert.False(TransientHttp.LooksLikeTheLine(
            new HttpRequestException(HttpRequestError.Unknown, "500", null, HttpStatusCode.InternalServerError)));
        Assert.False(TransientHttp.LooksLikeTheLine(
            new HttpRequestException(HttpRequestError.ResponseEnded, "cut off")));

        // Nothing answered.
        Assert.True(TransientHttp.LooksLikeTheLine(new HttpRequestException(HttpRequestError.NameResolutionError, "dns")));
        Assert.True(TransientHttp.LooksLikeTheLine(new HttpRequestException(HttpRequestError.ConnectionError, "refused")));
        Assert.True(TransientHttp.LooksLikeTheLine(new HttpRequestException("unclassified")));
        Assert.True(TransientHttp.LooksLikeTheLine(new SocketException()));
        Assert.True(TransientHttp.LooksLikeTheLine(new IOException()));
        Assert.True(TransientHttp.LooksLikeTheLine(new TaskCanceledException("", new TimeoutException())));
    }

    [Fact]
    public void ACallerWalkingAwayIsNotAVerdictOnTheLine()
    {
        Assert.False(TransientHttp.LooksLikeTheLine(new TaskCanceledException()));
        Assert.False(TransientHttp.LooksLikeTheLine(new OperationCanceledException()));
        Assert.False(TransientHttp.LooksLikeTheLine(new InvalidOperationException()));
    }

    // ---- how long ----------------------------------------------------------------------------

    [Fact]
    public void TheWaitGrowsAndIsCapped()
    {
        var first = Enumerable.Range(0, 200).Select(_ => TransientHttp.Backoff(1, null)).ToList();
        var second = Enumerable.Range(0, 200).Select(_ => TransientHttp.Backoff(2, null)).ToList();

        Assert.All(first, w => Assert.InRange(w.TotalMilliseconds, 200, 400));
        Assert.All(second, w => Assert.InRange(w.TotalMilliseconds, 400, 800));

        // Nothing grows past the ceiling, whatever attempt it is asked about.
        Assert.All(
            Enumerable.Range(1, 12).Select(a => TransientHttp.Backoff(a, null)),
            w => Assert.True(w <= TimeSpan.FromSeconds(5), $"{w} is past the cap"));
    }

    [Fact]
    public void HalfOfEveryWaitIsRandomSoThatTenThreadsDoNotComeBackTogether()
    {
        // This matters because callers fail together: ten callers on one endpoint fail in the same
        // millisecond, and a wait they all agree on sends all ten back in the same millisecond too.
        var waits = Enumerable.Range(0, 500).Select(_ => TransientHttp.Backoff(1, null).TotalMilliseconds).ToList();

        Assert.True(waits.Max() - waits.Min() > 100,
            $"the waits are bunched between {waits.Min():F0} and {waits.Max():F0} ms");
        Assert.True(waits.Distinct().Count() > 100, "the wait is barely random at all");
    }

    [Fact]
    public void TheServersOwnNumberIsPreferredAndStillSpreadAndCapped()
    {
        var said = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
        var waits = Enumerable.Range(0, 200).Select(_ => TransientHttp.Backoff(1, said)).ToList();

        // Obeyed: never less than what it asked for.
        Assert.All(waits, w => Assert.InRange(w.TotalMilliseconds, 2000, 2500));
        // And spread, so ten callers told the same two seconds do not return in the same instant.
        Assert.True(waits.Select(w => w.TotalMilliseconds).Distinct().Count() > 50);

        // A server asking for longer than anyone here is willing to wait is capped.
        Assert.Equal(TimeSpan.FromSeconds(5),
            TransientHttp.Backoff(1, new RetryConditionHeaderValue(TimeSpan.FromMinutes(3))));
    }

    [Fact]
    public void ARetryAfterOfNothingIsIgnoredRatherThanObeyed()
    {
        var wait = TransientHttp.Backoff(1, new RetryConditionHeaderValue(TimeSpan.Zero));
        Assert.InRange(wait.TotalMilliseconds, 200, 400);

        // The HTTP-date form carries no Delta. Falling back is right; guessing is not.
        var dated = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(30));
        Assert.InRange(TransientHttp.Backoff(1, dated).TotalMilliseconds, 200, 400);
    }

    // ---- wording a failure -------------------------------------------------------------------

    [Fact]
    public void SayingNotNowIsNotTheSameQuestionAsSendingItAgain()
    {
        // 500 is the service saying "not now" for the purpose of what a person is told, and is still not
        // grounds to send a POST twice. The two answers are allowed to differ.
        Assert.True(TransientHttp.SaysNotNow(HttpStatusCode.InternalServerError));
        Assert.False(Post.After(HttpStatusCode.InternalServerError));

        Assert.True(TransientHttp.SaysNotNow(HttpStatusCode.ServiceUnavailable));
        Assert.False(TransientHttp.SaysNotNow(HttpStatusCode.NotFound));
        Assert.False(TransientHttp.SaysNotNow(HttpStatusCode.Unauthorized));
    }
}
