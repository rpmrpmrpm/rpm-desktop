namespace RPM.Core.Audio;

/// <summary>
/// How long the pump has watched a device hand nothing back while the device owed it audio.
///
/// <para>Two rules apply. A wait counts only if the pump performed it (see
/// <see cref="OutputRouting.ShouldPresumeLost"/>), and only if the device owed a buffer during it. A
/// paused source returns nothing, and that is normal. A plain int counter would keep growing through a
/// pause, and the "is it playing" test would run only when the count was read. The first read after
/// play would then see the whole pause as silence and wrongly reopen a healthy device.
/// <see cref="Waited"/> takes the same fact the check uses, so a pause cannot be counted.</para>
///
/// <para>The count is read and written on the pump's thread. It is reset from any thread that finds
/// the device alive again.</para>
/// </summary>
public sealed class DeviceSilence
{
    private int _watchedMs;

    /// <summary>How long the device has handed nothing back, counted in waits made while it owed
    /// audio.</summary>
    public int WatchedMs => _watchedMs;

    /// <summary>One wait the pump performed with nothing coming back.</summary>
    /// <param name="ms">How long the wait was.</param>
    /// <param name="owed">Whether the device owed a buffer during it: the source had been started and
    /// was not paused.</param>
    public void Waited(int ms, bool owed)
    {
        if (owed)
            _watchedMs += ms;
    }

    /// <summary>The device handed something back, a new track started, or the sound moved.</summary>
    public void Heard() => _watchedMs = 0;

    /// <inheritdoc cref="OutputRouting.ShouldPresumeLost"/>
    public bool PresumeLost(int noProgressMs, bool owed) =>
        OutputRouting.ShouldPresumeLost(_watchedMs, noProgressMs, owed);
}
