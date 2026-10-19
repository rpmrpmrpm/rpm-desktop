namespace RPM.Desktop.Services.Audio;

/// <summary>
/// The reason this computer's sound output is not playing, as found by the local sink.
///
/// <para>This is a fixed set of cases, because each case needs different advice. A missing library is
/// fixed by reinstalling the app. No device is fixed by connecting one. A device that refused to start
/// is fixed by closing the app that uses it, or by choosing another device. No case contains exception
/// text. When an exception was thrown, it was written to the diagnostics ring when it was caught (see
/// <see cref="RPM.Core.Diagnostics.Faults.Note"/>).</para>
/// </summary>
public abstract record OutputTrouble
{
    private OutputTrouble() { }

    /// <summary>The sound library did not load. A file the app ships is missing, damaged or blocked.
    /// No device change can fix this, so the sink stops looking for devices.</summary>
    public sealed record LibraryMissing() : OutputTrouble;

    /// <summary>The library loaded and the machine has no playback device at all.</summary>
    public sealed record NoDevice() : OutputTrouble;

    /// <summary>The machine has playback devices and the one chosen would not open or start.</summary>
    /// <param name="Device">The driver's name for it, as the speaker picker lists it.</param>
    public sealed record Refused(string Device) : OutputTrouble;
}
