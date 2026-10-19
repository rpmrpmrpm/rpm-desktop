namespace RPM.Core.Playback;

/// <summary>What the engine is holding, which is not the same question as what was asked for.</summary>
public enum Loaded
{
    /// <summary>No source at all, between one track being released and the next being handed
    /// over.</summary>
    Nothing,

    Playing,
    Paused,
}

/// <summary>What pressing a transport key comes to.</summary>
public enum TransportMove
{
    /// <summary>The key changes nothing: pause on a paused player, play on a playing one.</summary>
    Nothing,

    Pause,
    Resume,

    /// <summary>There is nothing to pause yet. Pause when the source arrives.</summary>
    Remember,

    /// <summary>A remembered pause is withdrawn: play was pressed before the source arrived.</summary>
    Forget,
}

/// <summary>
/// Play, pause, and the difference between what the player is doing and what it has been asked to do.
///
/// <para>
/// Starting a track takes time. A URL is signed, a clip is read from the disk, and an asset is opened. The
/// old source is released at the first instant, so the previous track stops at once and does not keep
/// playing under the new track's title. For that short time the engine holds nothing, and "is it playing?"
/// is false. That answer is correct, but it is the wrong question. Somebody who has pressed next and then
/// pause has asked for silence. A player that answers "there is nothing to pause" throws the press away and
/// starts playing a second later.
/// </para>
///
/// <para>
/// This type fixes that. The press is remembered and applied as soon as a source exists. It also answers
/// <see cref="WantsToPlay"/>, which is what a play key and the lock screen should show. That answer is the
/// listener's intent, not the engine's state. So a key that was pressed to pause does not turn back into a
/// play key while the track they paused is still opening.
/// </para>
///
/// <para>
/// This type lives in RPM.Core rather than in the platform head, so it can be tested without a device or an
/// audio engine. It works alongside <see cref="StartGate"/>. Every case below can happen on the lock
/// screen, where the system sends play and pause as separate commands instead of one toggle. So a wrong
/// answer there does not just do nothing. It does the opposite of what was asked.
/// </para>
/// </summary>
public sealed class Transport
{
    /// <summary>Volatile because the press arrives on the main thread, and the handover happens on
    /// whichever thread finished the load.</summary>
    private volatile bool _pending;

    /// <summary>Pause, pressed when there was nothing yet to pause.</summary>
    public bool PausePending => _pending;

    /// <summary>
    /// A track is being got ready.
    ///
    /// <para>Anything remembered for the last track is dropped here. Choosing a track means the listener
    /// wants to hear it. A pause pressed during the previous load must not carry over and silence the track
    /// they just picked.</para>
    /// </summary>
    public void Starting() => _pending = false;

    /// <summary>Stopped, or handed to something that does not come through the start path at all, such as a
    /// radio station. Nothing is coming, so no pause is owed.</summary>
    public void Forgotten() => _pending = false;

    /// <summary>
    /// A source is being handed to the engine: whether it should be paused on arrival.
    ///
    /// <para>This consumes the pending press. It has been applied, and applying it again would pause the
    /// next track as well.</para>
    /// </summary>
    public bool Arrives()
    {
        var pending = _pending;
        _pending = false;
        return pending;
    }

    /// <summary>
    /// Whether the player has been asked for sound. A play key shows this, and the lock screen uses it as
    /// the playback rate.
    ///
    /// <para>When the engine holds a source, its state is the answer. Only while it holds nothing is the
    /// listener's intent all there is to go on.</para>
    /// </summary>
    /// <param name="starting">Whether a track is on its way to the engine.</param>
    public bool WantsToPlay(bool starting, Loaded loaded) => loaded switch
    {
        Loaded.Playing => true,
        Loaded.Paused => false,
        _ => starting && !_pending,
    };

    public TransportMove Pause(bool starting, Loaded loaded)
    {
        if (loaded == Loaded.Playing) return TransportMove.Pause;
        if (loaded == Loaded.Paused) return TransportMove.Nothing;

        // Nothing is coming, so there is nothing to attach the pause to. A pause kept while the
        // player is idle would silence whatever is chosen next.
        if (!starting || _pending) return TransportMove.Nothing;

        _pending = true;
        return TransportMove.Remember;
    }

    public TransportMove Play(bool starting, Loaded loaded)
    {
        if (loaded == Loaded.Paused) return TransportMove.Resume;
        if (loaded == Loaded.Playing) return TransportMove.Nothing;

        if (!starting || !_pending) return TransportMove.Nothing;

        _pending = false;
        return TransportMove.Forget;
    }

    /// <summary>A headset button, a car steering-wheel button or the in-app key. One press that does the
    /// opposite of what the listener asked for last.</summary>
    public TransportMove Toggle(bool starting, Loaded loaded) =>
        WantsToPlay(starting, loaded) ? Pause(starting, loaded) : Play(starting, loaded);
}
