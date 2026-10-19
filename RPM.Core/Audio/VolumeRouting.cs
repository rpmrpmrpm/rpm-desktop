namespace RPM.Core.Audio;

/// <summary>Which knob the volume control is actually turning.</summary>
public enum VolumeKnob
{
    /// <summary>Our own gain, applied to the samples before they reach the sound device. This is what the
    /// app has always done, and it stays that way for speakers wired to the machine.</summary>
    App,

    /// <summary>The Bluetooth speaker's own volume, reached through the machine's output volume. See
    /// <see cref="VolumeRouting"/> for why those are the same knob.</summary>
    Bluetooth,

    /// <summary>The cast speaker's own volume, set over the network.</summary>
    Cast,
}

/// <summary>Whose volume the slider moves, and how loud our own samples should be underneath it.
///
/// <para>There are two ways to make music quieter, and they are not interchangeable. One is to scale the
/// samples on the way out. That is all the app can do for a speaker on the end of a cable. The other is to
/// turn the speaker down, which is what the speaker's own buttons do. For a Bluetooth speaker the machine
/// can do this too. Setting a Bluetooth output device's volume in CoreAudio (or WASAPI) is sent on to the
/// speaker as AVRCP absolute volume, which is the same message its own buttons send. There is no separate
/// Bluetooth API for this, and none is needed.</para>
///
/// <para>Turning the speaker down is the better answer where it is available. This is not only because the
/// numbers agree afterwards. Scaling the samples throws away bits before a lossy encoder gets them. The
/// speaker's own attenuation happens after everything. At a quarter volume the app's way has two bits less
/// resolution, and the quiet passages suffer most.</para>
///
/// <para>It has a cost, which is why it is a rule and not a default. The machine's output volume belongs to
/// the whole machine. Turning the record down also turns the browser and the notification sounds down. A
/// speaker too old for absolute volume ignores the message. Then the machine attenuates in software, and
/// nothing has been gained. So the app takes the knob only where the endpoint really is a Bluetooth device
/// that really accepts a level.</para>
/// </summary>
public static class VolumeRouting
{
    /// <summary>Which knob the slider is on.
    ///
    /// <para>Casting comes first. The samples are leaving the building, so this machine's output device is
    /// not in the path at all.</para></summary>
    /// <param name="casting">Whether the music is going to a cast speaker rather than to this
    /// computer.</param>
    /// <param name="hardware">Whether the local endpoint is one whose own volume we can turn. That is a
    /// Bluetooth device that accepted a level when asked.</param>
    public static VolumeKnob Choose(bool casting, bool hardware)
        => casting ? VolumeKnob.Cast
         : hardware ? VolumeKnob.Bluetooth
         : VolumeKnob.App;

    /// <summary>The gain our own pipeline should apply under the chosen knob, 0..100.
    ///
    /// <para>Full scale is used under a hardware knob, and only there. If our gain stays at the listener's
    /// number while the speaker is also set to it, the same attenuation is applied twice. 50% becomes 25%.
    /// That is the failure mode when the speaker's knob is used without giving up our own.</para>
    ///
    /// <para>Casting keeps the number, because the cast sink does not use it as a gain. It sends the number
    /// to the receiver, and keeps the samples at full scale for the encoder.</para></summary>
    public static int GainFor(VolumeKnob knob, int volume)
        => knob == VolumeKnob.Bluetooth ? 100 : Math.Clamp(volume, 0, 100);

    /// <summary>Whether a level reported by the machine should be taken as the listener's own.
    ///
    /// <para>Every write we make comes straight back as a notification. So does the state the endpoint was
    /// in a moment before. If those are believed unconditionally, a drag fights its own echo. For example,
    /// the slider is dragged to 40, the endpoint reports the 55 it was still at, and the slider jumps back
    /// to 55. So a report that disagrees with what we just asked for counts only after we have been quiet
    /// long enough for it to be someone else's doing. <see cref="CastSink"/> follows the same rule, for the
    /// same reason.</para>
    /// </summary>
    /// <param name="reported">The level the machine says the endpoint is at, 0..100.</param>
    /// <param name="wanted">The level we last asked for, or -1 if we have never asked.</param>
    /// <param name="sinceWriteMs">How long ago we last asked.</param>
    /// <param name="echoMs">How long after our own write a disagreeing report is presumed to be an echo of
    /// the level before it.</param>
    public static bool ShouldAdopt(int reported, int wanted, long sinceWriteMs, long echoMs)
        => wanted < 0 || reported == wanted || sinceWriteMs >= echoMs;
}
