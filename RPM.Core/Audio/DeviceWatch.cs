namespace RPM.Core.Audio;

/// <summary>Who carries out a move of the sound to another endpoint.</summary>
public enum MoveCarrier
{
    /// <summary>A pump is attached to a track. It reads a name handed to it between blocks, so
    /// that nothing else touches the device while the pump feeds it.</summary>
    Pump,

    /// <summary>No pump is attached: no track has begun, or the track's pump has made its last call
    /// into the sink (a drain at the end, a refused block, a stop). The watcher moves the device itself,
    /// because nothing else would read a name handed over.</summary>
    Watcher,
}

/// <summary>What the sink's device watcher does at one look.</summary>
public abstract record WatcherStep
{
    private WatcherStep() { }

    /// <summary>Nothing to do. This is also the answer after a failed move with no new reason to try
    /// again: the endpoints are the same as when it failed, and nothing has asked for sound
    /// since.</summary>
    public sealed record Nothing : WatcherStep;

    /// <summary>Hand <paramref name="Device"/> to the pump, which moves at its next block.</summary>
    public sealed record HandToPump(string Device) : WatcherStep;

    /// <summary>Move to <paramref name="Device"/> now, on the watcher's own thread.</summary>
    public sealed record MoveNow(string Device) : WatcherStep;
}

/// <summary>
/// The device watcher's rule: what to do at a look, and how long to sleep before the next.
///
/// <para><b>A lost device with no pump is one state, not a loop.</b> If the watcher handed the move to
/// a pump that does not exist, nothing would read it and nothing would clear the lost state. The lost
/// state selects the short 200 ms clock, so the watcher would loop on that clock forever. So the
/// carrier decides: with no pump the watcher moves the device itself once. If that fails, it waits for
/// the endpoints to change or for a play to ask, on the ordinary clock.</para>
/// </summary>
public static class DeviceWatch
{
    /// <summary>Between looks while a held track waits for somewhere to play. The music is stopped,
    /// so the watcher should move to a device as soon as one is available.</summary>
    public const int HeldTrackMs = 200;

    /// <summary>Between looks where the machine does not announce every change.</summary>
    public const int UnannouncedMs = 500;

    /// <summary>Between looks where the machine announces every change. This is only a safety net
    /// for a notification that never arrives.</summary>
    public const int AnnouncedMs = 5000;

    /// <summary>How long the watcher sleeps before its next look. The short clock is for a track that
    /// is being held, and only a pump can hold one. With no pump attached nothing is waiting, and a
    /// device change wakes the watcher anyway.</summary>
    public static int SleepMs(MoveCarrier carrier, bool lost, bool machineAnnouncesEverything) =>
        lost && carrier is MoveCarrier.Pump ? HeldTrackMs
        : machineAnnouncesEverything ? AnnouncedMs
        : UnannouncedMs;

    /// <summary>What to do at a look.</summary>
    /// <param name="carrier">Who would carry a move out. It has no default value: handing a name to a pump
    /// that is not there is the loop this rule prevents.</param>
    /// <param name="want">The endpoint <see cref="OutputRouting.ChooseFromEnumeration"/> picks now.</param>
    /// <param name="intended">The endpoint asked for last time.</param>
    /// <param name="connected">Whether the sound library reports the open endpoint as present.</param>
    /// <param name="lost">Whether the sound is going nowhere.</param>
    /// <param name="unchangedSinceFailure">The last move the watcher made failed, the machine lists the
    /// same endpoints it failed against, and no play has asked since. Used only for the watcher. A pump
    /// that holds a track keeps trying on its short clock until it gives up.</param>
    public static WatcherStep Next(MoveCarrier carrier, string want, string? intended, bool connected,
                                   bool lost, bool unchangedSinceFailure)
    {
        if (!OutputRouting.ShouldMove(want, intended, connected, lost))
            return new WatcherStep.Nothing();

        return carrier switch
        {
            MoveCarrier.Pump => new WatcherStep.HandToPump(want),
            MoveCarrier.Watcher when unchangedSinceFailure => new WatcherStep.Nothing(),
            MoveCarrier.Watcher => new WatcherStep.MoveNow(want),
            _ => throw new ArgumentOutOfRangeException(nameof(carrier), carrier, "not a carrier"),
        };
    }
}
