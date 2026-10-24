using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RPM.Desktop.Presentation.Main.LeftPane.Models;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// Radio's search: one field in the light glass in the middle of the screen, radio blurred behind it. Once
/// there is a query the field moves to the top, and under it a card of the same glass holds the stations
/// the directory found. OK on the remote
/// lets the listener type. The most relevant results are listed in the card and read again when a
/// character is typed. When nothing is found, the card says so in its middle. The arrow keys choose a
/// station, and OK on it plays the station and closes the search.
///
/// <list type="bullet">
/// <item><b>The field has the focus when the search opens</b>, so typing goes straight into it; OK in it
/// asks the directory at once instead of waiting for the typing to stop, and Down goes to the first
/// result.</item>
/// <item><b>The results are the radio pane's own model</b> (RadioModel), a copy of its own so the main
/// window's pane keeps its query: the directory's most played stations matching the words, most played
/// first, asked again 450 ms after the typing stops (RadioModel.TypingPause). The directory is
/// volunteer-run, and asking on every key would be a request per letter.</item>
/// <item><b>OK on a result</b> tunes it, says so along the foot, and closes the search.</item>
/// </list>
///
/// <para>Back closes the search (see PegasusPlayer.TakeBack). The query is kept for the next time.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    private Fader _radioSearchFade = null!;

    /// <summary>The search's own copy of the radio pane's model, made the first time it opens.</summary>
    private RadioModel? _searchModel;

    /// <summary>What every result tile is drawn at. Replaced on a resize, and handed to each tile as the
    /// repeater realizes it (see RadioSearchTiles_ElementPrepared).</summary>
    private PegasusRadioLook? _searchLook;

    /// <summary>Whether the search is open, or opening.</summary>
    public bool RadioSearchOpen { get; private set; }

    /// <summary>
    /// Opens the search or closes it: a fade either way, out of reach of the pointer as it starts closing,
    /// collapsed once the fade out lands if it has not been opened again. Opened only over radio with
    /// nothing else over it. Opening puts the focus in the field, for the remote and the pointer alike;
    /// closing puts it back on radio.
    /// </summary>
    public void ShowRadioSearch(bool open)
    {
        if (RadioSearchOpen == open || (open && !RadioHomeInFront))
            return;

        RadioSearchOpen = open;
        RadioSearchLayer.IsHitTestVisible = open;
        RPM.Core.Diagnostics.Scope.Said("radio", open ? "search opened" : "search closed");

        if (open)
        {
            var model = EnsureSearchModel();
            RadioSearchLayer.Visibility = Visibility.Visible;
            _radioSearchFade.To(1, MenuInMs, null);
            SizeRadio(QueueScale);
            model.EnsureLoaded();
            PaintSearchNote();
            FocusFieldLater(RadioSearchBox);
            return;
        }

        _radioSearchFade.To(0, MenuOutMs, () =>
        {
            if (!RadioSearchOpen)
                RadioSearchLayer.Visibility = Visibility.Collapsed;
        });
        FocusScreenLater(null);   // with the remote in use: radio's banner card
    }

    private RadioModel EnsureSearchModel()
    {
        if (_searchModel is { } model)
            return model;

        model = new RadioModel(Radio, DispatcherQueue);
        model.PropertyChanged += SearchModel_PropertyChanged;
        RadioSearchTiles.ItemsSource = model.Stations;
        _searchModel = model;
        return model;
    }

    private void SearchModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RadioModel.IsBusy) or nameof(RadioModel.EmptyNote)
            or nameof(RadioModel.Error) or nameof(RadioModel.DialStations))
            PaintSearchNote();
    }

    /// <summary>
    /// What stands in the middle of the results card when there is nothing to list: the model's own
    /// sentence ("No stations match your search.", or that the directory could not be reached), and the
    /// record turning while the directory is being asked with nothing listed yet.
    /// </summary>
    private void PaintSearchNote()
    {
        if (_searchModel is not { } model)
            return;

        var empty = model.Stations.Count == 0;
        RadioResultsSpinner.Spinning = model.IsBusy && empty;

        var note = model.IsBusy ? null : empty ? model.EmptyNote ?? model.Error : null;
        RadioResultsNote.Text = note ?? "";
        RadioResultsNote.Visibility = note is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Each letter typed: the model asks the directory once the typing stops. The first letter
    /// takes the field to the top and brings the results card up under it; clearing the field takes it
    /// back to the middle (see PlaceRadioSearchField).</summary>
    private void RadioSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_searchModel is { } model)
            model.SearchText = RadioSearchBox.Text;
        PlaceRadioSearchField();
    }

    /// <summary>The search's height and scale as SizeRadioSearch last had them, so a letter typed can
    /// place the field without a resize.</summary>
    private double _radioSearchHeight, _radioSearchScale = 1;

    /// <summary>
    /// Where the field stands: in the middle of the screen while it is empty, with nothing under it, and
    /// at the top of the screen once there is a query, with the results card from under it to near the
    /// foot, so the stations found get most of the screen. The TV's search does the same (StationSearch):
    /// a field left in the middle gave the results only the lower half.
    /// </summary>
    private void PlaceRadioSearchField()
    {
        var scale = _radioSearchScale;
        var field = 64 * scale;
        var asked = RadioSearchBox.Text.Trim().Length > 0;
        var top = asked ? HomeTopPad : Math.Max(HomeTopPad, _radioSearchHeight / 2 - field / 2);
        RadioSearchColumn.Margin = new Thickness(0, top, 0, 34 * scale);
        RadioResultsCard.Visibility = asked ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>OK (Enter) in the field asks at once, rather than after the typing pause.</summary>
    private void RadioSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.NavigationAccept
                          or Windows.System.VirtualKey.GamepadA))
            return;

        e.Handled = true;
        _searchModel?.Search();
    }

    private void RadioSearchGo_Click(object sender, RoutedEventArgs e) => _searchModel?.Search();

    /// <summary>A result tile realized: it is given the look, and reads its station itself.</summary>
    private void RadioSearchTiles_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Element is PegasusStationTile tile && _searchLook is { } look)
            tile.Wear(look);
    }

    /// <summary>OK on a result: tuned, and the search closes onto radio.</summary>
    private void PickSearchResult(RadioStationItem item)
    {
        TuneFromRadio(item.Station);
        ShowRadioSearch(false);
    }

    /// <summary>
    /// Down from the field, or from the key beside it, goes to the first result rather than to whichever
    /// tile stands under the field's middle: the list is read from its start.
    /// </summary>
    private Control? IntoSearchResults(FrameworkElement from, FocusNavigationDirection direction, Control? next)
    {
        if (direction != FocusNavigationDirection.Down
            || !(ReferenceEquals(from, RadioSearchBox) || ReferenceEquals(from, RadioSearchGo)))
            return next;

        return RadioSearchTiles.TryGetElement(0) is Control first && CanTakeFocus(first) ? first : next;
    }

    /// <summary>
    /// The search's sizes, off the window and the collection's scale: the column half the screen wide with
    /// the field where PlaceRadioSearchField puts it, and the results card from
    /// under the field to near the foot of the screen, four tiles across.
    /// </summary>
    private void SizeRadioSearch(double scale, double width, double height)
    {
        var column = Math.Max(560, width * 0.5);
        var field = 64 * scale;
        var gap = 12 * scale;

        RadioSearchColumn.Width = column;
        _radioSearchHeight = height;
        _radioSearchScale = scale;
        PlaceRadioSearchField();

        RadioSearchField.Height = field;
        var corner = new CornerRadius(12 * scale);
        RadioSearchFrost.CornerRadius = RadioSearchGlass.CornerRadius = corner;
        RadioSearchBox.FontSize = 26 * scale;
        RadioSearchBox.Margin = new Thickness(22 * scale, 0, 22 * scale, 0);

        RadioSearchGo.Width = RadioSearchGo.Height = field;
        RadioSearchGo.Margin = new Thickness(gap, 0, 0, 0);
        RadioSearchGo.CornerRadius = corner;
        RadioSearchGoMark.FontSize = 22 * scale;

        RadioResultsCard.Margin = new Thickness(0, gap, 0, 0);
        RadioResultsFrost.CornerRadius = RadioResultsGlass.CornerRadius = corner;
        RadioResultsNote.FontSize = 18 * scale;
        RadioResultsNote.Margin = new Thickness(30 * scale);

        var pad = 22 * scale;
        var between = 26 * scale;
        var edge = Math.Floor((column - pad * 2 - between * 3) / 4);
        RadioResultsScroll.Padding = new Thickness(pad);
        RadioResultsLayout.MinItemWidth = edge;
        RadioResultsLayout.MinItemHeight = edge + (10 + 15 * 1.4) * scale;
        RadioResultsLayout.MinColumnSpacing = between;
        RadioResultsLayout.MinRowSpacing = 22 * scale;

        // A new look when the sizes changed, handed to every tile already realized; the rest take it as
        // they are realized.
        if (_searchLook is { } was && Math.Abs(was.Edge - edge) < 0.5 && Math.Abs(was.Scale - scale) < 0.001)
            return;

        _searchLook = new PegasusRadioLook(
            scale, edge,
            (Style)Resources["PegasusPlainButton"],
            (FontFamily)Resources["PegasusFa"],
            (FontFamily)Resources["PegasusFaRegular"],
            PickSearchResult,
            Heart: static _ => { });

        var count = RadioSearchTiles.ItemsSourceView?.Count ?? 0;
        for (var i = 0; i < count; i++)
            if (RadioSearchTiles.TryGetElement(i) is PegasusStationTile tile)
                tile.Wear(_searchLook);
    }
}
