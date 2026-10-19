using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;

namespace RPM.Desktop.Presentation.Settings;

/// <summary>How the app looks: the picture behind it, and anything else that is about appearance
/// rather than what the app does. A backdrop has nothing to do with where the music lives, so it has
/// its own section.
/// </summary>
public sealed partial class DisplaySettingsPage : Page
{
    private readonly IServiceProvider _services;
    private readonly SettingsService _settings;
    private readonly FolderPickerService _picker;

    public DisplaySettingsPage(IServiceProvider services)
    {
        _services = services;
        _settings = services.GetRequiredService<SettingsService>();
        _picker = services.GetRequiredService<FolderPickerService>();

        InitializeComponent();

        RefreshBackground();

        // Set before the handlers can hear anything. Assigning IsOn raises Toggled. Without this,
        // opening the page would write the value it just read back into settings, and wake the shell
        // for no reason.
        RecordToggle.IsOn = _settings.HeaderRecord;
        ArtistPhotoToggle.IsOn = _settings.HeaderArtistPhoto;
        _loaded = true;

        // The row lights whichever backdrop is actually standing. One of them is a supporter's, so the
        // row moves when the account does. Signing out while this page is open takes the light off the
        // gold tile and puts it on the default, which is what is behind the app by then.
        var supporter = services.GetRequiredService<Services.Account.SupporterService>();
        supporter.Changed += OnStandingChanged;
        Unloaded += (_, _) => supporter.Changed -= OnStandingChanged;
    }

    /// <summary>False until the switches are set from what is stored. This stops the Toggled events
    /// raised on the way in from being read as the user flipping them.</summary>
    private bool _loaded;

    // Art display: the record and the photograph behind it
    //
    // Both write straight through to settings. The shell listens to settings.Changed (see
    // ShellPage.ApplyArtDisplay). The header changes under the open dialog. That is the point of a
    // switch that controls what is on the screen behind it.

    private void OnRecordToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
            return;

