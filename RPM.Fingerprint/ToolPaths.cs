using System.Runtime.InteropServices;

namespace RPM.Fingerprint;

/// <summary>
/// Finds the external command-line tools that the app runs: <c>ffmpeg</c> (decode/cut/encode) and
/// <c>fpcalc</c> (Chromaprint fingerprints).
///
/// The build fetches per-RID binaries next to the app (see scripts/fetch-tools.*), and this resolver
/// prefers those. Relying on PATH alone is not enough: a GUI app started from Finder or Explorer
/// usually gets a PATH that does not include Homebrew or winget folders.
///
/// A published app uses only its own copies (<see cref="ToolSource.OwnFolder"/>): the publish fails
/// without them, so a missing one was removed after install, and the launch check should report an
/// incomplete install instead of using Homebrew's copy. A different ffmpeg could also decode to
/// different samples under the same AudioKey in the shared analysis pool. A development build also
/// uses the machine's tools as a fallback (<see cref="ToolSource.OwnFolderThenMachine"/>), because
/// its fetch may have failed.
///
/// In RPM.Fingerprint, not RPM.Core, because Fingerprint is the leaf assembly: Core references it,
/// and the app references both, so both can see it.
/// </summary>
public static class ToolPaths
{
    /// <summary>Resolved ffmpeg path. Falls back to the bare name, so the OS reports a missing tool.</summary>
    public static string Ffmpeg => s_ffmpeg.Value;

    /// <summary>Resolved fpcalc path. Falls back to the bare name, so the OS reports a missing tool.</summary>
    public static string Fpcalc => s_fpcalc.Value;

    /// <summary>The AppContext switch that a publish of RPM.Desktop sets (RuntimeHostConfigurationOption
    /// in RPM.Desktop.csproj). It is in the runtimeconfig, or compiled into a Native AOT binary, so it
    /// is set from the first call and needs no setup code.</summary>
    public const string ShipsToolsSwitch = "RPM.ShipsTools";

    // Resolution is a few stat() calls, but it happens on every decode/fingerprint call in a loop
    // over a library, so it is cached. Lazy (not a static field initialiser) so a test that sets an
    // override env var before first use still takes effect.
    private static readonly Lazy<string> s_ffmpeg = new(() => Resolve("ffmpeg", "RPM_FFMPEG"));
    private static readonly Lazy<string> s_fpcalc = new(() => Resolve("fpcalc", "RPM_FPCALC"));

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>The AppContext switch that a Release build of RPM.Desktop sets to false. When it is
    /// false, no RPM_* environment switch is read. Must be the same name as
    /// RPM.Core.Diagnostics.DevSwitch.AppContextName, which this assembly cannot see; change both. The
    /// Lambda transcoder does not set it, and points ToolPaths at its ffmpeg with RPM_FFMPEG.</summary>
    private const string DevSwitchesSwitch = "RPM.DevSwitches";

    private static bool OverridesHonoured => !(AppContext.TryGetSwitch(DevSwitchesSwitch, out var on) && !on);

    private static string Resolve(string name, string overrideVar) =>
        Resolve(name,
            OverridesHonoured ? Environment.GetEnvironmentVariable(overrideVar) : null,
            AppContext.BaseDirectory,
            AppContext.TryGetSwitch(ShipsToolsSwitch, out var ships) && ships
                ? ToolSource.OwnFolder
                : ToolSource.OwnFolderThenMachine,
            Environment.GetEnvironmentVariable("PATH"));

    internal static string Resolve(string name, string? overridePath, string ownFolder, ToolSource source, string? path)
    {
        var exe = IsWindows ? name + ".exe" : name;

        // 1. Explicit override: for a machine with a custom build, and used by CI and the
        //    transcoder. Never in a Release build of the app, which passes null here.
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)) return overridePath;

        // 2. Bundled next to the app binary. This is the path for released builds: fetch-tools puts
        //    the per-RID binary into the output dir, which on macOS is Contents/MacOS inside the .app.
        var bundled = Path.Combine(ownFolder, exe);
        if (File.Exists(bundled)) return bundled;

        if (source == ToolSource.OwnFolderThenMachine)
        {
            // 3. PATH: for a development machine (brew install ffmpeg chromaprint, winget, apt).
            if (!string.IsNullOrEmpty(path))
            {
                foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    // A malformed PATH entry (illegal chars, stray quotes) must not crash the app.
                    try
                    {
                        var candidate = Path.Combine(dir.Trim('"'), exe);
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch (ArgumentException) { }
                }
            }

            // 4. Well-known install dirs. A GUI app started from Finder gets a minimal PATH without
            //    /opt/homebrew/bin, so brew-installed tools are not found at step 3 even though they
            //    are installed. The same is true for winget's shim dir on Windows.
            foreach (var dir in WellKnownDirs())
            {
                var candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate)) return candidate;
            }
        }

        // 5. Not found: return the bare name. The process start then fails with the OS message
        //    ("The system cannot find the file specified"), which callers show, and AppPayload's
        //    launch check reads it as missing.
        return name;
    }

    private static IEnumerable<string> WellKnownDirs()
    {
        if (IsWindows)
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(local))
                yield return Path.Combine(local, "Microsoft", "WinGet", "Links");
            yield return @"C:\ProgramData\chocolatey\bin";
        }
        else
        {
            yield return "/opt/homebrew/bin";   // Apple Silicon Homebrew
            yield return "/usr/local/bin";      // Intel Homebrew, and the usual Linux local install
            yield return "/usr/bin";
        }
    }
}

/// <summary>Where <see cref="ToolPaths"/> may find a tool.</summary>
internal enum ToolSource
{
    /// <summary>The app's own folder only: a published app, which ships its tools.</summary>
    OwnFolder,

    /// <summary>The app's own folder, then PATH and the usual install folders: a development build,
    /// whose fetch may have failed.</summary>
    OwnFolderThenMachine,
}
