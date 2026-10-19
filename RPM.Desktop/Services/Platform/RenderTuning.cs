using System.Collections;
using System.Reflection;
#if !RPM_WINUI
using Uno.UI;
#endif

namespace RPM.Desktop.Services.Platform;

/// <summary>How hard the window is allowed to work to draw itself.
///
/// <para>Uno's Skia renderer has no dirty rectangles. One frame is the whole visual tree, walked from the
/// root. Two caches make that affordable. Each visual's paint is recorded once into an <c>SKPicture</c> and
/// replayed until it is marked dirty. A subtree that has not changed for a while is collapsed into one
/// recorded picture for all of its visuals. The settings below decide when the second cache is used, and
/// how many frames per second the window is asked for.</para>
///
/// <para>Each value here is read by the <c>Microsoft.UI.Composition.Visual</c> constructor. So this must
/// run before the first visual exists, which means in the App constructor, before
/// InitializeComponent.</para>
/// </summary>
public static class RenderTuning
{
    /// <summary>Applied once, before any visual is built.
    ///
    /// <para>The frame rate is fixed at 60, not taken from the display. Uno's default follows the refresh
    /// rate. On a 120 Hz panel, such as a recent MacBook or a gaming monitor, the whole tree is drawn twice
    /// as often, including every live glass panel. Nothing on this stage moves fast enough to look
    /// different at 60.</para>
    ///
    /// <para>The subtree collapsing thresholds are set well below Uno's defaults (50 clean frames and 100
    /// visuals). Most panels here are still. Examples are a track list that nobody is scrolling, a settings
    /// page, and the left pane's tree. With the default thresholds, a subtree would have to stay unchanged
    /// for most of a second before it was cached. Collapsing is safe at any threshold. Any change
    /// re-invalidates the whole ancestor chain up to the root (Visual.InvalidateParentChildrenPicture). So
    /// a collapsed picture is only shown while nothing under it has moved.</para>
    /// </summary>
    public static void Apply()
    {
#if !RPM_WINUI
        FeatureConfiguration.CompositionTarget.SetFrameRateAsScreenRefreshRate = false;
        FeatureConfiguration.CompositionTarget.FrameRate = 60f;

        FeatureConfiguration.Rendering.EnableVisualSubtreeSkippingOptimization = true;
        FeatureConfiguration.Rendering.VisualSubtreeSkippingOptimizationCleanFramesThreshold = 10;
        FeatureConfiguration.Rendering.VisualSubtreeSkippingOptimizationVisualCountThreshold = 25;
#endif
        // Nothing to set on the WinUI head. Every setting above tunes Uno's Skia renderer. It sets the
        // frame rate that the window asks for, and when an unchanging subtree is collapsed into one
        // recorded picture. WinUI has neither, because it does not walk and re-record the tree. Windows
        // composes the visuals, and only the changed ones are rasterised again.
    }

    /// <summary>Writes one line naming the renderer the window actually came up on: <c>[render] Windows
    /// GlRenderer</c>, <c>[render] macOS Metal</c>. Called once at startup.
    ///
    /// <para>The line is useful because a fallback is silent. The Windows host tries Vulkan, then OpenGL,
    /// and then falls back to a CPU rasteriser. On the CPU path every frosted panel becomes a Gaussian blur
    /// on the UI thread. The line tells a heavy app apart from a machine with no working GPU driver.</para>
    ///
    /// <para>A one-shot timer is used, not a CompositionTarget.Rendering handler that waits for the first
    /// frame. On Skia that event does not watch frames. It requests them. While any handler is attached,
    /// each finished frame asks for the next one (see <see cref="PlayingBars"/>). The line is written with
    /// Console, not Debug.WriteLine. Debug.WriteLine only reaches an attached debugger.</para>
    /// </summary>
    public static void LogBackendOnce()
    {
        Shell.Ticker.Once(
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread(),
            TimeSpan.FromSeconds(2),
            () => Console.WriteLine($"[render] {Backend()}"));
    }

    /// <summary>The renderer in use, read off the platform host itself.
    ///
    /// <para>Reflection is used, because neither host publishes the result. On Windows, the decision is
    /// held in a private <c>IRenderer</c> field for each window. Its type (Gl, Vulkan or Software) is the
    /// answer. On macOS the resolved surface type is on an internal singleton. Both return "unknown" rather
    /// than throwing. A diagnostic that goes quiet is acceptable. One that crashes the app is not.</para>
    ///
    /// <para>This does not read <c>Compositor.IsSoftwareRenderer</c>. It looks like the answer, but no host
    /// assembly ever sets it. It is null everywhere, and it reports "unknown" even on a healthy GPU. It
    /// also does not read the hosts' own logs. Their renderer lines are Debug-level, under a category
    /// prefix that also covers the UIA providers, drag and drop, and the event loop. Raising them would
    /// flood the log on every pointer move.</para>
    /// </summary>
    private static string Backend()
    {
        try
        {
#if RPM_WINUI
            // There is one renderer, and it is the system's. It is named, not reported as "unknown",
            // because the line answers "what drew this frame", and this head has no fallback.
            return "WinUI";
#else
            if (OperatingSystem.IsMacOS())
            {
                var host = Type.GetType("Uno.UI.Runtime.Skia.MacOS.MacSkiaHost, Uno.UI.Runtime.Skia.MacOS");
                var current = host?
                    .GetProperty("Current", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?
                    .GetValue(null);
                var surface = current is null
                    ? null
                    : host!.GetProperty("RenderSurfaceType")?.GetValue(current);
                return surface is null ? "unknown" : $"macOS {surface}";
            }

            if (OperatingSystem.IsWindows())
            {
                var wrapper = Type.GetType("Uno.UI.Runtime.Skia.Win32.Win32WindowWrapper, Uno.UI.Runtime.Skia.Win32");
                if (wrapper?.GetField("_hwndToWrapper", BindingFlags.Static | BindingFlags.NonPublic)?
                        .GetValue(null) is not IDictionary windows)
                    return "unknown";

                foreach (var window in windows.Values)
                {
                    if (wrapper.GetField("_renderer", BindingFlags.Instance | BindingFlags.NonPublic)?
                            .GetValue(window) is { } renderer)
                        return $"Windows {renderer.GetType().Name}";
                }
            }

            return "unknown";
#endif
        }
        catch
        {
            return "unknown";
        }
    }
}
