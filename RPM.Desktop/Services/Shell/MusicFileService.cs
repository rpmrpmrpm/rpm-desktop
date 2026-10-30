using System.Diagnostics;
using System.IO.Pipes;
using RPM.Core.Metadata;
using RPM.Core.Scanning;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// Makes rpm one of the apps Windows opens music files with, and plays the files it is opened with.
///
/// <para><b>Offered, not imposed.</b> Since Windows 8 no app may make itself the default for a file type.
/// The choice is the user's, and Windows keeps it under a hash that only its own settings screen writes. So
/// switching this on does what an installer would: it registers rpm as a handler for every audio type
/// <see cref="AudioFileScanner"/> reads (a ProgID, an Open With entry per extension, and the Capabilities
/// that put rpm in Settings' Default apps), and then opens that page at rpm's entry so the user can choose
/// it. Everything is under HKCU, so it needs no installer and no elevation, like
/// <see cref="DiscAutoPlayService"/>.</para>
///
/// <para><b>The registry is the only record.</b> Nothing about this is in settings.json. The switch reads
/// the registry each time it is shown.</para>
///
/// <para><b>One rpm plays them.</b> A file double-clicked while rpm is open is handed to the running copy
/// over a named pipe (<see cref="TryHandOver"/>, <see cref="Listen"/>), and the new process ends before it
/// has a window. Without this, every double-click would start a second app with a second player.</para>
/// </summary>
public sealed class MusicFileService
{
    private const string ProgId = "RPM.AudioFile";
    private const string AppName = "rpm";
    private const string CapabilitiesPath = @"Software\rpm\Capabilities";

    private static string ProgIdKey => $@"HKCU\Software\Classes\{ProgId}";
    private static string CapabilitiesKey => $@"HKCU\{CapabilitiesPath}";
    private const string RegisteredApplicationsKey = @"HKCU\Software\RegisteredApplications";

    /// <summary>The pipe the running copy listens on. Per user, because two people signed in at once each
    /// have their own rpm.</summary>
    private static string PipeName => $"rpm.desktop.open.{Environment.UserName}";

