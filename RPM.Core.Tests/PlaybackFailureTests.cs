using System.Net;
using System.Net.Sockets;
using RPM.Core.Playback;

namespace RPM.Core.Tests;

/// <summary>
/// Reading a failure as something a listener can be told. The distinction that carries the whole thing
/// is between a cancellation and a timeout: HttpClient reports both as a TaskCanceledException, one of
/// them is worth a line on the screen and the other is somebody skipping a track.
/// </summary>
public class PlaybackFailureTests
{
    [Fact]
    public void Says_nothing_about_a_track_that_was_skipped()
    {
        Assert.Equal(PlaybackFailure.None, PlaybackFailures.Read(new OperationCanceledException()));
        Assert.Equal(PlaybackFailure.None, PlaybackFailures.Read(new TaskCanceledException()));
        Assert.Equal(PlaybackFailure.None, PlaybackFailures.Read(null));

        // No message to show: a skip must not reach the screen as "A task was canceled." in red
        // under the title of the track being played.
        Assert.Null(PlaybackFailure.None.StringKey());
    }

    [Fact]
    public void Tells_a_timeout_from_a_cancellation()
    {
        // What HttpClient throws when its own Timeout elapses: the same type as a cancellation, with a
        // TimeoutException inside it. Nothing else distinguishes them.
        var timedOut = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout",
                                                 new TimeoutException());

        Assert.Equal(PlaybackFailure.Timeout, PlaybackFailures.Read(timedOut));
        Assert.Equal("Rpm_Playback_Slow", PlaybackFailures.Read(timedOut).StringKey());
    }

    [Fact]
    public void Reads_no_route_as_being_offline()
    {
        Assert.Equal(PlaybackFailure.Offline, PlaybackFailures.Read(new SocketException(50)));

        var dns = new HttpRequestException("No such host is known", new SocketException(11001));
        Assert.Equal(PlaybackFailure.Offline, PlaybackFailures.Read(dns));
        Assert.Equal("Rpm_Playback_Offline", PlaybackFailures.Read(dns).StringKey());
    }

    [Fact]
    public void Reads_a_refused_token_as_something_to_sign_in_for()
    {
        var refused = new HttpRequestException("Unauthorized", null, HttpStatusCode.Unauthorized);

        Assert.Equal(PlaybackFailure.SignedIn, PlaybackFailures.Read(refused));
        Assert.Equal("Rpm_Playback_SignedOut", PlaybackFailures.Read(refused).StringKey());
    }

    [Fact]
    public void Reads_an_answer_that_was_no_as_the_store_saying_no()
    {
        var gone = new HttpRequestException("Not Found", null, HttpStatusCode.NotFound);

        Assert.Equal(PlaybackFailure.Store, PlaybackFailures.Read(gone));
        Assert.Equal("Rpm_Playback_Failed", PlaybackFailures.Read(gone).StringKey());
    }

    [Fact]
    public void Looks_inside_an_aggregate()
    {
        var wrapped = new AggregateException(new HttpRequestException("boom", new SocketException(50)));

        Assert.Equal(PlaybackFailure.Offline, PlaybackFailures.Read(wrapped));
    }

    [Fact]
    public void Has_something_to_say_about_anything_else()
    {
        Assert.Equal(PlaybackFailure.Unknown, PlaybackFailures.Read(new InvalidOperationException("no decoder")));
        Assert.Equal("Rpm_Playback_Failed", PlaybackFailures.Read(new InvalidOperationException()).StringKey());
    }

    [Fact]
    public void A_record_that_stopped_before_it_started_was_never_reached()
    {
        // The head clip is three seconds and it comes off the phone's own disk, so everything up to
        // here can play without a single byte of the record having arrived.
        Assert.Equal(PlaybackFailure.Unreachable, PlaybackFailures.Ended(0));
        Assert.Equal(PlaybackFailure.Unreachable, PlaybackFailures.Ended(2.5));
        Assert.Equal(PlaybackFailure.Unreachable, PlaybackFailures.Ended(PlaybackFailures.Started));
    }

    [Fact]
    public void A_record_that_stopped_part_way_through_was_arriving_until_it_was_not()
    {
        // Two minutes in is a tunnel far more often than it is a fault, and telling somebody the
        // track could not be played would be the wrong sentence about the right event.
        Assert.Equal(PlaybackFailure.Dropped, PlaybackFailures.Ended(120));
        Assert.Equal(PlaybackFailure.Dropped, PlaybackFailures.Ended(PlaybackFailures.Started + 0.1));
    }

    [Fact]
    public void Every_failure_worth_saying_has_something_to_say()
    {
        foreach (var failure in Enum.GetValues<PlaybackFailure>())
        {
            var key = failure.StringKey();

            if (failure is PlaybackFailure.None) Assert.Null(key);
            else Assert.False(string.IsNullOrWhiteSpace(key), $"{failure} says nothing");
        }
    }
}
