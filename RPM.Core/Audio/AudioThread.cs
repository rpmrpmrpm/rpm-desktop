namespace RPM.Core.Audio;

/// <summary>
/// Scheduling for the threads that must not miss their deadline: the pump that feeds the sound
/// device, and the decoder that keeps the pump supplied.
///
/// <para>A plain <see cref="Thread"/> is not enough for audio. On macOS a .NET thread runs at the default
/// priority, and <see cref="Thread.Priority"/> moves it by only three steps. AppKit's main thread and the
/// compositor's render thread run at the user-interactive level on the performance cores. With a busy
/// visualiser there, the audio pump could be placed on an efficiency core, miss a 50 ms block, and leave
/// the device queue empty. The listener hears a glitch.</para>
///
/// <para>So the pump asks for the user-interactive level, the same as the interface. The decoder asks for
/// one class lower: it works ahead into a buffer, so it can yield to the pump, but it must not drop to the
/// efficiency cores either. On macOS and iOS this is the thread's scheduling priority, not its QoS class,
/// which the system refuses for .NET threads (see <see cref="Threading.DarwinThread"/>).</para>
/// </summary>
public static class AudioThread
{
    /// <summary>Marks the calling thread as the one feeding the sound device.</summary>
    public static void EnterPlayback() => Promote(Threading.DarwinThread.Interactive, ThreadPriority.Highest);

    /// <summary>Marks the calling thread as one that keeps the playback thread supplied.</summary>
    public static void EnterDecode() => Promote(Threading.DarwinThread.Initiated, ThreadPriority.AboveNormal);

    private static void Promote(int darwinPriority, ThreadPriority managed)
    {
        // iOS as well as macOS: same kernel, and a phone also has performance and efficiency cores.
        if (Threading.DarwinThread.Applies)
        {
            Threading.DarwinThread.SetPriority(darwinPriority);
            return;
        }

        try { Thread.CurrentThread.Priority = managed; } catch { }
    }
}
