using Microsoft.CodeAnalysis;

namespace RPM.Analyzers;

/// <summary>
/// The rules, their numbers and the message each one reports.
///
/// <para>All of them are errors, not warnings. In a solution this size a warning is easy to miss, and
/// each of these rules is easy to break by accident.</para>
/// </summary>
internal static class Rules
{
    private const string Category = "RPM";

    /// <summary>An HTTP client built by hand instead of by the one factory that adds the online gate,
    /// the connection budget and the catalogue guard.</summary>
    public static readonly DiagnosticDescriptor HandBuiltHttpClient = new(
        id: "RPM001",
        title: "Build HTTP clients with Wire",
        messageFormat: "'new {0}' builds a client by hand. Use RPM.Core.Net.Wire, which adds the online "
                     + "gate, the connection budget and the catalogue guard. A client built here has "
                     + "none of them.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Wire is the only place where a client that leaves the local network is built. "
                   + "This makes it impossible to write an ordinary client that skips the online gate "
                   + "or calls a catalogue directly.");

    /// <summary>A catalogue's address written into a project that is not the API.</summary>
    public static readonly DiagnosticDescriptor CatalogueAddress = new(
        id: "RPM002",
        title: "Only the rpm API may call a catalogue",
        messageFormat: "'{0}' is a catalogue. Only the rpm API (the server) may send requests to it, "
                     + "because the server holds the keys, the rate limit and the cache that all users "
                     + "share. Add or use an endpoint on the API instead.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The catalogues limit requests per address, and their answers are the same for "
                   + "every user. A request from the app uses that user's own quota for an answer the "
                   + "server already has for everyone. CatalogueReach blocks such requests at run "
                   + "time; this rule blocks them at compile time.");

    /// <summary>A client's base address set by hand instead of by a Destination.</summary>
    public static readonly DiagnosticDescriptor ClientAddress = new(
        id: "RPM004",
        title: "Point a client with a Destination",
        messageFormat: "Setting HttpClient.BaseAddress here gives the client an address without what "
                     + "that address needs. Build the client from a RPM.Core.Net.Destination instead: "
                     + "RpmApi.At for rpm's API, Destination.Service for anything else.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Where a client sends requests and what each request must carry belong together, "
                   + "and Destination holds both. The API refuses every read that names no user unless "
                   + "the app key is on the request, and Destination adds the key together with the "
                   + "address. A string parameter that accepts any address cannot do that.");

    /// <summary>Key state read from Uno's table of key events instead of from the keyboard.</summary>
    public static readonly DiagnosticDescriptor KeyState = new(
        id: "RPM005",
        title: "Ask HeldKeys which modifiers are down",
        messageFormat: "'{0}' reads Uno's table of key events, which on macOS still shows Cmd as down "
                     + "after Cmd-Tab. Use RPM.Desktop.Services.Platform.HeldKeys.Now, which reads the "
                     + "keyboard.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Uno clears its key table when the window's activation state changes, and its "
                   + "macOS host never reports that change. After Cmd-Tab the table still shows Cmd as "
                   + "down, so a normal click is read as Cmd-click until Cmd is pressed and released "
                   + "inside the app again.");

    /// <summary>A handler added to Window.Activated instead of to AppActivation.</summary>
    public static readonly DiagnosticDescriptor WindowActivated = new(
        id: "RPM006",
        title: "Use AppActivation for the app coming to the front",
        messageFormat: "Window.Activated is never raised on macOS after launch, so this handler would run "
                     + "on Windows only. Use RPM.Desktop.Services.Platform.AppActivation.WhenActivated.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Uno's macOS host registers no focus callback, so the window's activation state "
                   + "never changes and the event never fires. Code that must run when the app comes "
                   + "to the front would run on Windows only.");

    /// <summary>A property a XAML ItemsSource binds to, typed as something a Native AOT build passes
    /// on with no items.</summary>
    public static readonly DiagnosticDescriptor BoundList = new(
        id: "RPM007",
        title: "Type a bound list as a partial class of this project",
        messageFormat: "'{0}' is bound to an ItemsSource in XAML and is typed {1}. Under Native AOT a "
                     + "bound list reaches the control with no items unless CsWinRT generated WinRT "
                     + "interfaces for its type. It always does that for a partial class of this "
                     + "project, but for a generic type only when other code happens to need it. "
                     + "Give it a sealed partial class in Presentation/Common/BoundCollections.cs.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "On the Windows Native AOT build, an ObservableCollection<T>, a collection "
                   + "expression typed IReadOnlyList<T> and a List<T> can all reach the list empty "
                   + "through a {Binding}, with no exception. A generic type can work by chance, when "
                   + "other code also sets it from code, but a binding cannot rely on that. The rule "
                   + "matches property names against every ItemsSource=\"{Binding X}\" in the "
                   + "project's XAML, on types marked [RpmBindable].");

