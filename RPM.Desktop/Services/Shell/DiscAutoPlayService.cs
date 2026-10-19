using System.Diagnostics;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// Makes rpm the app that the machine opens when a music CD is put in, and takes it back off again.
///
/// <para>Neither platform lets an app declare this in its own manifest. The choice belongs to the
/// user and lives in the user's own settings, so registering means writing exactly what the system's
/// own settings screen writes, on the user's say-so, in the user's own preference domain:</para>
///
/// <list type="bullet">
///   <item><description><b>macOS</b>: System Settings' "CDs &amp; DVDs" pane is a front end for the
///   <c>com.apple.digihub</c> preference domain. It has one key for each kind of disc. A third-party app
///   is <c>action 5</c> plus the path to its bundle. The built-in choices have values above 100. For
///   example, a music CD's factory setting is 101, which opens Music.app. Deleting the key restores that
///   factory setting. rpm cannot add itself to the pane's own popup list, because that list belongs to
///   Apple and no plist key adds to it. So writing the preference is the registration. The pane then
///   shows it as "Open other application: rpm".</description></item>
///   <item><description><b>Windows</b>: AutoPlay. A handler is a ProgID with a verb plus an entry
///   under AutoplayHandlers for the <c>PlayCDAudioOnArrival</c> event, and the user's choice among the
///   registered handlers is UserChosenExecuteHandlers. All three live in HKCU, so this needs no
///   installer and no elevation.</description></item>
/// </list>
///
/// <para>The OS is the only source of truth, so nothing about this is kept in settings.json. The state
/// is read from the OS each time the settings page opens. If a user changes it in the system's own
/// screen, that change is the one that counts.</para>
/// </summary>
public sealed class DiscAutoPlayService
{
    /// <summary>The preference domain and key behind macOS' "When you insert a music CD".</summary>
    private const string MacDomain = "com.apple.digihub";
    private const string MacKey = "com.apple.digihub.cd.music.appeared";

    /// <summary>digihub's "open this other application", as opposed to the numbered built-ins.</summary>
    private const string MacOpenApplicationAction = "5";

    /// <summary>Our AutoPlay handler's name and the ProgID it invokes. Both are ours to choose. They
    /// only have to be unique on the machine and stable across versions, because unregistering finds
    /// them by name.</summary>
    private const string WindowsHandler = "RpmRipAudioCd";
    private const string WindowsProgId = "RPM.RipAudioCd";

    private const string WindowsAutoPlayRoot =
        @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers";

    /// <summary>The event Windows raises for an audio CD, as opposed to a blank disc or a video DVD.</summary>
    private const string WindowsCdEvent = "PlayCDAudioOnArrival";

    /// <summary>
    /// Whether this build can register at all. On macOS, that means running as a real .app bundle.
    /// A development run from bin/ has no bundle for the system to open. Registering the path of a
    /// build folder would leave a dead entry after that folder is deleted.
    /// </summary>
    public bool IsSupported => OperatingSystem.IsWindows() || MacBundlePath() is not null;

