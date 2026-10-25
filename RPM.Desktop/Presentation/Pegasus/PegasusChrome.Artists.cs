using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Library;
using RPM.Core.Metadata.Providers;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Services.Art;
using Loc = RPM.Desktop.Strings.Loc;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The artists list in Pegasus: every act the collection files a record under, as a round photograph with
/// the name under it (no count of records, as on the Android TV app), six to a row at a scale of 1. OK on
/// one opens the artist's page (PegasusChrome.ArtistPage).
///
/// <para><b>Like the desktop artists wall, for a remote.</b> It uses the same acts in the same name order
/// (<see cref="ArtistWall.Build"/>), the same photographs (<see cref="ArtistPortraits"/>) and a search
/// field over the same names (<see cref="ArtistWall.Matches"/>). The search field and the phone's four sort
/// orders (<see cref="ArtistSort"/>) are in the tools at the top right, where the album wall keeps its own.
/// A photograph is round, so an act never looks like a record on a wall of sleeves.</para>
///
/// <para><b>The alphabet down the right edge, in name order.</b> The desktop's index rail is a pointer's
/// way along a long list; the remote's is the TV's ladder (<see cref="ArtistLadder"/>; FOBO c86660c2e, ANDR
/// 1128831). Only in name order: in the other orders a letter's acts are not together. It holds the
/// letters the list has (<see cref="ArtistLetters"/>), A to Z and "#" last, so no letter leads nowhere.
/// Right from the last card of a row enters it, on the letter of the card the remote came from, and it
/// grows. Up and Down step through the letters and scroll the list so the row of the letter's first act
/// stands where the first row does. Left, OK and Back go to that act. Right goes nowhere, and Up on A or
/// Down on "#" bounces. A click on a letter scrolls the list there too.</para>
///
/// <para><b>Photographs are fetched three at a time.</b> A card asks for its photograph only while its row
/// is on screen. The frame tick starts the next request whenever fewer than three are in progress (see
/// <see cref="PumpArtistFaces"/>). A row that scrolls away gives back its decoded pictures and keeps the
/// encoded bytes. So scrolling back decodes them again without asking the network.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>How many artists stand in a row at a scale of 1: the wall's eight, so an act and a record
    /// are one size across the collection's screens. Fewer and larger on a tall screen, as the wall's
    /// covers are (see LibraryColumnsAt).</summary>
    private const int ArtistColumns = LibraryColumns;

    private static int ArtistColumnsAt(double scale) =>
        Math.Max(1, (int)Math.Round(ArtistColumns / Math.Max(1, scale)));

    /// <summary>The gap between two photographs, across and down. It is made of the StackLayout Spacing on
    /// ArtistsWall in the chrome's XAML (6), and the room each card has on both sides (8 and 8). These must
    /// add up to this value.</summary>
    private const double ArtistGap = 22;

    /// <summary>How many photographs are asked for at once.</summary>
    private const int ArtistFacesAtOnce = 3;

    /// <summary>How many photographs' bytes are kept between builds of the list. Each is a cut-down copy of
    /// a few tens of kilobytes.</summary>
    private const int ArtistFacesKept = 400;

    private ArtistOrdering _artistOrdering = ArtistOrdering.Name;
    private string _artistsSearch = "";
    private bool _artistsSearchFocused;
    private DispatcherQueueTimer? _artistsSearchSettle;

    /// <summary>Every act of the shelf the list was built from, as a card, in name order.</summary>
    private List<PegasusArtistCard> _artistCards = [];

    /// <summary>The rows on screen: <see cref="_artistCards"/> searched, ordered and cut.</summary>
    private readonly List<PegasusArtistRow> _artistRows = [];

    private int _artistColumnsCut = ArtistColumns;
    private readonly PegasusFaceMetrics _artistMetrics = new();

    /// <summary>The shelf the cards were made from. Compared with <see cref="_shelf"/> by reference on
    /// every visit, so a newer read is noticed without anybody being told.</summary>
    private LibraryAlbumIndex.ArtistShelf? _artistsFrom;

    /// <summary>The cards whose rows are realized: the only ones that ask for a photograph.</summary>
    private readonly HashSet<PegasusArtistCard> _artistsOnScreen = [];

    private int _artistFacesOut;

    /// <summary>Photographs already fetched, by act name: the cut-down bytes, or null for an act the
    /// catalogues have no photograph of. Kept across builds so a visit after an import does not fetch every
    /// face again.</summary>
    private readonly Dictionary<string, byte[]?> _artistFaces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The alphabet down the right edge, and the one stop the remote has on it. Built once, in
    /// <see cref="BuildArtistLadder"/>.</summary>
    private ArtistLadder _ladder = null!;
    private RemoteLine _ladderKey = null!;

    /// <summary>The ladder's letters, each with the place of its first act in <see cref="_artistsShown"/>.
    /// Empty while the ladder is not shown.</summary>
    private IReadOnlyList<ArtistLetter> _ladderLetters = [];

    /// <summary>The place in the list of the card that last had the focus, so the ladder opens on its
    /// letter.</summary>
    private int _ladderFrom;

    /// <summary>A row the repeater laid out, for the height every row has (see PegasusArtistTemplate).</summary>
    private FrameworkElement? _artistRowSample;

    /// <summary>The ladder's room at the screen's right edge and under its last letter, in fixed pixels (the
    /// TV's 12 and 28).</summary>
    private const double LadderEdge = 12, LadderFoot = 28;

    /// <summary>Whether the artists list is the screen in front, with nothing over it.</summary>
    private bool ArtistsInFront => LibraryUp && !RecordUp && _collection == CollectionScreen.Artists;

    // Filling the list.

    /// <summary>The list as it should stand on arriving at it: built again if the shelf is newer than the
    /// cards, and every photograph that failed last time asked again.</summary>
    private void EnsureArtistsFilled()
    {
        foreach (var card in _artistCards)
            if (card.Face == FaceState.Failed)
                card.Face = FaceState.NotAsked;

        if (_shelf is not null && !ReferenceEquals(_artistsFrom, _shelf))
            FillArtists();
        else if (_shelf is null)
            ShowArtistsEmpty(null);
    }

    /// <summary>
    /// Makes the cards from the shelf, keeping the ones already made where the shelf names the same acts
    /// with the same records (most visits: nothing changed), then lays them out.
    /// </summary>
    private void FillArtists()
    {
        if (_shelf is not { } shelf)
            return;

        var acts = shelf.Acts;
        var same = acts.Count == _artistCards.Count;
        for (var i = 0; same && i < acts.Count; i++)
            same = _artistCards[i].Act == acts[i]
                   || (string.Equals(_artistCards[i].Name, acts[i].Name, StringComparison.Ordinal)
                       && _artistCards[i].Act.Albums.Count == acts[i].Albums.Count
                       && _artistCards[i].Act.Together.Count == acts[i].Together.Count);

        _artistsFrom = shelf;

        // The acts are the same as the cards on screen, so the cards stay, with their rows and focus. A
        // read can land a moment after the list is opened. Rebuilding the rows then would lose the remote's
        // place.
        if (same && _artistRows.Count > 0)
            return;

        if (!same)
        {
            _artistsOnScreen.Clear();
            _artistCards = acts.Select(act => MakeArtistCard(act, shelf)).ToList();
        }

        RefillArtists();
    }

    private PegasusArtistCard MakeArtistCard(ArtistWallItem act, LibraryAlbumIndex.ArtistShelf shelf)
    {
        var compilations = ArtistCredit.IsVariousArtists(act.Name);
        var first =act.Own.Select(f => shelf.AlbumFacts.TryGetValue(f, out var facts) ? facts.Album : null)
                           .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

        var card = new PegasusArtistCard
        {
            Name = act.Name,
            Letter = compilations ? "" : ArtistWall.Initial(act.Name).ToString(),
            Glyph = compilations ? "\uf0c0" : "",
            Metrics = _artistMetrics,
            Act = act,
            FirstRecord = first,
        };

        // A photograph already fetched on an earlier visit, or known not to exist.
        if (compilations)
            card.Face = FaceState.None;
        else if (_artistFaces.TryGetValue(act.Name, out var bytes))
        {
            card.PictureBytes = bytes;
            card.Face = bytes is null ? FaceState.None : FaceState.Shown;
        }
        return card;
    }

    /// <summary>The cards searched and ordered, cut into rows and handed to the repeater. What every change
    /// of the search or the order lands on.</summary>
    private void RefillArtists()
    {
        if (_shelf is not { } shelf)
            return;

        var wanted = ArtistWall.SearchKey(_artistsSearch);
        var shown = wanted.Length == 0
            ? _artistCards
            : _artistCards.Where(c => ArtistWall.SearchKey(c.Name).Contains(wanted, StringComparison.Ordinal)).ToList();

        if (_artistOrdering != ArtistOrdering.Name)
        {
            var byAct = shown.ToDictionary(c => c.Act, (IEqualityComparer<ArtistWallItem>)ReferenceEqualityComparer.Instance);
            shown = ArtistSort.By(shown.Select(c => c.Act).ToList(), _artistOrdering, shelf.AlbumFacts)
                              .Select(a => byAct[a])
                              .ToList();
        }

        for (var i = 0; i < shown.Count; i++)
            shown[i].Index = i;

        FillArtistLadder(shown);
        CutArtists(shown, ArtistColumnsAt(QueueScale));
        ArtistsScroll.ChangeView(null, 0, null, disableAnimation: true);
        SizeArtists(QueueScale);   // the cards make room for the ladder, or take it back

        ShowArtistsEmpty(shown.Count > 0
            ? null
            : _artistCards.Count == 0 ? "Artists_Empty" : "Artists_NoMatch");
    }

    /// <summary>The cards on screen, as last searched and ordered.</summary>
    private List<PegasusArtistCard> _artistsShown = [];

    private void CutArtists(List<PegasusArtistCard> shown, int columns)
    {
        _artistsShown = shown;
        _artistColumnsCut = columns;
        _artistsOnScreen.Clear();
        _artistRows.Clear();
        for (var at = 0; at < shown.Count; at += columns)
        {
            var row = new PegasusArtistRow();
            row.Cards.AddRange(shown.GetRange(at, Math.Min(columns, shown.Count - at)));
            _artistRows.Add(row);
        }

        // Set again rather than changed in place: the list is a plain one, replaced whole.
        ArtistsWall.ItemsSource = null;
        ArtistsWall.ItemsSource = _artistRows;
    }

    /// <summary>The line that stands in the list's place, or nothing.</summary>
    private void ShowArtistsEmpty(string? key)
    {
        ArtistsEmpty.Text = key is null ? "" : Loc.Get(key);
        ArtistsEmpty.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // The cards on screen.

    private void ArtistsWall_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (args.Index < 0 || args.Index >= _artistRows.Count)
            return;

        _artistRowSample = args.Element as FrameworkElement;
        foreach (var card in _artistRows[args.Index].Cards)
        {
            _artistsOnScreen.Add(card);

            // A photograph fetched before and given back when its row scrolled away: decoded again from the
            // bytes, without the network.
            if (card is { Face: FaceState.Shown, Picture: null, PictureBytes: { } bytes })
                ShowArtistFaceAsync(card, bytes);
        }
    }

    private void ArtistsWall_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if ((args.Element as FrameworkElement)?.DataContext is not PegasusArtistRow row)
            return;

        foreach (var card in row.Cards)
        {
            _artistsOnScreen.Remove(card);
            card.Picture = null;   // the decoded picture goes; the bytes stay on the card
        }
    }

    /// <summary>
    /// Starts photograph requests for cards on screen, until <see cref="ArtistFacesAtOnce"/> are in
    /// progress. This runs from the frame tick while the list is in front, and when a request finishes.
    /// </summary>
    private void PumpArtistFaces()
    {
        if (_artistFacesOut >= ArtistFacesAtOnce || _artistsOnScreen.Count == 0)
            return;

        foreach (var card in _artistsOnScreen)
        {
            if (_artistFacesOut >= ArtistFacesAtOnce)
                return;
            if (card.Face != FaceState.NotAsked)
                continue;
            LoadArtistFaceAsync(card);
        }
    }

    /// <summary>
    /// One act's photograph. The picture is chosen first (<see cref="ArtistPortraits.SquareAsync"/>). That
    /// choice is settled once per act for the whole app, so the list and the artist's page show the same
    /// one. Then its cut-down copy is made (<see cref="ArtistPortraits.PictureAsync"/>), and it is decoded
    /// at the card's size.
    /// </summary>
    /// <remarks>
    /// No cancellation token reaches the network. Both requests are shared and settled per act. A card that
    /// has scrolled away still wants its photograph for when it comes back. So whether the card is still on
    /// screen is checked after the decode. A card that is no longer on screen keeps the bytes and drops the
    /// picture.
    /// </remarks>
    private async void LoadArtistFaceAsync(PegasusArtistCard card)
    {
        card.Face = FaceState.Asking;
        _artistFacesOut++;
        try
        {
            var chosen = await ArtistPortraits.SquareAsync(card.Name, card.FirstRecord);
            if (!chosen.Answers(out var url, out _))
            {
                card.Face = FaceState.Failed;
                return;
            }

            if (url is null)
            {
                KeepArtistFace(card.Name, null);
                card.Face = FaceState.None;
                return;
            }

            var picture = await ArtistPortraits.PictureAsync(url);
            if (!picture.Answers(out var portrait, out _))
            {
                card.Face = FaceState.Failed;
                return;
            }

            if (portrait is null)
            {
                KeepArtistFace(card.Name, null);
                card.Face = FaceState.None;
                return;
            }

            KeepArtistFace(card.Name, portrait.Thumb);
            card.PictureBytes = portrait.Thumb;
            card.Face = FaceState.Shown;
            ShowArtistFaceAsync(card, portrait.Thumb);
        }
        catch (Exception ex)
        {
            // Both requests answer through Asked and are not expected to throw. If one does, it is
            // recorded, and the card is asked again on the next visit. Otherwise the exception would reach
            // the dispatcher from an async void method, which ends the app on the WinUI head.
            RPM.Core.Diagnostics.Faults.Note("pegasus", "an artist's photograph", ex);
            card.Face = FaceState.Failed;
        }
        finally
        {
            _artistFacesOut--;
        }
    }

    private void KeepArtistFace(string name, byte[]? bytes)
    {
        if (_artistFaces.Count >= ArtistFacesKept)
            _artistFaces.Clear();
        _artistFaces[name] = bytes;
    }

    /// <summary>Decodes a card's photograph at the card's size and shows it, if the card is still on screen
    /// when the decode lands.</summary>
    private async void ShowArtistFaceAsync(PegasusArtistCard card, byte[] bytes)
    {
        var edge = (int)Math.Ceiling(_artistMetrics.Edge * (XamlRoot?.RasterizationScale is > 0 and var s ? s : 1));
        edge = Math.Clamp(edge, ArtScaler.TinyEdge, ArtScaler.TileEdge);
        try
        {
            var bitmap = await DecodeArtAsync(bytes, edge);
            if (_artistsOnScreen.Contains(card))
                card.Picture = bitmap;
        }
        catch (Exception)
        {
            // Not a picture the decoder reads. The letter stays.
            card.Face = FaceState.None;
        }
    }

    // Presses.

    /// <summary>A card: the act's page, with the card as the place the focus comes back to.</summary>
    private void ArtistCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PegasusArtistCard card } element)
            OpenArtistPage(card.Act, element);
    }

    // The search.

    private void ArtistsSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _artistsSearch = ArtistsSearch.Text ?? "";

        if (_artistsSearchSettle is null)
        {
            _artistsSearchSettle = DispatcherQueue.CreateTimer();
            _artistsSearchSettle.Interval = LibrarySearchSettle;
            _artistsSearchSettle.IsRepeating = false;
            _artistsSearchSettle.Tick += (timer, _) =>
            {
                timer.Stop();
                if (_collection == CollectionScreen.Artists)
                    RefillArtists();
            };
        }
        _artistsSearchSettle.Stop();
        _artistsSearchSettle.Start();
    }

    private void ArtistsSearch_FocusChanged(object sender, RoutedEventArgs e)
        => _artistsSearchFocused = ArtistsSearch.FocusState != FocusState.Unfocused;

    // The order.

    /// <summary>The order the list is in: one of the phone's four (<see cref="ArtistOrdering"/>), chosen
    /// from a menu that opens from the sort button.</summary>
    private void ArtistsSort_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        var scale = QueueScale;

        Add(ArtistOrdering.Name, "ArtistSort_Name", "\uf15d");     // a to z
        Add(ArtistOrdering.Albums, "ArtistSort_Albums", "\uf51f"); // a record
        Add(ArtistOrdering.Tracks, "ArtistSort_Tracks", "\uf001"); // a note
        Add(ArtistOrdering.Added, "ArtistSort_Added", "\uf274");   // a date

        menu.ShowAt(ArtistsSortButton);

        void Add(ArtistOrdering ordering, string key, string glyph)
        {
            var chosen = ordering == _artistOrdering;
            var item = new MenuFlyoutItem
            {
                Text = Loc.Get(key),
                FontSize = 12 * scale,
                MinHeight = 26 * scale,
                Padding = new Thickness(11 * scale, 3 * scale, 11 * scale, 4 * scale),
                Icon = new FontIcon
                {
                    FontFamily = (FontFamily)Resources["PegasusFa"],
                    FontSize = 12 * scale,
                    Glyph = chosen ? "\uf00c" : glyph,
                },
            };
            item.Click += (_, _) =>
            {
                if (_artistOrdering == ordering)
                    return;
                _artistOrdering = ordering;
                RefillArtists();
            };
            menu.Items.Add(item);
        }
    }

    // Sizes.

    /// <summary>The cards' side and writing, the tools and the empty line at this scale. The rows are cut
    /// again where the scale asks for another number to a row.</summary>
    private void SizeArtists(double scale)
    {
        _artistMetrics.Scale = scale;

        ArtistsSearchBox.Width = LibrarySearchWidth * scale;
        ArtistsSearchBox.Height = LibrarySearchHeight * scale;
        ArtistsSearchBox.Padding = new Thickness(9 * scale, 0, 9 * scale, 0);
        ArtistsSearchMark.FontSize = 12 * scale;
        ArtistsSearch.FontSize = 13 * scale;
        ArtistsSortButton.Width = 34 * scale;
        ArtistsSortButton.Height = 28 * scale;
        ArtistsSortButton.FontSize = 12 * scale;
        ArtistsEmpty.FontSize = 22 * scale;   // the TV's couch size for a screen's message

        var width = LibraryLayer.ActualWidth > 0 ? LibraryLayer.ActualWidth : Root.ActualWidth;
        if (width <= 0)
            return;

        var columns = ArtistColumnsAt(scale);
        if (columns != _artistColumnsCut && _artistsShown.Count > 0)
            CutArtists(_artistsShown, columns);
        _artistColumnsCut = columns;

        // The ladder stands at the right edge, under the tools and level with the first row, and the cards
        // keep its width at rest clear of it, as the TV's grid does. Grown, it reaches over them.
        _ladder.Sizing = scale;
        ArtistsLadderHost.Margin = new Thickness(0, WallTop, LadderEdge, LadderFoot);
        var ladder = _ladderLetters.Count > 0 ? _ladder.RestWidth : 0;

        var room = width - (LibraryEdgeInset * 2) - LibraryScrollbarRoom - ladder - (ArtistGap * (columns - 1));
        _artistMetrics.Edge = Math.Max(72, room / columns);
    }

    // The ladder.

    /// <summary>Builds the ladder and its stop for the remote. From BuildArtistsGenres.</summary>
    private void BuildArtistLadder()
    {
        _ladder = new ArtistLadder();
        _ladder.RungClicked += place =>
        {
            _ladder.Current = place;
            ScrollArtistsToLetter();
        };

        // One stop for the whole ladder, as on the TV: OK on it goes to the first act of the letter in
        // hand. Marked as a zoomed card whose photograph is empty (see ArtistLadder.Photo), so the host
        // draws no glow round it: the letter in hand shows the focus.
        _ladderKey = new RemoteLine(_ladder, press: ToLetterHead, menu: null, turn: null)
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        FocusLift.SetPart(_ladderKey, LiftPart.Card);
        _ladderKey.GotFocus += (_, _) =>
        {
            // Entering, the ladder opens on the letter of the card the remote came from.
            if (_ladder.Active)
                return;
            _ladder.Current = LetterOfCard(_ladderFrom);
            _ladder.Active = true;
        };
        _ladderKey.LostFocus += (_, _) => _ladder.Active = false;
        ArtistsLadderHost.Children.Add(_ladderKey);

        ArtistsWall.GotFocus += (_, e) =>
        {
            if (e.OriginalSource is FrameworkElement { DataContext: PegasusArtistCard card })
                _ladderFrom = card.Index;
        };
    }

    /// <summary>The ladder for the list as now searched and ordered: its letters in name order, none in
    /// the other orders, where a letter's acts are not together.</summary>
    private void FillArtistLadder(List<PegasusArtistCard> shown)
    {
        _ladderLetters = _artistOrdering == ArtistOrdering.Name
            ? ArtistLetters.Of(shown, card => card.Act.Initial)
            : [];
        _ladder.SetLetters(_ladderLetters);
        _ladderFrom = 0;
        ArtistsLadderHost.Visibility = _ladderLetters.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The ladder's place of the letter the card at <paramref name="index"/> files under.</summary>
    private int LetterOfCard(int index)
    {
        if (index < 0 || index >= _artistsShown.Count)
            return 0;
        var letter = _artistsShown[index].Act.Initial;
        for (var i = 0; i < _ladderLetters.Count; i++)
            if (_ladderLetters[i].Letter == letter)
                return i;
        return 0;
    }

    /// <summary>Scrolls the list so the row of the first act of the letter in hand stands where the first
    /// row stands at rest, as the TV's ladder does (scrollToItem). At once, not eased: a held key steps the
    /// letters at the keyboard's rate.</summary>
    private void ScrollArtistsToLetter()
    {
        if (_ladderLetters.Count == 0 || _artistRowSample is not { ActualHeight: > 0 } sample)
            return;
        var spacing = (ArtistsWall.Layout as StackLayout)?.Spacing ?? 0;
        var row = _ladderLetters[_ladder.Current].First / Math.Max(1, _artistColumnsCut);
        ArtistsScroll.ChangeView(null, row * (sample.ActualHeight + spacing), null, disableAnimation: true);
    }

    /// <summary>The card of the first act of the letter in hand, or null where it cannot take the focus.
    /// The list was scrolled to its row when the letter was stepped to, so the row is realized; it is laid
    /// out once more here if it is not yet.</summary>
    private Control? LetterHeadCard()
    {
        if (_ladderLetters.Count == 0)
            return null;
        var first = _ladderLetters[_ladder.Current].First;
        var columns = Math.Max(1, _artistColumnsCut);
        for (var tries = 0; tries < 2; tries++)
        {
            if (ArtistsWall.TryGetElement(first / columns) is ItemsRepeater row
                && row.TryGetElement(first % columns) is Control card
                && CanTakeFocus(card))
                return card;
            ArtistsScroll.UpdateLayout();
        }
        return null;
    }

    /// <summary>OK or Back on the ladder: the first act of the letter in hand takes the focus.</summary>
    private void ToLetterHead()
    {
        if (LetterHeadCard() is { } card)
            RemoteSearch.Give(card);
    }

    /// <summary>Back with the remote on the ladder: the focus goes to the first act of the letter in hand,
    /// as the TV's Back does, and the list stays. False where the ladder does not have the focus.</summary>
    private bool LadderTakesBack()
    {
        if (_collection != CollectionScreen.Artists || _ladderLetters.Count == 0 || !FocusWithin(_ladderKey))
            return false;
        ToLetterHead();
        return true;
    }

    /// <summary>
    /// The arrows on and into the ladder, after the wall's own rule for its sort key
    /// (<see cref="ToSortKey"/>). On the ladder Up and Down step the letter in hand and scroll the list to
    /// it, and the focus stays (a bounce at A and at "#"); Left goes to the letter's first act; Right goes
    /// nowhere. Right from a card with no card beyond it in its row (the last of a row, or the last card)
    /// enters the ladder.
    /// </summary>
    private Control? AcrossArtistLadder(FrameworkElement from, FocusNavigationDirection direction, Control? next)
    {
        next = ToSortKey(from, direction, next);
        if (_ladderLetters.Count == 0)
            return next;

        if (Inside(from, _ladderKey))
        {
            switch (direction)
            {
                case FocusNavigationDirection.Up or FocusNavigationDirection.Down:
                    var to = _ladder.Current + (direction == FocusNavigationDirection.Up ? -1 : 1);
                    if (to < 0 || to >= _ladder.Count)
                        return null;
                    _ladder.Current = to;
                    ScrollArtistsToLetter();
                    return _ladderKey;
                case FocusNavigationDirection.Left:
                    return LetterHeadCard();
                default:
                    return null;
            }
        }

        if (direction == FocusNavigationDirection.Right
            && Inside(from, ArtistsWall)
            && (next is null || !Inside(next, ArtistsWall))
            && CanTakeFocus(_ladderKey))
            return _ladderKey;

        return next;
    }

    /// <summary>One frame of the ladder's grow, while the artists list is up. From TickArtistsGenres.</summary>
    private void TickArtistLadder()
    {
        if (_ladderLetters.Count > 0)
            _ladder.Tick();
    }

    /// <summary>Whether the artists list's search field has the keys. See IsLibrarySearchFocused.</summary>
    private bool ArtistsSearchHasKeys => ArtistsInFront && _artistsSearchFocused;

    /// <summary>Takes the keys off the artists search field, leaving what was typed.</summary>
    private void DropArtistsSearchFocus()
    {
        ArtistsWall.Focus(FocusState.Programmatic);
        _artistsSearchFocused = false;
    }
}
