using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging.Console;
using Microsoft.UI.Dispatching;
using RPM.Core.Models;
using RPM.Desktop.Presentation.RPaMp;
using Uno.UI; // UseStudio(): the Uno App MCP inspection channel (DEBUG only)

namespace RPM.Desktop;

public partial class App : Application
{
    public App()
    {
        // This runs first, before anything worth recording. The diagnostic ring is opened once for each
        // process. It is never closed, and it is written to whether or not anyone reads it.
        RPM.Core.Diagnostics.Scope.Start();

        // The boot track records where the start-up had got to. The marks use the same clock as the thread
        // outages. So a long splash screen can be read as a place in the start-up, and not only as a
        // feeling. The first mark includes time that no in-process stopwatch can see: the runtime, the
        // assemblies, and the Uno host, all before any of our code ran.
        RPM.Core.Diagnostics.Scope.Said("boot", $"app ctor, {Uptime.TotalMilliseconds:F0}ms after exec");

        // Records who started this process, so that an unexpected ring entry shows where it came from
        // (LaunchOrigin).
        try { RPM.Core.Diagnostics.Scope.Said("boot", RPM.Desktop.Services.Platform.LaunchOrigin.Describe()); } catch { }

        // On macOS, records which handler SIGUSR1 points at. See SigWatch for the crash that this is for.
        SigWatch.Start();

        // Records any exception that the framework cannot deliver, before it ends the process. It is
        // installed here because the important failures happen before OnLaunched. The application resources
        // are resolved between the end of the constructor and the call to OnLaunched. A dictionary that
        // will not load there comes out of WinUI as a stowed exception. The process then dies with no stack
        // and no message. This handler does not mark the exception as handled. The app still fails, but it
        // records the reason first.
        UnhandledException += (_, e) =>
        {
            // This is the one exception that IS marked as handled: a refusal of the library database that
            // is already on screen. LibraryService has shown the problem screen (or taken down a borrowed
            // shelf), and it has written the event to the ring. About 230 call sites read the database with
            // no protection around them. Ending the process here would also take down that screen. Any
            // other exception still fails the app. See LibraryService.Reported.
            if (e.Exception is { } thrown && IsRefusalOnScreen(thrown))
            {
                e.Handled = true;
                try { RPM.Core.Diagnostics.Scope.Said("library", $"a refusal already on screen reached the last-chance handler ({thrown.GetType().Name})"); } catch { }
                return;
            }

            var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] unhandled: {e.Exception}{Environment.NewLine}{Environment.NewLine}";
            try { RPM.Core.Diagnostics.Scope.Said("boot", $"unhandled: {e.Exception?.GetType().Name}: {e.Message}"); } catch { }
#if RPM_WINUI
            // A crash file is written on WinUI only. There, an exception that is left unhandled here ends
            // the process. On the Uno Skia head, this event is raised only for exceptions that Uno has
            // already recovered from (Application.RaiseRecoverableUnhandledException, with fatal: false).
            // So a crash file written here would report a run that did not crash. On that head, a fatal
            // exception reaches AppDomain.UnhandledException, which Platforms/Desktop/Program.cs records.
            if (e.Exception is { } failure)
                try { Crashes.Record(failure, "application"); } catch { }
#endif
            try { Console.Error.Write(text); } catch { }
            try { RPM.Core.Crashes.StartupCrashLog.Append(RPM.Desktop.Services.Platform.LocalData.Dir, text); }
            catch { }
        };

        // This must run before anything touches the clipboard. The Win32 host caches its OEM decoder on the
        // first read, and it caches a failure too. So the repair must be in place before that first read.
        OemCodePageFix.Apply();

        // This must run before the first visual exists. Every composition Visual reads the subtree-caching
        // thresholds in its constructor. A later call would only affect the visuals built after it.
        RenderTuning.Apply();

        // The app is dark as the APPLICATION's theme, and not only as a forced theme on the main window's
        // content. OnLaunched still applies the theme to the content. The application theme is needed for
        // the trees that the content theme cannot reach. A ContentDialog's scrim lives on the popup root.
        // That is a SIBLING of the main content, and it resolves its brushes against the application theme,
        // which otherwise follows the OS. On a light OS, the scrim came out 60% white, so every dialog
        // opened under a white fog. This can be set only here, before any window exists.
        RequestedTheme = ApplicationTheme.Dark;

        // The two marks surround the resource load. This is the one step in this constructor that can fail
        // without saying anything. App.xaml merges seven dictionaries. A missing key, or a control whose
        // default style will not build, comes out of WinUI as a stowed exception. That ends the process
        // fast, without throwing. There is no stack and no message, and the previous mark is the only clue
        // to where it stopped. These two marks turn that into a readable line.
        RPM.Core.Diagnostics.Scope.Said("boot", "app resources");
        this.InitializeComponent();

