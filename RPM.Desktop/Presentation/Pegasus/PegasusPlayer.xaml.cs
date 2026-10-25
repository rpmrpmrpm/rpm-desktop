using RPM.Core.Diagnostics;
using RPM.Desktop.Presentation.RPaMp;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// Pegasus, full screen: the third of the app's views, beside the library and RPaMp (see
/// <see cref="ViewModeService"/>).
///
/// <para>The record sits half out of its sleeve in the middle of the window. What is playing is written down
/// the left of it, and the room behind is lit in the record's own colours. Nothing else is on the screen. The
/// four keys in the corner stand down five seconds after the last pointer move, and they take the mouse pointer
/// with them.</para>
///
/// <para>Nothing here is rebuilt from the library. The header (<see cref="ArtDisplay"/>) is where a track's
/// identity is resolved and its cover decoded. This view reads those straight off it. So the two surfaces always
/// agree, and the tag read and the decode happen once. The stage owns a GL context. It is built on first use,
/// then switched on and off (<see cref="PegasusStage.SetActive"/>) rather than built again.</para>
/// </summary>
public sealed partial class PegasusPlayer : UserControl
{
    /// <summary>How long the keys stay up after the last pointer move.</summary>
    private static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);

    /// <summary>How long the view takes to arrive over the app.</summary>
    private const int OpenFadeMs = 520;

    /// <summary>How long the New Year greeting takes to arrive, and to go. It is slower to arrive than anything
    /// else in this view. It is the one thing on this screen that nobody pressed a button for. A wish that
    /// snapped on would look like an error dialog.</summary>
    private const int GreetingInMs = 900;
    private const int GreetingOutMs = 420;

    private RPaMpModel? _amp;              // the shared transport, for the space bar
    private IPegasusRoom? _stage;          // the room, in GL. It is held as the interface (see IPegasusRoom)
    private FrameworkElement? _stageElement;   // Also held as an element, for the tree and the sizing
    private PegasusChrome? _chrome;        // the record, the sleeve and the words over it

    /// <summary>
    /// What a right-click on one song in the collection's record screen can ask for. The page fills these in.
    /// The entries edit files, send them to the cloud and open records. A visualizer has no business knowing how
    /// to do those things. The hooks are the same ones that the library window's own wall is given, so one song
    /// offers one menu wherever it is listed.
    /// </summary>
    public TrackMenuHooks? TrackMenuHooks
    {
        get => _trackMenuHooks;
        set
        {
            _trackMenuHooks = value;
            if (_chrome is not null)
                _chrome.TrackMenuHooks = value;
        }
    }

    private TrackMenuHooks? _trackMenuHooks;

    /// <summary>
    /// What home needs from the page to read, play and answer its menus: the method that MainPage wires its own
    /// home with, and the way to Add music. It is handed on to the chrome, which builds the home screen that this
    /// view opens on when nothing is on the deck (see PegasusChrome.Home).
    /// </summary>
    public PegasusHomeHooks? HomeHooks
    {
        get => _homeHooks;
        set
        {
            _homeHooks = value;
            if (_chrome is not null)
                _chrome.HomeHooks = value;
        }
    }

    private PegasusHomeHooks? _homeHooks;
    private ArtDisplay? _art;

    private DispatcherQueueTimer? _idle;   // one-shot: restarted by every pointer move
    private bool _pinned;

    private readonly ChromeFade _rootFade;
    private readonly ChromeFade _keysFade;
    private readonly ChromeFade _greetingFade;

    /// <summary>The black cover that the view stands up behind. See the loading notes further down.</summary>
    private readonly ChromeFade _loadingFade;

    // ── the New Year greeting ──
    // The New Year greeting.
    //
    // It is not a member of PegasusView, and it is deliberately kept out of it. That enum is three ways of
    // looking at ONE picture, and each one takes something away. A card that wishes the listener a happy new
    // year is not one of those ways. The greeting is drawn over the bare room instead (see EffectiveView). That
    // leaves the listener's own choice of composition untouched while it is up. Ending the greeting is one flag
    // going false, and the screen they left is the screen they get back.
    private bool _greeting;

    /// <summary>Whether the collection is up over the room (see PegasusChrome.Library). It is held here, and not
    /// only in the chrome, because it is a fact about what the ROOM is drawing. A wall of covers stands in front
    /// of the bare room, never in front of a record that is turning. The listener's own composition is not
    /// written while this is in force (see <see cref="EffectiveView"/>).</summary>
    private bool _library;

    /// <summary>The file the greeting was raised for. It comes down when the deck moves off this record,
    /// so the wish lasts exactly as long as the song does.</summary>
    private string? _greetingFor;

    /// <summary>Whether that record has actually reached the deck yet. Without this flag the greeting would take
    /// itself down on the way in. It is raised at the same moment the record is asked for. Until the record
    /// arrives, the deck still holds the previous record, or nothing at all.</summary>
    private bool _greetingLanded;

    /// <summary>Which composition is up. It is held here, not in the chrome, so it survives the view being left and
    /// coming back. A composition is a way of watching, not a property of the thing being watched. Once the
    /// listener has chosen it, they should not have to choose it again on the way back, nor after a restart: the
    /// layout and the lyric key's choice are kept in the settings (see <see cref="ReadStoredLayout"/>).</summary>
    private PegasusView _view;

    /// <summary>Whether the stored layout and lyric choice have been read. They are read on the first
    /// <see cref="Show"/>, not in the constructor, so the settings are asked once the app is standing.</summary>
    private bool _storedRead;

    /// <summary>Which composition to put back when the lyric is closed.</summary>
    private PegasusView _beforeLyrics = PegasusView.Spread;

    /// <summary>
    /// The listener asked for the words and has not asked to stop.
    ///
    /// <para>This is a standing choice, not a state of the screen. The difference shows on every change of
    /// record. The sheet in hand always belongs to the record that just left. Without this choice, the lyric
    /// would empty and close itself between every two songs. The wish outlives the sheet. Records with no lyric
    /// are passed over on the cover. The first record that does have lyrics puts the words back up, without the
    /// listener having to ask twice.</para>
    /// </summary>
    private bool _lyricSticky;

    public PegasusPlayer()
    {
        InitializeComponent();
        // This uses its own writer, not ChromeFade(Root). So the moment the fade-in lands is also the moment
        // the view starts covering the window (see Covers).
        _rootFade = new ChromeFade(v =>
        {
            if (Root.Opacity != v)
                Root.Opacity = v;
            SetCovers(IsShowing && v >= 1);
        }, Root.Opacity);
        _keysFade = new ChromeFade(Keys);
        _greetingFade = new ChromeFade(Greeting);
        _loadingFade = new ChromeFade(Loading);
        _flashFade = new ChromeFade(Flash);

        Unloaded += (_, _) => Hide();

        // Escape and Back are not listened for here. The shell passes them on as it goes down (see TakeBack).

        // The real GL failure reason arrives on the logging thread. It is subscribed once, not once per stage. This
        // is a static event, so a subscription per launch would keep this control alive for every stage ever built.
        GlDiagnostics.Changed += () => DispatcherQueue?.TryEnqueue(UpdateStatus);
    }

    /// <summary>True while the view is up.</summary>
    public bool IsShowing => Root.Visibility == Visibility.Visible;

    /// <summary>
    /// Whether the view is up and fully opaque, so nothing under it can be seen. This is true from the frame the
    /// fade-in lands until <see cref="Hide"/> begins. The root is solid black and fills the window. Once it is at
    /// full opacity, every layer of the shell beneath it is drawn for nobody.
    /// </summary>
    public bool Covers { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="Covers"/> changes. The shell stops drawing what is under
    /// the view while it is true (see <c>ShellPage.Cover</c>). The shell reads <see cref="Covers"/> rather than
    /// trusting the order of the calls.</summary>
    public event Action? CoversChanged;

    private void SetCovers(bool on)
    {
        if (Covers == on)
            return;
        Covers = on;
        CoversChanged?.Invoke();
    }

    /// <summary>Raised when the user leaves: Back (or Escape) is pressed twice on home, with nothing left above it.
    /// The close key is collapsed, so this is the only way out. The view-mode service takes this as "back to the
    /// library", which is what closing a view means.</summary>
    public event Action? Closed;

    /// <summary>
    /// Puts the view up.
    ///
    /// <para><paramref name="openOn"/> opens on a named composition instead of the one it was left on. Only the
    /// still-image capture passes it. A still of the room is a still of the room, whatever the last person to use
    /// the app was watching. Cycling the button to get there would take extra steps, and the capture should not have to do them. It is set
    /// before the chrome is dressed, so the chrome is laid out that way, rather than dissolving into it while the
    /// shot is taken.</para>
    /// </summary>
    public void Show(PegasusView? openOn = null)
    {
        ReadStoredLayout();
        if (openOn is { } view)
            _view = view;

        if (IsShowing)
        {
            ApplyView(immediate: false);
            ShowKeys();
            RestartIdle();
            return;
        }

        _amp = App.Services.GetRequiredService<RPaMpService>().Transport;

        // Whether the collection is the screen this visit opens on. This is asked before anything is stood up,
        // because the answer decides what is stood up FIRST.
        var openingOnLibrary = !_greeting && _amp?.Deck is null;

        // The whole view is black before anything is stood up. The room, the shaders and the collection all arrive
        // behind that black (see the loading note below).
        BeginLoading();

        // The room waits when the collection is the opening screen and no room is standing yet.
        //
        // A GL context and two shaders compiled against the driver take the machine's whole attention for a moment.
        // That moment belongs to the thing the listener came for: a wall of a few thousand records, read from the
        // database and laid out. Stood up together, the two share one thread and the listener waits on both.
        // Stood up one after the other, the collection arrives at its own speed, and the room lights up behind it
        // a moment later. That is a background task arriving late, and nobody waits for a background task.
        //
        // The cover has nothing to wait on from the room yet. It starts waiting when the room's turn is booked
        // (see StandUpRoomLater). So the black lifts on a screen with everything finished behind it, not on a wall
        // over an unlit room.
        _roomWaiting = openingOnLibrary && _stage is null;
        _waitStage = !_roomWaiting;

        // The flag goes up BEFORE the composition does. The greeting does the same, for the same reason.
        // EffectiveView reads the flag. Without it, the chrome is stood up on the listener's own composition
        // (record, sleeve and words, laid out at full strength by the immediate pass inside EnsureChrome), and then
        // it dissolves to the bare room a moment later, when ShowLibrary says where we are.
        //
        // The collection is drawn over the room through a 45% black, and the room shows through every gap between
        // the covers. So what is UNDER that layer is still visible through the wall. The record's sleeve showed
        // through the wall, and with nothing on the deck, the sleeve is the "no artwork" mark. Set here, the
        // composition is laid out as the bare room from its first frame, and there is nothing to dissolve out of.
        _library = openingOnLibrary;

        // The room and the composition are stood up inside a try. The try must be out here, not inside them.
        // Standing the stage up needs the GL assembly, and the runtime resolves a method's types as it JITs that
        // method. So where GL is missing, EnsureStage cannot begin, and a try written inside it would never be
        // reached. Caught at the call site instead, the view still opens. The record still turns, the words are
        // still read, and the black behind them says why the room is not lit (see IPegasusRoom).
        try
        {
            if (!_roomWaiting)
                EnsureStage();
            EnsureChrome();
        }
        catch (Exception ex)
        {
            Faults.Note("gl", "starting the room", ex);
            StatusText.Text = StageTrouble.Line($"{ex.GetType().Name}: {ex.Message}");
            StatusText.Visibility = Visibility.Visible;

            // There is no room to wait for on this machine. The composition runs well without a room, and the
            // cover must not stay over it for four seconds.
            _waitStage = false;
        }

        // The room can also fail without anything on the stage knowing. The context is made by Uno's own native
        // wrapper. Where that cannot get a pixel format, the stage is left standing with no renderer, no error of
        // its own, and nothing drawn. That report goes to the logging bridge and nowhere else. So the status line
        // listens to it directly (see UpdateStatus).
        if (!_glListening)
        {
            _glListening = true;
            GlDiagnostics.Changed += () => DispatcherQueue?.TryEnqueue(UpdateStatus);
        }

        UpdateStatus();

        // A stage that was already stood up on an earlier visit does not say so again. It answered once, and the
        // answer is still on it.
        //
        // This asks what the room has PAINTED, not whether it has a renderer string. The string is set when the
        // context is made. On the first visit that is before the shaders are compiled, and long before anything is
        // drawn. So reading it here would say "already answered" about a room that had never been lit, and the
        // cover would come off an empty surface. See PegasusStage.HasPainted.
        if (_stage is null || _stage.HasPainted || _stage.LastError is not null)
            _waitStage = false;

        HookArt();

        // Up from nothing. The view covers the app either way, so without this the change is one frame. The library
        // is replaced by a black rectangle, and the first thing drawn in it is whatever the GL stage had ready. For
        // the first frame or two, that is usually nothing at all. Coming up over half a second puts the room and
        // the record over the app, not in place of it. The window's own move to full screen happens underneath,
        // unseen.
        _rootFade.To(0, 0);
        Root.Visibility = Visibility.Visible;
        _rootFade.To(1, OpenFadeMs);

        SizeStage(StageHost.ActualWidth, StageHost.ActualHeight);
        _stage?.SetActive(true);
        _chrome?.SetActive(true);
        Root.Focus(FocusState.Programmatic);   // the view holds the focus; the first key places it (see FocusIsLost)

        // Nothing is on the deck, so the visualizer has nothing to visualize. A record-shaped hole in the middle of
        // an empty room is not a screen anybody opened this for. So the collection comes up instead. The room is
        // still lit, and the way to fill the hole is on that screen. Never do this when something IS playing. Then
        // the picture of it is exactly what was asked for.
        //
        // It comes up on HOME, the main window's home screen, rather than on the wall. Home shows what has been
        // played and what goes with it, which answers the question an empty deck raises. The key under the words
        // still opens the wall.
        //
        // Not for the greeting, which is the one night when the empty room is the point.
        if (openingOnLibrary)
        {
            // Straight up at full strength. This is the screen the view opens ON, behind the black. There is no
            // composition underneath it to cross from (see ShowLibrary).
            _chrome?.ShowLibrary(CollectionScreen.Home, immediate: true);

            // ...and the cover stays on until home is ready: its screen up with the pictures at the top loaded. The
            // main window's splash waits on the same thing. This is checked as a fact, not only listened for, so a
            // screen that was ready before this line is not waited for.
            _waitLibrary = _chrome is { LibraryUp: true, CollectionIsReady: false };
        }

        // A wall filled on an earlier visit has nothing left to arrive. The room gets its turn straight away, on the
        // same beat it would have had. Without this, a room held back for a collection that was already there would
        // wait for a reading that never happens.
        if (!_waitLibrary)
            StandUpRoomLater();

        ShowKeys(immediate: true);
        RestartIdle();
        TryEndLoading();   // a warm stage and a filled wall are both possible on a second visit

        // The window goes fully full screen where the platform can. The view covers the app either way. This also
        // takes the title bar and the desktop away. It is best effort.
        FullScreenWindow.Set(true);

        // Going full screen re-parents and re-activates the native window. That can drop the view's keyboard focus,
        // so focus is taken again a tick later, after the resize settles. Then Escape still exits.
        //
        // The stage is resized on the same tick, and this is not a needless repeat. The transition resizes the window
        // twice in quick succession: un-maximize, then cover the monitor. If the window does not settle where it was
        // left (the presenter can snap it back after SetWindowPos), the stage keeps the size it was given at the
        // widest moment. An OVERSIZED stage does more than look wrong. GLCanvasElement fills only the viewport, so
        // the shader draws into the bottom of a taller framebuffer, and the room comes out solid black. Re-asserting
        // the size against the host's actual size costs one comparison, and it closes that off whatever the window
        // did.
        DispatcherQueue?.TryEnqueue(() =>
        {
            if (!IsShowing)
                return;
            Root.Focus(FocusState.Programmatic);
            SizeStage(StageHost.ActualWidth, StageHost.ActualHeight);
        });
    }

    /// <summary>
    /// Puts the view up on the New Year greeting. This happens one night a year, and only from
    /// <see cref="NewYearHerald"/> by way of the view service. <paramref name="record"/> is the file being put on.
    /// The greeting uses it to know when it is over.
    ///
    /// <para>The flags go up BEFORE the view does. So the room is laid out as the greeting from its first frame.
    /// It does not open on the listener's own composition and then dissolve out of it. Their composition is not
    /// changed at all (see <see cref="EffectiveView"/>).</para>
    /// </summary>
    public void ShowNewYear(string record)
    {
        _greeting = true;
        _greetingFor = record;
        _greetingLanded = false;

        Show();

        Greeting.Visibility = Visibility.Visible;
        _greetingFade.To(1, GreetingInMs);
    }

    /// <summary>
    /// The greeting comes down, and the listener's own composition comes back.
    ///
    /// <para>This is called when the record it was raised for is over. It is also called on any gesture that means
    /// "put my own screen back": the layout key, the lyric key, or Escape. It is never called on a pointer
    /// move. Not moving the mouse for five seconds at midnight is the most likely thing in the world. A wish that
    /// vanished because nobody touched anything would be a wish nobody saw.</para>
    /// </summary>
    private void EndGreeting()
    {
        if (!_greeting)
            return;

        _greeting = false;
        _greetingFor = null;
        _greetingLanded = false;
        _greetingFade.To(0, GreetingOutMs);

        // Left visible at zero opacity rather than collapsed. There is no callback to collapse it when the fade
        // lands. An empty panel at zero opacity costs one arrange for the rest of this visit. Hide() collapses it
        // properly on the way out.
        ApplyView(immediate: false);
    }

    /// <summary>Takes the view down and stops it rendering. It does NOT raise <see cref="Closed"/>. This is the
    /// service putting the view away, and the service already knows.</summary>
    public void Hide()
    {
        if (!IsShowing)
            return;

        // This runs first, so the shell is drawing again before anything here comes down. The root is collapsed at
        // the end of this turn, and the frame after that must have the library in it.
        SetCovers(false);

        _stage?.SetActive(false);

        // The collection goes away with the view. It does not wait behind it. The next visit decides for itself which
        // screen it opens on, so a wall left standing would be the last visit's answer to a question asked again.
        // This happens before the chrome stands down, whose own stand-down lands the fade.
        _chrome?.HideLibrary();
        _chrome?.SetActive(false);
        StopRing();
        _idle?.Stop();
        _rootFade.Stop();
        _keysFade.Stop();
        HideFlash(immediate: true);

        // The greeting does not survive the view being left. It is put away through the fade, not by writing the
        // opacity directly. A fade that was told nothing about a change it did not make would think it was already
        // where it had been asked to go, and would refuse to move the next time.
        _greeting = false;
        _greetingFor = null;
        _greetingLanded = false;
        _greetingFade.To(0, 0);
        Greeting.Visibility = Visibility.Collapsed;
        FullScreenWindow.SetCursorHidden(false);   // never leave without giving the pointer back
        Root.Opacity = 1;                          // stopped and settled, so the next Show starts from nothing
        Root.Visibility = Visibility.Collapsed;
        UnhookArt();
        FullScreenWindow.Set(false);
    }

    // ── the room ──

    /// <summary>
    /// Stands the GL room up, once.
    ///
    /// <para>This is the ONE method in this control that names <see cref="PegasusStage"/>. It is kept that way on
    /// purpose (see <see cref="IPegasusRoom"/>). It is never inlined, so a machine without the GL assembly fails
    /// HERE, at a call the caller has wrapped. It does not fail somewhere further up the stack that has no wrapper.
    /// A stage also throws on construction where hardware acceleration is not available. Both failures come out of
    /// the same door.</para>
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void EnsureStage()
    {
        if (_stage is not null)
            return;

        StatusText.Visibility = Visibility.Collapsed;

        var stage = new PegasusStage
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // The animation the listener chose and the brightness of the screen it opens under, from its first frame.
        stage.SetStyle(RoomStyle);
        stage.SetBrightness(RoomBrightness, immediate: true);
        _stage = stage;
        _stageElement = stage;
        StageHost.Children.Add(stage);
        stage.CompileStateChanged += UpdateStatus;
        stage.CompileStateChanged += OnStageAnswered;   // …and the cover over the view comes off
    }

    /// <summary>Whether the room is being held back until the collection has arrived (see the note in
    /// <see cref="Show"/>). It is cleared the moment its turn is booked, so it is only taken once.</summary>
    private bool _roomWaiting;

    /// <summary>
    /// Gives the room its turn, now that the collection has the screen.
    ///
    /// <para>It runs on the NEXT turn of the queue, not this one, and at the back of it. The wall has just been
    /// handed its rows and has not been laid out yet. Standing a GL context up on the same beat would put the
    /// compile in front of exactly the pass this is meant to clear. A low-priority enqueue lands after that pass.</para>
    ///
    /// <para>It does nothing where the room was never held back. A visit that opened on a record stood the room up
    /// with everything else. This is not a second door into that.</para>
    /// </summary>
    private void StandUpRoomLater()
    {
        if (!_roomWaiting)
            return;
        _roomWaiting = false;

        // ...and NOW the cover has something to wait on from it. The room's wait gets its own four seconds, and it
        // starts where the collection's wait ended. Inheriting what was left of the collection's wait would give a
        // room on a slow driver no time at all.
        if (_loadingUp)
        {
            _waitStage = true;
            ArmLoadingGiveUp();
        }

        DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            // The listener left while the collection was being read, or something else stood the room up.
            if (!IsShowing || _stage is not null)
            {
                _waitStage = false;
                TryEndLoading();
                return;
            }

            try
            {
                EnsureStage();
            }
            catch (Exception ex)
            {
                // The same answer as the opening path gives. The composition runs well without a room, and the black
                // behind it says why it is not lit. There is nothing left to wait for, either.
                Faults.Note("gl", "starting the room", ex);
                StatusText.Text = StageTrouble.Line($"{ex.GetType().Name}: {ex.Message}");
                StatusText.Visibility = Visibility.Visible;
                _waitStage = false;
                TryEndLoading();
                return;
            }

            // Everything a room misses by arriving late: the composition it is drawn as, its size, the
            // colours of whatever is on the deck, and the chrome's own handle on it.
            _chrome?.SetStage(_stage);
            SizeStage(StageHost.ActualWidth, StageHost.ActualHeight);
            _stage?.SetView(EffectiveView, immediate: true);
            _stage?.SetActive(true);
            OnChromeChanged();
        });
    }

    /// <summary>Stands the composition up over the room, once, and hands it the room. The glow behind a lyric is
    /// lit by the same low end that lights the room, and there is only one place where that is measured. If a room
    /// fails to stand up, the composition can still run without one.</summary>
    private void EnsureChrome()
    {
        if (_chrome is null)
        {
            _chrome = new PegasusChrome();
            _chrome.LyricsChanged += OnLyricsChanged;
            _chrome.LyricViewRequested += ToggleLyrics;
            _chrome.LayoutRequested += StepLayout;
            _chrome.ShiftChanged += OnCompositionShifted;
            _chrome.Announced += ShowFlash;
            _chrome.LibraryShown += OnLibraryShown;
            _chrome.LibraryFilled += OnLibraryFilled;
            _chrome.LibraryWarming += OnLibraryWarming;
            _chrome.LibraryWarmProgress += OnLibraryWarmProgress;
            _chrome.RoomPalette += OnRoomPalette;
            _chrome.LineMenuOpen = () => LineMenuOpen;
            _chrome.Settings = this;   // the side menu's settings panel (see PegasusPlayer.Settings)
            _chrome.ExitRequested += Leave;
            ChromeHost.Children.Add(_chrome);
        }

        // These are handed over every time, not only when the chrome is built. The page usually wires them long before
        // this view is first opened. So the chrome, when it is eventually built, can collect them and does not miss
        // them. Home's hooks are set only when they differ, because setting them wires home again.
        _chrome.TrackMenuHooks = _trackMenuHooks;
        if (!ReferenceEquals(_chrome.HomeHooks, _homeHooks))
            _chrome.HomeHooks = _homeHooks;

        if (_amp is not null)
            _chrome.SetTransport(_amp);
        _chrome.SetStage(_stage);
        ApplyView(immediate: true);
    }

    /// <summary>
    /// The collection came up over the room, or went away. The room is drawn bare underneath it. The composition's
    /// own keys go with it. The layout key cycles pictures of a record, and there is no record on this screen to
    /// cycle.
    /// </summary>
    private void OnLibraryShown(bool up)
    {
        _library = up;
        ApplyView(immediate: false);
        ShowKeys();
    }

    /// <summary>
    /// The queue drawer came in, or went out. The room goes with the composition over it.
    ///
    /// <para>The two are one picture drawn with two technologies. The record, the sleeve and the words are XAML.
    /// The room they stand in is GL. The drawer's whole point is that it pushes the picture aside rather than
    /// covering it. Moving only the part of it that this control does not own would tear the record away from the
    /// light behind it. It would also tear the spectrum away from the disc it comes out from behind. So the chrome
    /// hands out one figure, and both are written from it.</para>
    ///
    /// <para>This is a translate, never a resize. The stage's size is the shader's resolution. Shrinking it to fit
    /// the room left over would re-lay a GL surface on every frame of a quarter-second slide. It would also move the
    /// spectrum's start, which is worked out from that resolution, away from the record it should come out from
    /// behind. Slid instead, the room keeps its size and simply travels. What runs off the right edge is a strip of
    /// empty room, and the drawer covers the strip of black it leaves on the left.</para>
    /// </summary>
    private void OnCompositionShifted(double x)
    {
        if (_stageElement is not { } stage)
            return;
        if (stage.RenderTransform is not TranslateTransform shift)
            stage.RenderTransform = shift = new TranslateTransform();
        shift.X = x;
    }

    // ── what just happened ──

    /// <summary>How long a message stays up before it starts to leave, and how long each half of that takes. It is
    /// long enough to read a song title twice, and short enough that nobody waits for it.</summary>
    private static readonly TimeSpan FlashHold = TimeSpan.FromSeconds(2.6);
    private const int FlashInMs = 140;
    private const int FlashOutMs = 320;

    private DispatcherQueueTimer? _flashTimer;   // one-shot: restarted by every message
    private readonly ChromeFade _flashFade;

    // The message card's values before the scale is applied: the font size, the padding, the corner radius and the
    // widest it can be.
    private const double FlashFontSize = 14;
    private const double FlashPadX = 18;
    private const double FlashPadY = 9;
    private const double FlashCorner = 16;
    private const double FlashMaxWidth = 640;

    /// <summary>
    /// Sizes the message card at the scale that the rest of the writing in this view uses. That is the chrome's
    /// <see cref="PegasusChrome.TextScale"/>, which is half again on a 1440 pixel high screen. It is asked each time
    /// a message is shown, so it uses the window's size at that moment.
    /// </summary>
    private void SizeFlash()
    {
        var scale = _chrome?.TextScale ?? 1;
        FlashText.FontSize = FlashFontSize * scale;
        Flash.Padding = new Thickness(FlashPadX * scale, FlashPadY * scale, FlashPadX * scale, FlashPadY * scale);
        Flash.CornerRadius = new CornerRadius(FlashCorner * scale);
        Flash.MaxWidth = FlashMaxWidth * scale;
    }

    /// <summary>
    /// Puts one sentence along the foot of the screen.
    ///
    /// <para>A second message that arrives while the first is up REPLACES it, and the clock starts again. It does not
    /// queue behind the first. These messages answer presses, and the answer to the press just made is the only one
    /// anybody is waiting for. Pressing the heart twice quickly should read as one message correcting itself, not
    /// two stacking up.</para>
    /// </summary>
    private void ShowFlash([Shown] string message)
    {
        if (!IsShowing || string.IsNullOrWhiteSpace(message))
            return;

        FlashText.Text = message;
        SizeFlash();
        Flash.Visibility = Visibility.Visible;
        _flashFade.To(1, FlashInMs);

        _flashTimer ??= CreateFlashTimer();
        _flashTimer?.Stop();
        _flashTimer?.Start();
    }

    private DispatcherQueueTimer? CreateFlashTimer()
    {
        if (DispatcherQueue is null)
            return null;

        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = FlashHold;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => HideFlash();
        return timer;
    }

    /// <summary>Takes it down. This is called by the clock, and straight away when the view is left. A message left
    /// half faded on a control that has stopped ticking would still be there on the way back in.</summary>
    private void HideFlash(bool immediate = false)
    {
        _flashTimer?.Stop();
        if (immediate)
        {
            _flashFade.To(0, 0);
            Flash.Visibility = Visibility.Collapsed;
            return;
        }

        _flashFade.To(0, FlashOutMs);
        // It is collapsed a moment after the fade lands, so the layout stops walking past it. The opacity is what
        // the user sees. This only tidies up behind it.
        Services.Shell.Ticker.Once(DispatcherQueue, TimeSpan.FromMilliseconds(FlashOutMs + 40), () =>
        {
            if (_flashFade.Target == 0)
                Flash.Visibility = Visibility.Collapsed;
        });
    }

    private bool _glListening;

    /// <summary>
    /// What the black is, when it is black.
    ///
    /// <para><b>Silence can mean two different things.</b> The status line speaks only when the STAGE carries an
    /// error of its own. But a context that dies inside Uno's native wrapper sets neither that nor a renderer
    /// string. The stage then stands there having drawn nothing, and the reason goes only to the logging bridge
    /// (<see cref="GlDiagnostics"/>). So a room that never came up looks exactly like one that is working, and the
    /// line written to explain the black says nothing at all. On an AMD Radeon, the report was
    /// <c>SetPixelFormat failed: The pixel format is invalid</c>, and the screen gave no hint that GL was the thing
    /// that had failed.</para>
    ///
    /// <para>The wrapper's failure is detected as "no renderer string and nothing painted", because that is the
    /// state it leaves behind. A healthy room that is still compiling is in the same state for a frame or two. It
    /// is not caught by this check, because there is no complaint on the bridge to show.</para>
    /// </summary>
    /// <summary>The GL complaint last written to the log, so the same one is not written twice.</summary>
    private string? _glSaid;

    private void UpdateStatus()
    {
        var reason = GlDiagnostics.Last;

        string? detail =
            _stage is null ? null
            : _stage.LastError is { } err ? (reason is null ? "GL: " + err : "GL: " + err + "\n" + reason)
            : _stage is { HasPainted: false, RendererInfo: null } && reason is not null ? "GL: " + reason
            : null;

        // Written once per change. The stage raises this on every compile-state change, and the same complaint written
        // again would only be noise in the log.
        if (detail is not null && detail != _glSaid)
            Said.Line("gl", $"room: {detail}");
        _glSaid = detail;

        var say = StageTrouble.Line(detail);
        StatusText.Text = say ?? "";
        StatusText.Visibility = say is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void StageHost_SizeChanged(object sender, SizeChangedEventArgs e)
        => SizeStage(e.NewSize.Width, e.NewSize.Height);

    /// <summary>
    /// Sizes the GL stage to fill the host, on every platform.
    ///
    /// <para>The stage is not drawn as a square on the larger edge. Uno 6.7's GLCanvasElement renders into a
    /// framebuffer of the element's own size. It sets the viewport to that size and reads it back at that size. So
    /// the room fills a 1024x640 window and a 1710x1073 full screen without any square. A square costs 1.6 times a
    /// 16:10 window's pixels on every frame, because the square is rendered, read back to the processor and uploaded
    /// again. On macOS that cost was the largest single cause of dropped frames in Pegasus.</para>
    /// </summary>
    private void SizeStage(double w, double h)
    {
        if (_stageElement is not { } s || w <= 0 || h <= 0)
            return;

        s.Width = w;
        s.Height = h;
    }

    // ── the three compositions ──

    /// <summary>
    /// The layout key, in the row under the record or in the foot: one key that cycles through the layouts. Three
    /// layouts is one too many for a toggle and far too few for a menu. They are ordered so that each press takes
    /// something away: first everything, then the reading of it, then the record itself. After the record it wraps
    /// back to the beginning (see <see cref="NextLayout"/>, which also keeps the choice).
    ///
    /// <para>The new layout's name shows at the top of the screen for two seconds. A key that cycles cannot show on
    /// itself where the next press goes, and a picture that changed with no word about it reads as a fault.</para>
    /// </summary>
    private void StepLayout()
    {
        // With the greeting up, the first press means "put my own screen back", not "advance one". The greeting is not
        // in the cycle. Pressing past it into the NEXT composition would take the listener somewhere they never asked
        // to go.
        if (_greeting)
        {
            EndGreeting();
            ShowKeys();
            RestartIdle();
            return;
        }

        NextLayout();
        _chrome?.ShowLayoutName(Layout);
        _chrome?.KeepLayoutFocus();

        ShowKeys();
        RestartIdle();
    }

    /// <summary>
    /// Reads the layout and the lyric key's choice from the settings, once. A layout this build does not know, or
    /// none stored, opens on the full layout. The lyric itself is not put up here. It needs a sheet, and the first
    /// sheet that arrives puts it up (see <see cref="OnLyricsChanged"/>).
    /// </summary>
    private void ReadStoredLayout()
    {
        if (_storedRead)
            return;
        _storedRead = true;

        var settings = App.Services.GetRequiredService<SettingsService>();
        _view = _beforeLyrics = Enum.TryParse<PegasusView>(settings.PegasusLayout, out var stored)
                                && stored is PegasusView.Spread or PegasusView.Centred or PegasusView.Room
            ? stored
            : PegasusView.Spread;
        _lyricSticky = settings.PegasusLyrics;
    }

    /// <summary>Keeps the layout and the lyric key's choice in the settings, so the player opens as it was left after a
    /// restart. Called on each press that changes either of them, never for a layout the view only shows (the
    /// greeting, the collection, a still taken of the room).</summary>
    private void StoreLayout()
    {
        // Each write is the whole settings file, so only what changed is written.
        var settings = App.Services.GetRequiredService<SettingsService>();
        var layout = Layout.ToString();
        if (settings.PegasusLayout != layout)
            settings.PegasusLayout = layout;
        if (settings.PegasusLyrics != _lyricSticky)
            settings.PegasusLyrics = _lyricSticky;
    }

    /// <summary>
    /// The lyric, on and off, from the lyric key. The key sits in the row under the record. When the lyric is on,
    /// the key is lit in the lyric view's foot, as the way back (see PegasusChrome.LyricViewButton). It has its own
    /// mark, next to the heart.
    ///
    /// <para>This key is not part of the cycle above. The other three views always exist, but the lyric only
    /// sometimes does. A cycle that contains something usually absent is a button whose behaviour changes from
    /// record to record. Leaving the lyric puts back whichever composition was on before, so a glance at the words
    /// leaves the screen where it found it.</para>
    /// </summary>
    private void ToggleLyrics()
    {
        // As with the layout key: the greeting is what is in front of the listener, so the first press takes it
        // away instead of reaching past it for the words.
        if (_greeting)
        {
            EndGreeting();
            ShowKeys();
            RestartIdle();
            return;
        }

        if (_view.IsLyric())
        {
            _lyricSticky = false;      // The listener asked to stop, and it stays stopped through the next record.
            _view = _beforeLyrics;
        }
        else if (_chrome is { HasLyrics: true })
        {
            // From the record view, the lyric view keeps the record beside the words. From the other views, it shows the
            // words alone (see PegasusViews.LyricViewFor).
            _lyricSticky = true;
            _beforeLyrics = _view;
            _view = PegasusViews.LyricViewFor(_view);
        }

        // The choice is kept across visits and restarts, as the layout is.
        StoreLayout();
        ApplyView(immediate: false);
        ShowKeys();
        RestartIdle();
    }

    /// <summary>
    /// The swearing and the drugs, masked or as written. This is for somebody who would rather read the sheet the
    /// way its author wrote it.
    ///
    /// <para>Slurs are not on this switch. They stay masked whichever way it is set. That is the one thing this
    /// screen does without being asked, and it will not stop doing it (see <see cref="LyricCensor"/>). What changes
    /// here is the wider list, and it changes for every record. An explicit pressing is exactly what somebody who
    /// asked for this had in mind.</para>
    ///
    /// <para>This is a setting, not a state of this screen, and it is kept as one. How a listener wants words shown
    /// holds across records, across visits to this view, and across restarts. Nothing about the sheet is fetched
    /// again. The masking happens when the text is drawn. So this is a rewrite of the text already on screen, and the
    /// scroll never loses its place.</para>
    /// </summary>
    /// <summary>
    /// Turns measuring on and off. With it on, the timing of each word is measured from the recording. With it off,
    /// each line's time is shared out across its words.
    ///
    /// <para>It is kept in settings, not for the session, the same as the censor beside it. A listener who wants the
    /// words followed properly wants it on the next record too. Nothing is fetched again, and the sheet on screen
    /// does not move. Only where its words are thought to fall changes. The lines already measured keep their answer.</para>
    /// </summary>
    private void Align_Click(object sender, RoutedEventArgs e)
    {
        // The key stays on the screen for everybody. What it does is what a listener has to be able to find out about.
        // A corner with three keys for one account and four for another is a worse screen than one that answers.
        // Nothing is stored on a refusal either. The setting keeps whatever it said, so support bought from this very
        // window turns the words on at the next press, without needing to be found again.
        if (!Reach.MayAlignWords)
        {
            _ = Dialogs.SupporterDialog.ShowAsync(
                XamlRoot, Dialogs.SupporterDialog.Occasion.Refused(Strings.Loc.Get("Supporters_Karaoke")));
            ShowKeys();
            RestartIdle();
            return;
        }

        var settings = App.Services.GetRequiredService<SettingsService>();
        settings.AlignWords = !settings.AlignWords;
        ApplyAlign();

        ShowKeys();
        RestartIdle();
    }

    /// <summary>What this account may have, of what is enforced on this machine. It is read when it is needed, not
    /// held, so support bought during the session applies without a restart.</summary>
    private static Services.Account.SupporterReach Reach =>
        Services.Account.SupporterReach.For(
            App.Services.GetRequiredService<Services.Account.SupporterService>().Standing);

    /// <summary>Puts the stored choice on the sheet and on the key that sets it. This is done the same way the
    /// censor does. The button is dressed correctly the first time the lyric view comes up in a session.
    /// </summary>
    private void ApplyAlign()
    {
        // The stored choice AND the standing. This is the one place the model can be turned on from. A supporter whose
        // support has run out still has the setting saying yes. So this checks the standing too, and the alignment is
        // turned off for them.
        var on = App.Services.GetRequiredService<SettingsService>().AlignWords && Reach.MayAlignWords;

        if (_chrome is not null)
            _chrome.AlignWords = on;

        AlignButton.Background = new SolidColorBrush(on
            ? Color.FromArgb(0xAA, 0x03, 0x71, 0xD0)
            : Color.FromArgb(0x55, 0x00, 0x00, 0x00));
        AlignButton.Foreground = new SolidColorBrush(Color.FromArgb(on ? (byte)0xFF : (byte)0xE6, 0xFF, 0xFF, 0xFF));

        IconName.Set(AlignButton, Strings.Loc.Get(on ? "Viz_AlignOn" : "Viz_AlignOff"));
    }

    /// <summary>
    /// Steps to the next sheet this record has.
    ///
    /// <para>Nothing is put on screen from here. The service replaces the record's sheet and says so. The chrome is
    /// already listening (see PegasusChrome.OnLyricReplaced). That is the same route that the pane's own button's
    /// answer arrives by. Two surfaces that show one record's words must not end up timed two ways. The way to make
    /// sure of that is for neither of them to paint its own answer.</para>
    /// </summary>
    private async void LyricAnother_Click(object sender, RoutedEventArgs e)
    {
        ShowKeys();
        RestartIdle();

        if (_lyricBusy || _amp?.Deck is not { } deck)
            return;   // The button counts presses, not a rate. With no transport there is no deck.

        if (deck.DurationSeconds <= 0 || string.IsNullOrWhiteSpace(deck.Title))
            return;

        _lyricBusy = true;
        LyricAnotherButton.IsEnabled = false;
        try
        {
            var next = await App.Services.GetRequiredService<LyricsService>()
                .AnotherAsync(deck.Artist, deck.Title, deck.Album, deck.DurationSeconds);
            if (next.IsSynced)
                App.Services.GetRequiredService<LyricSheetShare>().Chosen(deck.FilePath, next);

            // An empty answer means there was nothing else to show. This is the only sheet anybody has, or the lookup
            // could not be reached. Say so out loud. The screen is about to look exactly as it did before the press,
            // which looks the same as a key that does not work.
            if (!next.IsSynced)
                ShowFlash(Strings.Loc.Get("TrackInfo_LyricsNoOtherVersion"));
        }
        catch
        {
            // The lyric is decoration on a record that is already playing. A lookup that does not answer leaves the sheet
            // that is up alone.
        }
        finally
        {
            _lyricBusy = false;
            LyricAnotherButton.IsEnabled = true;
        }
    }

    /// <summary>Whether a question about the alternatives is already in the air. The same guard that the pane's own
    /// button keeps, for the same reason: holding the key down should not open five lookups.</summary>
    private bool _lyricBusy;

    private void Censor_Click(object sender, RoutedEventArgs e)
    {
        var settings = App.Services.GetRequiredService<SettingsService>();
        settings.MaskProfanity = !settings.MaskProfanity;
        ApplyCensor();
        ApplyAlign();

        ShowKeys();
        RestartIdle();
    }

    /// <summary>Puts the stored choice on the sheet and on the key that sets it. This is called on every press, and
    /// whenever the composition changes. That is what dresses the button correctly the first time the lyric view
    /// comes up in a session.</summary>
    private void ApplyCensor()
    {
        var on = App.Services.GetRequiredService<SettingsService>().MaskProfanity;

        if (_chrome is not null)
            _chrome.MaskProfanity = on;

        // Lit while the censor is ON. This is the same reading that the pin and the align key give. The azure disc
        // means the key is doing something, and the plain dark disc means it is standing by.
        CensorButton.Background = new SolidColorBrush(on
            ? Color.FromArgb(0xAA, 0x03, 0x71, 0xD0)
            : Color.FromArgb(0x55, 0x00, 0x00, 0x00));
        CensorButton.Foreground = new SolidColorBrush(Color.FromArgb(on ? (byte)0xFF : (byte)0xE6, 0xFF, 0xFF, 0xFF));

        // The tip says what the NEXT press does.
        IconName.Set(CensorButton, Strings.Loc.Get(on ? "Viz_CensorOff" : "Viz_CensorOn"));
    }

    /// <summary>A sheet arrived, or the record moved on and took one with it. If the lyric is on screen and there is
    /// nothing left to sing, the view moves on by itself rather than sitting blank.</summary>
    private void OnLyricsChanged()
    {
        // A sheet for the new record: back to the words, without being asked again.
        if (_lyricSticky && !_view.IsLyric() && _chrome is { HasLyrics: true })
            _view = PegasusViews.LyricViewFor(_beforeLyrics);

        // ...and away from them only once the asking is OVER. While a lookup is still out, the view holds. This is what
        // carries a listener across a change of record. Otherwise they would be bounced to the cover and back for the
        // second it takes to answer.
        else if (_view.IsLyric() && _chrome is { HasLyrics: false, LyricsPending: false })
            _view = _beforeLyrics;

        ApplyView(immediate: false);
    }

    // ── standing up ──
    //
    // Opening this view takes a few things, and each one takes a moment: a GL context, two shaders compiled against
    // the driver on the machine, a window going full screen, and, when the collection is the opening screen, a few
    // thousand records read from the database. The screen shows a cover while all of that happens, and the view
    // arrives finished.

    /// <summary>How long the cover waits before it gives up and shows whatever there is. A machine with no GL never
    /// answers. A spinner that nobody can dismiss is worse than a room that will not light.</summary>
    private static readonly TimeSpan LoadingGiveUp = TimeSpan.FromSeconds(4);

    private bool _loadingUp;

    /// <summary>What is still being waited on: the room answering, and the collection's first read where this visit
    /// opens on it. Both must be in before the cover comes off. The cover exists so that the wall never arrives
    /// under a listener who has already started reading it.</summary>
    private bool _waitStage, _waitLibrary;

    private DispatcherQueueTimer? _loadingTimer;

    /// <summary>
    /// The cover goes up before anything is stood up. It is not faded in, because there is nothing to dissolve
    /// from. The view itself is arriving underneath it.
    ///
    /// <para>The record turns on every opening, including the collection's. A wall of covers could appear without
    /// announcing itself, but that holds only for a short wait. On a first launch the collection has never been
    /// packed, and every record in it is read from disk. That is a wait long enough that a black screen with nothing
    /// turning on it looks like a hung app. So the record turns, and the counter under it shows how far the reading
    /// has got.</para>
    /// </summary>
    private void BeginLoading()
    {
        _loadingUp = true;
        Loading.Visibility = Visibility.Visible;
        _loadingFade.To(1, 0);
        LoadingRecord.Spinning = true;

        ShowWarmProgress(double.NaN);   // no figure yet: the counter stays away until there is one

        ArmLoadingGiveUp();
    }

    /// <summary>
    /// The percentage under the ring. <paramref name="done"/> is the fraction of the collection that is dressed. It
    /// is NaN for "nothing to say yet". Everything that is not the collection's first read is in that state, and the
    /// counter stays hidden in it.
    /// </summary>
    private void ShowWarmProgress(double done)
    {
        if (double.IsNaN(done))
        {
            LoadingCount.Text = "";
            LoadingCount.Visibility = Visibility.Collapsed;
            return;
        }

        // Rounded down, never up. A counter that reads 100% while records are still coming has lied about the one
        // number it exists to give.
        var percent = (int)(Math.Clamp(done, 0, 1) * 100);
        LoadingCount.Text = percent.ToString(System.Globalization.CultureInfo.CurrentCulture) + "%";
        LoadingCount.Visibility = Visibility.Visible;
    }

    /// <summary>Starts the wait's own clock. It starts AGAIN when something else joins the wait. The room is held back
    /// until the collection is in, and then it gets its four seconds, not what is left of somebody else's.</summary>
    private void ArmLoadingGiveUp(TimeSpan? patience = null)
    {
        _loadingTimer ??= DispatcherQueue?.CreateTimer();
        if (_loadingTimer is null)
            return;
        _loadingTimer.Interval = patience ?? LoadingGiveUp;
        _loadingTimer.IsRepeating = false;
        _loadingTimer.Tick -= LoadingGaveUp;
        _loadingTimer.Tick += LoadingGaveUp;
        _loadingTimer.Start();
    }

    private void LoadingGaveUp(DispatcherQueueTimer sender, object args) => EndLoading();

    /// <summary>Lifts the cover when both answers are in, or when the wait has gone on long enough.</summary>
    private void TryEndLoading()
    {
        if (!_waitStage && !_waitLibrary)
            EndLoading();
    }

    private void EndLoading()
    {
        if (!_loadingUp)
            return;

        _loadingUp = false;
        _waitStage = _waitLibrary = false;
        _loadingTimer?.Stop();
        _loadingFade.To(0, 380);

        // A room that was held back for a collection that never arrived gets its turn here. That happens when a read
        // does not answer, or when the wait runs out. It is booked once either way. The collection's own cue has
        // usually taken it long before this.
        StandUpRoomLater();

        // The record is stopped, and the cover collapses, once the fade has run. A timer is used rather than a callback,
        // because this fade has none. A turning record left in the tree at zero opacity would keep a timer repainting
        // the window behind a shader for the rest of the visit.
        DispatcherQueue?.TryEnqueue(async () =>
        {
            await Task.Delay(420);
            if (_loadingUp)
                return;
            LoadingRecord.Spinning = false;
            ShowWarmProgress(double.NaN);
            Loading.Visibility = Visibility.Collapsed;
        });
    }

    /// <summary>The room has answered. It is lit, or it has said why it is not. Either way, there is nothing more to
    /// wait for from it.</summary>
    private void OnStageAnswered()
    {
        _waitStage = false;
        TryEndLoading();
    }

    /// <summary>The collection has its records. This is also the room's cue, where the room was held back for exactly
    /// this (see <see cref="StandUpRoomLater"/>).
    ///
    /// <para>This means records, not yet pictures. While the covers are still coming in, the wait is picked up again
    /// by <see cref="OnLibraryWarming"/>. That runs first, because the loader starts before this is raised. So the
    /// flag below is not the last word on it.</para></summary>
    private void OnLibraryFilled()
    {
        if (!_libraryWarming)
            _waitLibrary = false;
        TryEndLoading();
        StandUpRoomLater();
    }

    private bool _libraryWarming;

    /// <summary>
    /// The collection is dressing itself, or it has finished.
    ///
    /// <para>Two things happen while it is dressing itself. The room stands still, and the cover stays over the wall.
    /// The first makes the second shorter. The shader and the covers want the same cores. A room left running would
    /// slow the collection down. The second is the point of the first. The listener should meet a finished wall, not
    /// one that assembles itself under them.</para>
    ///
    /// <para>The collection has its own patience, and it is a long one. The four seconds that the room is given suit
    /// a shader that has either compiled or not. A collection of several thousand records legitimately takes longer
    /// than that the first time. Cutting it off at four seconds would lift the cover over exactly the half-built wall
    /// it is there to hide.</para>
    /// </summary>
    private void OnLibraryWarming(bool warming)
    {
        _libraryWarming = warming;

        if (warming)
        {
            // There is nothing to hold for on the first visit. The room has not been stood up yet, and it is already
            // waiting its turn behind the collection (see StandUpRoomLater).
            _stage?.SetActive(false);

            _waitLibrary = true;
            if (!_loadingUp)
                BeginLoading();
            ArmLoadingGiveUp(LibraryWarmStall);
            return;
        }

        _stage?.SetActive(true);
        _waitLibrary = false;
        if (_loadingUp)
            ShowWarmProgress(1);   // the last figure seen is the one it finished on
        TryEndLoading();
        StandUpRoomLater();
    }

    /// <summary>
    /// The collection has dressed a little more of itself. The counter under the ring is updated, and the wait's own
    /// clock starts again.
    ///
    /// <para>Starting the clock AGAIN is the point. A flat time limit would lift the cover after a fixed delay,
    /// whatever the collection had got through. A library big enough to need the wait would then show a wall that is
    /// only partly dressed. The listener would see a counter reach a small number, and the black lift off an
    /// unfinished wall. What the wait is for is a read that has STOPPED, not a read that is taking a while. Progress
    /// arriving is the proof that it has not stopped. So the clock measures the gap between batches, not the length
    /// of the whole job. A collection that keeps answering is waited for however long it takes.</para>
    /// </summary>
    private void OnLibraryWarmProgress(double done)
    {
        if (!_loadingUp)
            return;   // the sweep runs on behind a wall the listener is already reading

        ShowWarmProgress(done);
        ArmLoadingGiveUp(LibraryWarmStall);
    }

    /// <summary>How long the cover waits on a collection that has stopped saying anything. This is not how long the
    /// cover waits on the collection (see <see cref="OnLibraryWarmProgress"/>). The limit is generous, because one
    /// batch of a dozen-odd records on a slow disk may take a moment. It is short enough that a read which will never
    /// answer does not hold the screen for good.</summary>
    private static readonly TimeSpan LibraryWarmStall = TimeSpan.FromSeconds(20);

    /// <summary>Puts the chosen composition on both halves at once. The chrome dissolves its words across while the
    /// stage eases the spectrum and the dim on the left away. The two changes overlap into one.
    /// <paramref name="immediate"/> is for a composition that is SHOWN in this state, rather than changed into it.</summary>
    private void ApplyView(bool immediate)
    {
        // The chosen composition is remembered across visits. The lyric may not be available when we come back to it,
        // because a different record is playing by then. Falling back here, rather than waiting for the lookup to fail,
        // keeps the opening frame from being a blank screen.
        if (_view.IsLyric() && _chrome is { HasLyrics: false, LyricsPending: false })
            _view = _beforeLyrics;

        // The lyric key appears once there is a sheet. It STAYS while a sheet is being fetched for a listener who is
        // already following along. Otherwise it would blink out and back on every change of record, and blink out from
        // under the very view it is the way out of.
        var lyric = !_library
                 && (_chrome is { HasLyrics: true }
                     || (_lyricSticky && _chrome is { LyricsPending: true }));
        // The chrome draws this key in the row under the record and in the foot, and fills both while the lyric is on.
        // Beside the record, the row is on screen and the key is the way back. Alone, the foot is, and its key is.
        _chrome?.ShowLyricKey(lyric, on: _view.IsLyric());

        // The censor key belongs to the words, and it appears only where they are. It is dressed on the way in, not only
        // on a press. So it already shows the stored choice the first time the lyric comes up, including on the run
        // after the one the choice was made in.
        AlignButton.Visibility = !_library && _view.IsLyric()
            ? Visibility.Visible
            : Visibility.Collapsed;

        CensorButton.Visibility = !_library && _view.IsLyric()
            ? Visibility.Visible
            : Visibility.Collapsed;

        // The key that asks for a different sheet altogether is about the words in the same way. It appears in the same
        // place.
        LyricAnotherButton.Visibility = !_library && _view.IsLyric()
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyCensor();
        ApplyAlign();

        // The foot of the screen is empty on every composition except one. The lyric puts its own seek bar and its own
        // keys down there (see PegasusChrome.LyricFoot). A message is lifted clear of them, not laid over them. It is
        // transient and cannot be clicked through. A sentence sitting on a playhead would look like two unrelated
        // things drawn on top of each other. Beside the record, the foot holds the column's own bar and keys, as the drawn-in view
        // does. So the message stands in the same place there.
        Flash.Margin = new Thickness(24, 0, 24, EffectiveView is PegasusView.Lyrics or PegasusView.Room ? 170 : 44);

        _chrome?.SetView(EffectiveView, immediate);
        _stage?.SetView(EffectiveView, immediate);
        _stage?.SetBrightness(RoomBrightness, immediate);
    }

    /// <summary>
    /// How bright the room is drawn behind the player: a fifth darker than behind the collection, so the lines
    /// over it read well. The collection lays its own shade over the room (the wall's 45% black, the screens'
    /// shades), and the player lays none.
    /// </summary>
    private const float PlayerRoomBrightness = 0.8f;

    /// <summary>How bright the room is drawn for the screen that is over it now (see
    /// <see cref="PlayerRoomBrightness"/>).</summary>
    private float RoomBrightness => _library ? 1f : PlayerRoomBrightness;

    /// <summary>
    /// What the chrome and the room are actually drawn as. This is the listener's chosen composition every time
    /// except while the greeting or the collection is up.
    ///
    /// <para>While the greeting is up, this is <see cref="PegasusView.Room"/>. That is the bare room, with no record,
    /// no words and no spectrum. The greeting is meant to be the only thing on the screen, and the room behind it is
    /// already doing the celebrating. For example, the mirror ball is up for some records.</para>
    ///
    /// <para>The listener's own <c>_view</c> is never written while this is in force. So taking the greeting down is
    /// one flag, and there is no state to put back, because none was taken away.</para>
    ///
    /// <para>The same holds while the collection is up. A wall of covers is the thing being looked at, and the record
    /// it is laid over would be two pictures fighting for one screen. The room behind it keeps running on whatever is
    /// playing. That is the whole point of showing the library here, rather than closing the visualizer to find
    /// something.</para>
    /// </summary>
    private PegasusView EffectiveView => _greeting || _library ? PegasusView.Room : _view;

    // ── what is playing, read off the header ──

    private void HookArt()
    {
        if (_art is null)
        {
            _art = ShellPage.Current?.Art;
            if (_art is not null)
                _art.ChromeChanged += OnChromeChanged;
        }
        OnChromeChanged();
    }

    private void UnhookArt()
    {
        if (_art is not null)
            _art.ChromeChanged -= OnChromeChanged;
        _art = null;
    }

    /// <summary>
    /// A screen over the room has asked it to wear a record that is not the one playing. This is the collection's own
    /// record screen, where a record is being READ, not heard (see PegasusChrome.Record).
    ///
    /// <para>Null puts the room's colours back. It does that by asking <see cref="OnChromeChanged"/>, rather than by
    /// restoring anything. The room should wear whatever is on the deck AT THAT MOMENT. That may differ from what was
    /// on it when the screen went up. A remembered palette would leave the room wearing a track that finished while
    /// somebody read.</para>
    /// </summary>
    private void OnRoomPalette(PegasusChrome.RoomColours? colours)
    {
        if (colours is { } wear)
            _stage?.SetPalette(wear.Tint, wear.Glow, wear.Shade);
        else
            OnChromeChanged();
    }

    /// <summary>The composition dresses itself from the header's republished chrome. It also hands the cover's colours
    /// on to the stage. That is how the room ends up the colour of the record that is playing in it.
    ///
    /// <para>This does not happen while a record is being read over the room. That screen owns the colours until it is
    /// left. A track that changes underneath it must not repaint the room out from under the sleeve somebody is looking
    /// at. See <see cref="OnRoomPalette"/>.</para></summary>
    private void OnChromeChanged()
    {
        if (_art is not { } art)
            return;
        _chrome?.Apply(art);
        if (_chrome?.RecordUp != true)
            _stage?.SetPalette(art.TrackTint, art.TrackGlow, art.TrackShade);

        // Whether the room hangs a mirror ball for this record.
        //
        // The answer is read off the DECK, not off the header beside it. Both tags come from the one place. The header
        // republishes what it prints, and it does not print a genre. So half the question would have had nowhere to
        // come from. Asking the two halves of one record's identity of two different objects is how they come to
        // disagree about which record is playing. The deck is the record on the deck. This is called from here because
        // the header republishes on every change of track, which is exactly when the answer can differ. An empty deck
        // answers no, which is what a stopped player should look like.
        _stage?.SetMirrorBall(MirrorBall.HangsFor(_amp?.Deck?.Artist, _amp?.Deck?.Genre));

        // Also watched from here: whether a New Year greeting has outlived the record it was raised for. This is where a
        // change of record arrives. The greeting first waits until the record it was raised for has reached the deck.
        // It comes down only once that record is gone. Without this order, the greeting would cancel itself in the
        // moment between being put up and the deck catching up with it.
        if (_greeting)
        {
            if (string.Equals(_amp?.Deck?.FilePath, _greetingFor, StringComparison.Ordinal))
                _greetingLanded = true;
            else if (_greetingLanded)
                EndGreeting();
        }
    }

    // ── the keys stand down ──
    //
    // Five seconds after the last pointer move, everything that is not the picture fades out, so the record is alone
    // on the screen. The next move brings it back. The pin holds it up indefinitely. The fade changes the opacity of
    // ONE container only. That is a composition-only property, so an idle screen does not pay for a layout pass. A
    // layout pass is exactly what the stage cannot afford (see PipelineProbe).

    private void Root_PointerActivity(object sender, PointerRoutedEventArgs e)
    {
        // The ring stands down while the pointer is in use (see PegasusPlayer.Remote). This applies only to a pointer
        // that really moved or pressed. Content that moves under a resting cursor, such as the view scrolling to the
        // focus, also raises pointer events. Those must not take keyboard mode away.
        if (PointerReallyUsed(e))
        {
            PointerUsed();
            ShowKeys();
            RestartIdle();
        }
    }

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        _pinned = !_pinned;

        // Pinned wears the RPM azure the active rail buttons use; loose is the plain dark disc.
        PinButton.Background = new SolidColorBrush(_pinned
            ? Color.FromArgb(0xAA, 0x03, 0x71, 0xD0)
            : Color.FromArgb(0x55, 0x00, 0x00, 0x00));
        PinButton.Foreground = new SolidColorBrush(Color.FromArgb(_pinned ? (byte)0xFF : (byte)0xE6, 0xFF, 0xFF, 0xFF));
        IconName.Set(PinButton, Strings.Loc.Get(_pinned ? "Viz_Unpin" : "Viz_Pin"));

        ShowKeys();
        RestartIdle();
    }

    /// <summary>Restarts the five-second countdown, or stops it while the keys are pinned.</summary>
    private void RestartIdle()
    {
        if (_pinned)
        {
            _idle?.Stop();
            return;
        }
        _idle ??= CreateIdleTimer();
        _idle?.Stop();
        _idle?.Start();
    }

    private DispatcherQueueTimer? CreateIdleTimer()
    {
        if (DispatcherQueue is null)
            return null;
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = IdleDelay;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => HideKeys();
        return timer;
    }

    private void ShowKeys(bool immediate = false)
    {
        FullScreenWindow.SetCursorHidden(false);
        Keys.IsHitTestVisible = true;
        _keysFade.To(1, immediate ? 0 : 140);
    }

    private void HideKeys()
    {
        if (_pinned || !IsShowing)
            return;

        // A paused record holds the controls up, and so does the cast menu (see PegasusChrome.HoldsControls).
        // The keys here and the pointer stay with them, and the count starts again.
        if (_chrome is { HoldsControls: true })
        {
            RestartIdle();
            return;
        }

        Keys.IsHitTestVisible = false;   // faded keys must not swallow the pointer
        _keysFade.To(0, 420);
        PegasusChrome.CloseOpenToolTips(XamlRoot);   // a tooltip the remote's focus opened goes with its key

        // The pointer goes with them. It is the last thing on the screen that is not the record. A pointer parked over a
        // record turning full screen is as much of an intrusion as the buttons that just left. The pin holds it up with
        // everything else. Somebody who pinned the controls wants something to point at them with.
        FullScreenWindow.SetCursorHidden(true);
    }

    // ── leaving ──

    /// <summary>How close together two presses of Back have to be to leave the view from the side menu over home: the
    /// TV's EXIT_WINDOW_MS. It was half a second, the system's double-click time, when two quick presses on home were
    /// the only way out; the first press now shows a notice, and two seconds is time to read it and press again.</summary>
    private const long DoubleEscapeMs = 2000;

    /// <summary>When Back last asked to leave and found nothing else to go back from, in
    /// <see cref="Environment.TickCount64"/> milliseconds. It is far in the past until then, and again after a leave.
    /// So a third press does not pair with the second.</summary>
    private long _escapeAt = long.MinValue / 2;

    private void Close_Click(object sender, RoutedEventArgs e) => Leave();

    /// <summary>Cmd or Ctrl+F while the view is up: opens the collection's wall and its search field (see
    /// <see cref="PegasusChrome.SearchCollection"/>).</summary>
    public void SearchCollection() => _chrome?.SearchCollection();

    /// <summary>
    /// Back, from the remote or Escape. The shell hands it over (see ShellPage.OnPreviewKeyDown) while this view is
    /// up. Each press goes up one level from wherever the listener is.
    ///
    /// <para>First, anything open over the screen is closed. That includes a track's menu, a queue row that the keys
    /// are carrying, the wall's menu, the drawer, the search field, a record's or a list's page, and the greeting. A
    /// record open in the drawer goes back to the drawer's shelf before the drawer closes. Then the screen itself goes
    /// up one level. The lyric view goes back to the record turning, the wall goes back to home, and the record turning
    /// goes back to home. On home, with nothing left above it, Back opens the side menu, as on the TV. In the menu over
    /// home, Back does not close it: the first press says along the foot that a second leaves, and a second within two
    /// seconds leaves the view. The menu's Exit leaves at once. One stray press must not take the view away, and the
    /// notice says why the first press did nothing.</para>
    ///
    /// <para>The shell takes this on the way DOWN, from the window's root. It is not taken by this view on the way up
    /// from the focused control. On the way up, the key reached this view only while the focus was inside it. After a
    /// change of screen it often is not, because the control that had the focus is collapsed and the focus goes to
    /// nothing. The key would be lost.</para>
    /// </summary>
    /// <param name="repeat">The key is being held down. A repeat does nothing. If it were not ignored, holding Back would
    /// walk up every level at the keyboard's repeat rate.</param>
    public void TakeBack(bool repeat)
    {
        if (repeat)
            return;

        if (CloseLineMenu())
        {
            Stepped("closed a track's menu");
            return;
        }
        if (ClosePopupWithFocus())
        {
            Stepped("closed a menu");
            return;
        }
        if (_chrome is { QueueMoving: true })
        {
            _chrome.EndQueueMove(place: false);
            Stepped("put the carried queue row back");
            return;
        }
        if (_chrome?.CloseMenuPanel() == true)
        {
            Stepped("closed the menu's panel");
            return;
        }
        if (_chrome is { MenuOverHome: true })
        {
            BackTowardsLeaving("in the menu over home");
            return;
        }
        if (_chrome is { LibraryMenuOpen: true })
        {
            _chrome.ShowLibraryMenu(false);
            Stepped("closed the menu");
            return;
        }
        if (_chrome?.CloseRadioOverlay() is { } closed)
        {
            Stepped("closed radio's " + closed);
            return;
        }
        if (_chrome is { IsQueueOpen: true })
        {
            // A record or a list open in the drawer goes back to the shelf it came from. Otherwise the drawer closes.
            if (_chrome.DrawerBack())
            {
                Stepped("drawer: record to the shelf");
                return;
            }
            _chrome.SetQueueOpen(false);
            Stepped("closed the drawer");
            return;
        }
        if (_chrome?.CloseCarouselLetters() == true)
        {
            Stepped("closed the album carousel's letters");
            return;
        }
        if (_chrome is { IsLibrarySearchFocused: true })
        {
            _chrome.DropLibrarySearchFocus();
            Stepped("left the search field");
            return;
        }
        if (_chrome is { RecordUp: true })
        {
            _chrome.HideRecord();
            Stepped("closed the page");
            return;
        }
        if (_chrome is { TunerUp: true })
        {
            _chrome.HideTuner();
            Stepped("closed the tuner");
            return;
        }
        if (_greeting)
        {
            EndGreeting();
            Stepped("ended the greeting");
            return;
        }

        // The screens themselves, each to the one above it.
        if (_chrome is { LibraryUp: false } && _view.IsLyric())
        {
            ToggleLyrics();
            Stepped("lyric view to the record turning");
            return;
        }
        if (_chrome is { WallInFront: true })
        {
            _chrome.BackToHome();
            Stepped("wall to home");
            return;
        }
        if (_chrome is { RadioHomeInFront: true })
        {
            _chrome.BackToHome();
            Stepped("radio to home");
            return;
        }
        if (_chrome is { ArtistsGenresInFront: true })
        {
            _chrome.StepBackFromArtistsGenres();
            Stepped("artists and genres: one step back");
            return;
        }
        if (_chrome is { LibraryUp: false })
        {
            _chrome.ShowHome();
            Stepped("record turning to home");
            return;
        }

        // Home, with nothing above it: the side menu, where Back twice leaves. A press before this one does not
        // pair with the next: the count starts in the menu.
        if (_chrome?.OpenMenuByBack() == true)
        {
            _escapeAt = long.MinValue / 2;
            Stepped("home: opened the side menu");
            return;
        }

        // Nowhere to open the menu (no collection to stand it on): the view itself, on the second press.
        BackTowardsLeaving("with nothing above");

        // The second press within the window leaves the view; the first says so along the foot and waits.
        void BackTowardsLeaving(string where)
        {
            var now = Environment.TickCount64;
            if (now - _escapeAt <= DoubleEscapeMs)
            {
                _escapeAt = long.MinValue / 2;
                Say($"back: second press {where}, leaving");
                Leave();
                return;
            }

            _escapeAt = now;
            ShowFlash(Strings.Loc.Get("Viz_ExitDoublePress"));
            ShowKeys();
            RestartIdle();
            Say($"back: first press {where}, waiting for a second");
        }

        void Stepped(string what)
        {
            ShowKeys();
            RestartIdle();
            Say("back: " + what);

            // Whatever had the focus may have been put away with what was closed. A focus left on nothing goes to the
            // screen's own default. That happens on the chrome's frame tick, once the screen can take it (see
            // PegasusChrome.TickFocus).
            _chrome?.RefocusIfLost();
        }

        static void Say(string line) => RPM.Core.Diagnostics.Scope.Said("window", line);
    }

    /// <summary>
    /// The space bar plays and pauses wherever the focus is in this view, except in a field being typed in. It uses
    /// the same command the deck's own transport runs. So this view is never a second opinion about what the player is
    /// doing. The keys come back up with it. Pressing pause and seeing nothing change looks like a dropped keypress.
    ///
    /// <para>This is taken on the way DOWN, before the focused control acts on the key. A key in this view keeps the
    /// focus after it is pressed. Examples are the collection's key under the words, a card on home, and the
    /// collection's back key. A focused button presses itself on the space bar. So the space bar pressed the last key
    /// again instead of pausing. After going back to home, it moved on to the record turning. After opening the wall,
    /// it did nothing at all. The shell leaves the key to this view while it is up (see SpaceBar), so this is the one
    /// place that answers it.</para>
    /// </summary>
    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Space || e.OriginalSource is TextBox)
            return;

        e.Handled = true;

        // Held down, it pauses once, rather than playing and pausing at the keyboard's repeat rate.
        if (HeldKeys.IsRepeat(e))
            return;

        _amp?.PlayPauseCommand.Execute(null);
        ShowKeys();
        RestartIdle();
    }

    /// <summary>The other half of <see cref="Root_PreviewKeyDown"/>. A focused button presses itself when the space bar
    /// comes back up, so the release is taken as well.</summary>
    private void Root_PreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Space && e.OriginalSource is not TextBox)
            e.Handled = true;
    }

    /// <summary>The user asked to leave. The view is not taken down here. <see cref="Closed"/> is the view-mode
    /// service's cue to move the app back to the library. The service is the one that puts each view away, so exactly
    /// one thing decides what is on screen.</summary>
    private void Leave() => Closed?.Invoke();
}