        _settings.HeaderRecord = RecordToggle.IsOn;
    }

    private void OnArtistPhotoToggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded)
            return;

        _settings.HeaderArtistPhoto = ArtistPhotoToggle.IsOn;
    }

    /// <summary>May arrive on any thread, because the standing is read from the network.</summary>
    private void OnStandingChanged() => DispatcherQueue.TryEnqueue(RefreshBackground);

    // Backdrop
    //
    // Three things can be behind the app. They are ranked in one place only (Backdrop.Resolve): a
    // picture the user picked, then a built-in they named, then the default (Backdrop.BuiltIn). This
    // row shows all of them at once as pictures. That is the only question being asked, which one is in
    // use, and a picture answers it faster than a name does.

    private const string BgGlob = "background-*";

    private const double TileWidth = 132, TileHeight = 82;

    /// <summary>Rebuilds the row. It is rebuilt on every change rather than patched. The tiles are a
    /// short fixed list plus at most one that comes and goes, so rebuilding is simpler than
    /// reconciling, and no tile holds state worth keeping.</summary>
    private void RefreshBackground()
    {
        var custom = _settings.CustomBackgroundPath;
        var hasCustom = !string.IsNullOrWhiteSpace(custom) && File.Exists(custom);

        // A custom picture outranks a named built-in. While one is set, it is the tile that reads as
        // chosen, whatever else is stored underneath.
        var reach = Services.Account.SupporterReach.For(
            _services.GetRequiredService<Services.Account.SupporterService>().Standing);

        // This is what is actually behind the app, not what settings stores. When support lapses,
        // "gold" stays stored on purpose, so it comes back with the support. The default is what is on
        // screen. The row must light the default, or it would claim a backdrop the user is not looking
        // at.
        var chosenId = Backdrop.EffectiveId(_settings.BuiltInBackground, reach);

        BgTiles.Children.Clear();

        foreach (var preset in Backdrop.Presets)
        {
            // A supporter's backdrop that this account cannot have is still shown, as itself. The row
            // is how someone finds out the picture exists, and a grey rectangle sells nothing. What
            // changes is what a tap does.
            var refused = preset.Supporters && !reach.MaySupporterBackdrop;

            BgTiles.Children.Add(BackgroundTile(
                new BitmapImage(new Uri(preset.Uri)),
                Loc.Get(preset.NameKey),
                selected: !hasCustom && chosenId == preset.Id,
                refused
                    ? () => _ = Presentation.Dialogs.SupporterDialog.ShowAsync(
                        XamlRoot,
                        Presentation.Dialogs.SupporterDialog.Occasion.Refused(Loc.Get("Supporters_Backdrop")))
                    : () => ChooseBuiltIn(preset.Id),
                mark: refused));
        }

        if (hasCustom)
            BgTiles.Children.Add(BackgroundTile(
                new BitmapImage(new Uri(custom!)),
                Loc.Get("Settings_BackgroundYours"),
                selected: true,
                () => { }));

        BgTiles.Children.Add(PickerTile());
    }

    /// <summary>One backdrop, as a picture of itself with its name under it.</summary>
    /// <param name="mark">Whether to put the supporter's heart in the corner. The heart means
    /// "supporters only". It is drawn only while the backdrop is out of this account's reach. To a
    /// supporter every tile is theirs, so a mark on one of them would mean nothing.</param>
    private Border BackgroundTile(ImageSource source, string caption, bool selected, Action onPick,
        bool mark = false)
    {
        var picture = new Grid { Children = { new Image { Source = source, Stretch = Stretch.UniformToFill } } };

        if (mark)
            picture.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 5, 5, 0),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(5, 2, 5, 2),
                Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x99, 0, 0, 0)),
                Child = new FontIcon
                {
                    Style = (Style)Application.Current.Resources["FaIcon"],
                    Glyph = "\uf004",   // fa-heart, the same icon used for supporter items
                    FontSize = 9,
                    Foreground = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x04, 0xC8, 0xFF)),
                },
            });

        var frame = new Border
        {
            Width = TileWidth,
            Height = TileHeight,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = selected
                ? new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x04, 0xC8, 0xFF))
                : (Brush)Application.Current.Resources["DmCardBorderBrush"],
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x22, 0, 0, 0)),
            Child = picture,
        };

        var tile = new Border { Child = new StackPanel { Spacing = 6, Children = { frame, Caption(caption, selected) } } };
        tile.Tapped += (_, _) => onPick();
        return tile;
    }

    /// <summary>The last tile. It has the same shape as a backdrop, but holds a plus instead of a
    /// picture. So "or one of my own" sits in the row with the rest, not beside it as a
    /// button.</summary>
    private Border PickerTile()
    {
        var frame = new Border
        {
            Width = TileWidth,
            Height = TileHeight,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["DmCardBorderBrush"],
            Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0x22, 0, 0, 0)),
            Child = new FontIcon
            {
                Style = (Style)Application.Current.Resources["FaIcon"],
                Glyph = "\uf067",   // fa-plus
                FontSize = 18,
            },
        };

        var tile = new Border
        {
            Child = new StackPanel { Spacing = 6, Children = { frame, Caption(Loc.Get("Settings_ChooseImage"), selected: false) } },
        };
        tile.Tapped += (_, _) => PickBackground();
        return tile;
    }

    private static TextBlock Caption(string text, bool selected) => new()
    {
        Text = text,
        FontSize = 11,
        HorizontalAlignment = HorizontalAlignment.Center,
        Foreground = new SolidColorBrush(selected
            ? Microsoft.UI.ColorHelper.FromArgb(0xFF, 0x04, 0xC8, 0xFF)
            : Microsoft.UI.ColorHelper.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
    };

    /// <summary>Puts one of the app's own pictures behind it. Picking a built-in also drops the user's
    /// own picture. The tile that was lit goes out, and a picture that no longer stands anywhere is not
    /// worth keeping in local data.</summary>
    private void ChooseBuiltIn(string id)
    {
        ClearBackgroundCopies();
        _settings.CustomBackgroundPath = null;
        // Raises Changed, so the shell restyles at once.
        _settings.BuiltInBackground = string.IsNullOrEmpty(id) ? null : id;
        RefreshBackground();
    }

    private async void PickBackground()
    {
        var path = await _picker.PickImageFileAsync();
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            // Keep a stable copy in local data, so the backdrop survives the original being moved or
            // deleted. A new file name each time stops the bitmap cache from showing an old picture.
            ClearBackgroundCopies();
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var dest = Path.Combine(LocalData.Dir, $"background-{Guid.NewGuid():N}{ext}");
            File.Copy(path, dest, overwrite: true);

            // Raises Changed, so the shell restyles at once.
            _settings.CustomBackgroundPath = dest;
            RefreshBackground();
        }
        catch { /* leave the current backdrop in place on any copy failure */ }
    }

    /// <summary>
    /// Put the first-run hints back and show the first one again.
    ///
    /// <para>Takes effect at once, behind this dialog. The shell redraws the hints as soon as it is
    /// told. So closing Settings lands on the first hint, and no restart is needed for the change to be
    /// seen.</para>
    /// </summary>
    private void OnReplayHints(object sender, RoutedEventArgs e)
    {
        Coachmarks.CoachmarkState.Reset();

        // The button says so, because the hints are behind this dialog. Nothing else here would show
        // that the press did anything.
        ReplayHintsButton.IsEnabled = false;
        ReplayHintsButton.Content = Loc.Get("Settings_HintsBack");
    }

    /// <summary>Removes every kept background copy from local data.</summary>
    private static void ClearBackgroundCopies()
    {
        try
        {
            foreach (var old in Directory.EnumerateFiles(LocalData.Dir, BgGlob))
                try { File.Delete(old); } catch { /* a locked copy is harmless, since nothing refers to it */ }
        }
        catch { /* local data cannot be read, so there is nothing to clean */ }
    }
}