        RPM.Core.Diagnostics.Scope.Said("boot", "app ctor done");
    }

    protected Window? MainWindow { get; private set; }
    protected IHost? Host { get; private set; }

    public static IServiceProvider Services => ((App)Current).Host!.Services;

    /// <summary> Whether <paramref name="error"/>, or an exception inside it, is a database refusal that
    /// the library service has already shown on screen. It returns false before the app or its host is
    /// built, because no library can have reported anything then. It is asked by the last-chance handler
    /// below. On the WinUI head, it is also asked by the interface thread's context
    /// (<see cref="RefusalGuardContext"/>), which is set up before the app exists.
    /// </summary>
    internal static bool IsRefusalOnScreen(Exception error)
    {
        if (Current is not App { Host: { } host } || host.Services.GetService<LibraryService>() is not { } library)
            return false;

        if (error is AggregateException aggregate)
            return aggregate.Flatten().InnerExceptions.Any(library.Reported);

        for (Exception? inner = error; inner is not null; inner = inner.InnerException)
            if (library.Reported(inner))
                return true;

        return false;
    }

    public static Window? AppWindow => ((App)Current).MainWindow;

    /// <summary>
    /// How long ago this process was started. What the splash's floor and ceiling are measured against.
    ///
    /// <para><b>This is not a stopwatch, and the reason matters.</b> A Stopwatch that is started in a
    /// static field can start late. A class with static field initialisers and no static constructor is
    /// <c>beforefieldinit</c>. So the runtime may delay the initialiser until the first read of a static
    /// FIELD. Nothing read this field until <see cref="Presentation.Shell.RevealGate"/> ticked for the
    /// first time. That is too late. The clock would start only when the drawing thread was free, and the
    /// splash's floor would then run from that late point, queued behind the start-up work.</para>
    ///
    /// <para>So the anchor is a fact, not a clock that someone has to remember to start. It is the moment
    /// the operating system says this process began. It gives the same answer whenever it is asked. It also
    /// counts the runtime and the assembly loading before any of our code ran. A late initialiser now costs
    /// nothing, because a start time read late is still the same start time.</para>
    /// </summary>
    public static TimeSpan Uptime => DateTime.UtcNow - ProcessStart;

    /// <summary>The build's version as the app shows it, as Major.Minor.Build. It is the tag's version on a
    /// release build, and 0.0.0 on any other build (see RPM.Desktop.csproj). The About dialog, the Profile
    /// page, and the error and crash reports all read this value.</summary>
    internal static string VersionText { get; } =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "";

    /// <summary>Which head this is, as crash reports name it.</summary>
    internal static string HeadName => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "mac" : "linux";

    /// <summary>Where crash files and pending crash reports live.</summary>
    internal static string CrashDirectory => Path.Combine(RPM.Desktop.Services.Platform.LocalData.Dir, "crashes");

    /// <summary> Writes an unhandled exception to a crash file. It is made on first use, which is the first
    /// line of the entry point on both heads. So the handlers hold it before anything can fail. The next
    /// launch's CrashReportService reads what it wrote.
    /// </summary>
    internal static RPM.Core.Crashes.CrashRecorder Crashes => _crashes ??=
        RPM.Core.Crashes.CrashRecorder.ForThisProcess(() => CrashDirectory, VersionText, HeadName);

    private static RPM.Core.Crashes.CrashRecorder? _crashes;

    /// <summary>
    /// A number that belongs to this launch and stays the same for the whole launch.
    /// </summary>
    /// <remarks> The home screen's suggestions are drawn from a pool, not taken from the top of it
    /// (<see cref="RPM.Core.Library.HomeShelf.Suggested"/>). This value is used for that draw. So the row
    /// is different each time the app is opened. Within one session, it is the SAME every time that screen
    /// is read. A row that rearranged itself while someone was looking at it would be worse than a row that
    /// never changed.
    ///
    /// <para>The value comes from the clock, not from a random number generator. It has to differ between
    /// two launches that are a second apart. It also has to be a number, not a promise about how the values
    /// are spread.</para>
    /// </remarks>
    public static int Launch { get; } = (int)(DateTime.UtcNow.Ticks & int.MaxValue);

    /// <summary>When the operating system says this process began, in UTC so a clock change or a
    /// daylight-saving step cannot make the app appear to have started in the future.</summary>
    private static readonly DateTime ProcessStart =
        System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();

    /// <summary>Toggles the main window between maximised and its previous size. On Windows and Linux, this
    /// uses the native presenter. The X11 backend implements Maximize, Restore and State through the window
    /// manager's own hints. On macOS, it uses AppKit's own zoom.
    ///
    /// There is no other approach that works. The obvious one reads the display's work area and calls
    /// <c>AppWindow.MoveAndResize</c>. That cannot be written, because Uno implements neither
    /// <c>DisplayArea.GetFromWindowId</c> and <c>WorkArea</c>, nor <c>MoveAndResize</c>. It would compile
    /// and do nothing.</summary>
    public static void ToggleMaximizeMainWindow()
    {
        if (AppWindow?.AppWindow is not { } aw)
            return;
        if ((OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            && RPM.Desktop.Services.Platform.WindowPresenter.Overlapped(aw) is { } presenter)
        {
            try
            {
                if (presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized)
                    presenter.Restore();
                else
                    presenter.Maximize();
            }
            catch { }
            return;
        }
        if (OperatingSystem.IsMacOS())
        {
            // Native zoom toggles maximize/restore itself, staying off the setFrame abort path.
            try { MacWindowChrome.ZoomWindow("rpm"); } catch { }
        }
    }

    /// <summary>Held for the life of the process, purely so a second one can tell there is a
    /// first.</summary>
    private static Mutex? _instanceLock;

    /// <summary> Windows AutoPlay starts the registered app again each time a disc is inserted. So a
    /// machine that already has RPM open would get a second copy, with two libraries on one file, two
    /// players and two windows. A launch with <c>--rip-disc</c> therefore stops when another copy holds the
    /// lock. The copy that is already running watches the drive, and it will ask about the disc itself.
    ///
    /// <para>macOS does not need this. Its disc handling goes through Launch Services, which activates the
    /// running app and does not start another copy. An ordinary launch is left alone on both systems. So
    /// this is not a general single-instance rule.</para>
    /// </summary>
    /// <returns>False when this process should quit without starting.</returns>
    private static bool ClaimSingleInstance()
    {
        if (!OperatingSystem.IsWindows())
            return true;

        try
        {
            _instanceLock = new Mutex(initiallyOwned: true, @"Local\rpm.desktop", out var first);
            if (first || !Environment.GetCommandLineArgs().Contains("--rip-disc"))
                return true;
        }
        catch
        {
            return true; // A lock that cannot be taken is not a reason to refuse to start
        }

        // This happens before any window exists, so nothing flashes up on the way out.
        Environment.Exit(0);
        return false;
    }

    [SuppressMessage("Trimming", "IL2026:Members annotated with 'RequiresUnreferencedCodeAttribute' require dynamic access otherwise can break functionality when trimming application code", Justification = "Uno.Extensions APIs are used in a way that is safe for trimming in this context.")]
    // The host binds options from configuration, and the binder reads their properties by reflection.
    // In a published Native AOT build, that only works while those properties are kept. So they are
    // rooted here. Without this, the app would start with an AppConfig whose three fields are all null.
    // That looks like an install with no API and no Cast receiver.
    //
    // The IL3050 warning that comes with the same call sites is about ConfigurationBinder calling
    // Array.CreateInstance for collection properties. None of these types has a collection property, and
    // the AOT build has been checked for them. A collection property added to any of them would bring
    // that warning back into play.
    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor,
        typeof(Models.AppConfig))]
    [DynamicDependency(
        DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor,
        typeof(Microsoft.Extensions.Logging.Console.ConsoleFormatterOptions))]
    [UnconditionalSuppressMessage(
        "AOT", "IL3050:RequiresDynamicCode",
        Justification = "Options binding over types with no collection properties, verified on a " +
                        "published Native AOT build.")]
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Puts a stack trace under every "Exception thrown:" line that the debugger shows, so that a caught
        // exception can be traced to the line that threw it. It is off unless RPM_EXCEPTION_DIAG=1.
        RPM.Core.Diagnostics.Scope.Said("boot", "OnLaunched");

        RPM.Desktop.Services.Shell.ExceptionTrace.Install();

        // Counts every request that this app sends over the network, by host. The tally is printed on the
        // wall diagnostic line. So a question such as "why does scrolling cause network traffic" gets a
        // host name and a number, and not a guess about which cache is missing. It is off unless
        // RPM_WALL_DIAG=1.
        RPM.Desktop.Services.Shell.NetDiag.Install();

        // A disc inserted while RPM is already open must not open a second copy. See ClaimSingleInstance.
        if (!ClaimSingleInstance())
            return;

        // Do not open an audio device for the WinUI control sounds. The app plays none of them. The idle
        // sink would register the app as an audio client, which shows a speaker icon on the taskbar, as if
        // the app were playing.
        ElementSoundPlayer.State = ElementSoundPlayerState.Off;

        // The ten accents are read HERE, on the UI thread, before any library is available to draw from.
        // The read needs a resource scope. Uno keeps one scope per thread, so the read works only on this
        // thread. Nearly everything that asks for a palette colour is a worker thread drawing a picture. If
        // the first asker did the read, it could be a background thread making a placeholder sleeve. The
        // read would then throw, and the empty list it fell back to would become the app's palette until
        // the app was restarted. See RpmPalette.Prime.
        Presentation.Common.RpmPalette.Prime();

        // Background work that nobody waits on must not take the app down when the garbage collector
        // finally sees it. Examples are a fire-and-forget send to a speaker that has gone, or a probe that
        // was abandoned in mid-flight.
        //
        // This does NOT cover every case. For example, the Sharpcaster heartbeat ping into a dead socket
        // throws from an `async void` timer handler. An exception from async void is raised on the thread
        // pool, and it is not stored on a task. So it never becomes an unobserved task exception, and no
        // handler here can see it. The only defence against that is to close the connection as soon as the
        // speaker has gone. SharpcasterReceiver does that, and it closes on the first sign of trouble.
        //
        // Each exception is observed, and it is written to the ring first. A fault that reaches this point
        // was seen by nothing else. So this line is the only record of it in a Release build. It is inside
        // a try, because this runs on the finalizer thread, where a throw ends the process.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { RPM.Core.Diagnostics.Faults.Note("unobserved", "a task nobody awaited", e.Exception); } catch { }
            e.SetObserved();
        };

        // `--gallery`: the design harness. It runs the whole app against a seeded demo library. Services
        // that would need a network or a sign-in are replaced by mocks. This lets every screen be opened
        // and used with something to look at. DEBUG only. It is read here so the service registrations
        // below can swap in the mocks. It is declared inside the DEBUG block, because every use is inside
        // that block. So a Release build has no unused variable.
#if DEBUG
        var gallery = Environment.GetCommandLineArgs().Contains("--gallery");

        // `--gallery-wall`: the harness opened on a thousand albums, and not seven. It is used for
        // measuring start-up at a size that matters. What the shell does before the window can be shown
        // scales with the size of the collection. A launch with seven records shows none of that.
        var galleryWall = Environment.GetCommandLineArgs().Contains("--gallery-wall");
        gallery |= galleryWall;

        // `--gallery-shelf=<covers folder>`: the harness opened on a shelf of real sleeves. The catalogue
        // is invented, and it is written to fit those sleeves. It is used for photographing the interface
        // (see GalleryShelfLibrary). `--gallery-portraits=<folder>` adds photographs for the invented
        // artists. Without it, the header uses the record's own colours.
        var galleryShelf = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--gallery-shelf=", StringComparison.Ordinal))?["--gallery-shelf=".Length..];
        var galleryPortraits = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--gallery-portraits=", StringComparison.Ordinal))?["--gallery-portraits=".Length..];
        gallery |= galleryShelf is not null;

        // `--shoot=<vizId>[:<view>]`: on top of the harness, shows that visualizer full screen, with a
        // recorded clip playing under it. It is used for the gallery card stills. See GalleryShoot.
        Presentation.Gallery.GalleryShoot.Wanted = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--shoot=", StringComparison.Ordinal))?["--shoot=".Length..];

        // `--shoot-cover=<path>`: the record that the shoot is lit by. A stage that paints itself in the
        // cover's colours makes a different picture for each cover. So the cover is part of the shot, and
        // not a detail of it. See GalleryShoot.Material.
        Presentation.Gallery.GalleryShoot.Cover = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith("--shoot-cover=", StringComparison.Ordinal))?["--shoot-cover=".Length..];

        // `--place-genres`: a tool that places each genre's shipped photograph in its own head, by hand, in
        // a window beside the app. It edits only scripts/genre-photos.json, in the source tree that this
        // build was made from. See GenrePlacerPage. DEBUG only. If there is no source tree above the
        // binary, it says so, and does not fail quietly.
        var placeGenres = Environment.GetCommandLineArgs().Contains("--place-genres");
