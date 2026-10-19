#if RPM_WINUI
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.Versioning;
using Microsoft.UI.Xaml;
using RPM.Desktop.Services.Shell;
using Windows.Media;
using Windows.Storage.Streams;

namespace RPM.Desktop.Services.Platform;

/// <summary>
/// Windows' media controls through <c>SystemMediaTransportControls</c>: the media keys, headphone buttons,
/// and the media panel in the volume flyout and on the lock screen.
///
/// <para>The app plays through its own engine, not a <c>MediaPlayer</c>, so nothing fills these controls
/// automatically. The instance comes from <c>SystemMediaTransportControlsInterop.GetForWindow</c>, which is
/// the way an unpackaged desktop app gets one; <c>GetForCurrentView</c> needs a CoreWindow and throws
/// here.</para>
///
/// <para><c>ButtonPressed</c> and <c>PlaybackPositionChangeRequested</c> are raised on a thread-pool
/// thread. They are passed on as <see cref="Commanded"/> unchanged and the service marshals them onto the
/// dispatcher.</para>
///
/// <para>Failures are logged and swallowed. The app's own transport works without these controls.</para>
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
internal sealed class WindowsNowPlaying : ISystemTransport
{
    private readonly SystemMediaTransportControls _controls;
    private RandomAccessStreamReference? _cover;

    private WindowsNowPlaying(SystemMediaTransportControls controls)
    {
        _controls = controls;
        _controls.IsPlayEnabled = true;
        _controls.IsPauseEnabled = true;
        _controls.IsNextEnabled = true;
        _controls.IsPreviousEnabled = true;
        _controls.ButtonPressed += OnButtonPressed;
        _controls.PlaybackPositionChangeRequested += OnPositionRequested;

        // Off until there is something to show, so an app with nothing loaded is not listed in the flyout.
        _controls.IsEnabled = false;
    }

    /// <summary>The controls for the app's main window, or null when Windows does not give them.</summary>
    public static WindowsNowPlaying? For(Window window)
    {
        if (NativeWindow.Handle(window) is not { } hwnd)
            return null;

        try
        {
            return new WindowsNowPlaying(SystemMediaTransportControlsInterop.GetForWindow(hwnd));
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Said.Line("nowplaying", $"system media controls unavailable: {ex.GetType().Name} 0x{ex.HResult:X8}");
            return null;
        }
    }

    public event Action<RemoteCommand, double>? Commanded;

    public void Show(NowPlayingCard card)
    {
        try
        {
            var display = _controls.DisplayUpdater;
            display.Type = MediaPlaybackType.Music;
            display.MusicProperties.Title = card.Title;
            display.MusicProperties.Artist = card.Artist ?? "";
            display.MusicProperties.AlbumTitle = card.Album ?? "";
            display.Thumbnail = _cover;
            display.Update();

            // A station has no length: a timeline of zero hides the seek bar.
            var length = TimeSpan.FromSeconds(card.Duration);
            _controls.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                Position = TimeSpan.FromSeconds(card.Elapsed),
                MaxSeekTime = length,
                EndTime = length,
            });

            _controls.PlaybackStatus = card.Playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
            _controls.IsEnabled = true;
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Said.Line("nowplaying", $"system media controls refused an update: {ex.GetType().Name} 0x{ex.HResult:X8}");
        }
    }

    public void Clear()
    {
        try
        {
            _cover = null;
            _controls.PlaybackStatus = MediaPlaybackStatus.Closed;
            _controls.DisplayUpdater.ClearAll();
            _controls.DisplayUpdater.Update();
            _controls.IsEnabled = false;
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Said.Line("nowplaying", $"system media controls refused a clear: {ex.GetType().Name} 0x{ex.HResult:X8}");
        }
    }

    public void SetArtwork(byte[]? picture)
    {
        _cover = null;
        if (picture is not { Length: > 0 })
            return;

        try
        {
            // A native InMemoryRandomAccessStream is used, because the shell reads the thumbnail from its
            // own process. A .NET stream wrapped with AsRandomAccessStream does not reach it. The write
            // goes to memory and does not need the UI thread, so waiting for it here cannot deadlock.
            var stream = new InMemoryRandomAccessStream();
            stream.WriteAsync(picture.AsBuffer()).AsTask().GetAwaiter().GetResult();
            stream.Seek(0);
            _cover = RandomAccessStreamReference.CreateFromStream(stream);
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Said.Line("nowplaying", $"cover not passed to the system media controls: {ex.GetType().Name} 0x{ex.HResult:X8}");
        }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        RemoteCommand? command = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => RemoteCommand.Play,
            SystemMediaTransportControlsButton.Pause => RemoteCommand.Pause,
            SystemMediaTransportControlsButton.Next => RemoteCommand.Next,
            SystemMediaTransportControlsButton.Previous => RemoteCommand.Previous,
            _ => null,
        };

        if (command is { } c)
            Commanded?.Invoke(c, 0);
    }

    private void OnPositionRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args) =>
        Commanded?.Invoke(RemoteCommand.SeekTo, args.RequestedPlaybackPosition.TotalSeconds);
}
#endif
