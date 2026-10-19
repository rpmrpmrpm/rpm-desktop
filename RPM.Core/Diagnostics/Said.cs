using System.Diagnostics;

namespace RPM.Core.Diagnostics;

/// <summary>
/// Where a diagnostic line goes, decided once.
///
/// <para><b>Not the debug channel, while there is a ring.</b> Each <c>Debug.WriteLine</c> that reaches
/// an attached debugger is a round trip to it. The runtime holds the thread until the debugger
/// answers, and that can hold up the whole process. A diagnostic that writes a line per realised row
/// would therefore stop the app hundreds of times a second, and the timings it reports would be the
/// stalls the diagnostic itself caused.</para>
///
/// <para>So: into the ring, which is a lock-free write into a mapped file that nothing waits on, and
/// which puts the line on the same clock as the stalls and throws it is about. Read it back with
/// <c>rpmscope log</c>.</para>
///
/// <para>Without a ring (<c>RPM_SCOPE=0</c>, or a head that never started one) the line goes to
/// <c>Debug.WriteLine</c> when a debugger is attached, because a diagnostic nobody can read is worse
/// than a slow one. A run with no debugger attached prints to the terminal, which costs nothing and
/// is where somebody running from a shell is looking.</para>
/// </summary>
public static class Said
{
    /// <param name="channel">The tag printed in front of the line: "walldiag", "db", "net".</param>
    public static void Line(string channel, string line)
    {
        Scope.Said(channel, line);

        // A terminal is free to write to and is the only place a shell run can be read.
        if (!Debugger.IsAttached)
        {
            Console.WriteLine($"[{channel}] {line}");
            return;
        }

        // A debugger IS attached. Say it there only when the ring cannot carry it.
        if (!Scope.On)
            Debug.WriteLine($"[{channel}] {line}");
    }
}