    /// <summary>An ItemsSource set from code to a value whose concrete type the compiler cannot see.</summary>
    public static readonly DiagnosticDescriptor ItemsSourceFromCode = new(
        id: "RPM008",
        title: "Give ItemsSource a concrete class",
        messageFormat: "ItemsSource is given a {0} here. Under Native AOT CsWinRT generates WinRT "
                     + "interfaces for the type it sees at this line, and from an interface or an array "
                     + "it cannot see the list's real type. Give it a List<T> or another concrete class.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "A collection expression typed IReadOnlyList<T> has a type that the compiler "
                   + "generates. Given to a repeater under Native AOT, it fails with \"Argument "
                   + "'source' is not a supported vector\", which can close the app. Arrays are also "
                   + "refused, because they have not been tested under Native AOT.");

    /// <summary>A property a XAML binding indexes into ({Binding Bands[3].Gain}), on the WinUI head,
    /// typed as something CsWinRT gives no indexer to.</summary>
    public static readonly DiagnosticDescriptor IndexedBinding = new(
        id: "RPM011",
        title: "Give a list that a binding indexes into its own [RpmBindable] type",
        messageFormat: "'{0}' is indexed in a XAML binding ({{Binding {1}[n]}}) and is typed {2}. Under "
                     + "Native AOT, WinUI reads the index through CsWinRT, which supports it only on a "
                     + "type marked [RpmBindable]. Give it a sealed partial [RpmBindable] class in "
                     + "Presentation/Common/BoundCollections.cs, as EqBandList is.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "For example, the equalizer sliders bind Bands[0].Gain to Bands[9].Gain. With "
                   + "Bands typed ObservableCollection<EqBand>, the binding throws "
                   + "NotSupportedException on the Windows Native AOT build and the sliders are not "
                   + "bound. Checked on the WinUI head only, because Uno reads an index on any IList.");

    /// <summary>AppWindow.Presenter read anywhere except WindowPresenter.</summary>
    public static readonly DiagnosticDescriptor AppWindowPresenter = new(
        id: "RPM009",
        title: "Reach the presenter through WindowPresenter",
        messageFormat: "AppWindow.Presenter is read here. Under Native AOT a cast of it to "
                     + "OverlappedPresenter returns null, so maximizing, the caption buttons and hiding "
                     + "the title bar do nothing. Use "
                     + "RPM.Desktop.Services.Platform.WindowPresenter.Overlapped.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "WindowPresenter wraps the native object as an OverlappedPresenter again when the "
                   + "cast fails. Code that casts the presenter itself opens the window framed and "
                   + "not maximized under Native AOT.");

    /// <summary>A XamlReader.Load call.</summary>
    public static readonly DiagnosticDescriptor XamlReaderLoad = new(
        id: "RPM010",
        title: "Put markup in a .xaml file, not in a string",
        messageFormat: "Under Native AOT, casting the result of XamlReader.Load to ControlTemplate "
                     + "throws. Put the markup in a .xaml file: a default style in "
                     + "Themes/Generic.xaml or a resource.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Markup parsed from a string at run time is not available to Native AOT, so the "
                   + "cast of the result throws. A .xaml file is compiled with the app.");

    /// <summary>The name MusicBrainz written into a string.</summary>
    public static readonly DiagnosticDescriptor CatalogueName = new(
        id: "RPM003",
        title: "rpm names SonoVault, not MusicBrainz",
        messageFormat: "\"{0}\" names MusicBrainz. rpm names SonoVault: the catalogue's data reaches "
                     + "the app under SonoVault's licence, so the app credits SonoVault and its screens "
                     + "show that name. If this string is a tag name, a wire field or a stored key "
                     + "that another program chose, add it to CatalogueNameAnalyzer.Fixed.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Which catalogue the app names is a licence requirement, not a choice of words. "
                   + "Only strings are checked: MusicBrainzReleaseId is the name of a field inside an "
                   + "audio file, so it cannot be renamed, and users never see it.");

    /// <summary>A PeekPixels result left to the finalizer.</summary>
    public static readonly DiagnosticDescriptor PeekPixelsWithoutUsing = new(
        id: "RPM012",
        title: "Declare the pixmap from PeekPixels with using",
        messageFormat: "Declare this pixmap with using (using var pixels = image.PeekPixels();). "
                     + "SKPixmap.Encode passes the native pixmap to Skia and keeps no reference to "
                     + "itself, and Skia reads it for the whole call, so a pixmap the finalizer "
                     + "collects during the call is freed while Skia is reading it.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Skia's encoder holds the pixmap by reference. If the finalizer frees the pixmap "
                   + "while Skia is encoding, the app can crash with SIGBUS in Skia's JPEG encoder.");

