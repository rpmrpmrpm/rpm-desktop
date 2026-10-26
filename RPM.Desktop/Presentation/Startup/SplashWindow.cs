#if RPM_WINUI
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Graphics;
using WinRT;
#else
using Microsoft.UI.Xaml;
#endif

namespace RPM.Desktop.Presentation.Startup;

/// <summary>
/// The startup, in a window of its own: the rpm mark alone in the middle of the screen, standing on the
/// desktop with nothing behind it, and no app window behind it until the app is ready to be looked at.
///
/// <para><b>The window is clear</b> (see <see cref="Clear"/>): a fully transparent brush from the system
/// compositor in the slot desktop acrylic uses, with the frame extended over the whole window. Two things
/// break it, both found on screen as an opaque black card: the layered style (so a clear window fades its
/// mark on the compositor, not its alpha) and non-client rendering turned off (so it is not cut or given
/// <c>WindowEdges.Round</c>). Where the clear backdrop is refused, the window is the glass card described
/// below, with its edge; everything about the glass applies to that fallback.</para>
///
/// <para><b>Why a separate window.</b> If the splash were a black rectangle inside the main window, the
/// first thing on screen would be a full-size black window at whatever size and place Windows opened
/// it, and it would resize itself when the splash came off. Instead the main window is cloaked during
/// startup (see <see cref="Services.Platform.WindowCloak"/>) and appears once, fully drawn and at its
/// saved size.</para>
///
/// <para><b>The glass is the system's.</b> Desktop acrylic blurs and tints whatever is behind the
/// window, which is the desktop. A transparent root, <c>DwmExtendFrameIntoClientArea</c> alone and the
/// accent policy all leave a WinUI window opaque (see <see cref="Services.Platform.WindowBackground"/>);
/// the clear backdrop above is the one other way through.</para>
///
/// <para><b>Driven by a controller, not by <c>Window.SystemBackdrop</c>.</b> The XAML backdrop follows
/// the window's activation and drops to a flat fallback colour when the window is not the active one.
/// This window is never the active one for long, because the main window is activated just after it,
/// so the card would be flat grey. The controller is told <c>IsInputActive = true</c> once and never
/// told otherwise, so the glass does not depend on where the focus is.</para>
///
/// <para><b>The edge belongs to the card and is there from the first frame.</b> The compositor draws a
/// pale line round a window and, on a LAYERED window, cannot be told otherwise: both
/// <c>DWMWA_WINDOW_CORNER_PREFERENCE</c> and <c>DWMWA_BORDER_COLOR</c> return S_OK and change nothing
/// (see also <see cref="Services.Platform.WindowShape.CutInset"/>). <c>DWMWA_NCRENDERING_POLICY</c> set
/// to <c>DWMNCRP_DISABLED</c> (the older call, which means draw no non-client area at all) IS honoured
/// on the same window. With it, the only edge on screen is the one that the card draws: one pixel of
/// #2EFFFFFF, the same as the home banner's card edge. The shape is cut with a region for the same
/// reason: the compositor's own line cannot be changed in another way.</para>
///
/// <para>Do not remove the layered style once the entrance has finished. That does remove the line, but
/// then the edge CHANGES a few hundred milliseconds after the card appears, and an edge must not
/// change.</para>
///
/// <para><b>The fade is not on the drawing thread.</b> That thread is building the interface for the
/// whole life of this window, and a fade driven from it would be very slow (see
/// <see cref="Shell.SplashFade"/>). The window's own alpha is a layered-window attribute, stepped from
/// a pool timer, so the drawing thread is not involved. The glass fades with the card, so the layered
/// style and desktop acrylic work together. Where the style is refused, the card's contents fade on
/// the compositor instead and the glass appears in one step.</para>
///
/// <para><b>The fade starts on a drawn frame, not on a timer.</b> A window that has just been activated
/// is a black rectangle for a few hundred milliseconds while the glass and the card are composed. A
/// fade that starts at Activate would fade in that black and then jump to the card. This is why the
/// entrance waits on <see cref="Services.Shell.NextFrames"/>.</para>
///
/// <para><b>Two desktop heads, two routes.</b> Everything above is the WinUI half. macOS does the same
/// with AppKit: a borderless floating NSWindow, <c>NSWindow.alphaValue</c> animated by Core Animation,
/// and a corner radius on the content view's layer. It paints its own glass, because a Skia surface
/// cannot be seen through on that head. The second half of this file is that code, and
/// <see cref="Services.Platform.MacWindowCard"/> is what it calls. On every other head this class
/// returns false and the shell keeps the in-window overlay.</para>
/// </summary>
internal static class SplashWindow
{
#if RPM_WINUI
    /// <summary>The card, in logical pixels. The mark fills it, 24 in from either side (see
    /// SplashCard.xaml).</summary>
    private const int CardWidth = 300;