    /// <summary>True when this copy of rpm is what the machine currently opens for a music CD.</summary>
    public async Task<bool> IsDefaultAsync()
    {
        if (OperatingSystem.IsMacOS())
        {
            if (MacBundlePath() is not { } bundle)
                return false;

            // No key at all means the factory setting is still in force, and `defaults read` exits
            // non-zero saying so.
            var (ok, output) = await RunAsync("defaults", ["read", MacDomain, MacKey]);
            return ok && output.Contains(bundle, StringComparison.Ordinal);
        }

        if (OperatingSystem.IsWindows())
        {
            var (ok, output) = await RunAsync("reg",
                ["query", $@"{WindowsAutoPlayRoot}\UserChosenExecuteHandlers\{WindowsCdEvent}"]);
            return ok && output.Contains(WindowsHandler, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>Registers or unregisters. It reports what the OS says afterwards, not what was asked
    /// for. A write that did not take must not leave the switch showing that it did.</summary>
    public async Task<bool> SetDefaultAsync(bool makeDefault)
    {
        if (OperatingSystem.IsMacOS())
            await SetMacAsync(makeDefault);
        else if (OperatingSystem.IsWindows())
            await SetWindowsAsync(makeDefault);

        return await IsDefaultAsync();
    }

    // ── macOS ────────────────────────────────────────────────────────────────────────────────

    private static async Task SetMacAsync(bool makeDefault)
    {
        if (!makeDefault)
        {
            // Deleting the key rather than writing "ignore": what the user is undoing is having
            // chosen rpm, and what they get back is the machine's own default.
            await RunAsync("defaults", ["delete", MacDomain, MacKey]);
            return;
        }

        if (MacBundlePath() is not { } bundle)
            return;

        await RunAsync("defaults",
            ["write", MacDomain, MacKey, "-dict", "action", "-int", MacOpenApplicationAction,
             "path", "-string", bundle]);
    }

    /// <summary>
    /// The .app this process is running out of, or null when it is not running out of one.
    ///
    /// <para>Found by walking up from the executable, not assumed. An app bundle is usually
    /// <c>rpm.app/Contents/MacOS/rpm</c>, but that is only the usual layout. The answer here has to be
    /// a path that the system can actually open.</para>
    /// </summary>
    private static string? MacBundlePath()
    {
        if (!OperatingSystem.IsMacOS())
            return null;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                return dir.FullName;
            dir = dir.Parent;
        }

        return null;
    }

    // ── Windows ──────────────────────────────────────────────────────────────────────────────

    private static string HandlerKey => $@"{WindowsAutoPlayRoot}\Handlers\{WindowsHandler}";
    private static string EventKey => $@"{WindowsAutoPlayRoot}\EventHandlers\{WindowsCdEvent}";
    private static string ChosenKey => $@"{WindowsAutoPlayRoot}\UserChosenExecuteHandlers\{WindowsCdEvent}";
    private static string ProgIdKey => $@"HKCU\Software\Classes\{WindowsProgId}";

    /// <summary>
    /// Takes rpm's AutoPlay handler out of the registry, for the uninstaller.
    ///
    /// <para>Called from Velopack's before-uninstall hook in the WinUI head's <c>Main</c>, which runs
    /// this executable with the hook argument and ends the process afterwards, and kills it after 30
    /// seconds. So it is synchronous, and each reg.exe gets five seconds. The keys are in HKCU, the
    /// uninstalling user's, which is the user that registered them.</para>
    ///
    /// <para>The user's choice for an audio CD is cleared only when it is rpm: someone who picked
    /// another app after rpm keeps that choice. The handler, its event entry and the ProgID are rpm's
    /// own and always go.</para>
    /// </summary>
    public static void ForgetOnUninstall()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var (ok, chosen) = Run("reg", ["query", ChosenKey, "/ve"]);
        if (ok && chosen.Contains(WindowsHandler, StringComparison.OrdinalIgnoreCase))
            Run("reg", ["delete", ChosenKey, "/f"]);

        Run("reg", ["delete", EventKey, "/v", WindowsHandler, "/f"]);
        Run("reg", ["delete", HandlerKey, "/f"]);
        Run("reg", ["delete", ProgIdKey, "/f"]);
    }

    private static async Task SetWindowsAsync(bool makeDefault)
    {
        // reg.exe rather than Microsoft.Win32.Registry: this project's single target framework is
        // net9.0-desktop, which does not carry the registry API, and taking a package reference for
        // six writes on one platform costs more than it saves. reg.exe ships with every Windows.
        var handlerKey = HandlerKey;
        var eventKey = EventKey;
        var chosenKey = ChosenKey;
        var progIdKey = ProgIdKey;

        if (!makeDefault)
        {
            await RunAsync("reg", ["delete", chosenKey, "/f"]);
            await RunAsync("reg", ["delete", eventKey, "/v", WindowsHandler, "/f"]);
            await RunAsync("reg", ["delete", handlerKey, "/f"]);
            await RunAsync("reg", ["delete", progIdKey, "/f"]);
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
            return;

        // The verb AutoPlay invokes. %L is the drive that AutoPlay is firing for. The app does not need
        // it to find the disc, because the watcher looks at every drive. The handler contract passes it.
        // An argument that the app ignores costs less than a contract that is only partly kept.
        await RunAsync("reg", ["add", progIdKey, "/ve", "/d", Loc.Get("Rip_AutoPlayAction"), "/f"]);
        await RunAsync("reg", ["add", $@"{progIdKey}\shell\open\command", "/ve", "/d",
            $"\"{exe}\" --rip-disc \"%L\"", "/f"]);

        await RunAsync("reg", ["add", handlerKey, "/v", "Action", "/d", Loc.Get("Rip_AutoPlayAction"), "/f"]);
        await RunAsync("reg", ["add", handlerKey, "/v", "Provider", "/d", "rpm", "/f"]);
        await RunAsync("reg", ["add", handlerKey, "/v", "InvokeProgID", "/d", WindowsProgId, "/f"]);
        await RunAsync("reg", ["add", handlerKey, "/v", "InvokeVerb", "/d", "open", "/f"]);
        await RunAsync("reg", ["add", handlerKey, "/v", "DefaultIcon", "/d", $"{exe},0", "/f"]);

        // Registered. The handler now appears in Settings' AutoPlay list. Then it is chosen, which is
        // the half that makes it the default rather than one more option in a menu.
        await RunAsync("reg", ["add", eventKey, "/v", WindowsHandler, "/d", "", "/f"]);
        await RunAsync("reg", ["add", chosenKey, "/ve", "/d", WindowsHandler, "/f"]);
    }

    // ── running the tool ─────────────────────────────────────────────────────────────────────

    private static ProcessStartInfo Start(string command, string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        return psi;
    }

    /// <summary><see cref="RunAsync"/> for the uninstall hook, which has no reason to be async and a
    /// deadline: five seconds, then the tool is left to finish on its own.</summary>
    private static (bool Ok, string Output) Run(string command, string[] arguments)
    {
        try
        {
            using var process = Process.Start(Start(command, arguments));
            if (process is null)
                return (false, "");

            var stdout = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5_000))
                return (false, "");
            return (process.ExitCode == 0, stdout.Result);
        }
        catch
        {
            return (false, "");
        }
    }

    /// <summary>Runs a command and hands back whether it succeeded and what it said. Never throws:
    /// every caller here treats a failure as "not registered", which is the truth.</summary>
    private static async Task<(bool Ok, string Output)> RunAsync(string command, string[] arguments)
    {
        try
        {
            using var process = Process.Start(Start(command, arguments));
            if (process is null)
                return (false, "");

            var stdout = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return (process.ExitCode == 0, stdout);
        }
        catch
        {
            return (false, "");
        }
    }
}