    /// <summary>An objc_msgSend import that returns a struct, outside ObjcStructReturn.</summary>
    public static readonly DiagnosticDescriptor StructFromMsgSend = new(
        id: "RPM013",
        title: "Send struct-returning messages through ObjcStructReturn",
        messageFormat: "'{0}' imports {1} returning the struct {2}. On an Intel Mac a struct larger "
                     + "than 16 bytes must go through objc_msgSend_stret, which does not exist on arm64, "
                     + "so one declaration cannot be right on both. Use "
                     + "RPM.Desktop.Services.Platform.ObjcStructReturn.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "On x86_64, objc_msgSend with a large struct return treats the hidden return "
                   + "buffer as the receiver, and the app crashes. On arm64 the same code works, so "
                   + "the bug shows only on Intel Macs.");

    /// <summary>A diagnostic line written to the debug or trace channel instead of the ring.</summary>
    public static readonly DiagnosticDescriptor DebugOutput = new(
        id: "RPM014",
        title: "Write diagnostic lines with Said.Line",
        messageFormat: "{0}.{1} {2}, and under a debugger it stops the thread on every call. Use "
                     + "RPM.Core.Diagnostics.Said.Line, which writes to the ring in both builds, or "
                     + "Faults.Note for an exception.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Debug.WriteLine is [Conditional(\"DEBUG\")], and every build that ships is "
                   + "Release, so a failure reported that way is never seen. Trace writes to the same "
                   + "debugger channel. Said.Line is the one place that decides where a line goes.");

    /// <summary>A key's repeat flag read from the event, which the macOS host never sets.</summary>
    public static readonly DiagnosticDescriptor KeyRepeat = new(
        id: "RPM015",
        title: "Ask HeldKeys whether a key press is a repeat",
        messageFormat: "'{0}' is always false on macOS: Uno's macOS host fills in only the scan code, so "
                     + "a held key arrives as a series of new presses. Use "
                     + "RPM.Desktop.Services.Platform.HeldKeys.IsRepeat, which asks AppKit on a Mac.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "MacOSWindowHost.CreateArgs in Uno sets CorePhysicalKeyStatus.ScanCode and nothing "
                   + "else, and the native host does not filter AppKit's repeats. Code that ignores "
                   + "repeats by reading the event's flag works on Windows only.");

    /// <summary>A literal with words in it put where the screen shows it.</summary>
    public static readonly DiagnosticDescriptor ShownLiteral = new(
        id: "RPM016",
        title: "Put words on screen through the string table",
        messageFormat: "\"{0}\" goes on screen through {1}, in English in every language. Add it to "
                     + "scripts/strings.json and use Loc.Get or Loc.Format.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Strings set from code are not checked by any XAML rule, so they are easy to "
                   + "leave untranslated. A literal given to a text property of a XAML element, a "
                   + "tooltip, an accessible name, a list's items or a parameter marked [Shown] is "
                   + "reported, unless it has no letters (a glyph, a number, a separator) or is the "
                   + "app's own name.");

    /// <summary>An environment variable read outside DevSwitch.</summary>
    public static readonly DiagnosticDescriptor EnvironmentSwitch = new(
        id: "RPM017",
        title: "Read environment switches with DevSwitch",
        messageFormat: "Environment.GetEnvironmentVariable({0}) reads a switch in every build. Use "
                     + "RPM.Core.Diagnostics.DevSwitch.Read, which returns null in a Release build of "
                     + "the app.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Development switches must not work in the shipped app. For example, RPM_API_URL "
                   + "would send the sign-in token to whatever host it names. Operating-system "
                   + "variables the app needs to find files (PATH, HOME, TMPDIR, the XDG folders) are "
                   + "not switches and are allowed.");

    /// <summary>An ONNX Runtime session built outside OnnxSession.</summary>
    public static readonly DiagnosticDescriptor HandBuiltOnnxSession = new(
        id: "RPM018",
        title: "Open ONNX sessions with OnnxSession",
        messageFormat: "'new InferenceSession' can crash the process while the model loads, because "
                     + "nothing keeps the SessionOptions alive during the native call. Use "
                     + "RPM.Onnx.OnnxSession.Open, which keeps them alive and then disposes them.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "InferenceSession passes the options to CreateSession as a plain pointer. If the "
                   + "garbage collector finalises the SessionOptions during that call, the runtime "
                   + "reads freed memory. OnnxSession.Open is the one place that builds a session.");
}
