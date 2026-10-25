using RPM.Core.Analysis;
using RPM.Core.Cloud;

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
        var next = Layout switch
        {
            PegasusView.Spread => PegasusView.Centred,
            PegasusView.Centred => PegasusView.Room,
            _ => PegasusView.Spread,
        };

        // With the lyric on, the layout is where the lyric goes back to. The lyric itself stays, in the form the new
        // layout gives it: beside the record in the compact layout, alone in the others (PegasusViews.LyricViewFor).
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
}
