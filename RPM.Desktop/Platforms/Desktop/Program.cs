using Uno.UI.Hosting;

namespace RPM.Desktop;

internal class Program
{
#if DEBUG
    private static Timer? _crashTest;
#endif

    [STAThread]
    public static void Main(string[] args)
    {
        // The firewall helper. It runs before Velopack, so that the elevated helper run does not
        // start the whole app. Only the Windows head uses it. This head ships on macOS and Linux, where
        // the argument is never passed. It is here so that a Windows development run of this head,
        // which asks for the firewall rule, does not start a second, elevated app.
        Services.Platform.WindowsFirewall.ServeIfAsked(args);

        // Before anything else that sets up the app.
        //
        // This call is not only how the app learns it was installed. It is also how the installer, the
        // updater and the uninstaller reach it: they start this executable with hooks of their own
        // (--veloapp-install, --veloapp-updated and so on), and this call serves them and exits the
        // process. Put a window, a log file or a lock ahead of it and those runs get a whole app
        // starting up around a hook that is about to end it.
        //
        // It does nothing at all in a copy that was not installed, which is every run from the IDE.
        Velopack.VelopackApp.Build().Run();

        // macOS .app bundles launch with CWD=/. Set it to BaseDirectory, so that relative paths work.
        if (OperatingSystem.IsMacOS())
            Environment.CurrentDirectory = AppContext.BaseDirectory;

        // A crash on any thread, written down before the process ends. Launched from Finder, stderr
        // goes nowhere, so without this a crash leaves no record. The next launch reads the file
        // (Services/Diagnostics/CrashReportService).
        _ = App.Crashes;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                App.Crashes.Record(ex, "appdomain");
        };

        // A normal exit that does not go through the main window's Closed event. ProcessExit covers
        // this on Windows and Linux. On macOS, ProcessExit does not run in either build. So Cmd+Q and
        // the Dock menu's Quit would leave the run without a Closing record. MacQuit listens to AppKit's
        // own terminate notification instead, and that notification covers both of these.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RPM.Core.Diagnostics.Scope.Closing();
        Services.Platform.MacQuit.SaysClosing();

#if DEBUG
        // RPM_CRASH_TEST=managed throws on a thread pool thread ten seconds after launch. This tests
        // the crash report path by hand. The timer is kept in a field, because an unreferenced Timer
        // can be collected before it fires.
        if (RPM.Core.Diagnostics.DevSwitch.Read("RPM_CRASH_TEST") == "managed")
            _crashTest = new Timer(_ => throw new InvalidOperationException("RPM_CRASH_TEST"), null, 10_000, Timeout.Infinite);
#endif

        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseX11()
            .UseLinuxFrameBuffer()
            .UseMacOS()
            // Windows draws through OpenGL. This is required, not a preference.
            //
            // Vulkan would suit the shell better, because it has less driver overhead for a UI that
            // redraws whole frames. But with Vulkan every GL surface in the app fails. Pegasus's room and
            // all the visualizers show black, and nothing on screen says why. On Windows, SetPixelFormat
            // can be called only once per device context. The Vulkan swapchain takes the window's pixel
            // format first, so Uno's GLCanvasElement is refused one: "INativeOpenGLWrapper creation
            // failed :: SetPixelFormat failed: The pixel format is invalid". The stage starts, reports
            // no error, and never draws a frame.
            //
            // On one AMD Radeon machine, Vulkan gives painted=False and no renderer string. OpenGL gives
            // painted=True and an OpenGL 4.6 compatibility context. So the room uses OpenGL.
            //
            // The backend choice does not fix the minimise bug. Both backends lay out against the 0x0
            // client rect of a minimised window while the render thread keeps drawing. On GL this shows
            // as a corner at the bottom left, and on Vulkan as a stretched image. Switching backends only
            // moves the symptom. See WindowRestore for how the minimize case is handled.
            .UseWin32(b => b.RenderingBackend(Win32RenderingBackend.OpenGL))
            .Build();

        host.Run();
    }
}
