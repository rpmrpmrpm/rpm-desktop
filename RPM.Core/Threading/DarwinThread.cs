using System.Runtime.InteropServices;

namespace RPM.Core.Threading;

/// <summary>
/// The scheduling priority of the calling thread on macOS and iOS.
/// </summary>
/// <remarks>
/// <para><b>Why not the quality-of-service class.</b> Apple's own lever is the thread's QoS class, but
/// <c>pthread_set_qos_class_self_np</c> refuses every .NET thread (it returns EPERM): the runtime creates its
/// threads with explicit scheduling attributes, and such a thread has opted out of QoS. A call that is refused
/// changes nothing and says so only in its return value.</para>
///
/// <para><b>What works.</b> A .NET thread is <c>SCHED_OTHER</c> at priority 31, in a range of 15 to 47, and
/// <c>pthread_setschedparam</c> moves it anywhere in that range without privileges. The levels below are the
/// base priorities of the QoS classes the callers mean, so a thread here sits where Apple would put a thread of
/// that class. <see cref="Thread.Priority"/> moves the same number by only three steps (28 to 33).</para>
/// </remarks>
internal static class DarwinThread
{
    /// <summary>The base priority of the user-interactive class: the interface, and the audio pump.</summary>
    public const int Interactive = 47;

    /// <summary>The base priority of the user-initiated class: work the user is waiting for.</summary>
    public const int Initiated = 37;

    /// <summary>The base priority of the utility class: long work the user is not waiting for.</summary>
    public const int Utility = 20;

    /// <summary>Whether this system has the lever at all.</summary>
    public static bool Applies => OperatingSystem.IsMacOS() || OperatingSystem.IsIOS();

    /// <summary>The calling thread's priority, or null where it cannot be read.</summary>
    public static int? Priority
    {
        get
        {
            if (!Applies)
                return null;
            try
            {
                return pthread_getschedparam(pthread_self(), out _, out var param) == 0 ? param.Priority : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>Moves the calling thread to <paramref name="priority"/> under its own policy. Returns whether
    /// the system took it.</summary>
    public static bool SetPriority(int priority)
    {
        if (!Applies)
            return false;
        try
        {
            var self = pthread_self();
            if (pthread_getschedparam(self, out var policy, out var param) != 0)
                return false;
            param.Priority = priority;
            return pthread_setschedparam(self, policy, ref param) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary><c>struct sched_param</c> on Darwin: the priority and four bytes the system keeps.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SchedParam
    {
        public int Priority;
        private int _opaque;
    }

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern IntPtr pthread_self();

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int pthread_getschedparam(IntPtr thread, out int policy, out SchedParam param);

    [DllImport("/usr/lib/libSystem.dylib")]
    private static extern int pthread_setschedparam(IntPtr thread, int policy, ref SchedParam param);
}
