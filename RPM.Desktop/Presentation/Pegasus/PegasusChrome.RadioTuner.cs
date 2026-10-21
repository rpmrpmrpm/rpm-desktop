using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Radio;
using RPM.Core.Threading;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main.Home;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using Loc = RPM.Desktop.Strings.Loc;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The tuner: radio's own page, opened from radio's wheel. It has a wide dial at the top, a station list
/// under it (the main app's browser, with text and icons sized for Pegasus), and to the right of the
/// list a radio player: a small room, the station's logo in its middle, the station's information
/// under it and a play/stop key under that.
///
/// <list type="bullet">
/// <item><b>The dial</b> is the main window's (RadioDial), laid out at a width of its own and scaled up
/// whole, so it is wider than the pane's and nothing on it is stretched. It is one stop for the remote:
/// Left and Right move the needle a station at a time, held keys walk the band, and the station is tuned
/// when the key comes up (see CommitDialStep), so walking the band does not open a stream per station. OK
/// plays or stops.</item>
/// <item><b>The list</b> is the radio pane's model and its rows, drawn larger (see PegasusStationRow): a
/// copy of the model of its own, so the main window's pane keeps its query. Search, country, order,
/// filter and favourites are the pane's; the country, order and filter open menus at this view's
/// size.</item>
/// <item><b>The player</b> shows the station tuned: its logo in a well as the pane's rows draw it, in a
/// small room of its own colours, what it is playing, and the key that plays or stops it.</item>
/// </list>
///
/// <para><b>A page over radio</b>, as a record's page is over the wall: it crosses with the collection,
/// which is collapsed under it once the cross lands and comes back when it goes (see
/// <see cref="CrossToTuner"/>). <see cref="LibraryUp"/> stays true, which keeps the host drawing the bare
/// room behind it. Back closes it onto radio.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>How the wheel's entries open the tuner.</summary>
    private enum TunerOpening
    {
        /// <summary>As it was last left.</summary>
        AsLeft,

        /// <summary>With the list of music genres open over the filter key.</summary>
        Music,

        /// <summary>Listing news stations, from every country.</summary>
        News,

        /// <summary>Listing the stations of the listener's country, of every kind.</summary>
        Local,
    }

    /// <summary>The kind of station News lists: one of RadioGenres.All.</summary>
    private const string NewsGenre = "News";

    /// <summary>The kinds of station that are not music, left out of Music's list of genres.</summary>
    private static readonly string[] NotMusic = ["Talk", NewsGenre, "Sports"];

    private Fader _tunerFade = null!;

    /// <summary>The tuner's own copy of the radio pane's model, made the first time it opens.</summary>
    private RadioModel? _tunerModel;

    /// <summary>The remote's one stop over the dial.</summary>
    private RemoteLine? _tunerDialKey;

    private StationWell? _tunerWell;

    /// <summary>What every row is drawn at; replaced on a resize (see SizeTuner).</summary>
    private PegasusRadioLook? _tunerLook;

    /// <summary>The station tuned last while the tuner has been open, so the play key can play it again
    /// after a stop.</summary>
    private RadioStation? _tunerLast;

    /// <summary>Which station the player's well and room were last asked for, so an answer about another
    /// station is dropped.</summary>
    private string? _tunerPlayerFor;

    /// <summary>Which song the player's well was last asked for (SongCovers.KeyOf), or null for none, so an
    /// answer about another song is dropped.</summary>
    private string? _tunerSongFor;

    /// <summary>The station's logo as the well last drew it, kept so it can go back up when a song with no
    /// cover follows one with a cover.</summary>
    private RadioStationItem.FoundLogo _tunerLogo;

    /// <summary>Whether the well is showing the song's cover rather than the station's logo.</summary>
    private bool _tunerShowsSong;

    /// <summary>The station the keys moved the needle to and have not tuned yet. Tuned when the key comes
    /// up (see CommitDialStep).</summary>
    private RadioStation? _dialPreview;

    /// <summary>Music's list of genres, waiting for the filter key to be laid out.</summary>
    private bool _tunerGenresPending;

    /// <summary>Whether the tuner is up.</summary>
    public bool TunerUp { get; private set; }

    /// <summary>The dial's key, its well, and the dial's own press. Once, from BuildRadio.</summary>
    private void BuildTunerDialKey()
    {
        _tunerFade = new Fader(TunerLayer);

        _tunerDialKey = new RemoteLine(new Grid(), PlayOrStopTuner, menu: null, turn: StepTunerDial)
        {
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        TunerDialRow.Children.Add(_tunerDialKey);

        // A drag on the dial with the pointer tunes where the needle lands, as in the main window.
        TunerDial.Tuned += station => _tunerModel?.Tune(station);

        _tunerWell = new StationWell((FontFamily)Resources["PegasusFa"]);
        TunerWellHost.Children.Add(_tunerWell.Root);
    }

    /// <summary>Opens the tuner from radio's wheel, narrowed the way the entry asks.</summary>
    private void OpenTuner(TunerOpening how)
    {
        if (TunerUp || !RadioUp || RecordUp)
            return;

        var model = EnsureTunerModel();
        switch (how)
        {
            case TunerOpening.Music:
                model.FavoritesOnly = false;
                _tunerGenresPending = true;
                break;

            case TunerOpening.News:
                model.FavoritesOnly = false;
                model.SelectedCountry = null;
                model.Sieve = StationSieve.Off with { Genre = NewsGenre };
                break;

            case TunerOpening.Local:
                model.FavoritesOnly = false;
                model.Sieve = StationSieve.Off;
                var (code, name) = ListenerCountry();
                if (code is not null)
                    model.SelectedCountry = new RadioFacetItem(new RadioFacet(name, code, 0));
                break;
        }

        model.EnsureLoaded();
        CrossToTuner();
        RPM.Core.Diagnostics.Scope.Said("radio", $"tuner opened: {how}");
    }

    private RadioModel EnsureTunerModel()
    {
        if (_tunerModel is { } model)
            return model;

        model = new RadioModel(Radio, DispatcherQueue);
        model.PropertyChanged += TunerModel_PropertyChanged;
        model.TunedChanged += SyncTunerDial;
        TunerRows.ItemsSource = model.Stations;
        _tunerModel = model;
        return model;
    }

    /// <summary>Takes radio down and puts the tuner up. The collection is collapsed once its fade lands,
    /// which is what stops radio's rooms behind the tuner (see ApplyRadioShown).</summary>
    private void CrossToTuner()
    {
        TunerUp = true;

        TunerLayer.Visibility = Visibility.Visible;
        TunerLayer.IsHitTestVisible = true;
        _tunerFade.To(1, ScreenCrossMs, null);
        TunerRoom.Shown = true;
        SizeTuner();
        SyncTunerDial();
        PaintTunerTools();

        LibraryLayer.IsHitTestVisible = false;
        _libraryFade.To(0, ScreenCrossMs, () =>
        {
            if (TunerUp)
                SetLibraryLayerShown(false);
        });

        FocusScreenLater(_tunerDialKey);   // with the remote in use: the dial
    }

    /// <summary>Back from the tuner: it goes, and radio comes back over the same beat.</summary>
    public void HideTuner()
    {
        if (!TunerUp)
            return;
        TunerUp = false;
        _dialPreview = null;
        _tunerGenresPending = false;

        TunerLayer.IsHitTestVisible = false;
        TunerRoom.Shown = false;
        _tunerFade.To(0, ScreenCrossMs, () =>
        {
            if (!TunerUp)
                TunerLayer.Visibility = Visibility.Collapsed;
        });

        SetLibraryLayerShown(true);
        LibraryLayer.IsHitTestVisible = true;
        _libraryFade.To(1, ScreenCrossMs, null);
        FocusScreenLater(null);   // with the remote in use: radio's banner card
    }

    /// <summary>Takes the tuner off the screen without crossing back to anything, for the collection going
    /// down under it (see HideLibrary). The record page's DropRecord, for the tuner.</summary>
    private void DropTuner()
    {
        if (!TunerUp)
            return;
        TunerUp = false;
        _dialPreview = null;
        _tunerGenresPending = false;

        TunerLayer.IsHitTestVisible = false;
        TunerLayer.Visibility = Visibility.Collapsed;
        _tunerFade.Set(0);
        TunerRoom.Shown = false;
    }

    /// <summary>Where the focus starts on the tuner: the dial.</summary>
    private Control? TunerFocusTarget() => _tunerDialKey;

    /// <summary>
    /// The tuner's own moves, past what the search by layout finds. Down from the dial or from the list's
    /// tools goes to the list's first station: the dial spans the page and the search by layout finds
    /// the tool under its middle, the favourites key, and nothing in the list stands under that key, so
    /// the next Down would go nowhere. Right out of the list goes to the player's key, which stands
    /// level with only the bottom rows.
    /// </summary>
    private Control? AcrossTuner(FrameworkElement from, FocusNavigationDirection direction, Control? next)
    {
        switch (direction)
        {
            case FocusNavigationDirection.Down when ReferenceEquals(from, _tunerDialKey) || Inside(from, TunerTools):
                return TunerRows.TryGetElement(0) is PegasusStationRow { MainKey: var first } && CanTakeFocus(first)
                    ? first
                    : next;

            case FocusNavigationDirection.Right when Inside(from, TunerBrowser)
                                                     && (next is null || !Inside(next, TunerBrowser)):
                return CanTakeFocus(TunerPlayKey) ? TunerPlayKey : next;

            default:
                return next;
        }
    }

    // ── the model ──

    private void TunerModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RadioModel.DialStations) or nameof(RadioModel.StatusText)
            or nameof(RadioModel.IsReconnecting) or nameof(RadioModel.Current)
            or nameof(RadioModel.CurrentLogo) or nameof(RadioModel.CurrentLogoGround)
            or nameof(RadioModel.CurrentLogoFills))
            SyncTunerDial();
        else
            PaintTunerTools();
    }

    /// <summary>
    /// Feeds the dial the listed stations, points the needle at the one playing and writes its readout:
    /// RadioView.SyncDial, for this dial. The needle is left where the keys put it while a step is waiting
    /// for its key to come up.
    /// </summary>
    private void SyncTunerDial()
    {
        if (_tunerModel is not { } model)
            return;

        // Logo first, for the reason RadioView gives: showing or hiding the badge lays the dial out again,
        // and a layout snaps the needle where it stands.
        TunerDial.SetLogo(model.CurrentLogo, model.CurrentLogoGround, model.CurrentLogoFills);
        TunerDial.SetStations(model.DialStations);
        if (_dialPreview is null)
            TunerDial.PointAt(model.Current?.Uuid);
        TunerDial.SetStatus(model.StatusText);
        TunerDial.SetReconnecting(model.IsReconnecting);

        PaintTunerTools();
        SyncTunerPlayer();
    }

    /// <summary>The list's tools and notes: the country, the dots that say an order or a filter is in
    /// force, the favourites heart, the record turning while the directory is asked with nothing listed,
    /// and why the list is empty where it is.</summary>
    private void PaintTunerTools()
    {
        if (_tunerModel is not { } model)
            return;

        TunerCountryLabel.Text = model.CountryLabel;
        TunerSortDot.Visibility = model.OrderChosen ? Visibility.Visible : Visibility.Collapsed;
        TunerFilterDot.Visibility = model.FiltersOn ? Visibility.Visible : Visibility.Collapsed;

        var on = model.FavoritesOnly;
        TunerFavoritesMark.FontFamily = (FontFamily)Resources[on ? "PegasusFa" : "PegasusFaRegular"];
        TunerFavoritesMark.Foreground = (Brush)Application.Current.Resources[on ? "FavoriteMarkBrush" : "FavoriteMarkIdleBrush"];

        var empty = model.Stations.Count == 0;
        TunerSpinner.Spinning = model.IsBusy && empty;

        // A failure is said even with stations listed (the favourites stay up when the directory goes
        // quiet), and an empty list says why.
        var note = model.Error ?? (empty && !model.IsBusy ? model.EmptyNote : null);
        TunerNote.Text = note ?? "";
        TunerNote.Visibility = note is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── the player ──

    /// <summary>
    /// The player, from the tuner as it stands: what the station is playing as a title and an artist (the
    /// announcement split where it names both, see IcyTitle.Split), else what the connection is doing (the
    /// dial's own readout, RadioModel.StatusText), and the station's name under them. With nothing tuned
    /// it says so and keeps the last station's mark, which the play key plays again. Asked on every change
    /// of the tuner (see ShowWhatRadioPlays) and of the model.
    /// </summary>
    private void SyncTunerPlayer()
    {
        if (_tunerModel is not { } model || _tunerWell is null)
            return;

        var radio = Radio;
        var station = radio.Current;
        if (station is not null)
            _tunerLast = station;

        if (station is null)
        {
            TunerTitle.Text = Loc.Get("Radio_NothingTuned");
            TunerArtist.Visibility = Visibility.Collapsed;
            TunerStation.Text = _tunerLast?.Display ?? "";
        }
        else
        {
            var (artist, title) = IcyTitle.Split(HomeView.Printable(radio.NowPlayingTitle, station));
            TunerTitle.Text = title is { Length: > 0 } ? title : model.StatusText;
            TunerArtist.Text = artist ?? "";
            TunerArtist.Visibility = artist is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
            TunerStation.Text = station.Display;
        }

        TunerStation.Visibility = TunerStation.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        TunerPlayMark.Text = station is null ? "" : "";   // play; stop
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            TunerPlayKey, Strings.Loc.Get(station is null ? "Radio_PlayStation" : "Radio_StopStation"));

        var shown = station ?? _tunerLast;
        if (shown?.Uuid != _tunerPlayerFor)
        {
            _tunerPlayerFor = shown?.Uuid;
            _tunerSongFor = null;
            _tunerShowsSong = false;
            _ = PaintTunerMarkAsync(shown);
        }

        // The song's cover over the station's logo, as the header shows it. Asked only when the song
        // changes, not on every change of the tuner.
        var (songArtist, songTitle) = station is null ? (null, null) : radio.NowPlayingSong;
        var song = station is not null && SongCovers.Asks(songArtist, songTitle)
            ? SongCovers.KeyOf(songArtist!, songTitle!)
            : null;
        if (song != _tunerSongFor)
        {
            _tunerSongFor = song;
            _ = PaintTunerSongAsync(song, songArtist, songTitle);
        }
    }

    /// <summary>
    /// The player's well for one song: the cover of the record it is on, filling the well as a sleeve does.
    /// With no song (an advert, a jingle, the station's own name), with no cover found, or with an ask that
    /// failed, the station's logo goes back up. Every answer is dropped if the song has changed meanwhile.
    /// </summary>
    private async Task PaintTunerSongAsync(string? song, string? artist, string? title)
    {
        if (_tunerWell is not { } well)
            return;

        try
        {
            if (song is not null
                && await App.Services.GetRequiredService<SongCovers>().CoverAsync(artist, title) is { } cover
                && _tunerSongFor == song
                && (await RemoteImage.BytesAsync(cover)).Answers(out var bytes, out _) && bytes is { Length: > 0 }
                && _tunerSongFor == song)
            {
                var raster = XamlRoot?.RasterizationScale is > 0 and var s ? s : 1;
                var drawn = double.IsNaN(well.Root.Width) ? 256 : well.Root.Width;
                var picture = await RemoteImage.DecodeArtAsync(bytes, (int)Math.Ceiling(drawn * raster));
                if (_tunerSongFor != song)
                    return;

                _tunerShowsSong = true;
                well.Show(picture, null, fills: true);
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A cover that fails to arrive says nothing about the song. The station's logo stands.
            RPM.Core.Diagnostics.Scope.Said("radio", $"tuner's player, song cover: {ex.GetType().Name}");
        }

        if (_tunerSongFor != song || !_tunerShowsSong)
            return;

        _tunerShowsSong = false;
        well.Show(_tunerLogo);
    }

    /// <summary>
    /// The player's well and room for one station: the room in the colours of the station's name at once,
    /// then in its mark's own colours, then the mark itself, in the order home's radio card takes them
    /// (a card recoloured before its picture lands looks as if the card is arriving). Every answer is
    /// dropped if the player has moved to another station meanwhile.
    /// </summary>
    private async Task PaintTunerMarkAsync(RadioStation? station)
    {
        if (_tunerWell is not { } well)
            return;

        if (station is null)
        {
            well.Show(default);
            return;
        }

        var uuid = station.Uuid;
        TunerRoom.Palette = new StationFace(station).Room;
        _tunerLogo = default;
        well.Show(default);

        try
        {
            var answer = await RadioStationItem.LogoBytesAsync(station);
            if (_tunerPlayerFor != uuid)
                return;

            if (answer.Answers(out var mark, out _) && mark is { Length: > 0 }
                && await Cancellable.RunAsync(() => CoverRoom.Of(mark), CancellationToken.None) is { Ran: true, Value: var painted }
                && _tunerPlayerFor == uuid)
                TunerRoom.Palette = painted;

            var raster = XamlRoot?.RasterizationScale is > 0 and var s ? s : 1;
            var drawn = double.IsNaN(well.Root.Width) ? 256 : well.Root.Width;
            var found = await RadioStationItem.LogoForAsync(station, (int)Math.Ceiling(drawn * raster));
            if (_tunerPlayerFor != uuid)
                return;

            // Kept for when the song has no cover. A song's cover that is already up stays up.
            _tunerLogo = found;
            if (!_tunerShowsSong)
                well.Show(found);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A station's site that fails to answer says nothing about the station. Its colour stands.
            RPM.Core.Diagnostics.Scope.Said("radio", $"tuner's player, {station.Display}: {ex.GetType().Name}");
        }
    }

    /// <summary>The player's key, and OK on the dial: stops the station playing, or plays the last one
    /// again, or the first listed where none has played.</summary>
    private void PlayOrStopTuner()
    {
        var radio = Radio;
        if (radio.Current is not null)
        {
            radio.Stop();
            return;
        }

        if ((_tunerLast ?? _tunerModel?.DialStations.FirstOrDefault()) is { } station)
            TuneFromRadio(station);
    }

    private void TunerPlay_Click(object sender, RoutedEventArgs e) => PlayOrStopTuner();

    // ── the dial under the remote ──

    /// <summary>Left or Right on the dial: the needle moves to the station before or after the one it is
    /// on, and the station is tuned when the key comes up. False at either end of the band, and the remote
    /// bounces.</summary>
    private bool StepTunerDial(int way)
    {
        if (_tunerModel is not { } model)
            return false;

        var stations = model.DialStations;
        if (stations.Count == 0)
            return false;

        var on = _dialPreview?.Uuid ?? Radio.Current?.Uuid;
        var at = -1;
        for (var i = 0; i < stations.Count && on is not null; i++)
            if (stations[i].Is(on))
            {
                at = i;
                break;
            }

        var next = at < 0 ? (way > 0 ? 0 : stations.Count - 1) : at + way;
        if (next < 0 || next >= stations.Count)
            return false;

        _dialPreview = stations[next];
        TunerDial.PointAt(_dialPreview.Uuid);
        return true;
    }

    /// <summary>Left or Right came up (see AdjustDone): the station the keys moved the needle to is
    /// tuned. Nothing where the keys moved nothing.</summary>
    private void CommitDialStep()
    {
        if (_dialPreview is not { } station)
            return;
        _dialPreview = null;

        if (!Radio.IsTuned(station.Uuid))
            TuneFromRadio(station);
    }

    /// <summary>Music's list of genres opens over the filter key once the key is laid out and
    /// shown.</summary>
    private void TickTuner()
    {
        if (!_tunerGenresPending || !TunerUp || !CanTakeFocus(TunerFilter))
            return;

        _tunerGenresPending = false;
        if (KeyboardDriven)
            RemoteSearch.Give(TunerFilter);
        ShowGenreMenu(TunerFilter);
    }

    // ── the list's tools ──

    private void TunerSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_tunerModel is { } model)
            model.SearchText = TunerSearch.Text;
    }

    private void TunerFavorites_Click(object sender, RoutedEventArgs e)
    {
        if (_tunerModel is { } model)
            model.FavoritesOnly = !model.FavoritesOnly;
    }

    /// <summary>Where the list is listening from: every country, or one of the directory's. A menu at
    /// this view's size rather than the pane's panel, so the remote walks it.</summary>
    private void TunerCountry_Click(object sender, RoutedEventArgs e)
    {
        if (_tunerModel is not { } model)
            return;

        var menu = new MenuFlyout();
        menu.Items.Add(Choice(Loc.Get("Radio_AllCountries"), model.SelectedCountry is not { Code.Length: > 0 },
            () => model.SelectedCountry = null));
        foreach (var country in model.Countries)
        {
            if (country.Code.Length == 0)
                continue;
            var wanted = country;
            menu.Items.Add(Choice(wanted.Name, model.SelectedCountry?.Code == wanted.Code,
                () => model.SelectedCountry = wanted));
        }
        ShowTunerMenu(menu, TunerCountry);
    }

    /// <summary>What order the list is in: the pane's five.</summary>
    private void TunerSort_Click(object sender, RoutedEventArgs e)
    {
        if (_tunerModel is not { } model)
            return;

        var menu = new MenuFlyout();
        Order("Radio_SortListed", StationOrdering.Listed);
        Order("Radio_SortNameUp", StationOrdering.NameUp);
        Order("Radio_SortNameDown", StationOrdering.NameDown);
        Order("Radio_SortVotes", StationOrdering.Votes);
        Order("Radio_SortQuality", StationOrdering.Quality);
        ShowTunerMenu(menu, TunerSort);

        void Order(string key, StationOrdering wanted) =>
            menu.Items.Add(Choice(Loc.Get(key), model.Order == wanted, () => model.Order = wanted));
    }

    /// <summary>What the list is narrowed to: a format, a kind of station and whether it advertises, the
    /// pane's three, each a submenu, and the way back to none of them.</summary>
    private void TunerFilter_Click(object sender, RoutedEventArgs e)
    {
        if (_tunerModel is not { } model)
            return;

        var menu = new MenuFlyout();

        var format = Section("Radio_FilterFormat");
        format.Items.Add(Choice(Loc.Get("Radio_AllFormats"), model.Sieve.Format is null,
            () => model.Sieve = model.Sieve with { Format = null }));
        foreach (var wanted in StationSieve.Formats)
            format.Items.Add(Choice(wanted, model.Sieve.Format == wanted,
                () => model.Sieve = model.Sieve with { Format = wanted }));

        var genre = Section("Radio_FilterGenre");
        genre.Items.Add(Choice(Loc.Get("Radio_AllGenres"), model.Sieve.Genre is null,
            () => model.Sieve = model.Sieve with { Genre = null }));
        foreach (var wanted in StationSieve.Genres)
            genre.Items.Add(Choice(wanted, model.Sieve.Genre == wanted,
                () => model.Sieve = model.Sieve with { Genre = wanted }));

        var ads = Section("Radio_FilterAds");
        ads.Items.Add(Choice(Loc.Get("Radio_AdsEither"), model.Sieve.Ads == AdsWanted.Either,
            () => model.Sieve = model.Sieve with { Ads = AdsWanted.Either }));
        ads.Items.Add(Choice(Loc.Get("Radio_AdsWithout"), model.Sieve.Ads == AdsWanted.Without,
            () => model.Sieve = model.Sieve with { Ads = AdsWanted.Without }));
        ads.Items.Add(Choice(Loc.Get("Radio_AdsWith"), model.Sieve.Ads == AdsWanted.With,
            () => model.Sieve = model.Sieve with { Ads = AdsWanted.With }));

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuEntry(Loc.Get("Radio_ClearFilters"), () => model.Sieve = StationSieve.Off));
        ShowTunerMenu(menu, TunerFilter);

        MenuFlyoutSubItem Section(string key)
        {
            var sub = new MenuFlyoutSubItem
            {
                Text = Loc.Get(key),
                FontSize = 12,
                MinHeight = 26,
                Padding = new Thickness(11, 3, 11, 4),
            };
            menu.Items.Add(sub);
            return sub;
        }
    }

    /// <summary>Music, from the wheel: the kinds of station that are music, to pick one. Opened over the
    /// filter key, where the same choice lives in the filter's Genre.</summary>
    private void ShowGenreMenu(FrameworkElement at)
    {
        if (_tunerModel is not { } model)
            return;

        var menu = new MenuFlyout();
        menu.Items.Add(Choice(Loc.Get("Radio_AllGenres"), model.Sieve.Genre is null,
            () => model.Sieve = model.Sieve with { Genre = null }));
        foreach (var wanted in StationSieve.Genres.Where(g => !NotMusic.Contains(g)))
            menu.Items.Add(Choice(wanted, model.Sieve.Genre == wanted,
                () => model.Sieve = model.Sieve with { Genre = wanted }));
        ShowTunerMenu(menu, at);
    }

    /// <summary>One answer in a menu, ticked where it is the one in force, at the desktop menus' size so
    /// <see cref="ForThisView"/> grows it with the rest.</summary>
    private static ToggleMenuFlyoutItem Choice(string text, bool chosen, Action pick)
    {
        var item = new ToggleMenuFlyoutItem
        {
            Text = text,
            IsChecked = chosen,
            FontSize = 12,
            MinHeight = 26,
            Padding = new Thickness(11, 3, 11, 4),
        };
        item.Click += (_, _) => pick();
        return item;
    }

    private void ShowTunerMenu(MenuFlyout menu, FrameworkElement at)
    {
        menu.Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft;
        ForThisView(menu).ShowAt(at);
    }

    /// <summary>A row realized: it is given the look, and reads its station itself.</summary>
    private void TunerRows_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is PegasusStationRow row && _tunerLook is { } look)
            row.Wear(look);
    }

    // ── sizes ──

    private void TunerLayer_SizeChanged(object sender, SizeChangedEventArgs e) => SizeTuner();

    /// <summary>
    /// The tuner's sizes, off the window and the collection's scale, in set proportions:
    /// the dial across the page and a little under three tenths of the screen tall, the list taking 37
    /// parts of the page's width under it and the player 59, with the rest between them.
    /// </summary>
    private void SizeTuner()
    {
        var width = TunerLayer.ActualWidth > 0 ? TunerLayer.ActualWidth : Root.ActualWidth;
        var height = TunerLayer.ActualHeight > 0 ? TunerLayer.ActualHeight : Root.ActualHeight;
        if (width <= 1 || _tunerDialKey is null || _tunerWell is null)
            return;

        var scale = QueueScale;
        var rows = scale * 1.25;   // the pane's rows, a quarter larger again than the rest of the view
        var side = Math.Max(LibraryEdgeInset, width * 0.054);
        var foot = 34 * scale;
        TunerPage.Margin = new Thickness(side, HomeTopPad, side, foot);
        var page = Math.Max(320, width - side * 2);

        // The dial, laid out at the width it would have at this scale and scaled up whole.
        const double dialHeight = 176;   // RadioDial.DialHeight
        var dial = Math.Clamp(height * 0.28 / dialHeight, 1, 4);
        TunerDial.Width = page / dial;
        TunerDialBox.Width = page;
        TunerDialBox.Height = dialHeight * dial;
        _tunerDialKey.CornerRadius = new CornerRadius(10 * dial);

        var gap = 24 * scale;
        TunerBody.Margin = new Thickness(0, 18 * scale, 0, 0);
        var body = Math.Max(200, height - HomeTopPad - foot - dialHeight * dial - 18 * scale);

        // The list's tools.
        var tool = 30 * rows;
        TunerTools.ColumnSpacing = 8 * scale;
        TunerTools.Margin = new Thickness(0, 0, 0, 10 * scale);
        TunerSearchBox.Height = tool;
        TunerSearchBox.CornerRadius = new CornerRadius(6 * scale);
        TunerSearchBox.Padding = new Thickness(10 * scale, 0, 10 * scale, 0);
        TunerSearchMark.FontSize = 12 * rows;
        TunerSearchMark.Margin = new Thickness(0, 0, 8 * scale, 0);
        TunerSearch.FontSize = 13 * rows;
        TunerCountry.Width = 132 * rows;
        TunerCountry.Padding = new Thickness(10 * scale, 0, 10 * scale, 0);
        TunerCountryLabel.FontSize = 12 * rows;
        TunerCountryMark.FontSize = 8 * rows;
        foreach (var key in new Button[] { TunerCountry, TunerSort, TunerFilter, TunerFavorites })
        {
            key.Height = tool;
            key.CornerRadius = new CornerRadius(6 * scale);
        }
        TunerSort.Width = TunerFilter.Width = TunerFavorites.Width = 34 * rows;
        TunerSortMark.FontSize = TunerFilterMark.FontSize = TunerFavoritesMark.FontSize = 12 * rows;
        TunerSortDot.Width = TunerSortDot.Height = TunerFilterDot.Width = TunerFilterDot.Height = 5 * rows;
        TunerSortDot.Margin = TunerFilterDot.Margin = new Thickness(0, -1, -2, 0);
        TunerRowsLayout.Spacing = 4 * scale;
        TunerNote.FontSize = 13 * rows;
        TunerNote.Margin = new Thickness(28 * scale, 26 * scale, 28 * scale, 0);

        // The player.
        var player = page * 0.59;
        TunerPlayer.Width = player;
        TunerPlayer.Height = body;
        TunerPlayer.Margin = new Thickness(gap, 0, 0, 0);
        var corner = new CornerRadius(14 * scale);
        TunerPlayerFrost.CornerRadius = TunerPlayerVeil.CornerRadius = TunerPlayerGlass.CornerRadius = corner;
        TunerRoom.CornerRadius = corner;
        _tunerWell.Size(Math.Min(body * 0.44, player * 0.4));
        TunerWellHost.Margin = new Thickness(0, 0, 0, 22 * scale);
        TunerTitle.FontSize = 26 * scale;
        TunerArtist.FontSize = 20 * scale;
        TunerStation.FontSize = 16 * scale;
        TunerTitle.MaxWidth = TunerArtist.MaxWidth = TunerStation.MaxWidth = player * 0.85;
        TunerPlayerBody.Spacing = 6 * scale;
        TunerPlayKey.Width = TunerPlayKey.Height = 64 * scale;
        TunerPlayKey.CornerRadius = new CornerRadius(32 * scale);
        TunerPlayKey.Margin = new Thickness(0, 22 * scale, 0, 0);
        TunerPlayMark.FontSize = 22 * scale;

        // A new look when the scale changed, handed to every row already realized.
        if (_tunerLook is { } was && Math.Abs(was.Scale - rows) < 0.001)
            return;

        _tunerLook = new PegasusRadioLook(
            rows, 0,
            (Style)Resources["PegasusPlainButton"],
            (FontFamily)Resources["PegasusFa"],
            (FontFamily)Resources["PegasusFaRegular"],
            Press: item => _tunerModel?.Toggle(item),
            Heart: item => _tunerModel?.ToggleFavorite(item));

        var count = TunerRows.ItemsSourceView?.Count ?? 0;
        for (var i = 0; i < count; i++)
            if (TunerRows.TryGetElement(i) is PegasusStationRow row)
                row.Wear(_tunerLook);
    }
}