#endif

        // Decides once, from the command line, whose sign-in this run may read. A design harness run
        // (`--gallery` and its variants) reads none. Its store is in memory and empty, so nothing that acts
        // for an account can act on the demo library. The gallery is started with this same value below,
        // and it cannot start without it. See SessionHome.
        var sessionHome = SessionHome.For(Environment.GetCommandLineArgs());

        var builder = this.CreateBuilder(args)
            .Configure(host => host
                #if DEBUG
                .UseEnvironment(Environments.Development)
                #endif
                .UseLogging(configure: (context, logBuilder) =>
                    logBuilder
                        // Warnings only, for everyone, development included. Debug output is where the
                        // framework and its dependencies write their housekeeping. For example, the Lottie
                        // player writes its version, duration and frame rate each time an animation loads.
                        // None of that is about this app. A console that scrolls by itself is one that
                        // nobody reads, so the useful lines get lost.
                        .SetMinimumLevel(LogLevel.Warning)
                        .CoreLogLevel(LogLevel.Warning)
                        // Our own categories keep their detail during a development run. What is silenced
                        // above is other people's logging, not ours.
                        .AddFilter("RPM", context.HostingEnvironment.IsDevelopment()
                            ? LogLevel.Debug
                            : LogLevel.Warning)
                        // The press on "generate a new list" has its own level in every build. A release
                        // run is where this is reported from. The line that says the press SUCCEEDED is as
                        // useful as the line that says it failed. Without it, a filtered-out success and a
                        // press that never fired look the same.
                        .AddFilter("RPM.Generate", LogLevel.Information)
                        // Shows the GLCanvasElement and ANGLE init failure reason inside the app
                        // (GlDiagnostics). The visualizer readout can then show it. No one needs to read
                        // the console, and there is no flood of messages each frame.
                        .AddFilter("Uno.WinUI.Graphics3DGL", LogLevel.Trace)
                        .AddProvider(new GlDiagnosticsLoggerProvider())
                        // One line per message, with the tag first and no namespace in front of it. This is
                        // the same shape as the Debug.WriteLine tracing used elsewhere. See
                        // RpmConsoleFormatter.
                        .AddConsoleFormatter<RpmConsoleFormatter, ConsoleFormatterOptions>()
                        .AddConsole(options => options.FormatterName = RpmConsoleFormatter.FormatterName),
                    enableUnoLogging: true)
                // The second copy of every line, in the format that the framework fixes, is removed.
                .ConfigureServices((_, services) => services.WithoutDebugLogProvider())
                .UseConfiguration(configure: configBuilder =>
                    configBuilder
                        .EmbeddedSource<App>()
                        .Section<Models.AppConfig>()
                )
                .UseLocalization()
                .ConfigureServices((context, services) =>
                {
                    var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
                    services
                        .AddSingleton(dispatcherQueue)
                        // What the drawing thread is doing. It is measured once, and three things read it:
                        // the ring, the splash and the governor. See UiPulse for why there is one watcher
                        // and not three.
                        .AddSingleton<RPM.Desktop.Services.Shell.UiPulse>()
                        // What that reading is used for: whether a background pass may run right now. Every
                        // sweep in this app asks it before each unit of work.
                        .AddSingleton<RPM.Desktop.Services.Shell.Governor>()
                        // The other half of the question that the splash asks. A free thread is not a
                        // finished screen. It is written by whatever lands first, and read by the reveal
                        // gate.
                        .AddSingleton<RPM.Desktop.Services.Shell.FirstScreen>()
                        .AddSingleton<NavigationService>()
                        .AddSingleton<FolderPickerService>()
                        .AddSingleton<SettingsService>()
                        .AddSingleton<SessionService>()
                        .AddSingleton<LibraryService>()
                        .AddSingleton<InboxWatchService>()
                        .AddSingleton<PlaylistCoverSweepService>()
                        .AddSingleton<LyricsService>()
                        .AddSingleton<LyricAlignment>()
                        .AddSingleton<LyricTimingShare>()
                        .AddSingleton<LyricSheetShare>()
                        // The keychain, DPAPI or the sealed file. This is decided once, from what this
                        // build and this system can use. For a harness run, it is a store in memory. See
                        // SessionStore.For.
                        .AddSingleton(_ => new AuthService(SessionStore.For(sessionHome)))
                        // Whether anything may go through the RPM API at all. This is decided in one place,
                        // so that a window and a background pass cannot disagree about whether this user
                        // may look up records.
                        .AddSingleton<AccountGate>()
                        .AddSingleton<SupporterService>()
                        // The next version of the app: whether there is one, and handing the swap to
                        // Velopack's updater when the app quits. A copy that was not installed by the
                        // installer never reads the feed. See AppUpdates.Managed.
                        .AddSingleton<RPM.Desktop.Services.Updates.AppUpdates>()
                        // Crashed runs since the last launch, and sending them when the user agreed.
                        .AddSingleton<RPM.Desktop.Services.Diagnostics.CrashReportService>()
                        .AddSingleton<IdentificationProvider>()
                        .AddSingleton(sp => CreateCloudClient(sp))
                        .AddSingleton(sp => CreateCommunityClient(sp))
                        // The cloud's work outlives whatever started it. For example, a right-click "add
                        // album to cloud" has no window at all. So the work reports to a job board that the
                        // deck's chip watches. This is the same shape as the rip.
                        .AddSingleton<CloudJobService>()
                        // The other half of the cloud: what the user's OTHER installs put there becomes
                        // rows in this library. So every machine shows the whole collection. It is resolved
                        // eagerly at startup (below). It has no callers, only a clock and two
                        // subscriptions.
                        .AddSingleton<CloudMirrorService>()
                        .AddSingleton<CloudSyncService>()
                        .AddSingleton<RPM.Desktop.Services.Account.AccountDeletion>()
                        // What a track is played from when its bytes are not on this disk: the signed URL,
                        // and the opening seconds to start on while it opens.
                        .AddSingleton<CloudStreamService>()
                        // The sleeve of a record that is only in the cloud. It has no folder here to read a
                        // cover.jpg from, and no file to read an embedded picture from.
                        .AddSingleton<CloudArtService>()
                        .AddSingleton<PlaybackService>()
                        // Library sharing over the local network: this machine's key, the computers
                        // it shares with, the beacon that finds them, and the service that ties the
                        // three to the library and the player.
                        .AddSingleton<RPM.Desktop.Services.Sharing.PeerBook>()
                        .AddSingleton<RPM.Desktop.Services.Sharing.PeerLibraryStore>()
                        .AddSingleton<RPM.Desktop.Services.Sharing.PeerDiscovery>()
                        .AddSingleton<RPM.Desktop.Services.Sharing.SharingService>()
                        // Who those computers belong to. It is kept apart from the sharing itself, because
                        // it is a fact held by the API and not by the machine on the network. Also, nothing
                        // about sharing waits for it.
                        .AddSingleton(services => new RPM.Desktop.Services.Sharing.PeerFaces(
                            services.GetService<RPM.Core.Cloud.RpmCommunityClient>(),
                            services.GetService<RPM.Desktop.Services.Shell.SettingsService>(),
                            services.GetService<RPM.Desktop.Services.Account.AuthService>()))
                        // Works out the gain each record plays at, from the library's measurements and from
                        // what the listener has said about the record. It is a singleton, because it caches
                        // per album, and a record is asked about one track at a time.
                        .AddSingleton<NormalizationService>()
                        // Measures the library in the background, so that one record plays at the level of
                        // the last one. It is resolved eagerly at startup (below). Like the fingerprint
                        // worker, it has no callers, only subscriptions.
                        .AddSingleton<LoudnessService>()
                        // The app's side of the shared pool: what the wider pool already knows about a
                        // record, and what this machine has to tell it. It is asked before a file is
                        // opened.
                        .AddSingleton<RecordingPoolClient>()
                        // The equaliser, and which speakers each curve belongs to. It is a singleton,
                        // because the player skin and the Sound settings page are two views of the same
                        // controls. Also, the curve has to follow the audio between devices, even when
                        // neither of those views is open.
                        .AddSingleton<EqService>()
                        // Watches the player and writes what was actually listened to into the mirror. It
                        // is resolved eagerly at startup (below). It has no callers, only a subscription.
                        .AddSingleton<PlayCounter>()
                        // Where the beat model runs: on this machine, or in the cloud. It is measured once
                        // at startup. Everything that reads a record asks it first.
                        .AddSingleton<DjModelService>()
                        .AddSingleton<CloudBeatModel>()
                        .AddSingleton<TrackAnalysisService>()
                        // Puts what the analyser measured (the tempo and the key) into the files it
                        // measured. It is resolved eagerly at startup (below). Its whole job is a
                        // subscription to the analyser. The settings page is the only thing that ever asks
                        // it for anything.
                        .AddSingleton<AnalysisTagService>()
                        // Reports which names this machine has found to stand for one record. This is a
                        // join that nothing else can make. It is resolved eagerly at startup (below). Like
                        // the tag writer, it is a subscription to the analyser, and nothing asks it for
                        // anything.
                        .AddSingleton<RecordingKeyService>()
                        // Fingerprints the library in the background, and reports to the recognition
                        // service. It is resolved eagerly at startup (below). Like the play counter, it has
                        // no callers, only subscriptions.
                        .AddSingleton<FingerprintService>()
                        .AddSingleton<DjService>()
                        .AddSingleton<RadioService>()
                        // Watches the player and records how much of each station has been listened to. It
                        // is resolved eagerly at startup (below), like the play counter beside it. It has
                        // no callers, only subscriptions.
                        .AddSingleton<RadioCounter>()
                        .AddSingleton<FanartService>()
                        .AddSingleton<ArtworkService>()
                        .AddSingleton<DiscogsService>()
                        .AddSingleton<PopularityService>()
                        .AddSingleton<Services.Catalog.PopularitySweepService>()
                        .AddSingleton<Services.Catalog.DiscogsStyleSweepService>()
                        .AddSingleton<Services.Cloud.MoodListPushService>()
                        .AddSingleton<PlayReportService>()
                        .AddSingleton<RPM.Core.Metadata.Providers.IRpmMetadataApi, RpmMetadataApi>()
                        .AddSingleton<RPM.Core.Metadata.Providers.ICoverSearchApi, RpmCoverSearchApi>()
                        .AddSingleton<CatalogService>()
                        .AddSingleton<TrackInfoService>()
                        .AddSingleton<SaliencyService>()
                        .AddSingleton<FaceService>()
                        .AddSingleton<BackdropPreselector>()
                        .AddSingleton<RPaMpService>()
                        // The app on the SYSTEM's transport controls: the media keys, the headphone
                        // buttons, and the Now Playing tile of the operating system. It is attached once
                        // the window is up (below).
                        .AddSingleton<NowPlayingService>()
                        // Which of the app's three views is shown. There is one owner, because a view
                        // cannot hide itself and show another one without knowing about the others.
                        .AddSingleton<ViewModeService>()
                        // A rip in progress belongs to the app, not to the popup that started it. The popup
                        // can be closed, and the disc keeps ripping onto the deck's chip.
                        .AddSingleton<RipJobService>()
                        // Notices a disc going into the drive, and asks on the deck whether to rip it. It
                        // is resolved eagerly at startup (below). It has no callers, only a timer.
                        .AddSingleton<DiscWatcher>()
                        // Registers RPM as the app that the machine opens when a music CD goes in. Only the
                        // settings page talks to it.
                        .AddSingleton<DiscAutoPlayService>()
                        // Watches the calendar for one minute a year. It is resolved eagerly at startup
                        // (below). Like the disc watcher, it has no callers, only a timer.
                        .AddSingleton<NewYearHerald>()
                        .AddTransient<ShellModel>();

                    // The rip screens need a disc in the drive before they can appear. Under the harness, a
                    // fake reader keeps one there (a demo CD and DVD), so those screens can be seen.
                    // Otherwise it is the real reader, chosen for the platform.
#if DEBUG
                    if (gallery)
                        services.AddSingleton<RipperService, Presentation.Gallery.GalleryRipperService>();
                    else
                        services.AddSingleton<RipperService>();
#else
                    services.AddSingleton<RipperService>();
#endif
                })
            );
        MainWindow = builder.Window;
        MainWindow.Title = "rpm";

        // When a layout pass is about to be abandoned as a cycle, Uno logs every InvalidateMeasure and
        // InvalidateArrange made during its last 10 iterations, at Warning level, naming each element.
        // Nothing is logged or measured unless a cycle is already happening. The bare "Layout cycle
        // detected" error that Uno throws otherwise does not say which element is causing it.
        DebugSettings.LayoutCycleTracingLevel = LayoutCycleTracingLevel.High;

        // One line, written once the window has had time to pick a renderer. Whether it is the GPU or the
        // CPU rasteriser shows the difference between "this app is heavy" and "this machine has no working
        // GPU driver".
        RenderTuning.LogBackendOnce();

        // Is the app complete? Nothing is fetched or built on the user's machine. The installer either laid
        // the payload down or it did not. So a missing tool or model means a broken install. Without this
        // check, each of these problems would show up much later, looking like something else. A track with no
        // length meant ffmpeg was missing. An album that never got a beat grid meant the model was missing.
        // Identification that never matched meant fpcalc was missing. None of these looked like an
        // incomplete install, which is what they all were.
        //
        // This runs before the host is built. So a broken install costs a few stat() calls, and not a
        // half-started app: no library is opened, no sync is begun, and no window is shown.
        if (!TryPreflight())
            return;

        // Opens the Uno Studio/RemoteControl channel, so that the App MCP (mcp__uno-app__*) can take
        // screenshots and inspect the live app. DEBUG only. The value false gives a clean window, with no
        // hot reload badge.
        #if DEBUG
        MainWindow.UseStudio(showHotReloadIndicator: false);
        #endif

        // App-wide guard against stray tooltips. It cancels the tooltip that Uno opens when a ContentDialog
        // gives focus in code to a child that has a tooltip.
        TooltipGuard.InstallGlobal();

        // macOS: stop AppKit from ringing the bell on every Backspace, Return and arrow key typed into a
        // field.
        if (OperatingSystem.IsMacOS())
            RPM.Desktop.Services.Platform.MacKeyBeep.Silence();

        // macOS: a run that is not bundled has no icon of its own. So the Dock would show whatever
        // LaunchServices makes of the executable file. This gives it the app icon instead.
        if (OperatingSystem.IsMacOS())
            RPM.Desktop.Services.Platform.MacDockIcon.Apply();

        // Windows: give the window its own title-bar / taskbar / alt-tab icon.
        if (OperatingSystem.IsWindows())
            TrySetWindowIcon(MainWindow);

        // Plain Frame navigation. The shell owns the Frame and navigates to
        // MainPage or to the setup wizard once it is loaded, on Opening.Choose's answer; each page
        // builds its own view model from the navigation parameter.
        Host = builder.Build();
        RPM.Core.Diagnostics.Scope.Said("boot", "host built");

