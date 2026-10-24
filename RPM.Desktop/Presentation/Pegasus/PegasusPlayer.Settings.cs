using RPM.Core.Analysis;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The host side of Pegasus's settings panel (see IPegasusSettings). It makes the same changes as the view
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

        // With the lyric on, the layout is where the lyric goes back to; the lyric itself stays.
        if (_view.IsLyric())
            _beforeLyrics = next;
        else
            _view = next;

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
}