    /// <summary>Whether this build can register at all. Windows only: macOS takes its document types from
    /// the bundle's Info.plist, not from the app at run time.</summary>
    public bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>True when rpm is registered as a handler for music files, pointing at this copy.</summary>
    public async Task<bool> IsOfferedAsync()
    {
        if (!IsSupported || Environment.ProcessPath is not { Length: > 0 } exe)
            return false;

        var (ok, output) = await RunAsync("reg", ["query", $@"{ProgIdKey}\shell\open\command", "/ve"]);
        return ok && output.Contains(exe, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Registers or unregisters. Registering also opens Windows' Default apps page at rpm, where the user
    /// makes it the app for music files. It reports what the registry says afterwards.
    /// </summary>
    /// <param name="offer">True to register.</param>
    /// <param name="askWindows">Whether to open the Default apps page after registering.</param>
    public async Task<bool> SetAsync(bool offer, bool askWindows = true)
    {
        if (!IsSupported)
            return false;

        if (!offer)
        {
            await ForgetAsync();
            return await IsOfferedAsync();
        }

        if (Environment.ProcessPath is not { Length: > 0 } exe)
            return false;

        var description = Loc.Get("Settings_OpenMusicFilesType");
        await RunAsync("reg", ["add", ProgIdKey, "/ve", "/d", description, "/f"]);
        await RunAsync("reg", ["add", $@"{ProgIdKey}\DefaultIcon", "/ve", "/d", $"{exe},0", "/f"]);
        await RunAsync("reg", ["add", $@"{ProgIdKey}\shell\open\command", "/ve", "/d", $"\"{exe}\" \"%1\"", "/f"]);

        await RunAsync("reg", ["add", CapabilitiesKey, "/v", "ApplicationName", "/d", AppName, "/f"]);
        await RunAsync("reg", ["add", CapabilitiesKey, "/v", "ApplicationDescription", "/d",
            Loc.Get("Settings_OpenMusicFilesHint"), "/f"]);

        foreach (var extension in AudioFileScanner.AudioExtensions)
        {
            await RunAsync("reg", ["add", $@"HKCU\Software\Classes\{extension}\OpenWithProgids",
                "/v", ProgId, "/t", "REG_NONE", "/f"]);
            await RunAsync("reg", ["add", $@"{CapabilitiesKey}\FileAssociations", "/v", extension, "/d", ProgId, "/f"]);
        }

        await RunAsync("reg", ["add", RegisteredApplicationsKey, "/v", AppName, "/d", CapabilitiesPath, "/f"]);

        // The registry change is announced by Explorer on its own schedule. The Default apps page reads it
        // when it opens, so it is opened after the writes, not alongside them.
        if (askWindows)
        {
            try
            {
                Process.Start(new ProcessStartInfo($"ms-settings:defaultapps?registeredAppUser={AppName}")
                {
                    UseShellExecute = true,
                });
            }
            catch { /* the registration stands; the user can pick rpm from Open with */ }
        }

        return await IsOfferedAsync();
    }

    private static async Task ForgetAsync()
    {
        foreach (var extension in AudioFileScanner.AudioExtensions)
            await RunAsync("reg", ["delete", $@"HKCU\Software\Classes\{extension}\OpenWithProgids",
                "/v", ProgId, "/f"]);
        await RunAsync("reg", ["delete", RegisteredApplicationsKey, "/v", AppName, "/f"]);
        await RunAsync("reg", ["delete", @"HKCU\Software\rpm", "/f"]);
        await RunAsync("reg", ["delete", ProgIdKey, "/f"]);
    }

    /// <summary>Takes rpm's file registration out of the registry, for the uninstaller. Synchronous, for
    /// the same reason as <see cref="DiscAutoPlayService.ForgetOnUninstall"/>.</summary>
    public static void ForgetOnUninstall()
    {
        if (!OperatingSystem.IsWindows())
            return;
        // Velopack ends the hook after 30 seconds, and the AutoPlay handler's removal runs first.
        try { ForgetAsync().Wait(TimeSpan.FromSeconds(10)); } catch { }
    }

    // ── the files rpm is opened with ─────────────────────────────────────────────────────────

    /// <summary>The music files on this process's command line: the ones that exist and that rpm reads.</summary>
    public static IReadOnlyList<string> FilesOnCommandLine() =>
        [.. Environment.GetCommandLineArgs()
            .Skip(1)
            .Where(a => !a.StartsWith('-')
                        && AudioFileScanner.AudioExtensions.Contains(Path.GetExtension(a))
                        && File.Exists(a))
            .Select(Path.GetFullPath)];

    /// <summary>
    /// Hands <paramref name="files"/> to a copy of rpm that is already running, and says whether one took
    /// them. Called before this process has a window, so a copy that takes them leaves nothing behind.
    /// </summary>
    public static bool TryHandOver(IReadOnlyList<string> files)
    {
        if (files.Count == 0)
            return false;

        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(1500);
            using var writer = new StreamWriter(pipe);
            foreach (var file in files)
                writer.WriteLine(file);
            writer.Flush();
            return true;
        }
        catch
        {
            return false;   // nobody is listening: this process becomes the one that plays them
        }
    }

    /// <summary>
    /// Listens, for the life of the process, for files handed over by a later launch, and gives each
    /// batch to <paramref name="play"/> on the pipe's own thread.
    /// </summary>
    public static void Listen(Action<IReadOnlyList<string>> play)
    {
        if (!OperatingSystem.IsWindows())
            return;

        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await pipe.WaitForConnectionAsync();
                    using var reader = new StreamReader(pipe);

                    var files = new List<string>();
                    while (await reader.ReadLineAsync() is { } line)
                        if (line.Length > 0 && File.Exists(line))
                            files.Add(line);

                    if (files.Count > 0)
                        play(files);
                }
                catch (Exception e)
                {
                    RPM.Core.Diagnostics.Said.Line("open", $"hand-over failed: {e.Message}");
                    await Task.Delay(1000);
                }
            }
        });
    }

    /// <summary>Plays <paramref name="files"/> in the player, in the order given, replacing the queue.
    /// The tags are read off the thread that asked.</summary>
    public static void Play(RPaMpService player, IReadOnlyList<string> files) => _ = Task.Run(() =>
    {
        var tracks = new List<RPM.Core.Models.TrackInfo>();
        foreach (var file in files)
        {
            try { tracks.Add(TagReader.Read(file)); }
            catch (Exception e) { RPM.Core.Diagnostics.Said.Line("open", $"could not read {file}: {e.Message}"); }
        }
        player.PlayTracksFrom(tracks);
    });

    // ── running the tool ─────────────────────────────────────────────────────────────────────

    /// <summary>Runs a command and hands back whether it succeeded and what it said. Never throws.</summary>
    private static async Task<(bool Ok, string Output)> RunAsync(string command, string[] arguments)
    {
        try
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

            using var process = Process.Start(psi);
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
