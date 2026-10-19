using Microsoft.UI.Windowing;
#if RPM_WINUI
using WinRT;
#endif

namespace RPM.Desktop.Services.Platform;

/// <summary>A window's presenter, as the type that holds its methods. This is the only way the app reaches
/// <see cref="OverlappedPresenter"/>. Hiding the title bar, maximising, minimising, and reading whether a
/// window is maximised all go through <see cref="Overlapped"/>.
///
/// <para><b>Do not use <c>appWindow.Presenter as OverlappedPresenter</c>.</b> Under Native AOT the property
/// can return the base <see cref="AppWindowPresenter"/>, and a projected class does not cast to a derived
/// one. Every caller used that cast, so under AOT each cast returned null and the calls did nothing. The
/// result was a system title bar, a window that did not open maximised, and minimise and maximise keys that
/// did nothing.</para>
///
/// <para><b>The re-wrap below is needed, even though it is not always reached.</b> With this class in the
/// build, the plain cast answers under AOT, and the window opens borderless and maximised. Without it, the
/// window opens framed and unmaximised. The likely cause is that the reference to
/// <c>OverlappedPresenter.FromAbi</c> keeps what CsWinRT needs to build the derived wrapper, and the
/// trimmer would otherwise drop it. This is inferred from one comparison, not proven. So the fallback
/// stays. If it is removed, the cast may stop working again.</para>
/// </summary>
internal static class WindowPresenter
{
    /// <summary>Each way of reaching the presenter is written to the ring once. A log of an AOT run then
    /// shows whether the cast or the re-wrap answered.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> Said = new();

    /// <summary>
    /// The window's presenter as an <see cref="OverlappedPresenter"/>, or null when the window is gone
    /// or its presenter is another kind (full screen, compact overlay).
    /// </summary>
    public static OverlappedPresenter? Overlapped(AppWindow? window)
    {
        if (window?.Presenter is not { } presenter)
            return null;

        if (presenter is OverlappedPresenter typed)
        {
            Say("cast");
            return typed;
        }

        if (presenter.Kind != AppWindowPresenterKind.Overlapped)
            return null;

#if RPM_WINUI
        // The native object is an OverlappedPresenter; only the managed wrapper was made for the base
        // type. Wrap the same native object again as the derived type.
        try
        {
            if (presenter is IWinRTObject winrt
                && OverlappedPresenter.FromAbi(winrt.NativeObject.ThisPtr) is { } rewrapped)
            {
                Say("re-wrapped from the base type");
                return rewrapped;
            }
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Faults.Note("window", "re-wrapping the window presenter", ex);
        }

        try
        {
            var asked = presenter.As<OverlappedPresenter>();
            Say("asked for with As");
            return asked;
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Faults.Note("window", "asking for the overlapped presenter", ex);
            return null;
        }
#else
        // Uno's presenters are plain .NET objects, so the cast above is the whole answer.
        return null;
#endif
    }

    private static void Say(string how)
    {
        if (Said.TryAdd(how, true))
            RPM.Core.Diagnostics.Scope.Said("window", $"overlapped presenter reached by {how}");
    }
}
