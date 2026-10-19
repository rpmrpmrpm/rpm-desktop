using System.Runtime.Versioning;
using Microsoft.UI.Xaml;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Services.Platform;

/// <summary>
/// The operating system's media controls, as <see cref="NowPlayingService"/> uses them: the media keys,
/// headphone buttons, and the Now Playing panel (Control Center on macOS, the volume flyout and lock
/// screen on Windows).
///
/// <para><see cref="Commanded"/> may be raised on any thread. The service marshals it onto the dispatcher.
/// The other three members are called on the UI thread only.</para>
/// </summary>
internal interface ISystemTransport
{
    /// <summary>A media key or a button in the system panel. The number is the position in seconds for
    /// <see cref="RemoteCommand.SeekTo"/>, and 0 for every other command.</summary>
    event Action<RemoteCommand, double>? Commanded;

    /// <summary>Replaces what the system shows with this card, including the cover last passed to
    /// <see cref="SetArtwork"/>.</summary>
    void Show(NowPlayingCard card);

    /// <summary>Removes the app from the system panel and drops the cover.</summary>
    void Clear();

    /// <summary>Sets the cover (encoded image bytes, or null for none). Shown from the next
    /// <see cref="Show"/> at the latest.</summary>
    void SetArtwork(byte[]? picture);
}

internal static class SystemTransport
{
    /// <summary>The media controls for this platform and head, or null where none are written (Linux, and
    /// the Skia head on Windows, which has no WinRT projection).</summary>
    public static ISystemTransport? For(Window window)
    {
        if (OperatingSystem.IsMacOS())
            return new MacTransport();

#if RPM_WINUI
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
            return WindowsNowPlaying.For(window);
#endif

        return null;
    }
}

/// <summary><see cref="MacNowPlaying"/> behind <see cref="ISystemTransport"/>.</summary>
[SupportedOSPlatform("macos")]
internal sealed class MacTransport : ISystemTransport
{
    public MacTransport() => MacNowPlaying.Enable();

    public event Action<RemoteCommand, double>? Commanded
    {
        add => MacNowPlaying.Commanded += value;
        remove => MacNowPlaying.Commanded -= value;
    }

    public void Show(NowPlayingCard card) => MacNowPlaying.Show(card);

    public void Clear() => MacNowPlaying.Clear();

    public void SetArtwork(byte[]? picture) => MacNowPlaying.SetArtwork(picture);
}
