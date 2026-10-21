using System.Diagnostics;

namespace RPM.Core.Files;

/// <summary>
/// Stopping a child process, such as ffmpeg, so that the files it was writing can be deleted.
///
/// <para><see cref="Process.Kill()"/> only asks for the stop and returns at once. On Windows a file that the
/// process still holds open cannot be deleted. A delete straight after the kill fails, and a half-written
/// file stays behind. So the process must have exited before anything it wrote is cleaned up.</para>
/// </summary>
internal static class ChildProcess
{
    /// <summary>How long to wait for a killed process to exit. ffmpeg exits within milliseconds of a kill.
    /// The limit only keeps a cancel from hanging if it does not.</summary>
    private const int ExitWaitMs = 5_000;

    /// <summary>Kills the process and everything it started, and waits for it to exit. Does nothing for a
    /// process that has already exited, and never throws.</summary>
    public static void StopAndWait(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(ExitWaitMs);
            }
        }
        catch { }
    }
}
