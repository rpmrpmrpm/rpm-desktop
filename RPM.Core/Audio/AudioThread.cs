using System.Runtime.InteropServices;

namespace RPM.Core.Audio;

/// <summary>
/// Scheduling for the threads that must not miss their deadline: the pump that feeds the sound
/// device, and the decoder that keeps the pump supplied.
///
/// <para>A plain <see cref="Thread"/> is not enough for audio. <see cref="Thread.Priority"/> does
/// nothing on Unix: the runtime keeps the value but does not apply it. On macOS the pump would then
/// run with the default quality-of-service (QoS) of a new thread, <i>unspecified</i>, which the
/// scheduler treats as low-priority work. On Apple silicon such a thread can be placed on the
/// <b>efficiency</b> cores, while AppKit's main thread and the compositor/GL render thread run at
/// <c>USER_INTERACTIVE</c> on the performance cores. With a busy visualiser on the performance cores,
/// the audio pump can end up on an efficiency core, miss a 50 ms block, and leave the device queue
/// empty. The listener hears a glitch.</para>
///
/// <para>So the pump asks for <c>USER_INTERACTIVE</c>, the same class as the UI. The decoder asks for
/// one class lower: it works ahead into a buffer, so it can yield to the pump, but it must not drop to
/// the efficiency cores either.</para>
/// </summary>
public static class AudioThread
{
    // qos_class_t values from <sys/qos.h>. The SDK does not expose them to managed code.
    private const uint QosUserInteractive = 0x21;
    private const uint QosUserInitiated = 0x19;

    /// <summary>Marks the calling thread as the one feeding the sound device.</summary>
    public static void EnterPlayback() => Promote(QosUserInteractive, ThreadPriority.Highest);

    /// <summary>Marks the calling thread as one that keeps the playback thread supplied.</summary>
    public static void EnterDecode() => Promote(QosUserInitiated, ThreadPriority.AboveNormal);

    private static void Promote(uint qosClass, ThreadPriority managed)
    {
        // Applied on Windows, ignored on Unix. That is why the QoS call below is needed.
        try { Thread.CurrentThread.Priority = managed; } catch { }

        // iOS as well as macOS: same kernel, same qos_class_t, and a phone also has performance and
        // efficiency cores.
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsIOS()) return;
        try { pthread_set_qos_class_self_np(qosClass, 0); } catch { }
    }

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int pthread_set_qos_class_self_np(uint qosClass, int relativePriority);
}
