using System.Net.Http;
using RPM.Core.Diagnostics;

namespace RPM.Core.Playback;

/// <summary>
/// What went wrong, in words a listener can use, while holding a phone to hear a song.
///
/// <para>
/// The rule is that a message must be either <em>actionable</em> or <em>true and short</em>. The exception
/// text is not used, because it reads as "A task was canceled." That is accurate about a CancellationToken
/// and useless to a listener. A listener cannot act on the name of a .NET exception. Showing it on the
/// now-playing screen in red suggests that something is broken, when the real cause may be a train going
/// into a tunnel.
/// </para>
///
/// <para>
/// The classification lives here, not in the phone head, so it can be tested without a device. The desktop
/// uses the same reading of the same exceptions. Each head turns a <see cref="PlaybackFailure"/> into text
/// in the user's own language.
/// </para>
/// </summary>
public enum PlaybackFailure
{
    /// <summary>Nothing to say. The attempt was stopped on purpose, for example a track skipped before it
    /// opened, or a screen closed. Never shown.</summary>
    None,

    /// <summary>The phone is not on a network, or the name would not resolve. The one failure the listener
    /// can actually fix.</summary>
    Offline,

    /// <summary>The network is there but the answer never came. Worth distinguishing from being offline:
    /// waiting longer, or moving, is what helps.</summary>
    Timeout,

    /// <summary>The session is no longer good. Signing in again is the fix.</summary>
    SignedIn,

    /// <summary>The store answered, and what it said was no. The track is not there any more, or the server
    /// is failing for a short time.</summary>
    Store,

    /// <summary>The record never really started: it was accepted, it produced the head clip or less, and
    /// then it stopped. Whatever was supposed to be behind it never arrived.</summary>
    Unreachable,

    /// <summary>The record was playing and stopped part way through. The bytes were coming and then they
    /// were not, which is a tunnel far more often than it is a fault.</summary>
    Dropped,

    /// <summary>The decoder would not take the file. Nothing about the network, and nothing waiting will
    /// fix it.</summary>
    NotPlayable,

    /// <summary>Anything else.</summary>
    Unknown,
}

public static class PlaybackFailures
{
    /// <summary>
    /// Reads an exception as one of the failures above.
    ///
    /// <para>Cancellation is <see cref="PlaybackFailure.None"/> and a timeout is not. This is the most
    /// important distinction, and the framework makes it hard. <see cref="HttpClient"/> reports its own
    /// timeout as a <see cref="TaskCanceledException"/>, which is the same type it throws when the caller
    /// cancels. The only difference is a <see cref="TimeoutException"/> inside it.</para>
    /// </summary>
    public static PlaybackFailure Read(Exception? error) => Faults.Read(error) switch
    {
        Fault.None => PlaybackFailure.None,
        Fault.Offline => PlaybackFailure.Offline,
        Fault.Timeout => PlaybackFailure.Timeout,
        Fault.SignedOut => PlaybackFailure.SignedIn,
        Fault.Server => PlaybackFailure.Store,

        // A file or disk error gives nothing a listener can use, so it gets the generic message.
        _ => PlaybackFailure.Unknown,
    };

    /// <summary>
    /// Reads a record that stopped rather than finished.
    ///
    /// <para>
    /// This is the other half of the job. It does not arrive as an exception. A source that broke returns
    /// no more audio, just like one that reached its end. The engine records the difference in
    /// <c>EndedAs</c>, but only as "failed". On its own, that is the same generic message for every case.
    /// </para>
    ///
    /// <para>
    /// How far the record got is what tells the two apart. The difference matters, because each one means
    /// something different to the listener. A record that stopped after a couple of seconds never really
    /// started. The head clip played, but the stream behind it never came. That points to the store or the
    /// account, and trying again is the answer. A record that stopped two minutes in was arriving well
    /// until it stopped. That is almost always a tunnel.
    /// </para>
    /// </summary>
    /// <param name="heardSeconds">How much of the record was actually played.</param>
    public static PlaybackFailure Ended(double heardSeconds) =>
        heardSeconds > Started ? PlaybackFailure.Dropped : PlaybackFailure.Unreachable;

    /// <summary>
    /// How much of a record counts as having started.
    ///
    /// <para>The head clip and a little more. Everything up to here can be played out of a file that was
    /// already on the phone before a single byte of the record arrived, so it says nothing at all about
    /// whether the record itself was reachable.</para>
    /// </summary>
    public const double Started = Fingerprint.AudioLadder.HeadSeconds + 1;

    /// <summary>The resource key a head shows for this failure, or null for the ones that are not worth
    /// saying. Keeps the mapping in one place rather than in a switch on every screen.</summary>
    public static string? StringKey(this PlaybackFailure failure) => failure switch
    {
        PlaybackFailure.None => null,
        PlaybackFailure.Offline => "Rpm_Playback_Offline",
        PlaybackFailure.Timeout => "Rpm_Playback_Slow",
        PlaybackFailure.SignedIn => "Rpm_Playback_SignedOut",
        PlaybackFailure.Store => "Rpm_Playback_Failed",
        PlaybackFailure.Unreachable => "Rpm_Playback_Unreachable",
        PlaybackFailure.Dropped => "Rpm_Playback_Dropped",
        PlaybackFailure.NotPlayable => "Rpm_Playback_NotPlayable",
        _ => "Rpm_Playback_Failed",
    };
}