#if DEBUG
        // Seed the throwaway demo library before the shell opens it, and turn on the harness so the
        // library sync stays off (the demo rows have no files behind them). The main window lands on
        // the seeded workbench; the gallery index opens beside it in its own window (below).
        if (sessionHome is SessionHome.Harness harnessRun)
        {
            if (galleryWall)
                Presentation.Gallery.GalleryBootstrap.PrepareWall(harnessRun);
            else if (galleryShelf is not null)
                Presentation.Gallery.GalleryBootstrap.PrepareShelf(harnessRun, galleryShelf, artistsDir: galleryPortraits);
            else if (gallery)
                Presentation.Gallery.GalleryBootstrap.Prepare(harnessRun);
        }
#endif

        // Restore the saved RPM sign-in and the local settings (no network access). This runs BEFORE any UI
        // is built. The shell reads the settings in its constructor, for example the art drawer's
        // remembered position. If the Load ran after the shell, the first session would use the defaults
        // instead.
#if DEBUG
        // The harness runs signed out. Every path that sends data to the account (generated lists, play
        // reports, cloud sync) asks for a sign-in first. Leaving the saved sign-in unread keeps the
        // invented library out of the user's account. If it were read, the demo shelf's generated lists
        // would be sent to the user's account in place of their own.
        if (!gallery)
#endif
            Host.Services.GetRequiredService<AuthService>().Load();
        var settings = Host.Services.GetRequiredService<SettingsService>();
        settings.Load();

        // The saved interface state: the pane, the search, the shelf's scroll position, and the queue. It
        // is read here for the same reason as the settings. The shell is built on the next line, and every
        // reader of the session comes after it. None of this saved state is trusted. See SessionRestore.
        Host.Services.GetRequiredService<SessionService>().Load();
        RPM.Core.Diagnostics.Scope.Said("boot", "settings and session read");

        // This runs before the first sink is built. So the first track opens on the chosen speakers, and
        // not on the machine's default speakers, which it would then leave a moment later.
        OpenAlSink.PreferredDevice = settings.AudioDevice;

        // The receiver that a cast launches. An empty value is a working state. The default receiver plays
        // the music, and only the next button on the speaker is missing. So this is a setting and not a
        // constant. A build with no registered receiver still casts, using the default receiver.
        CastConnection.ReceiverAppId =
            RPM.Core.Diagnostics.DevSwitch.Read("RPM_CAST_APP_ID")
            ?? Host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Models.AppConfig>>()
                   .Value.CastReceiverAppId;

        // ── the splash, and the window that is not behind it ────────────────────────────────────
        //
        // The startup shows a small glass card in the middle of the screen (see SplashWindow). It is not
        // this window painted black. There are two calls. They must run in this order, and only together.
        // First, the main window is hidden from the screen. The card is raised only if that worked. A
        // hidden window with no card over it is an app that has started and cannot be seen. If the card
        // does not open, the window comes straight back. The startup then works as before, with the black
        // overlay inside the shell.
        //
        // This runs before the shell is constructed, which takes about 590 ms of XAML work on this thread.
        // On Windows, the card is on screen during that time too. The shell is built only once the card has
        // been drawn (see the wait before `new ShellPage`). Without that wait, the card was not on screen
        // during it.
        //
        // Not under the development harnesses. `--rpamp` replaces the shell with the skin and never reveals
        // it, so nothing would bring the window back. `--gallery-wall` measures start-up, and a second
        // window's layout would be part of what it measured.
        //
        // ON macOS THE TWO CALLS RUN IN THE OTHER ORDER, and this is required. Uno creates the NSWindow
        // when a window is first shown, not when it is constructed. So there is nothing to hide yet. If it
        // were asked now, the cloak would answer false, and the card would never be raised. So the card
        // goes up first. It is the app's first window, which is fine, because it should be in front anyway.
        // The app's own window is hidden in the same turn as its Activate, below. The cost is one ordering
        // to get right. The benefit is the same startup.
        var harness = false;
#if DEBUG
        harness = gallery || galleryWall || placeGenres
                  || Environment.GetCommandLineArgs().Contains("--rpamp");
