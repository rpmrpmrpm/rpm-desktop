using System.ComponentModel;
using RPM.Core.Radio;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>
/// The Internet Radio pane: a tuning dial above a searchable station list, with the user's presets one
/// click away.
///
/// <para>The dial and the list show the same stations. Whatever the list shows is what the dial can reach.
/// So tuning by dragging the needle and tuning by pressing a row's play button both select the same
/// station. A station tuned from either place moves the needle.</para>
/// </summary>
public sealed partial class RadioView : UserControl
{
    private RadioModel? _model;

    /// <summary>The station search. Cmd or Ctrl+F moves the keyboard focus here (see
    /// MainPage.FocusSearch).</summary>
    internal TextBox SearchField => SearchBox;

    public RadioView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => HookModel();
        Loaded += (_, _) => HookModel();
        Unloaded += (_, _) => Unhook();
        Dial.Tuned += station => _model?.Tune(station);
    }

    private void HookModel()
    {
        var next = DataContext as RadioModel;
        if (ReferenceEquals(next, _model))
            return;

        Unhook();
        _model = next;
        if (_model is null)
            return;

        _model.PropertyChanged += OnModelChanged;
        _model.TunedChanged += SyncDial;
        _model.EnsureLoaded();
        SyncDial();
        SetFavoritesToggle();
    }

    private void Unhook()
    {
        if (_model is null)
            return;
        _model.PropertyChanged -= OnModelChanged;
        _model.TunedChanged -= SyncDial;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RadioModel.DialStations) or nameof(RadioModel.StatusText)
            or nameof(RadioModel.IsReconnecting) or nameof(RadioModel.Current))
            SyncDial();
        if (e.PropertyName is nameof(RadioModel.FavoritesOnly))
            SetFavoritesToggle();
    }

    /// <summary>The presets switch shows the app's favourite heart: filled and red while the switch is on,
    /// and a dim outline while it is off. It uses the same pair as the library header's switch, so one
    /// mark means one thing across the app.</summary>
    private void SetFavoritesToggle()
    {
        var on = _model?.FavoritesOnly == true;
        FavoritesIcon.FontFamily = (FontFamily)Resources[on ? "Fa" : "FaRegular"];
        FavoritesIcon.Foreground = (Brush)Application.Current.Resources[
            on ? "FavoriteMarkBrush" : "FavoriteMarkIdleBrush"];
    }

    /// <summary>Gives the dial the listed stations, points the needle at the playing station, and writes
    /// its readout. This is the only place that updates the dial, so the dial cannot disagree with the
    /// list under it.</summary>
    private void SyncDial()
    {
        if (_model is null)
            return;
        // Set the logo first. Showing or hiding the badge re-lays out the dial, and a re-layout snaps the
        // needle where it stands. If the logo were set after PointAt, the needle's travel would be cut short.
        Dial.SetLogo(_model.CurrentLogo, _model.CurrentLogoGround, _model.CurrentLogoFills);
        Dial.SetStations(_model.DialStations);
        Dial.PointAt(_model.Current?.Uuid);
        Dial.SetStatus(_model.StatusText);
        Dial.SetReconnecting(_model.IsReconnecting);
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RadioStationItem item })
            _model?.Toggle(item);
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RadioStationItem item })
            _model?.ToggleFavorite(item);
    }

    /// <summary>
    /// The filter panel: a format, a kind of station, and whether it has ads. Each is a list with the one
    /// in force wearing a tick.
    ///
    /// <para>It is one panel, not three controls on the pane's row. The row already holds a search field, a
    /// country and the presets switch. These filters are used rarely, so a panel is the right place for
    /// them. The album wall does the same for its ordering.</para>
    ///
    /// <para>It stays open while it is used. Three questions in three sections is more than one press in
    /// most cases. A panel that closed on the first press would make the second one a new trip.</para>
    ///
    /// <para>Each entry sets the WHOLE sieve, with one field changed. The model holds the sieve as one value
    /// (see StationSieve). So no stale field can be left behind, in any order. The listing is re-read once
    /// per press rather than once per question.</para>
    /// </summary>
    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null || sender is not FrameworkElement button)
            return;

        var panel = new Panel(Icons, FilterWidth, staysOpen: true);

        panel.Heading(Loc.Get("Radio_FilterFormat"));
        panel.Entry(Loc.Get("Radio_AllFormats"),
                    () => _model?.Sieve.Format is null,
                    () => Narrow(sieve => sieve with { Format = null }));
        foreach (var format in StationSieve.Formats)
        {
            var wanted = format;
            panel.Entry(wanted,
                        () => _model?.Sieve.Format == wanted,
                        () => Narrow(sieve => sieve with { Format = wanted }));
        }

        panel.Heading(Loc.Get("Radio_FilterGenre"));
        panel.Entry(Loc.Get("Radio_AllGenres"),
                    () => _model?.Sieve.Genre is null,
                    () => Narrow(sieve => sieve with { Genre = null }));
        foreach (var genre in StationSieve.Genres)
        {
            var wanted = genre;
            panel.Entry(wanted,
                        () => _model?.Sieve.Genre == wanted,
                        () => Narrow(sieve => sieve with { Genre = wanted }));
        }

        panel.Heading(Loc.Get("Radio_FilterAds"));
        Ads("Radio_AdsEither", AdsWanted.Either);
        Ads("Radio_AdsWithout", AdsWanted.Without);
        Ads("Radio_AdsWith", AdsWanted.With);

        panel.Show(button, Loc.Get("Radio_ClearFilters"),
                   undoable: () => _model?.Sieve.IsOn == true,
                   undo: () => { if (_model is not null) _model.Sieve = StationSieve.Off; });

        void Ads(string key, AdsWanted wanted) => panel.Entry(
            Loc.Get(key),
            () => _model?.Sieve.Ads == wanted,
            () => Narrow(sieve => sieve with { Ads = wanted }));

        void Narrow(Func<StationSieve, StationSieve> change)
        {
            if (_model is not null)
                _model.Sieve = change(_model.Sieve);
        }
    }

    /// <summary>
    /// The order the stations are listed in: the directory's own order, by name either way, or by the two
    /// numbers the directory publishes about a station.
    ///
    /// <para>It uses the same panel as the filter, and it closes on the press, like the country panel. An
    /// order is one answer, and there is nothing else on the panel to go on to. The way back is pinned
    /// under the list. The directory's own order is where every listing starts, so a listener who sorted
    /// by name does not have to remember it.</para>
    /// </summary>
    private void Sort_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null || sender is not FrameworkElement button)
            return;

        var panel = new Panel(Icons, SortWidth, staysOpen: false);

        panel.Heading(Loc.Get("Radio_SortHeading"));
        Order("Radio_SortListed", StationOrdering.Listed);
        Order("Radio_SortNameUp", StationOrdering.NameUp);
        Order("Radio_SortNameDown", StationOrdering.NameDown);
        Order("Radio_SortVotes", StationOrdering.Votes);
        Order("Radio_SortQuality", StationOrdering.Quality);

        panel.Show(button, Loc.Get("Radio_ResetOrder"),
                   undoable: () => _model?.OrderChosen == true,
                   undo: () => { if (_model is not null) _model.Order = StationOrdering.Listed; });

        void Order(string key, StationOrdering wanted) => panel.Entry(
            Loc.Get(key),
            () => _model?.Order == wanted,
            () => { if (_model is not null) _model.Order = wanted; });
    }

    /// <summary>
    /// Where the dial is listening from: the countries the directory lists, in alphabetical order, with the
    /// way back to all of them pinned under the list.
    ///
    /// <para>It uses the same panel as the filter button. A combo box would put two answers to one kind of
    /// question on one row, in two different shapes. It would also leave the way back to every country as
    /// one more line in a list of eighty, not as a button that stays in one place.</para>
    ///
    /// <para>It closes on the press, unlike the filters. A country is one answer, and choosing it starts a
    /// fresh search at the directory. There is nothing else on the panel to go on to.</para>
    /// </summary>
    private void Country_Click(object sender, RoutedEventArgs e)
    {
        if (_model is null || sender is not FrameworkElement button)
            return;

        var panel = new Panel(Icons, CountryWidth, staysOpen: false);

        foreach (var country in _model.Countries)
        {
            // A row that narrows by nothing is not listed. The pinned button is that answer. A row saying the
            // same thing would be a second way out that scrolls away.
            if (country.Code.Length == 0)
                continue;

            var wanted = country;
            // The tick compares the CODE, not the object. The country list is rebuilt each time the directory
            // is asked for it. A tick that compared objects would go out on a reload, while the search under
            // it was still narrowed to that country.
            panel.Entry(wanted.Name,
                        () => _model?.SelectedCountry?.Code == wanted.Code,
                        () => { if (_model is not null) _model.SelectedCountry = wanted; });
        }

        panel.Show(button, Loc.Get("Radio_ClearLocation"),
                   undoable: () => _model?.SelectedCountry?.Code.Length > 0,
                   undo: () => { if (_model is not null) _model.SelectedCountry = null; });
    }

    /// <summary>The pane's icon font, for the tick a chosen entry wears. It comes from the view's own
    /// resources, where the glyphs the rest of the pane uses also come from.</summary>
    private FontFamily Icons => (FontFamily)Resources["Fa"];

    /// <summary>How wide each panel is: wide enough for the longest label it carries, such as "Adult
    /// Contemporary" on one and a country's full name on the other. Not wider, because both hang off a
    /// pane that is itself narrow.</summary>
    private const double FilterWidth = 210;

    private const double CountryWidth = 200;

    private const double SortWidth = 190;

    /// <summary>A logo link that does not resolve. Directory entries often outlive the servers behind them,
    /// so this is common, not exceptional. The row falls back to the antenna mark.</summary>
    private void Logo_Failed(object sender, ExceptionRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RadioStationItem item })
            item.NoteLogoFailed();
    }
}