    private const int CardHeight = 180;

    /// <summary>The corner radius. Two values must agree: the region that the window is cut to (see
    /// <see cref="Shape"/>) and the <c>CornerRadius</c> of the card's border. If the border is rounder
    /// than the cut, the corners are chopped off. If it is squarer, a sliver of glass shows outside the
    /// edge. Eight is the radius that Windows 11 gives a window, so the card matches the windows around
    /// it.</summary>
    private const int CardRadius = 8;

    /// <summary>How long the card takes to appear, and how long it takes to go once the app is up.</summary>
    private const int FadeInMs = 420;

    private const int FadeOutMs = 320;

    /// <summary>How many frames the entrance waits for before it starts. A window that has just been
    /// activated is a BLACK RECTANGLE (for about 400 ms) until the glass and the card have been
    /// composed, and a fade started before that would fade in the black and then jump to the
    /// card.</summary>
    private const int DrawnFrames = 2;

    /// <summary>How long the entrance waits for those frames before it starts anyway. A card that
    /// appears late without its fade is better than a card that never appears.</summary>
    private static readonly TimeSpan DrawnBackstop = TimeSpan.FromMilliseconds(700);

    private static Window? _window;

    private static IntPtr _hwnd;

    /// <summary>Whether the window's alpha can be driven. False means the layered style was refused and
    /// the contents are faded instead, which needs different code.</summary>
    private static bool _layered;

    /// <summary>The fade in progress, kept so that a dismiss during the entrance stops it and does not
    /// race it. It is one field, because there is never more than one fade: the entrance is over long
    /// before the earliest reveal, and the reveal happens once.</summary>
    private static System.Threading.Timer? _fade;

    /// <summary>The glass, and the configuration it reads. Both are kept for the life of the window: if
    /// the controller is garbage collected, the backdrop goes with it, and the controller keeps reading
    /// the configuration object and does not copy its values.</summary>
    private static DesktopAcrylicController? _glass;

    private static SystemBackdropConfiguration? _glassConfig;

    /// <summary>The window as the controller was given it. It is kept so that the SAME target can be
    /// removed later: <c>As&lt;&gt;</c> asks for an interface and need not return the same object it
    /// returned before, and removing a different object removes nothing.</summary>
    private static ICompositionSupportsSystemBackdrop? _glassTarget;

    /// <summary>Whether the window is clear (see <see cref="Clear"/>) rather than glass.</summary>
    private static bool _clear;

    /// <summary>The window as the clear backdrop was given to it, so the same target is emptied on
    /// close.</summary>
    private static ICompositionSupportsSystemBackdrop? _clearTarget;

    /// <summary>The system compositor that made the clear brush, and the thread's system dispatcher queue
    /// that compositor needs. Both are kept for the life of the process: a queue controller that is
    /// released ends the queue, and a brush's compositor must outlive it.</summary>
    private static Windows.UI.Composition.Compositor? _clearCompositor;

    private static IntPtr _systemQueue;

    /// <summary>Whether the card has been told to go. A second dismiss would start a second fade from
    /// full strength, so the card would come back for about a third of a second. There IS a second
    /// caller: the backstop in App, which cannot know whether the reveal got there first.</summary>
    private static bool _going;

    /// <summary>True from the moment the card is raised until the window has closed. The shell checks
    /// this before running its own reveal, because there is nothing to reveal inside a window that
    /// nobody has seen.</summary>
    public static bool IsUp => _window is not null;

    private static readonly TaskCompletionSource DrawnSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the card has been drawn (or its backstop has passed), at the moment its
    /// fade starts. OnLaunched waits for it before building the shell on Windows, so the card is on
    /// screen while the shell's XAML is built and not after.</summary>
    public static Task Drawn => DrawnSource.Task;

