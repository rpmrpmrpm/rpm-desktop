using System.Diagnostics;

namespace RPM.Core.Audio;

/// <summary>
/// The volume exchange with a cast speaker: the level the speaker is set to, the level we last asked
/// for, and which of the two to trust when they differ.
///
/// <para>
/// While casting there is one volume control and it belongs to the speaker. Its own buttons change it,
/// the Google Home app changes it, a voice command changes it, and so does this app. So the volume is
/// an exchange of messages, not a plain setting, and a simple <c>SetVolume</c> call has two problems.
/// </para>
///
/// <para> <b>One.</b> Every change we send comes back as a status report, and so does the level the speaker
/// had just before. If the app believed every report, it would fight its own echo: it asks for 40, the
/// speaker reports the older 55, and the app moves back to 55. So a report that differs from our last
/// request counts only after we have sent nothing for long enough that someone else must have made the
/// change. See <see cref="VolumeRouting.ShouldAdopt"/>, the same rule the local endpoints use. </para>
///
/// <para>
/// <b>Two.</b> Dragging a slider, or holding a phone's volume key, produces changes much faster than a
/// speaker can answer them. Two sends in flight together can arrive in either order, and the speaker
/// keeps the one that arrives last. So sends are reduced to the latest value at a steady rate, with
/// one send in flight at a time.
/// </para>
///
/// <para>
/// The desktop and the phone both use this class, because the exchange is the same whatever sends it.
/// <see cref="CastSink"/> is the desktop's caller and <c>LpcmCastSink</c> is the phone's.
/// </para>
/// </summary>
public sealed class CastVolume
{
    /// <summary>
    /// How often a change actually goes out while the level is still moving.
    ///
    /// <para>A drag produces a change per pixel. Sending each one is wasteful and causes jitter: the
    /// speaker answers every one with a status, and those answers can arrive out of order with the
    /// drag. Sending only the latest value at a steady rate also matches how a hardware volume knob
    /// behaves.</para>
    /// </summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// How long after our own change we ignore what the speaker reports about the volume.
    ///
    /// <para>The speaker reports the level it had when it answered, not the level we are asking for
    /// now. A status that is in flight during a drag is therefore out of date, and adopting it would
    /// move the level backwards while the user is still dragging. After this long with nothing sent,
    /// the speaker's report is trusted again, because someone may have changed it with another
    /// control.</para>
    /// </summary>
    public static readonly TimeSpan DefaultEcho = TimeSpan.FromSeconds(2);

    private readonly Func<double, Task> _send;
    private readonly TimeSpan _interval;
    private readonly long _echoMs;

    /// <summary>Guards the exchange state: the level we last asked for, and when.</summary>
    private readonly object _gate = new();
    private readonly Stopwatch _sinceSent = new();

    private int _wanted = -1;
    private bool _sending;

    private volatile int _level = 100;
    private volatile bool _closed;

    /// <param name="send">Turns the speaker's own knob, 0..1. Called off the caller's thread, one
    /// call at a time, and never after <see cref="Close"/>.</param>
    public CastVolume(Func<double, Task> send)
        : this(send, DefaultInterval, DefaultEcho) { }

    public CastVolume(Func<double, Task> send, TimeSpan interval, TimeSpan echo)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _interval = interval;
        _echoMs = (long)echo.TotalMilliseconds;
    }

    /// <summary>Where the knob is, 0..100, as far as this side knows.</summary>
    public int Level => _level;

    /// <summary>
    /// The listener changed the volume.
    /// </summary>
    /// <param name="volume">0..100.</param>
    /// <param name="live">
    /// Whether there is a stream for the speaker to be turned up on. <b>It has no default value, on
    /// purpose.</b> The engine pushes its own level to a sink as soon as the sink is attached, before
    /// anything is loaded. Sending that level would make a speaker set to 20% jump to whatever level the
    /// player had, which is the bug this class exists to prevent. When this is false, the number is
    /// remembered and nothing is sent. The speaker's own level arrives a moment later through
    /// <see cref="Adopt"/> and replaces it.
    /// </param>
    public void Set(int volume, bool live)
    {
        _level = Math.Clamp(volume, 0, 100);
        if (!live || _closed) return;

        lock (_gate)
        {
            _wanted = _level;
            _sinceSent.Restart();
            if (_sending) return;       // the running sender also sends this value
            _sending = true;
        }

        _ = SendAsync();
    }

    /// <summary>
    /// The speaker reported its volume.
    /// </summary>
    /// <param name="deviceLevel">What it reported, 0..1, or null if it has not said.</param>
    /// <returns>The level to move the player to, 0..100, or null when there is nothing to do. Null
    /// covers four cases: the speaker gave no level, it is echoing our own request, the level has not
    /// really changed, and this cast is over. The last case matters: a status already on the wire when
    /// a cast ends arrives afterwards, and acting on it would set the volume of something else to a
    /// level that was never meant for it.</returns>
    public int? Adopt(double? deviceLevel)
    {
        if (deviceLevel is not double level || _closed) return null;

        int wanted;
        long since;
        lock (_gate)
        {
            wanted = _wanted;
            since = _sinceSent.IsRunning ? (long)_sinceSent.Elapsed.TotalMilliseconds : long.MaxValue;
        }

        var volume = (int)Math.Round(Math.Clamp(level, 0, 1) * 100);

        if (!VolumeRouting.ShouldAdopt(volume, wanted, since, _echoMs)) return null;

        // A difference of one point is the speaker's own rounding, not a real change. Following it
        // would move the level off the number the user just chose.
        if (Math.Abs(volume - _level) <= 1) return null;

        _level = volume;
        return volume;
    }

    /// <summary>Nothing more is sent to this speaker. The sender stops at its next loop
    /// iteration.</summary>
    public void Close() => _closed = true;

    /// <summary>Sends the latest wanted level, no faster than the interval, until it stops moving.
    /// One send in flight at a time: two overlapping sends can arrive in either order, and the
    /// speaker keeps the one that arrives last.</summary>
    private async Task SendAsync()
    {
        try
        {
            while (true)
            {
                int wanted;
                lock (_gate) wanted = _wanted;

                if (_closed) return;

                try { await _send(wanted / 100.0); }
                catch { /* the speaker is gone; the owner of the connection is told separately */ }

                await Task.Delay(_interval);

                lock (_gate)
                {
                    if (_wanted == wanted)
                    {
                        _sending = false;
                        return;
                    }
                }
            }
        }
        finally
        {
            lock (_gate) _sending = false;
        }
    }
}