#endif
        if (!harness && OperatingSystem.IsMacOS())
        {
            Presentation.Startup.SplashWindow.Show();
        }
        else if (!harness
                 && RPM.Desktop.Services.Platform.WindowCloak.Apply(MainWindow, cloaked: true)
                 && !Presentation.Startup.SplashWindow.Show())
        {
            RPM.Desktop.Services.Platform.WindowCloak.Apply(MainWindow, cloaked: false);
        }

        // The promise that the cloak makes: the app is on screen whatever happens to the start-up.
        //
        // Every normal route out of the splash goes through the reveal. The reveal has its own time limits,
        // but those limits apply to the GATE. The gate is only armed if step two of the opening returns. If
        // the library open throws, or the shell is never loaded, the window stays hidden with nothing on
        // screen. That would be an app that the user cannot even close. This backstop waits thirty seconds.
        // That is later than the gate's own backstop (20 seconds from launch), so it fires only when the
        // start-up did not finish.
        //
        // This runs on a POOL THREAD, and that is the whole reason. The backstop must not depend on the
        // thread that might fail. It was once a dispatcher timer, and that failed. A SqliteException during
        // the shell's construction stopped the interface thread, and the backstop was queued on that same
        // thread. The card then sat on the screen with no window behind it, for as long as the process
        // lived. A promise is only as good as what it does not depend on.
        //
        // Both halves are safe to call from here. The cloak is a DWM call on a handle, and the card's alpha
        // is a user32 call on a handle. Neither goes through the interface thread. The card's own Close
        // does go through it, and it may never run. A card with zero alpha is out of the way either way.
        if (Presentation.Startup.SplashWindow.IsUp)
        {
            _splashBackstop = new System.Threading.Timer(
                _ =>
                {
                    if (!Presentation.Startup.SplashWindow.IsUp || MainWindow is not { } waiting)
                        return;

                    RPM.Core.Diagnostics.Scope.Said("boot", "splash backstop: the startup never revealed");

                    // On macOS THIS RULE IS BROKEN, because AppKit has a rule of its own. Every message to
                    // an NSWindow must be sent on the main thread. Both halves of this work are messages to
                    // an NSWindow. So on that head, the work goes back to the interface thread. That is the
                    // same thread this timer exists to work around.
                    //
                    // This is not the same trade as it looks. On Windows, a dead interface thread still
                    // leaves a usable desktop. The compositor draws the window, and the user can move it,
                    // close it and read it. On macOS the interface thread IS the main thread. So a dead one
                    // leaves an app whose menu, buttons and Quit have all stopped. It shows a beachball,
                    // whatever this timer does with the alpha. The hop helps in one case: a start-up that
                    // is only SLOW still recovers. A broken start-up is not made worse by an AppKit call
                    // from a pool thread.
                    if (OperatingSystem.IsMacOS())
                    {
                        waiting.DispatcherQueue?.TryEnqueue(() =>
                        {
                            RPM.Desktop.Services.Platform.WindowCloak.Apply(waiting, cloaked: false);
                            Presentation.Startup.SplashWindow.Dismiss();
                        });
                        return;
                    }

                    RPM.Desktop.Services.Platform.WindowCloak.Apply(waiting, cloaked: false);
                    Presentation.Startup.SplashWindow.Dismiss();
                },
                null,
                SplashBackstop,
                System.Threading.Timeout.InfiniteTimeSpan);
        }

        // The card is drawn before the shell's XAML takes this thread. The card's first frames need this
        // thread. Without this wait, the card's fade-in waited until the shell was built and OnLaunched had
        // returned. On Windows, the wait brought the card's fade-in down from about 1.3 to 1.8 seconds to
        // about 0.7 to 0.8 seconds. The shell, the first screen and the reveal came no later. Windows only:
        // on macOS the card is raised in a different order (above), and the wait has not been measured
        // there.
        if (Presentation.Startup.SplashWindow.IsUp && OperatingSystem.IsWindows())
            await Presentation.Startup.SplashWindow.Drawn;

        MainWindow.Content = new ShellPage
        {
            DataContext = Host.Services.GetRequiredService<ShellModel>(),
        };
        RPM.Core.Diagnostics.Scope.Said("boot", "shell constructed");

        // Where the window opens: the size and place it was closed at. On a first run, or when the stored
        // frame no longer fits the screens that are here, it is maximised instead. The work area is used,
        // with the title bar kept. It is NOT macOS full screen.
        //
        // It is not applied now, though. This runs when the splash has gone and the interface is on screen.
        // Until then, the window is under a black rectangle with a logo on it. A window that jumped to
        // another size and place behind that would look as if the app were struggling to start. The handler
        // is subscribed before the window is activated, because the page starts its own reveal from Loaded.
        ShellPage.Revealed += RestorePlacement;

        // This is the one measurement of the drawing thread. It starts here, because the input half needs
        // content to attach to. A watchdog that measures an empty window measures nothing. It always runs.
        // The ring can be switched off, but the governor still has to know what the interface is doing.
        var pulse = Host.Services.GetRequiredService<RPM.Desktop.Services.Shell.UiPulse>();
        pulse.Start();

        // It is built now, and not on first use. It subscribes to the pulse in its constructor. A governor
        // that nobody has asked for would not advance any waves.
        Host.Services.GetRequiredService<RPM.Desktop.Services.Shell.Governor>();

        // These are the two tracks that everything else in the ring is read against. They record when the
        // drawing thread was unavailable, and when the user acted. Both are written from what the pulse
        // reports.
        RPM.Desktop.Services.Diagnostics.ScopeShell.Install(MainWindow, pulse);
        RPM.Desktop.Services.Diagnostics.FrameRateDiag.Install();
        RPM.Desktop.Services.Shell.WindowSight.Watch(MainWindow);

        // On every size change, this checks three sizes that should all be the same. They are the client
        // area that Win32 reports, the window size that XAML reports, and the size the content is laid out
        // at. It settles whether a black window with the interface in its corner is our layout or the
        // renderer's surface. See WindowTrace.
        RPM.Desktop.Services.Diagnostics.WindowTrace.Install(MainWindow);

        // Do not try to repair a broken renderer surface with an ordinary resize. A resize that happens
        // without the fault does not show that the resize repairs it. Once the surface is wrong, an
        // ordinary resize does not rebuild it.

        RPM.Core.Diagnostics.Scope.Said("boot", "activate");
        MainWindow.Activate();
        RPM.Core.Diagnostics.Scope.Said("boot", "activated");

        // ── macOS: the window exists now, so this is the first moment it can be hidden ──────────
        //
        // This must happen IN THIS TURN, and not on a later dispatcher tick. Activate creates the NSWindow
        // and orders it in. The window server composites on the next pass through the run loop. So an alpha
        // set here is applied before the app's first frame reaches the screen, and nothing of the window is
        // seen. A tick later is one composite later. The result would be the black window with the splash
        // overlay on it, visible for a frame behind the card.
        //
        // If the window cannot be hidden, the card must go. A small window floating over a visible app is
        // not a start-up. It is a defect. This is the same trade that the Windows path makes in the other
        // direction, where the cloak is tried first.
        if (Presentation.Startup.SplashWindow.IsUp
            && OperatingSystem.IsMacOS()
            && !RPM.Desktop.Services.Platform.WindowCloak.Apply(MainWindow, cloaked: true))
        {
            RPM.Core.Diagnostics.Scope.Said("boot", "splash closed: the window would not hide");
            Presentation.Startup.SplashWindow.Close();
        }

        // Linux: remove the native title bar ourselves. The watch starts now, so that it usually wins the
        // race with the window manager's first paint. The presenter call in WindowPlacement only clears the
        // Motif TITLE bit. KWin treats any non-zero decoration mask as "decorated". See LinuxWindowChrome,
        // which writes the full zero. The two writes converge, whichever one lands first.
        if (OperatingSystem.IsLinux())
            RPM.Desktop.Services.Platform.LinuxWindowChrome.DropTitleBar();

        // macOS: extend the content up under the title bar, so that the artwork header runs behind the
        // traffic lights. This happens after a dispatcher tick, because the NSWindow has to exist first.
        // The player's transparent-chrome pass uses the same timing.
        if (OperatingSystem.IsMacOS())
        {
            var dispatcher = Host.Services.GetRequiredService<DispatcherQueue>();
            dispatcher.TryEnqueue(() =>
            {
                // This is checked again inside the lambda, and not only outside it. The platform guard does
                // not carry into a closure, so the analyzer cannot see the check above.
                if (!OperatingSystem.IsMacOS()) return;
                var nsWindow = MacWindowChrome.FindWindowHandle("rpm");
                try { MacWindowChrome.ExtendContentUnderTitlebar(nsWindow); } catch { }

                // This also makes the window a drop target. On this head, the app must do that itself.
                // Uno's mac runtime implements no drag destination at all. So the XAML Drop event that the
                // Windows head raises never fires here. See MacFolderDrop.
                try { RPM.Desktop.Services.Platform.MacFolderDrop.Enable(MacWindowChrome.FindWindowHandle("rpm")); }
                catch { }
            });
        }

#if DEBUG
        // The gallery is its own window, like Settings. So the main window keeps showing a real screen
        // while the gallery is used to move around. A shoot needs the screen to itself, so the index stays
        // shut during one. Otherwise it would sit over the picture being photographed. Not for
        // `--gallery-wall`: a second window opening during start-up would add its own layout to the thing
        // being measured.
        if (gallery && !galleryWall && Presentation.Gallery.GalleryShoot.Wanted is null)
            Presentation.Gallery.GalleryWindow.OpenOrFocus();

        if (gallery)
            Presentation.Gallery.GalleryShoot.Fire();

        if (placeGenres)
            Presentation.Dev.GenrePlacerWindow.OpenOrFocus();
