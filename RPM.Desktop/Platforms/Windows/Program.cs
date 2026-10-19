using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using RPM.Desktop.Services.Platform;

namespace RPM.Desktop;

/// <summary>
/// The WinUI head's entry point. Only the net9.0-windows10.0.19041 build compiles this; the Skia head
/// has Platforms/Desktop/Program.cs, and each target framework sees only its own platform folder.
///
/// <para><b>Written by hand, not generated.</b> The WinUI markup compiler writes its own Main unless
/// DISABLE_XAML_GENERATED_MAIN is defined. The generated Main does the same first steps as this one,
/// but it does not catch exceptions. An exception inside Application.Start, such as a XAML resource
/// that will not load or a control whose constructor throws, becomes a stowed exception. The process
/// then ends with 0xc000027b. Windows Error Reporting records the address in Microsoft.UI.Xaml.dll, but
/// the managed exception is not recorded anywhere. This file records it.</para>
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // The firewall helper. This executable starts again, elevated, to add one firewall rule, and
        // then ends the process. It runs before Velopack, because Velopack applies a downloaded update
        // on startup, and that must not run elevated. It does nothing without its argument.
        WindowsFirewall.ServeIfAsked(args);

        // Runs before anything else that sets up the app, including the two handlers below and the
        // working directory. The installer, the updater and the uninstaller all start this executable
        // with a hook argument. This call serves the hook and then ends the process. Anything set up
        // before it would be set up inside a run that is about to end.
        //
        // It does nothing at all in a copy that was not installed, which is every run from the IDE.
        //
        // The uninstall hook removes the AutoPlay handler that RPM wrote into the user's registry,
        // before the uninstaller removes the files. Velopack ends the process after the hook.
        Velopack.VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => Services.Shell.DiscAutoPlayService.ForgetOnUninstall())
            .Run();

        // A shortcut or an installer can start the app with any working directory. The app reads several
        // files by a path relative to itself: ffmpeg, fpcalc, the ONNX weights and the fonts. So the
        // working directory is set to the folder of the binary before anything reads those files.
        try { Environment.CurrentDirectory = AppContext.BaseDirectory; } catch { }

        _ = App.Crashes;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Record("unhandled", e.ExceptionObject as Exception);

        // There is no handler for unobserved task exceptions here. App.OnLaunched records them in the
        // diagnostic ring on both heads. Writing each one to startup-crash.txt as well would fill that
        // file with failures that did not end the app.

        // The teardown is marked in two places. On this head the process has sometimes ended with
        // abort() (0xc0000409, raised inside ucrtbase) after "winui start returned". That crash has no
        // managed frames on the faulting thread, and nothing on the managed unhandled path. Not every
        // close ends this way. The crash harvest treats a fail-fast after the Closing record as this
        // abort, not as a new crash.
        //
        // The two marks show which part of the teardown the process was in. "process exit" is written
        // once the runtime has begun shutting down, before the finalizers and the native
        // DLL_PROCESS_DETACH calls. "main returning" is written before any of that. A crash with both
        // marks in the diagnostic ring happened during native teardown. A crash with neither happened
        // earlier.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Mark("process exit");
            try { RPM.Core.Diagnostics.Scope.Closing(); } catch { }
        };

        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Three marks around the one call that owns the whole run. Application.Start does not return
        // until the app is over. So "start returned" on the boot track means that the app chose to stop,
        // and did not fail.
        Mark("winui main");

        try
        {
            Application.Start(_unused =>
            {
                // The dispatcher queue of this thread becomes the synchronisation context. So an await
                // in App or in any page resumes on the UI thread. WinUI does not set this up itself.
                //
                // The context is wrapped. An exception that an async void handler throws after an await
                // is rethrown in a callback that this context runs. On WinUI, that path ends the process
                // with a stowed exception before any managed handler sees it. The wrapper keeps a database
                // refusal that is already on screen, and records any other exception before it ends the
                // process. See RefusalGuardContext.
                var queue = DispatcherQueue.GetForCurrentThread();
                RefusalGuardContext.Install(
                    new DispatcherQueueSynchronizationContext(queue),
                    App.IsRefusalOnScreen,
                    error => Record("dispatcher", error));
                Mark("winui app");
                _ = new App();
            });
            Mark("winui start returned");
        }
        catch (Exception ex)
        {
            Record("start", ex);
            throw;
        }

        Mark("main returning");
    }

    /// <summary>Writes one boot-track mark. It is separate from <see cref="Record"/>, because a mark is not
    /// a failure.</summary>
    private static void Mark(string what)
    {
        try { RPM.Core.Diagnostics.Scope.Start(); } catch { }
        try { RPM.Core.Diagnostics.Scope.Said("boot", what); } catch { }
    }

    /// <summary>Writes a failure where it can be read after the process has ended. It writes to the
    /// console when there is one, and always to startup-crash.txt in the app's local-data folder. See
    /// StartupCrashLog, which keeps only the newest entries under a size limit.</summary>
    private static void Record(string where, Exception? ex)
    {
        var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}: {ex}"
                 + (ex?.InnerException is null ? "" : $"{Environment.NewLine}  inner: {ex.InnerException}")
                 + Environment.NewLine + Environment.NewLine;

        try { Console.Error.Write(text); } catch { }
        try { RPM.Core.Crashes.StartupCrashLog.Append(LocalData.Dir, text); } catch { }
        if (ex is not null)
            try { App.Crashes.Record(ex, where); } catch { }
        try { RPM.Core.Diagnostics.Scope.Said("boot", $"{where}: {ex?.GetType().Name}: {ex?.Message}"); } catch { }
    }
}
