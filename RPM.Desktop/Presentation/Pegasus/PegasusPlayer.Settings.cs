using RPM.Core.Analysis;
using RPM.Core.Cloud;
using RPM.Core.Viz;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The host side of Pegasus's settings panel (see IPegasusSettings). It makes the same changes as the layout
/// key and the two lyric keys, but without the keys. The panel is opened from the side menu on home, so the
/// record keeps turning behind the collection while these change. The changes show when the view comes
/// back.
/// </summary>
public sealed partial class PegasusPlayer : IPegasusSettings
{
    /// <inheritdoc/>
    public PegasusView Layout => _view.IsLyric() ? _beforeLyrics : _view;

    /// <inheritdoc/>
    public void NextLayout()
    {
        var next = Layout == PegasusView.Spread ? PegasusView.Centred : PegasusView.Spread;

        // With the lyric on, the layout is where the lyric goes back to. The lyric itself stays, in the form the new
        // layout gives it: beside the record in the compact layout, alone in the full one (PegasusViews.LyricViewFor).
        if (_view.IsLyric())
        {
            _beforeLyrics = next;
            _view = PegasusViews.LyricViewFor(next);
        }
        else
        {
            _view = next;
        }

        // Kept in the settings, so the player opens on it after a restart.
        StoreLayout();
        ApplyView(immediate: false);
    }

    /// <inheritdoc/>
    public RoomStyle RoomStyle => RoomStyles.Named(App.Services.GetRequiredService<SettingsService>().PegasusRoomStyle);

    /// <inheritdoc/>
    public void NextRoomStyle()
    {
        var next = RoomStyles.Next(RoomStyle);
        App.Services.GetRequiredService<SettingsService>().PegasusRoomStyle = next.ToString();
        _stage?.SetStyle(next);
    }

    /// <inheritdoc/>
    public bool MaskProfanity => App.Services.GetRequiredService<SettingsService>().MaskProfanity;

    /// <inheritdoc/>
    public void ToggleMaskProfanity()
    {
        var settings = App.Services.GetRequiredService<SettingsService>();
        settings.MaskProfanity = !settings.MaskProfanity;
        ApplyCensor();
        ApplyAlign();
    }

    /// <inheritdoc/>
    public bool AlignWords => App.Services.GetRequiredService<SettingsService>().AlignWords && Reach.MayAlignWords;

    /// <inheritdoc/>
    public void ToggleAlignWords()
    {
        if (!Reach.MayAlignWords)
        {
            _ = Dialogs.SupporterDialog.ShowAsync(
                XamlRoot, Dialogs.SupporterDialog.Occasion.Refused(Strings.Loc.Get("Supporters_Karaoke")));
            return;
        }

        var settings = App.Services.GetRequiredService<SettingsService>();
        settings.AlignWords = !settings.AlignWords;
        ApplyAlign();
    }

    // ── volume levelling: the Sound section ──
    //
    // Written straight to the stored settings, as the main window's Sound page writes them. The levelling of the
    // next track reads them (NormalizationService.Decide), and switching it on starts the measuring pass, which
    // listens for the change. Nothing is applied here.

    /// <inheritdoc/>
    public NormalizeMode Levelling => App.Services.GetRequiredService<SettingsService>().NormalizeMode;

    /// <inheritdoc/>
    public void NextLevelling()
    {
        var settings = App.Services.GetRequiredService<SettingsService>();
        settings.NormalizeMode = settings.NormalizeMode switch
        {
            NormalizeMode.Album => NormalizeMode.Track,
            NormalizeMode.Track => NormalizeMode.Off,
            _ => NormalizeMode.Album,
        };
    }

    /// <inheritdoc/>
    public bool LevelsClassical => App.Services.GetRequiredService<SettingsService>().NormalizeClassical;

    /// <inheritdoc/>
    public void ToggleLevelsClassical()
    {
        var settings = App.Services.GetRequiredService<SettingsService>();
        settings.NormalizeClassical = !settings.NormalizeClassical;
    }

    // ── the album wall: the General section ──
    //
    // Written straight to the stored settings. The chrome reads both whenever its wall comes up, and again
    // after a change made in the panel while the wall is behind it (see PegasusChrome.ApplyAlbumView).

    /// <inheritdoc/>
    public bool AlbumCarousel => App.Services.GetRequiredService<SettingsService>().PegasusAlbumCarousel;

    /// <inheritdoc/>
    public void ToggleAlbumCarousel()
    {
        var settings = App.Services.GetRequiredService<SettingsService>();
        settings.PegasusAlbumCarousel = !settings.PegasusAlbumCarousel;
    }

    /// <inheritdoc/>
    public AlbumOrdering AlbumOrdering => PegasusChrome.SavedLibraryOrdering();

    /// <inheritdoc/>
    public void NextAlbumOrdering()
    {
        var orders = PegasusChrome.LibraryOrderings;
        var at = Array.IndexOf(orders, AlbumOrdering);
        App.Services.GetRequiredService<SettingsService>().PegasusAlbumOrdering = orders[(at + 1) % orders.Length];
    }

    // ── the screensaver: the General section ──

    /// <inheritdoc/>
    public int ScreensaverMinutes =>
        Screensaver.Minutes(App.Services.GetRequiredService<SettingsService>().PegasusScreensaverMinutes);

    /// <inheritdoc/>
    public void NextScreensaver()
    {
        App.Services.GetRequiredService<SettingsService>().PegasusScreensaverMinutes = Screensaver.Next(ScreensaverMinutes);

        // The wait starts again on the new choice. The press that made it was a key, so it counts from now.
        ArmScreensaver();
    }

    // ── the key legend: the General section ──

    /// <inheritdoc/>
    public bool KeyLegend => App.Services.GetRequiredService<SettingsService>().PegasusKeyLegend;

    /// <inheritdoc/>
    public void ToggleKeyLegend()
    {
        var settings = App.Services.GetRequiredService<SettingsService>();
        settings.PegasusKeyLegend = !settings.PegasusKeyLegend;
    }

    // ── the mini player: the Player section ──
    //
    // Written straight to the stored settings, by name. The chrome reads it on every frame and moves the card
    // when it changes (see PegasusChrome.PlaceMiniPlayer).

    /// <inheritdoc/>
    public MiniPlayerPlace MiniPlayerPlace =>
        Enum.TryParse<MiniPlayerPlace>(App.Services.GetRequiredService<SettingsService>().PegasusMiniPlayer, out var place)
        && Enum.IsDefined(place)
            ? place
            : MiniPlayerPlace.Top;

    /// <inheritdoc/>
    public void NextMiniPlayerPlace()
    {
        var places = Enum.GetValues<MiniPlayerPlace>();
        var next = places[(Array.IndexOf(places, MiniPlayerPlace) + 1) % places.Length];
        App.Services.GetRequiredService<SettingsService>().PegasusMiniPlayer = next.ToString();
    }

    // ── how the view is drawn: the Display section ──
    //
    // Written to the stored settings and shown at once, over everything the view draws (see PegasusPlayer.Picture).

    /// <inheritdoc/>
    public PictureLook Picture => PictureLooks.NamedPicture(App.Services.GetRequiredService<SettingsService>().PegasusPicture);

    /// <inheritdoc/>
    public void NextPicture()
    {
        var next = PictureLooks.Next(Picture);
        App.Services.GetRequiredService<SettingsService>().PegasusPicture = next.ToString();
        ApplyPicture(next);
    }
}
