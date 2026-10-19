namespace RPM.Core.Recording;

/// <summary>An audio input device (line-in, USB turntable, interface) as offered in the vinyl wizard's
/// device picker.</summary>
/// <param name="Id">Stable handle the capture backend opens the device by. Empty string = the system
/// default input.</param>
/// <param name="Name">Human-readable name for the picker.</param>
public sealed record InputDevice(string Id, string Name)
{
    /// <summary>What the name says this device is. It decides whether monitoring it through the
    /// speakers would feed back. See <see cref="InputDevices.Classify"/>.</summary>
    public InputDeviceKind Kind => InputDevices.Classify(Name);
}

/// <summary>One recorded record side: the lossless intermediate file it was captured to and how long it
/// ran. Sides are always captured to FLAC, whatever output format the user chose. The final per-track
/// encode happens when the album is cut.</summary>
public sealed record RecordedSide(int Index, string FilePath, double DurationSeconds);
