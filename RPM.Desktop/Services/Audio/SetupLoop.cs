namespace RPM.Desktop.Services.Audio;

/// <summary>
/// The setup wizard's music: the Android TV set-up's own soft loop (<c>Assets/Sounds/setup-loop.mp3</c>,
/// the TV app's <c>res/raw/setup_loop.mp3</c>), looped at half volume from the first step to the end of
/// setup, where it fades out over <see cref="FadeOutMs"/>. The file fades in and out within itself, so the
/// loop point is quiet.
///
/// <para><b>Not on the music's player.</b> Like <see cref="AppSound"/>, it shares nothing with the
/// player's OpenAL device, so it cannot stop, or be stopped by, a record. On the WinUI head it is Windows'
/// own media player. The Skia heads play no setup music.</para>
///
/// <para>Held for the process, not by the wizard's page: picking a language rebuilds the page, and the
/// music goes on through that. Every call is idempotent and safe on any head. Drawing thread only.</para>
/// </summary>
public static class SetupLoop
{
    private const double Volume = 0.5;
    private const int FadeOutMs = 1200;
    private const int FadeStepMs = 40;

#if RPM_WINUI
    private static Windows.Media.Playback.MediaPlayer? _player;
#endif

    /// <summary>Starts the loop, unless it is already playing.</summary>
    public static void Play()
    {
#if RPM_WINUI
        if (_player is not null)
            return;

        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", "setup-loop.mp3");
        if (!File.Exists(path))
            return;

        try
        {
            var player = new Windows.Media.Playback.MediaPlayer
            {
                IsLoopingEnabled = true,
                Volume = Volume,
                AudioCategory = Windows.Media.Playback.MediaPlayerAudioCategory.Media,
            };
            // Not on the system's media controls: this is a screen's music, not something being listened to.
            player.CommandManager.IsEnabled = false;
            player.Source = Windows.Media.Core.MediaSource.CreateFromUri(new Uri(path));
            player.Play();
            _player = player;
        }
        catch (Exception e)
        {
            RPM.Core.Diagnostics.Said.Line("setup", $"set-up music not played: {e.Message}");
        }
#endif
    }

    /// <summary>Fades the loop out and lets it go. Nothing happens when it is not playing.</summary>
    public static void Stop()
    {
#if RPM_WINUI
        var held = _player;
        _player = null;
        if (held is null)
            return;

        // The fade holds its own player and lets it go at its end, so a Play that follows starts a new
        // one rather than reviving a player on its way out.
        _ = Task.Run(async () =>
        {
            try
            {
                var steps = FadeOutMs / FadeStepMs;
                for (var i = 1; i <= steps; i++)
                {
                    held.Volume = Volume * (1 - (double)i / steps);
                    await Task.Delay(FadeStepMs);
                }
            }
            catch { }
            finally
            {
                try { held.Pause(); held.Dispose(); } catch { }
            }
        });
#endif
    }

    /// <summary>Stops the loop at once, for a window that is closing.</summary>
    public static void Silence()
    {
#if RPM_WINUI
        var held = _player;
        _player = null;
        try { held?.Pause(); held?.Dispose(); } catch { }
#endif
    }
}
