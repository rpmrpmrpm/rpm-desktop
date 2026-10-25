using RPM.Core.Analysis;
using RPM.Core.Viz;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// What Pegasus's own settings panel shows and changes (see PegasusChrome.SideMenu). Owned by the host,
/// PegasusPlayer, which holds the layout and puts the two stored settings on the lyric view; the chrome
/// only draws the rows and asks.
///
/// <para>The layout is stored (SettingsService), and the layout key in the player's row changes it too. The
/// two lyric settings are stored and are also set by the lyric view's own keys. Volume levelling is the
/// desktop's own (SettingsService, read by NormalizationService for every track), the same two settings as the
/// main window's Sound page.</para>
/// </summary>
public interface IPegasusSettings
{
    /// <summary>The layout the record turning comes back to: Spread or Centred. While the lyric is on, the
    /// one it goes back to when the lyric is turned off.</summary>
    PegasusView Layout { get; }

    /// <summary>Moves to the next layout, wrapping, in the order the layout key cycles them.</summary>
    void NextLayout();

    /// <summary>The animation the room draws behind the player and the collection (stored in
    /// SettingsService).</summary>
    RoomStyle RoomStyle { get; }

    /// <summary>Moves to the next animation, wrapping: Original, Yang, Lava, Alchemy. The room changes on its
    /// next frame, so the change shows behind the open panel.</summary>
    void NextRoomStyle();

    /// <summary>Whether explicit words in the lyrics are masked.</summary>
    bool MaskProfanity { get; }

    /// <summary>Turns the masking on or off.</summary>
    void ToggleMaskProfanity();

    /// <summary>Whether each word is timed against the recording: the stored choice and the account's
    /// standing together, as the lyric view applies it.</summary>
    bool AlignWords { get; }

    /// <summary>Turns the timing on or off. For an account that may not have it, shows why instead and
    /// stores nothing, as the lyric view's key does.</summary>
    void ToggleAlignWords();

    // ── volume levelling: the Sound section (the main window's Sound page sets the same two) ──

    /// <summary>How records are levelled: matched as albums, as tracks, or not at all.</summary>
    NormalizeMode Levelling { get; }

    /// <summary>Moves to the next levelling, wrapping: Album, Track, Off.</summary>
    void NextLevelling();

    /// <summary>Whether the albums marked classical are levelled too.</summary>
    bool LevelsClassical { get; }

    /// <summary>Turns levelling the albums marked classical on or off.</summary>
    void ToggleLevelsClassical();

    // ── the album wall: the General section (the Android TV app's own two rows) ──

    /// <summary>Whether the album wall is shown as a carousel rather than a grid.</summary>
    bool AlbumCarousel { get; }

    /// <summary>Switches the album wall between the grid and the carousel.</summary>
    void ToggleAlbumCarousel();

    /// <summary>The order the album wall opens in: the one last chosen there or here.</summary>
    RPM.Core.Cloud.AlbumOrdering AlbumOrdering { get; }

    /// <summary>Moves to the next order, wrapping, in the order the wall's sort key lists them.</summary>
    void NextAlbumOrdering();

    // ── the screensaver: the General section, after the album wall's rows, as on the TV ──

    /// <summary>After how many minutes without a key the screens make way for the room (see
    /// PegasusPlayer.Screensaver): one of <see cref="Screensaver.Choices"/>, 0 for off.</summary>
    int ScreensaverMinutes { get; }

    /// <summary>Moves to the next choice, wrapping from the longest to off.</summary>
    void NextScreensaver();

    /// <summary>Whether a screen, as it opens, shows for five seconds what the remote's keys do on it (the
    /// key legend, see PegasusChrome.Legend). Stored in SettingsService; on until changed.</summary>
    bool KeyLegend { get; }

    /// <summary>Turns the key legend on or off.</summary>
    void ToggleKeyLegend();

    // ── the mini player: the Player section (the Android TV app's Mini player row) ──

    /// <summary>Where the mini player stands over the collection, or Off for none (stored in
    /// SettingsService). At the top until changed.</summary>
    MiniPlayerPlace MiniPlayerPlace { get; }

    /// <summary>Moves to the next place, wrapping: Top, Bottom, Bottom left, Bottom right, Off. The card
    /// moves on the chrome's next frame, so the change shows behind the open panel.</summary>
    void NextMiniPlayerPlace();

    // ── how the view is drawn: the Display section (the Android TV app's rows) ──

    /// <summary>How bright and saturated the view is drawn (stored in SettingsService).</summary>
    PictureLook Picture { get; }

    /// <summary>Moves to the next look, wrapping: Standard, Softer, Muted. It shows at once, behind the open
    /// panel.</summary>
    void NextPicture();

    /// <summary>What the collection's screens are drawn on (stored in SettingsService).</summary>
    PageBackground PageBackground { get; }

    /// <summary>Moves to the next background, wrapping: Default, Pegasus, Dark, Moving. It shows at once, behind
    /// the open panel.</summary>
    void NextPageBackground();
}
