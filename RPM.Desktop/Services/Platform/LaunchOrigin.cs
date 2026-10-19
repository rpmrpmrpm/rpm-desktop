using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RPM.Desktop.Services.Platform;

/// <summary>
/// Who started this process, on one line for the ring: the process id, the parent's id and name, and
/// the flags it was started with.
///
/// <para>This line is written once at startup. It shows what started the process: launchd for a Finder
/// or Launch Services start, a shell or dotnet for a run from a terminal or the IDE, or the app itself for
/// a process it spawned.</para>
///
/// <para>Flags only, never values. An <c>rpm://</c> link can carry a sign-in code, and ring lines travel
/// with crash reports. So an argument that is not a <c>--flag</c> is counted and not written.</para>
/// </summary>
internal static class LaunchOrigin
{
    [DllImport("libc", EntryPoint = "getppid")]
    private static extern int GetParentPid();

    public static string Describe()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        var flags = args.Where(a => a.StartsWith("--", StringComparison.Ordinal))
            .Select(a => a.Split('=', 2)[0])
            .ToArray();
        var others = args.Length - flags.Length;

        return $"process {Environment.ProcessId}, started by {Parent()}, "
             + (flags.Length == 0 ? "no flags" : $"flags {string.Join(' ', flags)}")
             + (others == 0 ? "" : $", {others} other argument{(others == 1 ? "" : "s")}");
    }

    /// <summary>The parent's id and name on macOS and Linux, or "unknown" where the platform does not
    /// say it cheaply (Windows) or the parent has already gone.</summary>
    private static string Parent()
    {
        if (OperatingSystem.IsWindows())
            return "unknown";

        try
        {
            var parent = GetParentPid();
            try
            {
                using var process = Process.GetProcessById(parent);
                return $"{parent} ({process.ProcessName})";
            }
            catch (ArgumentException)
            {
                return $"{parent} (gone)";
            }
        }
        catch
        {
            return "unknown";
        }
    }
}