    /// <summary>
    /// Raises the card. Called once, on the interface's thread, before the main window is activated.
    /// </summary>
    /// <returns>False when there is no window to show (a head without acrylic, or a call that threw).
    /// The caller must then leave the main window visible, because a cloaked window with no splash is an
    /// app that never appears.</returns>
    public static bool Show()
    {
        if (_window is not null)
            return true;

        try
        {
            var window = new Window { Title = "rpm" };
            var card = new SplashCard();
            window.Content = card;

            // Nothing behind the mark: the desktop shows through. Where the window cannot be made clear,
            // it is the glass card it used to be, with its edge.
            _clear = Clear(window);
            if (!_clear)
            {
                Glass(window);
                card.DrawEdge();
            }

            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

            // No border, no title bar, no buttons, and not in alt-tab: it is a card and not a window
            // that anybody can use. It is always on top because the main window is activated after it
            // and would otherwise come up in front. That window is cloaked, so the user would see the
            // card disappear for no visible reason.
            if (RPM.Desktop.Services.Platform.WindowPresenter.Overlapped(window.AppWindow) is { } presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            if (window.AppWindow is { } appWindow)
                appWindow.IsShownInSwitchers = false;

            // The frame comes off BEFORE the window is sized. If it is removed afterwards, the window
            // stays 300x180 but its client area GROWS by the frame's two pixels, and the XAML island
            // keeps the size it laid out at. The card's own border would then be drawn at 298x178, and
            // its bottom and right edges would fall outside what is on screen.
            Frameless();

            // Make it invisible BEFORE it is shown. Otherwise the first frame is a card at full
            // strength and the fade starts from where it should end.
            //
            // A clear window is never layered: the layered style composes the window without its
            // per-pixel alpha, and the clear backdrop comes out black. Its mark fades on the compositor
            // instead, from nothing.
            if (_clear)
            {
                _layered = false;
                ElementCompositionPreview.GetElementVisual(card).Opacity = 0;
            }
            else
            {
                _layered = Alpha.Prepare(_hwnd);
            }

            Place(window);
            window.Activate();

            // The shape, and removing the compositor's own line from the window. Both happen after the
            // window is on screen, because before that nothing is composed to shape or to draw on.
            //
            // A clear window has no shape to cut and no line to hide behind an edge of its own: DWM is
            // told to draw no line and no rounding (it listens on a window that is not layered), and the
            // frame is extended over the whole window, which is what lets the clear backdrop show the
            // desktop. Non-client rendering must stay on for that, so WindowEdges.Round is not used.
            if (_clear)
            {
                Services.Platform.WindowEdges.Square(_hwnd, true, "splash");
                ExtendFrame();
            }
            else
            {
                Cut();
                Services.Platform.WindowEdges.Round(_hwnd, "splash");
            }

            _window = window;

            // The entrance starts when there is something to fade IN, not when the window is shown.
            // A window that has just been activated is a BLACK RECTANGLE for about 400 ms (the glass
            // and the card are composed after it), and a fade started at Activate would fade in the
            // black and then jump to the card.
            Services.Shell.NextFrames.After(window.DispatcherQueue, DrawnFrames, DrawnBackstop, drawn =>
            {
                DrawnSource.TrySetResult();
                if (_layered)
                    Alpha.To(255, FadeInMs, done: null);
                else
                    FadeContents(from: 0, to: 1, FadeInMs, done: null);

                RPM.Core.Diagnostics.Scope.Said(
                    "boot",
                    $"splash fades in ({(_layered ? "window" : "content")}, "
                    + $"{(drawn ? "drawn" : "backstop")})");
            });

            RPM.Core.Diagnostics.Scope.Said("boot", $"splash up ({(_clear ? "clear" : "glass")})");
            return true;
        }
        catch (Exception e)
        {
            // A head with no acrylic, or a window that would not open. Nothing has been hidden yet,
            // because the caller cloaks the main window only if this returned true. The startup
            // continues as if there were no splash window.
            RPM.Core.Diagnostics.Scope.Said("boot", $"splash refused: {e.GetType().Name}");
            Close();
            return false;
        }
    }

    /// <summary>
    /// Fades the card off and closes it. Called on the interface's thread once the app itself is
    /// visible, so the card dissolves over the interface and not over the desktop.
    /// </summary>
    public static void Dismiss()
    {
        if (_window is null || _going)
            return;
        _going = true;

        if (_layered)
        {
            Alpha.To(0, FadeOutMs, done: Close);
            return;
        }

        FadeContents(from: 1, to: 0, FadeOutMs, done: Close);
    }

    /// <summary>
    /// Closes the card at once, whatever state it is in. It is also the way out of a startup that ended
    /// in another way: the main window closing during it, or a fade that never reported.
    /// </summary>
    public static void Close()
    {
        var window = _window;
        _window = null;
        _hwnd = IntPtr.Zero;
        _going = false;

        try { _fade?.Dispose(); } catch { }
        _fade = null;

        if (window is null)
        {
            // Nothing to close: a card that never opened, or one that is already closed. The
            // controller is still released, because Glass may have created one.
            Unglass();
            return;
        }

        // A window must be closed on its own thread. The fade's completion arrives on a pool thread
        // (that is the purpose of that design), so the pool thread cannot call Close directly.
        var queue = window.DispatcherQueue;

        // Two turns of that thread, in this order, and never fewer. See Unglass for what the second
        // turn waits for.
        if (queue is null
            || !queue.TryEnqueue(() =>
               {
                   Unglass();
                   if (!queue.TryEnqueue(() => { try { window.Close(); } catch { } }))
                       try { window.Close(); } catch { }
               }))
        {
            // There is no queue, or it has stopped taking work: the app is closing and nothing will
            // run the queued work.
            Unglass();
            try { window.Close(); } catch { }
        }
    }

    /// <summary>
    /// Takes the glass off the card.
    /// </summary>
    /// <remarks>
    /// <b>The target is removed before the controller is disposed, and the window must not be closed in
    /// the same turn as either.</b> Otherwise the app crashes on the frame where the main window is
    /// first drawn, which is the frame where this card is dismissed. The crash looks like this:
    ///
    /// <code>
    /// Microsoft_UI_Xaml!DirectUI::DesktopWindowImpl::put_SystemBackdrop+0x20
    ///   mov rax,qword ptr [rcx]   rcx = 0x58
    /// Microsoft_UI_Xaml!DirectUI::Window::put_SystemBackdrop
    /// wuceffectsi!SystemBackdropInternal::BaseController::SetSystemBackdropOnTarget
    /// CoreMessaging!Windows::System::DispatcherQueue::DeferInvokeCallback
    /// </code>
    ///
    /// <para>The cause is a null <c>this</c> plus a field offset. The controller does not tear itself
    /// down inline: it POSTS <c>SetSystemBackdropOnTarget</c> onto the window's dispatcher queue. If the
    /// controller is disposed and the window is closed straight away, the window's
    /// <c>DesktopWindowImpl</c> is destroyed while that posted call is still in the queue, and the call
    /// then writes to it. Whether it crashes depends on which of the two runs first, so it happens on
    /// some launches and not on others.</para>
    ///
    /// <para>So the order is: remove the target, dispose, and let the queue run once before the window
    /// is destroyed. This also runs on the window's OWN thread: <c>Close</c> is reached from the fade's
    /// completion, which is a pool thread, and the controller's teardown belongs on the thread where
    /// its target lives.</para>
    ///
    /// <para>Every step is caught on its own. A controller that will not release its target is not a
    /// reason to leave it undisposed, and not a reason to leave the card on screen.</para>
    /// </remarks>
    private static void Unglass()
    {
        // The clear backdrop is emptied the same way and on the same turn, before the window closes.
        var clear = _clearTarget;
        _clearTarget = null;
        _clear = false;
        try { if (clear is not null) clear.SystemBackdrop = null; } catch { }

        var glass = _glass;
        var target = _glassTarget;

        _glass = null;
        _glassTarget = null;
        _glassConfig = null;

        if (glass is null)
            return;

        try
        {
            if (target is not null)
                glass.RemoveSystemBackdropTarget(target);
        }
        catch { }

        try { glass.Dispose(); } catch { }
    }

    /// <summary>
    /// Makes the window clear, so the mark stands on the desktop itself.
    /// </summary>
    /// <remarks>
    /// <para>None of the usual routes does this for a WinUI window (see
    /// <see cref="Services.Platform.WindowBackground"/>). What does is the slot desktop acrylic is put in:
    /// the window's system backdrop, given a fully transparent colour brush instead of a controller. The
    /// brush must come from the SYSTEM compositor (Windows.UI.Composition), not WinUI's, and that
    /// compositor needs a system dispatcher queue on this thread, which a WinUI thread does not have of
    /// its own.</para>
    ///
    /// <para>False when any part of it is refused; the caller then puts the glass up instead.</para>
    /// </remarks>
    private static bool Clear(Window window)
    {
        try
        {
            if (Windows.System.DispatcherQueue.GetForCurrentThread() is null && _systemQueue == IntPtr.Zero)
            {
                var options = new DispatcherQueueOptions
                {
                    Size = Marshal.SizeOf<DispatcherQueueOptions>(),
                    ThreadType = DQTYPE_THREAD_CURRENT,
                    ApartmentType = DQTAT_COM_STA,
                };
                if (CreateDispatcherQueueController(options, out _systemQueue) != 0)
                    return false;
            }

            _clearCompositor ??= new Windows.UI.Composition.Compositor();
            var target = window.As<ICompositionSupportsSystemBackdrop>();
            target.SystemBackdrop = _clearCompositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            _clearTarget = target;
            return true;
        }
        catch
        {
            _clearTarget = null;
            return false;
        }
    }

    /// <summary>Extends the frame over the whole client area (-1 on every side), for a clear window.</summary>
    private static void ExtendFrame()
    {
        if (_hwnd == IntPtr.Zero || !OperatingSystem.IsWindows())
            return;

        try
        {
            var margins = new FrameMargins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            DwmExtendFrameIntoClientArea(_hwnd, ref margins);
        }
        catch { }
    }

    /// <summary>
    /// Takes the window's frame off.
    /// </summary>
    /// <remarks>
    /// <para><b>The compositor cannot do either of these for this window.</b> Asking DWM to round the
    /// corners and to draw no border BOTH return S_OK and BOTH do nothing here (the diagnostic ring
    /// shows <c>corners 0x00000000, border 0x00000000</c> with a square-cornered card and a solid white
    /// thin line on the screen). The window is layered, which is how the fade works, and a layered
    /// window is composed from its own surface and is not decorated by the compositor.</para>
    ///
    /// <para>So the frame is removed with the style bits, which is where the white line comes from, and
    /// the rounding is CUT with a window region and not requested. The region's edge is not
    /// antialiased. The card's own one-pixel border is drawn at the same radius just inside it, and
    /// that border is what the user sees as the edge.</para>
    /// </remarks>
    private static void Frameless()
    {
        if (_hwnd == IntPtr.Zero || !OperatingSystem.IsWindows())
            return;

        try
        {
            var style = GetWindowLongPtr(_hwnd, GWL_STYLE).ToInt64();
            var wanted = (style & ~(WS_CAPTION | WS_THICKFRAME | WS_BORDER | WS_DLGFRAME | WS_SYSMENU))
                         | WS_POPUP;

            if (wanted != style)
            {
                SetWindowLongPtr(_hwnd, GWL_STYLE, new IntPtr(wanted));

                // The styles are read at the next frame change and not before, so the window is told
                // to recompute its frame without being moved, resized or raised.
                SetWindowPos(
                    _hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
        }
        catch { /* the window keeps its frame, so the card has a window edge round it */ }
    }

    /// <summary>
    /// Cuts the window to the card's rounded rectangle.
    /// </summary>
    /// <remarks>
    /// There is no inset. Moving the region a pixel inward does not help against the compositor's own
    /// line, because the line follows the window's SHAPE and not its rectangle. What removes that line
    /// is <c>DWMWA_NCRENDERING_POLICY</c>; see the remarks on this class.
    /// </remarks>
    private static void Cut()
    {
        if (_hwnd == IntPtr.Zero || !OperatingSystem.IsWindows())
            return;

        var scale = GetDpiForWindow(_hwnd) / 96.0;
        Services.Platform.WindowShape.CutInset(_hwnd, inset: 0, CardRadius, scale);
    }

    /// <summary>
    /// Puts desktop acrylic behind the card and keeps it there.
    /// </summary>
    /// <remarks>
    /// A machine that cannot do it loses nothing important: if <c>IsSupported</c> returns false, or the
    /// cast fails, the window keeps its flat background and the card is a plain dark panel. That looks
    /// worse but is not broken.
    /// </remarks>
    private static void Glass(Window window)
    {
        try
        {
            if (!DesktopAcrylicController.IsSupported())
                return;

            _glassConfig = new SystemBackdropConfiguration
            {
                // Never dimmed. The main window is activated just after this one, and the usual rule
                // (a backdrop goes flat when its window loses focus) would remove the glass on the
                // frame where the app's own window comes up behind it.
                IsInputActive = true,
                Theme = SystemBackdropTheme.Dark,
            };

            // Thin, not Base. Base is the dark smoked glass that a settings dialog sits behind. Thin
            // lets the colour of what is underneath show through, so it looks like bright glass and
            // not like a dark panel.
            _glass = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Thin };
            _glass.SetSystemBackdropConfiguration(_glassConfig);

            _glassTarget = window.As<ICompositionSupportsSystemBackdrop>();
            _glass.AddSystemBackdropTarget(_glassTarget);
        }
        catch
        {
            _glass = null;
            _glassConfig = null;
            _glassTarget = null;
        }
    }

    /// <summary>Centres the card on the screen that the pointer is on. The size is in physical pixels,
    /// which is what <c>MoveAndResize</c> uses, scaled by the display density, so the card has the same
    /// apparent size on every display and is not tiny on a dense one.</summary>
    private static void Place(Window window)
    {
        var scale = (_hwnd != IntPtr.Zero ? GetDpiForWindow(_hwnd) : 96u) / 96.0;
        var width = (int)Math.Round(CardWidth * scale);
        var height = (int)Math.Round(CardHeight * scale);

        var screen = Screen();
        window.AppWindow?.MoveAndResize(new RectInt32(
            screen.X + (screen.Width - width) / 2,
            screen.Y + (screen.Height - height) / 2,
            width,
            height));
    }

    /// <summary>The whole of the display that the pointer is on: its outer bounds, not its work area.
    /// The card is centred on the SCREEN, and the work area is the screen minus the taskbar, which would
    /// put the card a little too high. It falls back to the primary display when the pointer position
    /// is not available.</summary>
    private static RectInt32 Screen()
    {
        try
        {
            if (GetCursorPos(out var point))
            {
                var at = DisplayArea.GetFromPoint(
                    new PointInt32(point.X, point.Y), DisplayAreaFallback.Nearest);
                if (at is not null)
                    return at.OuterBounds;
            }
        }
        catch { }

        var primary = DisplayArea.Primary;
        return primary is not null
            ? primary.OuterBounds
            : new RectInt32(0, 0, CardWidth, CardHeight);
    }

    /// <summary>
    /// The fade for a window whose alpha cannot be driven: the card's CONTENTS, on the compositor, with
    /// the glass appearing in one step behind them. This is the second-best option: it cannot fade the
    /// glass, but it does not use the drawing thread, which is what matters.
    /// </summary>
    private static void FadeContents(double from, double to, int ms, Action? done)
    {
        if (_window?.Content is not UIElement content)
        {
            done?.Invoke();
            return;
        }

        try
        {
            var visual = ElementCompositionPreview.GetElementVisual(content);
            var compositor = visual.Compositor;

            var ease = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.215f, 0.61f), new Vector2(0.355f, 1f));

            var fade = compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, (float)from);
            fade.InsertKeyFrame(1f, (float)to, ease);
            fade.Duration = TimeSpan.FromMilliseconds(ms);

            if (done is null)
            {
                visual.StartAnimation("Opacity", fade);
                return;
            }

            // The completion of a composition animation does not come back on its own. A scoped batch
            // asks for it. See SplashFade, which has the same trap.
            var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            visual.StartAnimation("Opacity", fade);
            batch.End();
            batch.Completed += (_, _) => done();
        }
        catch
        {
            done?.Invoke();
        }
    }

    /// <summary>
    /// The window's own alpha, stepped from a pool thread.
    /// </summary>
    /// <remarks>
    /// <para>The desktop compositor applies a layered window's alpha over everything the window draws,
    /// the glass included. This is the only way to fade a WinUI window's backdrop. It is one user32 call
    /// that does not go through the window's message queue, so the interface's thread, which is busy
    /// for the whole life of this window, is not involved.</para>
    ///
    /// <para>The steps come from a timer and not from a curve given to another component, so there is
    /// something to stop: a dismiss during the entrance stops the entrance, so the two do not write the
    /// same attribute from two threads.</para>
    /// </remarks>
    private static class Alpha
    {
        /// <summary>How often the alpha is written. This is about one frame at 60 Hz, so a 420 ms
        /// entrance is about twenty-six writes and the steps cannot be seen.</summary>
        private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(16);

        /// <summary>Puts the window in the layered style and sets it invisible. Called before the window
        /// is shown.</summary>
        public static bool Prepare(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindows())
                return false;

            try
            {
                var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                if ((style & WS_EX_LAYERED) == 0)
                    SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(style | WS_EX_LAYERED));

                return SetLayeredWindowAttributes(hwnd, 0, 0, LWA_ALPHA);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Walks the alpha to <paramref name="target"/> over <paramref name="ms"/>, and calls
        /// <paramref name="done"/> on the pool when it lands.</summary>
        public static void To(byte target, int ms, Action? done)
        {
            var hwnd = _hwnd;
            if (hwnd == IntPtr.Zero)
            {
                done?.Invoke();
                return;
            }

            try { _fade?.Dispose(); } catch { }
            _fade = null;

            var from = target == 0 ? 255.0 : 0.0;
            var started = Environment.TickCount64;

            System.Threading.Timer? timer = null;
            timer = new System.Threading.Timer(_ =>
            {
                var t = Math.Clamp((Environment.TickCount64 - started) / (double)Math.Max(1, ms), 0, 1);

                // Cubic ease-out, the curve that everything else in this app fades on.
                var eased = 1 - Math.Pow(1 - t, 3);
                var value = (byte)Math.Clamp(Math.Round(from + (target - from) * eased), 0, 255);

                try { SetLayeredWindowAttributes(hwnd, 0, value, LWA_ALPHA); }
                catch { /* the window has gone; this is the last step in either case */ }

                if (t < 1)
                    return;

                try { timer?.Dispose(); } catch { }
                if (ReferenceEquals(_fade, timer))
                    _fade = null;

                done?.Invoke();
            }, null, TimeSpan.Zero, Step);

            _fade = timer;
        }
    }

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_CAPTION = 0x00C00000;
    private const long WS_THICKFRAME = 0x00040000;
    private const long WS_BORDER = 0x00800000;
    private const long WS_DLGFRAME = 0x00400000;
    private const long WS_SYSMENU = 0x00080000;
    private const long WS_POPUP = unchecked((long)0x80000000);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const long WS_EX_LAYERED = 0x00080000;
    private const int DQTYPE_THREAD_CURRENT = 2;
    private const int DQTAT_COM_STA = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size;
        public int ThreadType;
        public int ApartmentType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameMargins
    {
        public int Left, Right, Top, Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref FrameMargins margins);

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);
    private const int LWA_ALPHA = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint
    {
        public int X, Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out CursorPoint point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, int flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
#else
    // ══ macOS ══════════════════════════════════════════════════════════════════════════════════════
    //
    // The same card, built with AppKit. Every number below is the same as in the WinUI card, so both
    // heads show the same startup: 300 x 180 at a radius of 8, 420 ms in, 320 ms out, and the entrance
    // waits for the window to have drawn and not for a length of time.
    //
    // The difference is which API does the work. Desktop acrylic becomes an NSVisualEffectView behind
    // the content. The layered window's alpha becomes NSWindow.alphaValue, animated by Core Animation
    // and not stepped by this code. The region cut becomes a corner radius on two layers. The style
    // changes become one borderless mask. See MacWindowCard, which contains all of it.
    //
    // On every other head this does nothing and the shell keeps the black overlay inside its own
    // window.

    private const int CardWidth = 300;

    private const int CardHeight = 180;

    private const int CardRadius = 8;

    private const int FadeInMs = 420;

    private const int FadeOutMs = 320;

    private const int DrawnFrames = 2;

    private static readonly TimeSpan DrawnBackstop = TimeSpan.FromMilliseconds(700);

    /// <summary>
    /// The title this window wears, which is NOT the app's.
    /// </summary>
    /// <remarks>
    /// On this head a window is found by its title: <c>MacWindowChrome.FindWindowHandle</c> walks
    /// <c>NSApplication.windows</c> and returns the first one whose title matches. If two windows were
    /// called "rpm", the card could be returned to every one of those callers: the code that extends
    /// content under a title bar (the card has none), the folder drop target, and the frame restore
    /// that puts the app back where it was. The title is never shown, because the window is borderless
    /// and not in the switcher.
    /// </remarks>
    private const string CardTitle = "rpm splash";

    /// <summary>How long after the fade-out is requested before the window is closed. The animation
    /// belongs to Core Animation and reports nothing back without a block, so the close is timed and not
    /// waited for. A margin is added, because a window closed during the fade would make the card
    /// disappear suddenly.</summary>
    private static readonly TimeSpan CloseAfterFade = TimeSpan.FromMilliseconds(FadeOutMs + 90);

    private static Window? _window;

    /// <summary>The NSWindow behind it. It is kept and not looked up again: by the time the card is
    /// dismissed, the app's own window exists, and a second lookup could return the wrong window.</summary>
    private static nint _card;

    private static bool _going;

    /// <inheritdoc cref="IsUp"/>
    public static bool IsUp => _window is not null;

    private static readonly TaskCompletionSource DrawnSource = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the card has been drawn (or its backstop has passed), at the moment its
    /// fade starts. OnLaunched waits for it before building the shell on Windows, so the card is on
    /// screen while the shell's XAML is built and not after.</summary>
    public static Task Drawn => DrawnSource.Task;

    /// <summary>
    /// Raises the card. Called once, on the interface's thread, before the app's own window is
    /// activated.
    /// </summary>
    /// <returns>False when there is no card on screen: a head that is not macOS, a second window that
    /// this host will not open, or a window that AppKit would not dress. The caller must then leave the
    /// app's own window visible.</returns>
    public static bool Show()
    {
        if (_window is not null)
            return true;

        if (!OperatingSystem.IsMacOS())
            return false;

        try
        {
            // The card paints its own glass here, because the system's glass cannot be used. The
            // window is see-through and its layers are see-through, but the Skia surface drawn into
            // them is not: it reads (0,0,0) at every sample of the card, and an NSVisualEffectView
            // behind it has no effect. This is the same limit that MacWindowChrome.MaskToPanels
            // describes: what the tree does not paint is not transparent, it is whatever the buffer
            // held.
            var card = new SplashCard();
            card.DrawOwnGlass();

            var window = new Window { Title = CardTitle };
            window.Content = card;

            // The window is activated here and not by the caller: on this head the NSWindow does not
            // exist until the window is shown, and everything below is a message to that object.
            window.Activate();

            _card = Services.Platform.MacWindowChrome.FindWindowHandle(CardTitle);
            if (_card == 0
                || !Services.Platform.MacWindowCard.Dress(_card, CardWidth, CardHeight, CardRadius))
            {
                RPM.Core.Diagnostics.Scope.Said(
                    "boot", $"splash refused: the window was not dressed (ns {_card})");
                try { window.Close(); } catch { }
                _card = 0;
                return false;
            }

            _window = window;

            // The entrance starts when there is something to fade IN. A window that has just been
            // activated has not drawn its content yet, and a fade started before that would fade in an
            // empty card and then jump to the mark.
            Services.Shell.NextFrames.After(window.DispatcherQueue, DrawnFrames, DrawnBackstop, drawn =>
            {
                DrawnSource.TrySetResult();
                // The platform is checked again inside the lambda: the guard above does not apply
                // inside a closure, so the analyzer cannot see it from here.
                if (!OperatingSystem.IsMacOS())
                    return;

                Services.Platform.MacWindowCard.Fade(_card, 1.0, FadeInMs);
                RPM.Core.Diagnostics.Scope.Said(
                    "boot", $"splash fades in (window, {(drawn ? "drawn" : "backstop")})");
            });

            RPM.Core.Diagnostics.Scope.Said("boot", "splash up");
            return true;
        }
        catch (Exception e)
        {
            // A host that will not open a second window, or an AppKit call that threw. Nothing has
            // been hidden yet, because the caller hides the app's own window only if this returned
            // true. The startup continues as if there were no splash window.
            RPM.Core.Diagnostics.Scope.Said("boot", $"splash refused: {e.GetType().Name}");
            Close();
            return false;
        }
    }

    /// <summary>
    /// Fades the card off and closes it, once the app itself is visible, so the card dissolves over the
    /// interface and not over the desktop.
    /// </summary>
    public static void Dismiss()
    {
        if (_window is null || _going)
            return;
        _going = true;

        if (OperatingSystem.IsMacOS())
            Services.Platform.MacWindowCard.Fade(_card, 0.0, FadeOutMs);

        RPM.Core.Diagnostics.Scope.Said("boot", "splash dismissed");

        // The close is timed and not waited for: an NSAnimationContext group reports completion
        // through a block, and this interop cannot pass one. By now the app is up and its thread is
        // free, so the timer on the dispatcher fires on time.
        Services.Shell.Ticker.Once(_window.DispatcherQueue, CloseAfterFade, Close);
    }

    /// <summary>
    /// Uno's own close, and a log line when it fails.
    /// </summary>
    /// <remarks>
    /// Failures are logged and not swallowed. A <c>Close</c> that throws into an empty catch cannot be
    /// told apart from one that worked, and on this head the window can stay on screen after the call,
    /// which a silent catch cannot show.
    /// </remarks>
    private static void Shut(Window window)
    {
        // BOTH calls are made, and neither of them removes the NSWindow on Uno 6.7.65 with macOS
        // Skia: after either one the NSWindow is still on screen at its floating level.
        //
        // Window.Close does reach the native side. Uno's managed close detaches the content and drops
        // the window from its list, then calls uno_window_close, which is [window performClose:nil].
        // The card is borderless, so it has no close button to press, and AppKit plays the alert
        // sound and leaves the window open. MacWindowCard.QuietClose makes performClose: do nothing
        // on this window, which stops the sound.
        //
        // AppWindow.Destroy is not implemented on this head and is called for a later Uno version.
        // Do NOT close the window from outside: see the note in Close below.
        try
        {
            if (window.AppWindow is { } appWindow)
            {
                // This is not implemented in Uno yet and is called anyway: see the note above. The
                // warning is the analyzer reporting what that note already says.
#pragma warning disable Uno0001
                appWindow.Destroy();
#pragma warning restore Uno0001
                RPM.Core.Diagnostics.Scope.Said("boot", "splash destroyed");
            }
        }
        catch (Exception e)
        {
            RPM.Core.Diagnostics.Scope.Said("boot", $"splash destroy threw: {e.GetType().Name}");
        }

        try
        {
            window.Close();
            RPM.Core.Diagnostics.Scope.Said("boot", "splash close returned");
        }
        catch (Exception e)
        {
            RPM.Core.Diagnostics.Scope.Said("boot", $"splash close threw: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Closes the card at once, whatever state it is in. It is also the way out of a startup that ended
    /// in another way: the app's window closing during it, or a reveal that never came.
    /// </summary>
    public static void Close()
    {
        RPM.Core.Diagnostics.Scope.Said("boot", "splash closing");

        var window = _window;
        var card = _card;
        _window = null;
        _card = 0;
        _going = false;

        // NOTHING NATIVE is called here, on purpose. Two ways of removing this window from the screen
        // crash the app:
        //
        //   close      an NSWindow made in code is releasedWhenClosed, so it drops a reference that
        //              Uno still holds. The app crashes minutes later in objc_release, inside the
        //              main thread's autorelease pool drain, when the finalizer releases the second
        //              reference.
        //   orderOut:  the app crashes a few seconds later in uno_window_invalidate, where Uno's
        //              renderer asks a window that it still believes in to draw, through a pointer
        //              that no longer answers.
        //
        // So the card's window is left as it is: at zero alpha, ignoring mouse events since it was
        // dressed, and closed only through the managed call below. The cost is an invisible 300x180
        // window that stays in the app's window list for the rest of the session.
        _ = card;

        if (window is null)
            return;

        var queue = window.DispatcherQueue;
        if (queue is null || queue.HasThreadAccess)
        {
            Shut(window);
            return;
        }

        queue.TryEnqueue(() => Shut(window));
    }
#endif
}