#endif

        // This is the one network question that the rest of the app asks, instead of finding out for
        // itself. It is built here, so that it is measuring before the first pass needs an answer. It also
        // means the band under the library has something to say when the window appears. See NetworkReach.
        var reach = RPM.Core.Net.NetworkReach.Shared;

        // Picks up a cloud queue that the last run left half finished. It does nothing unless the user has
        // turned the cloud on and is signed in.
        RPM.Desktop.Services.Shell.Background.Run(() => Host.Services.GetRequiredService<CloudSyncService>().KickAsync());

        // This is the other half of the gating for those passes. They stop when there is no network, so
        // something has to wake them when there is one. Each of these re-reads its own state from the
        // database. So a kick that arrives at a bad moment finds nothing to do. It does not do the wrong
        // thing.
        reach.Changed += changed =>
        {
            if (changed != RPM.Core.Net.Reach.Online || Host is not { } host)
                return;

            // The work runs on the pool, for all three. This handler is raised from inside NetworkReach's
            // own measurement. On the "no interface is up" path, that measurement runs to completion
            // synchronously while it holds the lock that every request in the app asks AllowAsync through.
            // So resolving three services from the container here would hold that lock. Starting their work
            // here would also put it on whichever thread had made the measurement.
            RPM.Desktop.Services.Shell.Background.Run(() => host.Services.GetRequiredService<CloudSyncService>().KickAsync());
            RPM.Desktop.Services.Shell.Background.Run(() => host.Services.GetRequiredService<FingerprintService>().KickAsync());
            RPM.Desktop.Services.Shell.Background.Run(() => host.Services.GetRequiredService<CloudMirrorService>().RefreshAsync(force: true));
        };

        // Reads the user's cloud catalogue into the library, so this machine shows the records that their
        // other installs hold, as well as its own. It has the same shape as the counter below: it has no
        // callers.
        Host.Services.GetRequiredService<CloudMirrorService>();

        // Nothing asks the play counter for anything. It watches the player and writes to the mirror. So it
        // has to be built, and not waited for.
        Host.Services.GetRequiredService<PlayCounter>();

        // The radio counter has the same shape. It watches the player and writes to the station log, and
        // nothing asks it for anything.
        Host.Services.GetRequiredService<RadioCounter>();

        // The fingerprint worker has the same shape. It waits for the library to open
        // (LibraryService.Changed), and works from there. So it only needs to exist.
        Host.Services.GetRequiredService<FingerprintService>();

        // The loudness pass also waits for the library to open, and then works from there.
        Host.Services.GetRequiredService<LoudnessService>();

        // It is built here for the same reason as the loudness pass. It subscribes to the library opening
        // in its constructor. A service that nobody has asked for has not subscribed to anything.
        Host.Services.GetRequiredService<Services.Catalog.PopularitySweepService>();

        // The pass that reads the styles from albums already linked to a Discogs release works the same
        // way. The styles are the words that MoodFlavour narrows a list with.
        Host.Services.GetRequiredService<Services.Catalog.DiscogsStyleSweepService>();
        // It is constructed for its subscriptions. It waits for a library and a sign-in. Then it sends this
        // computer's generated lists, so that the phone shows the same lists.
        Host.Services.GetRequiredService<Services.Cloud.MoodListPushService>();
        // It is constructed for its subscription. It moves a fanart.tv key, which an older build left in
        // the settings file, onto the signed-in account. The server can then send that key for this user
        // from any device.
        Host.Services.GetRequiredService<FanartService>();

        // The tag writer listens for tracks that finish analysis, and writes what was measured into them.
        // Nothing calls it, so it has to be built.
        Host.Services.GetRequiredService<AnalysisTagService>();

        // The playlist cover sweep has the same shape. It does its whole job from its own constructor. It
        // removes master images left behind by a tree that was closed, or by a peer that was unpaired.
        // Nothing else calls it. It is built here, and the container's own singleton cache keeps it alive.
        // No reader is expected. Do not store it in a field just to keep it alive. The compiler gives no
        // warning for a field that is assigned in a constructor and never read. So a clean-up pass that
        // removes unused fields could turn the sweep off without any error.
        Host.Services.GetRequiredService<PlaylistCoverSweepService>();

        // It is built here, and not at the first screen that draws a shared library. It holds one thing
        // that the network needs before any screen exists: this machine's own display name. The beacon
        // announces that name, to show who is at this computer. If it were built later, the first
        // announcement of each session would go out without a name, and then be corrected a moment later.
        Host.Services.GetRequiredService<RPM.Desktop.Services.Sharing.PeerFaces>();

        // The analyser is built before anything can be signed in. So it cannot be given the pool in its
        // constructor. The pool is handed over here, once both exist. Until then it is null. That is also
        // the value on a machine with no cloud behind it.
        Host.Services.GetRequiredService<TrackAnalysisService>().Pool =
            Host.Services.GetRequiredService<RecordingPoolClient>();

        // The same applies to the cloud beat model. It stands in for the model weights on a machine that
        // cannot carry them. It is built on the cloud client, and the cloud client is built on a sign-in.
        Host.Services.GetRequiredService<TrackAnalysisService>().CloudBeats =
            Host.Services.GetRequiredService<CloudBeatModel>();

        // The fingerprinter is how a record is named, before the pool is asked whether anyone has read
        // another rip of it. It is built on the recognition backend, which is built on a sign-in. That is
        // the same reason as above.
        Host.Services.GetRequiredService<TrackAnalysisService>().Fingerprints =
            Host.Services.GetRequiredService<RPM.Desktop.Services.Catalog.FingerprintService>();

        // Can this computer run the beat model at all? This is measured once, on a background thread. The
        // real graph is timed, and the process is watched while it runs. So nothing has to guess later. It
        // starts when the governor allows processor work, after the reveal. If it ran from here, it would
        // take the processor cores that the home banner's models need while the splash is up. If a set is
        // started before then, that set runs the test itself. See DjModelService.
        Host.Services.GetRequiredService<DjModelService>().BeginWhenAdmitted(
            Host.Services.GetRequiredService<Services.Shell.Governor>());

        // The key publisher listens for the same thing. It reports what this machine now knows about which
        // names stand for one record. Nothing calls it either.
        Host.Services.GetRequiredService<RecordingKeyService>();

        // The disc watcher polls the optical drive, and puts the rip question on the deck when a disc turns
        // up. It is built here, so that it is already watching if the app was launched BY a disc going in.
        // That is what happens when the app is the machine's default handler.
        Host.Services.GetRequiredService<DiscWatcher>();

        // The new-year greeting is one night a year, when RPM puts a record on by itself. The herald
        // watches the clock for midnight on the first of January. Nothing calls it, and it has to be
        // ALREADY WATCHING before that moment arrives. So it cannot be built on first use.
        //
        // It is OFF. Building it here is the only thing that makes the greeting happen on its own. With
        // this line commented out, the clock is never checked, and the first of January passes like any
        // other night. The service is still REGISTERED, which is the difference between this and removing
        // it. Anything that asks for it by name still gets one. The gallery's rehearsal button does exactly
        // that, and building it there starts its watch from that moment. So the greeting can still be seen
        // on purpose, though it never arrives by itself. Put the line back to restore it.
        ////Host.Services.GetRequiredService<NewYearHerald>();

        // The player is built before the library is open. So it cannot be given the resolver in its
        // constructor. The resolver is handed over here, once both exist.
        Host.Services.GetRequiredService<PlaybackService>().Normalization =
            Host.Services.GetRequiredService<NormalizationService>();

        // A Bluetooth speaker's level IS this machine's output volume (see VolumeRouting). That is a
        // control that the whole machine shares: the volume keys, the tray slider, another app, and the
        // buttons on the speaker itself. Each of those changes is reported to us while the app is running.
        // This check is for the changes that are not reported. For example, a level set while RPM was in
        // the background, an endpoint that came back as a new one, or a notification that the driver never
        // sent. Coming to the front is the moment the slider is about to be looked at. Asking then costs
        // one property read.
        //
        // This goes through AppActivation, and not Window.Activated. Uno never raises Window.Activated on
        // macOS after launch. On macOS, this check would otherwise never run.
        RPM.Desktop.Services.Platform.AppActivation.WhenActivated(MainWindow, () =>
        {
            try { Host?.Services.GetRequiredService<PlaybackService>().SyncVolumeFromSpeakers(); }
            catch { /* a knob is never a reason to fail an activation */ }
        });

        // The other place that a track's bytes can be is another of the user's own computers, on this
        // network. It is built here, and not on first use, because sharing has to be ALREADY LISTENING. A
        // machine that nobody is announcing to cannot be found. Also, the player asks it before the cloud,
        // for every track that is not on this disk.
        var sharing = Host.Services.GetRequiredService<RPM.Desktop.Services.Sharing.SharingService>();
        Host.Services.GetRequiredService<PlaybackService>().Sharing = sharing;

        // …and this account's own art. This is the last place where the cover of a record being cast is
        // looked for. An offloaded album has a sleeve in the store, and no file on this disk to read one
        // from. See PlaybackService.CastCoverAsync.
        Host.Services.GetRequiredService<PlaybackService>().CloudArt =
            Host.Services.GetRequiredService<RPM.Desktop.Services.Cloud.CloudArtService>();

        // The streamer gets the same. Otherwise it has no way of knowing that a track id it is given
        // belongs to somebody else's account, and that it must not ask about it.
        Host.Services.GetRequiredService<CloudStreamService>().Sharing = sharing;

        sharing.RestartSoon();

        // Force dark theme on the root content
        if (MainWindow.Content is FrameworkElement root)
            root.RequestedTheme = ElementTheme.Dark;

        // The media keys, the headphone buttons, and the operating system's Now Playing tile. It is
        // attached here, and not in the container, because it hangs off the deck, and the deck is built
        // with the window.
        Host.Services.GetRequiredService<NowPlayingService>().Attach(MainWindow);

        #if DEBUG
        // Development shortcut: `--rpamp` hosts the RPaMp player AS the main window. It is seeded, and not
        // playing, so its look can be driven and screenshotted with the App MCP while it is being
        // developed.
        if (Environment.GetCommandLineArgs().Contains("--rpamp"))
        {
            var model = new RPaMpModel(
                Host.Services.GetRequiredService<PlaybackService>(),
                Host.Services.GetRequiredService<EqService>(),
                Host.Services.GetRequiredService<DispatcherQueue>(),
                Host.Services.GetRequiredService<RadioService>(),
                Host.Services.GetRequiredService<LibraryService>(),
                Host.Services.GetRequiredService<SettingsService>());
            model.AddTracks(SampleRPaMpTracks(), startPlaying: false);
            var view = new RPaMpView { DataContext = model, HostWindow = MainWindow.AppWindow };
            view.CloseRequested += () => MainWindow?.Close();
            MainWindow.Content = view;
            view.RequestedTheme = ElementTheme.Dark;

            // Uses the same transparent, chromeless treatment as the real player window (RPaMpService). So
            // the harness shows the same see-through gaps between the stacked panels.
            RPM.Desktop.Services.Platform.WindowBackground.Apply(
                MainWindow, new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent));
            try
            {
                if (RPM.Desktop.Services.Platform.WindowPresenter.Overlapped(MainWindow.AppWindow) is { } op)
                {
                    // Not on macOS, for the same reason that SkinWindow does not do it there. On macOS the
                    // switch is made at the style mask, and a window without NSWindowStyleMaskTitled has no
                    // traffic lights, no drag and no resize. The buttons are hidden natively below instead.
                    if (!OperatingSystem.IsMacOS())
                        op.SetBorderAndTitleBar(false, false);
                    op.IsResizable = false;
                    op.IsMaximizable = false;
                }
            }
            catch { }
            if (OperatingSystem.IsMacOS())
            {
                var dispatcher = Host.Services.GetRequiredService<DispatcherQueue>();
                dispatcher.TryEnqueue(() =>
                {
                    if (!OperatingSystem.IsMacOS()) return;   // The guard does not carry into the closure.
                    var win = MacWindowChrome.FindWindowHandle("rpm");
                    if (win == 0) return;
                    try { MacWindowChrome.MakeWindowTransparent(win); } catch { }
                    try { MacWindowChrome.SetTrafficLightsHidden(win, true); } catch { }
                });
            }
            // The window is fitted to the CASING, and not to a fixed size. Otherwise, opening the library
            // would widen the casing inside a window that stays 468 points, and the pane would be cropped
            // away. This calls SkinWindow's own method, and does not copy it. The method handles physical
            // pixels and the frame that the window keeps for itself. A copy here would get neither right.
            void FitToCasing() => SkinWindow.FitTo(MainWindow!, view);

            view.CasingSettled += FitToCasing;
            view.Loaded += (_, _) =>
            {
                SkinWindow.ClearRootBackground(view);
                FitToCasing();
            };
        }

        #endif

        // Closing the main window must silence any preview or RPaMp audio. On macOS, closing a window does
        // not end the process, so ProcessExit does not fire. That would leave ffplay running with no
        // window. The player now lives in this window, so closing the window also ends the player.
        MainWindow.Closed += (_, _) =>
        {
            // This run has begun to end normally. A crash after this point is still reported, except a
            // Windows fail-fast. That is the known crash on close. See RPM.Core.Crashes.CrashHarvest.
            RPM.Core.Diagnostics.Scope.Closing();

            // The splash, if the app was closed during its own start-up. It is a window of its own.
            // Otherwise it would be the last window left, with nothing behind it to close it.
            try { Presentation.Startup.SplashWindow.Close(); } catch { }

            // The opening sound is also stopped. It runs as a child process, and it would otherwise keep
            // playing after the window has gone.
            try { AppSound.Silence(); } catch { }

#if DEBUG
            try { Presentation.Gallery.GalleryWindow.Close(); } catch { }
            try { Presentation.Dev.GenrePlacerWindow.Close(); } catch { }
#endif
#if DEBUG && RPM_SKIA
            try { Presentation.MixLab.MixLabWindow.Close(); } catch { }
#endif
            // The window's own size and place are read while the window still exists. Then the whole
            // session note is written down. Writes are combined while the app runs. So the last second of a
            // session, which says where the window was left, would otherwise still be only in memory when
            // the window goes.
            try
            {
                var session = Host?.Services.GetRequiredService<SessionService>();
                if (session is not null && MainWindow is { } window)
                {
                    WindowPlacement.Remember(window, session);
                    session.Flush();
                }
            }
            catch { }
            // The last few plays of the session are sent on the way out. This is fire and forget. The
            // window is going, and nothing may wait for it. So this is the batch that most often does not
            // get sent. A counter that nobody waits on can afford that. See PlayReportService.
            try { _ = Host?.Services.GetService<PlayReportService>()?.SendAsync(); } catch { }
            // The radio station that is still on is closed before the player is stopped. A session of radio
            // counts as one listen. The sampler that would settle it gets no more ticks after this point.
            try { Host?.Services.GetRequiredService<RadioCounter>().Close(); } catch { }
            // A version that was fetched but not installed is applied now, on the way out. The updater is
            // started. It waits for this process to end, and then swaps the app over without starting it
            // again. The user sees the new version the next time they open RPM, as the window that offered
            // it said would happen. Nothing is downloaded here, because the update is already on disk. A
            // copy with nothing staged does nothing at all.
            try { Host?.Services.GetRequiredService<Services.Updates.AppUpdates>().Apply(restart: false); } catch { }
            try { Host?.Services.GetRequiredService<RPaMpService>().Shutdown(); } catch { }
            try { Host?.Services.GetRequiredService<PlaybackService>().Stop(); } catch { }
            // This stops the passes that hold the library (the cloud sync run and the cloud mirror) and waits
            // for them, for a few seconds at most, before it closes the library. See LibraryService.StopBeforeClose.
            try { Host?.Services.GetRequiredService<LibraryService>().Dispose(); } catch { }

            // The services are disposed here, which is what shuts down the native libraries that they hold.
            //
            // Four of them cache an ONNX InferenceSession for the life of the process: the saliency net,
            // the face detector, the key model and the beat model. Each one already implements IDisposable
            // and disposes its session. But nothing called them. They are container singletons. The
            // container only disposes them when the host is disposed, and nothing did that. So
            // onnxruntime.dll reached DLL_PROCESS_DETACH with live sessions still in it.
            //
            // Without the calls below, every close of the app ended in abort(): 0xc0000409,
            // FAST_FAIL_FATAL_APP_EXIT, raised inside ucrtbase on the MAIN thread, with no managed frames.
            // This happened after "process exit" had already been marked.
            //
            // The services are disposed ONE AT A TIME, and not by disposing the container that holds them.
            // Disposing the whole host does stop the abort. But it replaces it with a different crash: a
            // stowed exception with 0xc000027b, which is what this app fails with when something fails
            // inside XAML. See the note on Platforms/Windows/Program.cs. The container also holds services
            // that are part of the interface. Disposing those while the window is still closing would add a
            // second fault. The three services named below hold a native model, and nothing else that XAML
            // can be in the middle of.
            try { Host?.Services.GetRequiredService<Services.Art.SaliencyService>().Dispose(); } catch { }
            try { Host?.Services.GetRequiredService<Services.Art.FaceService>().Dispose(); } catch { }
            try { Host?.Services.GetRequiredService<Services.Audio.TrackAnalysisService>().Dispose(); } catch { }

            // The player is the other native library in this process. Its engine holds an OpenAL device and
            // a context (soft_oal.dll, through OpenAlSink). Stop above only stops the audio.
            // PlaybackService.Dispose is what closes them.
            try { Host?.Services.GetRequiredService<PlaybackService>().Dispose(); } catch { }

            // Then the app is asked to exit, because closing the window does not always end the process.
            //
            // The comment at the top of this handler says that a window close does not end the process on
            // macOS. On Windows it does not reliably end the process either. The cost is not an idle
            // process. In one case that was observed, the process was still running three and a half
            // minutes after its window had gone. It had no window handle left, 32 threads, 689 MB of
            // memory, and about half a core of processor time, sustained. Its main thread was in
            // `Win32EventLoop.RunOnce`, under `Win32Host.RunLoop`, under `Program.Main`. The window had
            // been destroyed, and the message loop kept pumping, so Main never returned. Such a process
            // keeps running the housekeeping sweeps against the same library file. So restarts stack up,
            // and each new launch is slower than the last.
            //
            // This is logged after the cleanup above, and not instead of it. The session, the placement and
            // the player have already been written down by the time this runs.
            try { Exit(); } catch { }
        };
    }

    /// <summary>How long the card may stay up before the window is brought back, whether or not the startup
    /// revealed. See where it is armed. This is a limit for a start-up that failed, and not for a slow
    /// one.</summary>
    private static readonly TimeSpan SplashBackstop = TimeSpan.FromSeconds(30);

    /// <summary>Held for the life of the process. A timer that nothing refers to can be collected before it
    /// is due. This one is due exactly when nothing else in the app is working.</summary>
    private static System.Threading.Timer? _splashBackstop;

    /// <summary>The number of frames the window must draw, after its size and place have been set, before
    /// it is uncloaked.
    ///
    /// <para>A length of time is not used, because it does not work. With a fixed delay of 64 ms after the
    /// reveal, the window came back showing its PREVIOUS frame. That was the black splash, wordmark and
    /// all, for about 300 ms before the interface appeared. A cloaked window is drawn at a rate that the
    /// compositor chooses. So a number of milliseconds is not a fixed value. Frames are what the wait must
    /// count.</para></summary>
    private const int UncloakFrames = 3;

    /// <summary>The time limit on that wait. A window that appears before its first frame is drawn shows a
    /// blink of the old one. A window that never appears means the app is not usable.</summary>
    private static readonly TimeSpan UncloakBackstop = TimeSpan.FromMilliseconds(600);

    /// <summary> The opening, once the interface is on screen. <see cref="ShellPage.Revealed"/> calls this.
    /// The window takes back the size and place that it was left at, and then the app plays its sound.
    ///
    /// <para>This order is required, and it must not be changed. A window that resizes itself while the
    /// user is looking at it looks like an app that is struggling to start. When there is a splash window,
    /// the rule is stronger. The window stays cloaked until the last line of this method uncloaks it. So
    /// the resize happens on a window that nobody can see, and the app appears once, at the size it was
    /// closed at. The sound plays after the window has settled, so that what is heard matches the
    /// interface, and not the window while it is still changing.</para>
    /// </summary>
    private void RestorePlacement()
    {
        RPM.Core.Diagnostics.Scope.Said("boot", "placement");

        if (MainWindow is not { } window || Host is null)
            return;

        // There are two mechanisms. See WindowPlacement for why. Windows and Linux maximise through the
        // presenter. macOS asks AppKit for the frame that it saved, and that needs the NSWindow and not the
        // managed window. Both run on the interface thread, which is where they must run. The call goes
        // through SkinWindow, which holds it back while the session's player view has the window.
        Presentation.Shell.SkinWindow.PlaceLibrary(window);

        // This call is guarded. A failure here is an abort() inside AppKit, which no catch can see. That is
        // the reason WindowPlacement keeps a record of its own attempts.
        if (OperatingSystem.IsMacOS())
            try { WindowPlacement.RestoreMac(MacWindowChrome.FindWindowHandle("rpm")); } catch { }

        // ── the window comes back, dressed ──────────────────────────────────────────────────────
        //
        // The window has been cloaked since before it was activated. So the size and place set just above
        // were applied to a window that nobody could see, and no frame shows it jumping. It is uncloaked a
        // few frames later, and not on this turn. The resize above is laid out and rendered on the frames
        // after this one. A window shown on this turn would show the size it had before the call.
        //
        // The card goes when the window is up, and not before. It dissolves over the app, and not off a
        // bare desktop. It is the only thing fading here. The shell skips its own reveal while the splash
        // window is up, because nobody is watching the inside of a cloaked window.
        if (Presentation.Startup.SplashWindow.IsUp)
        {
            RPM.Desktop.Services.Shell.NextFrames.After(
                window.DispatcherQueue, UncloakFrames, UncloakBackstop, drawn =>
                {
                    RPM.Core.Diagnostics.Scope.Said(
                        "boot", $"uncloak ({(drawn ? "drawn" : "backstop")})");
                    RPM.Desktop.Services.Platform.WindowCloak.Apply(window, cloaked: false);

                    // Bring the window to the front. Uncloaking does not do this on its own. The window was
                    // activated at launch and has been hidden ever since. So it is still wherever the stack
                    // left it, and whatever the user has been looking at may be over it. The old black
                    // window was on screen and in front the whole time. This keeps that behaviour, and does
                    // not add a new one. See Raise. Window.Activate does nothing here.
                    RPM.Desktop.Services.Platform.WindowCloak.Raise(window);

                    Presentation.Startup.SplashWindow.Dismiss();
                });
        }

        // The opening sound plays from the speakers that the music uses. It plays at the level the player
        // was left at, and it is cut short as soon as someone starts a record. It runs on its own thread.
        // See AppSound.
        try
        {
            var playback = Host.Services.GetRequiredService<PlaybackService>();
            AppSound.PlayOpen(playback.Volume, stop: () => playback.PlayingPath is not null);
        }
        catch { /* This is a flourish, and it must never hold up a window. */ }
    }


    /// <summary> The client for the cloud store. It points at the same Lambda that does identification, and
    /// it carries the same RPM token that every other authenticated call uses.
    /// </summary>
    /// <remarks> The timeout is generous, because the request sends a lossless album track up a home
    /// internet uplink. It is not a small JSON request. The default <see cref="HttpClient"/> timeout of 100
    /// seconds would give up on a 60 MB FLAC most of the way through, and report a failure.
    /// </remarks>
    private static RPM.Core.Cloud.RpmCloudClient CreateCloudClient(IServiceProvider services)
    {
        var config = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Models.AppConfig>>();
        var auth = services.GetRequiredService<AuthService>();

        var api = RPM.Core.Net.RpmApi.At(config.Value.IdentificationApiUrl)
            ?? RPM.Core.Net.RpmApi.Deployed;

        // Retries happen for the transient answers (502, 503 and 504, or a dropped connection). A body that
        // is too big to hold in memory is sent once, and then left to the upload queue's own schedule.
        // Every audio upload is that kind of body.
        var http = RPM.Core.Net.TransientHttp.Client(api, TimeSpan.FromMinutes(30));

        return new RPM.Core.Cloud.RpmCloudClient(http, _ => auth.GetAccessTokenAsync());
    }

    /// <summary> The client for the album wall. It handles RPM's shared album entries, the reviews written
    /// about them, and the profile that the reviews are signed with. It uses the same Lambda and the same
    /// token.
    /// </summary>
    /// <remarks> This timeout is short, unlike the cloud store's. Every call here is a page of text. An
    /// album page that hangs for half a minute waiting for reviews is worse than a page that draws without
    /// them. The token provider is asked on read paths too, but a token is never required. Reviews can be
    /// read while signed out. The client sends the bearer token only when there is one. That is what fills
    /// in how this user voted.
    /// </remarks>
    private static RPM.Core.Cloud.RpmCommunityClient CreateCommunityClient(IServiceProvider services)
    {
        var config = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<Models.AppConfig>>();
        var auth = services.GetRequiredService<AuthService>();

        var api = RPM.Core.Net.RpmApi.At(config.Value.IdentificationApiUrl)
            ?? RPM.Core.Net.RpmApi.Deployed;

        var http = RPM.Core.Net.TransientHttp.Client(api, TimeSpan.FromSeconds(30));

        return new RPM.Core.Cloud.RpmCommunityClient(http, _ => auth.GetAccessTokenAsync());
    }

    /// <summary> Checks the install against <see cref="RPM.Desktop.Services.Shell.AppPayload.Manifest"/>.
    /// It returns true when the app may go on. It returns false when it has already shown the failure
    /// screen instead, and the launch must stop there.
    ///
    /// <para>A missing optional item never stops the launch. It is reported on that screen only when
    /// something else has already stopped the launch. A feature that is quietly absent means the app is
    /// working as well as it can. That is not a reason to refuse to open.</para>
    ///
    /// <para>The check itself must never be the reason a launch fails. If the manifest walk throws, the app
    /// opens. A payload check that can break a working install is worse than no check at all.</para>
    /// </summary>
    private bool TryPreflight()
    {
        RPM.Core.Startup.PreflightReport report;
        try
        {
            report = RPM.Desktop.Services.Shell.AppPayload.Check();
        }
        catch
        {
            return true;
        }

        if (report.CanStart)
            return true;

#if DEBUG
        // On a development machine, the models are a 100 MB download that a fresh clone has not made yet. A
        // developer who wants the app anyway should not have to edit the manifest to get it. There is no
        // such escape in a shipped build. The code below, inside #if DEBUG, is compiled out of a shipped
        // build.
        if (RPM.Core.Diagnostics.DevSwitch.Read("RPM_SKIP_PREFLIGHT") == "1")
            return true;
#endif

        if (MainWindow is { } window)
        {
            window.Content = new Presentation.Startup.PreflightFailureView(report);
            window.Activate();
        }
        return false;
    }

    /// <summary>Sets the native window icon on Windows: the title bar, the taskbar and alt-tab. This is
    /// best effort. The icon is cosmetic, and a missing icon must never break the launch.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void TrySetWindowIcon(Window window)
    {
        try
        {
            var ico = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "window-icon.ico");
            if (System.IO.File.Exists(ico))
                window.AppWindow?.SetIcon(ico);
        }
        catch { /* cosmetic only */ }
    }

    #if DEBUG
    /// <summary>Fake playlist for the `--rpamp` design harness. The paths do not resolve, so these tracks
    /// are never played.</summary>
    private static IReadOnlyList<TrackInfo> SampleRPaMpTracks()
    {
        static TrackInfo T(string artist, string title, double dur) => new()
        {
            FilePath = $"/Sample/{artist} - {title}.mp3",
            Extension = ".mp3",
            Artist = artist,
            Title = title,
            DurationSeconds = dur,
        };
        return new[]
        {
            T("Daft Punk", "Digital Love", 301),
            T("Justice", "Genesis", 233),
            T("Boards of Canada", "Roygbiv", 152),
            T("Aphex Twin", "Windowlicker", 366),
            T("The Chemical Brothers", "Star Guitar", 386),
            T("Röyksopp", "Eple", 218),
            T("Moderat", "A New Error", 340),
        };
    }
    #endif

}
