using RPM.Core.Analysis;

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
    /// <summary>The layout the record turning comes back to: Spread, Centred or Room. While the lyric is
    /// on, the one it goes back to when the lyric is turned off.</summary>
    PegasusView Layout { get; }

    /// <summary>Moves to the next layout, wrapping, in the order the layout key cycles them.</summary>
    void NextLayout();

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
}
