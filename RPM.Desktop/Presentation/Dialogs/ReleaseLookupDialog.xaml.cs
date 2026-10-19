using RPM.Core.Diagnostics;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Metadata.Providers;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Dialogs.Models;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using RPM.Fingerprint.Catalog;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>The pressing the user settled on, with the listing fetched to score its match. It is handed
/// back so that the split dialog can align the tracks without a second throttled lookup.</summary>
/// <param name="Fill">What the track step did, when the caller offered one and the user went through it.
/// Null means the window stopped at the pressing. Either the step was never offered, or it was offered
/// and declined. The rows are already filled when this is read. What is left for the caller is
/// <see cref="TrackMetadataStep.CommitAsync"/>.</param>
/// <param name="OverwriteArt">Whether the record's pictures are replaced by this pressing's. This is the
/// window's own offer, so the answer comes from the user and not from the caller's guess. It is false
/// wherever no offer was made, because a caller that did not ask for pictures is owed none.</param>
public sealed record PickedRelease(
    ReleaseSearchResult Release, ReleaseDetail? Detail, TrackFill? Fill, bool OverwriteArt = false);

/// <summary>
/// Names a record from the catalogues. This is a resizable dialog (the same
/// <see cref="ResizableDialogShell"/> the split dialog uses), stacked over the split dialog. It has a
/// search form: album, artist, catalogue number, year, country and vinyl-only. The search runs against
/// MusicBrainz and Discogs at once, and each source's progress is shown on its own. Every hit is a row
/// with its sleeve, the facts that tell one pressing from another, and a <b>Match</b> whose reasoning is
/// one click away.
///
/// <para>Pick a row, then press <b>Use this release</b> (or double-click it). The choice goes back to the
/// split dialog, and this window closes. It is a modal task, not a second window kept open beside the
/// first.</para>
/// </summary>
public sealed partial class ReleaseLookupDialog : ContentDialog
{
    private readonly CatalogService _catalog;
    private readonly DispatcherQueue _dispatcher;
    private readonly ObservableCollection<ReleaseLookupRow> _rows = new();
    private readonly ObservableCollection<ProviderProgressVm> _sources = new();
    private readonly IReadOnlyList<double> _detected;
    private readonly List<MediaChoice> _media =
        ReleaseMediaInfo.All.Select(m => new MediaChoice(m)).ToList();
    private readonly TrackMetadataStep _step;
    private CancellationTokenSource? _searchCts;

    /// <summary>The pressing to open straight onto, skipping the search entirely. It is set only by
    /// <see cref="ShowTracksAsync"/>, for a record already linked to one. This window fetches its listing
    /// on the page the user is waiting on, rather than before the window opens.</summary>
    private ReleaseRef? _settled;

    /// <summary>The pressing whose tracks this window is showing now. It is held from the moment Next was
    /// pressed. Apply reads this, not the results list, because a search still finishing behind the
    /// second page can rebuild that list.</summary>
    private PickedRelease? _showing;

    /// <summary>The listing laid against the caller's files: the grid on page two.</summary>
    private ReleaseTrackFill? _fill;

    /// <summary>The fetch in progress for page two. Back, a second Next and closing the window all cancel
    /// it, so a listing that arrives late cannot paint over a page the user has left.</summary>
    private CancellationTokenSource? _listingCts;

    private PickedRelease? _result;
    private TrackFill? _applied;

    /// <summary>What the caller wants done about the record's pictures. This decides whether the window
    /// offers the choice at all, and where the offer starts. See <see cref="ArtAsk"/>.</summary>
    private readonly ArtAsk _ask;

    public ReleaseLookupDialog(
        CatalogService catalog, TrackMetadataStep step, IReadOnlyList<double> detected, string? album,
        string? artist, ReleaseMedia media, string? country = null, uint year = 0,
        string? catno = null, string? barcode = null, ArtAsk ask = ArtAsk.None)
    {
        InitializeComponent();
        _catalog = catalog;
        _step = step;
        _ask = ask;
        _detected = detected;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        // Wide enough for the form's column and a result row beside it. The row must still show a long
        // title, a country and a format without wrapping.
        Shell.AttachResize(this, minWidth: 820, minHeight: 460, maxWidth: 1180, defaultHeight: 620,
            defaultWidth: 940);

        ResultList.ItemsSource = _rows;
        SourceList.ItemsSource = _sources;
        foreach (var provider in _catalog.Search.Providers)
            _sources.Add(new ProviderProgressVm(provider.Id, provider.DisplayName));

        // The catalogue number and barcode fields can only resolve a pressing through a provider that
        // searches by those (Discogs). MusicBrainz alone cannot do it, and Discogs counts only once the
        // user has connected it. So the row is shown only when it can answer.
        bool codes = _catalog.Search.Providers.Any(p => p.IsAvailable && p.SupportsCatalogNumberSearch);
        CodeFieldsPanel.Visibility = codes ? Visibility.Visible : Visibility.Collapsed;

        SearchBox.Text = album ?? "";
        ArtistBox.Text = artist ?? "";
        // A pressing that the album has already been matched to carries its own printed codes. A
        // catalogue number is a much narrower question than a name: "SHVL 804" is one pressing, and "Dark
        // Side of the Moon" is two hundred. The field is seeded when the caller has the codes, so the
        // search opens on the narrow question. It stays editable, because the codes on a sleeve are also
        // the codes people mistype.
        CatnoBox.Text = catno ?? "";
        BarcodeBox.Text = barcode ?? "";
        // The medium starts on whatever the caller is holding: a record being split, or the CD in the
        // drive. It can be changed, because the pressing that names a rip need not be on the same medium
        // as the rip. A CD listing names a record's tracks well enough.
        MediaBox.ItemsSource = _media;
        SelectedMedia = media;
        CountryBox.Text = country ?? "";
        YearBox.Text = year != 0 ? year.ToString() : "";

        // The follow-up page is offered when the caller has files for it to name. The ribbon shows that
        // there is a second page before anything is picked. Without it, the window would look like a
        // one-page window with an extra button.
        if (_step.Offered)
        {
            TrackStepBox.Visibility = Visibility.Visible;
            TrackStepBox.IsChecked = _step.Ticked;
        }

        // The pictures are offered when the caller is taking them. The box is ticked over a record with
        // no pictures, because nobody opens this window for a record with no cover and then declines the
        // sleeve. It is unticked over a record that has pictures. A lookup run to fix a year must not
        // quietly replace a cover the user chose by hand.
        if (_ask != ArtAsk.None)
        {
            OverwriteArtBox.Visibility = Visibility.Visible;
            OverwriteArtBox.IsChecked = _ask == ArtAsk.Missing;
        }
        AlbumFieldsLabel.Text = Loc.Get("Lookup_AlbumFields");
        PaintPrimary();
        PaintSteps();

        // The user came here from a record they are naming, so the seeded search runs at once. If there
        // was nothing to seed, the window waits for the user to type.
        Loaded += (_, _) =>
        {
            // The pressing is already settled, and this window is only here for the tracks. No search
            // runs. The first thing on screen is the grid.
            if (_settled is { } settled) { _ = ToTracksAsync(settled, null, null); return; }

            // Gallery: fill the results with demo pressings instead of calling the network.
            if (GalleryMode.Active) { SeedGalleryResults(); return; }
            if (!string.IsNullOrWhiteSpace(SearchBox.Text)
                || !string.IsNullOrWhiteSpace(CatnoBox.Text)
                || !string.IsNullOrWhiteSpace(BarcodeBox.Text)) { _ = SearchAsync(); return; }

            // Nothing was seeded, so the window opens on the user rather than on a search. The results
            // column says what the window is for and points at the form.
            PaintResults(Asked.Nobody);
        };
        Closed += (_, _) => { _searchCts?.Cancel(); _listingCts?.Cancel(); };
    }

    /// <summary>Fills the results list with made-up pressings, so the lookup can be seen without a
    /// network search. The match scores are left unscored, and the rows show their facts only.</summary>
    private void SeedGalleryResults()
    {
        _rows.Clear();
        foreach (var release in Gallery.GalleryDemo.LookupResults())
            _rows.Add(new ReleaseLookupRow { Release = release });
        StatusText.Text = Loc.Format("Search_Found", _rows.Count);
        PaintResults(Asked.Answered);
    }

    /// <summary>Opens the lookup and returns the pressing the user chose, or null if they closed it
    /// without one.</summary>
    /// <param name="step">Whether this lookup can go on to name the caller's files, and where those files
    /// are written when it does. It is required. See <see cref="TrackMetadataStep"/> for why there is no
    /// default. Pass <see cref="TrackMetadataStep.None"/> where identifying a record is the whole
    /// task.</param>
    /// <param name="media">The medium the search starts narrowed to: what the caller is naming.</param>
    /// <param name="catno">The pressing's printed catalogue number, when the caller already knows it. It
    /// seeds the search, so the search opens on one pressing and not on a name shared by
    /// hundreds.</param>
    /// <param name="barcode">The same, for the barcode.</param>
    /// <param name="ask">What the caller does about the record's pictures. None, the default, gives a
    /// window that never mentions them. Every caller that only wants the words uses this.</param>
    public static async Task<PickedRelease?> ShowAsync(
        XamlRoot xamlRoot, CatalogService catalog, TrackMetadataStep step,
        IReadOnlyList<double> detected,
        string? album, string? artist, ReleaseMedia media, string? country = null, uint year = 0,
        string? catno = null, string? barcode = null, ArtAsk ask = ArtAsk.None)
    {
        var dialog = new ReleaseLookupDialog(catalog, step, detected, album, artist, media, country,
                                             year, catno, barcode, ask)
            { XamlRoot = xamlRoot };
        await DialogHost.ShowAsync(dialog);
        return dialog._result;
    }

    /// <summary>
    /// The track step on its own, for a record whose pressing is already settled.
    ///
    /// <para>An album that has been linked to a release has already gone through the "which pressing is
    /// this?" step. That happened in the album editor, the album page's picker, or an identified rip.
    /// Asking again would make the app forget what it was told. So the window opens on its second page,
    /// with that release's listing already laid against the files.</para>
    /// </summary>
    /// <param name="album">What to seed the search with, for the one case this page cannot answer: a
    /// pressing whose catalogue has no track listing. The window falls back to its search instead of
    /// closing with an error.</param>
    /// <returns>What was written onto the rows, or null if the window was closed without
    /// applying.</returns>
    public static async Task<TrackFill?> ShowTracksAsync(
        XamlRoot xamlRoot, CatalogService catalog, ReleaseRef settled, TrackMetadataStep step,
        string? album, string? artist)
    {
        var dialog = new ReleaseLookupDialog(catalog, step, [], album, artist, ReleaseMedia.Any)
            { XamlRoot = xamlRoot };
        dialog._settled = settled;
        await DialogHost.ShowAsync(dialog);
        return dialog._applied;
    }

    /// <summary>The medium the form filters on, as the picker holds it.</summary>
    private ReleaseMedia SelectedMedia
    {
        get => (MediaBox.SelectedItem as MediaChoice)?.Media ?? ReleaseMedia.Any;
        set => MediaBox.SelectedItem = _media.FirstOrDefault(c => c.Media == value) ?? _media[0];
    }

    private void Field_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _ = SearchAsync();
        }
    }

    private void Search_Click(object sender, RoutedEventArgs e) => _ = SearchAsync();

    /// <summary>Empties every search field, so the user can start from nothing. The usual case is a CD
    /// whose auto-lookup seeded the wrong album, artist or year, and those values fight a catalogue
    /// number or barcode search. The medium is left alone, because it is a scope and not seeded data.
    /// Focus goes to the catalogue box when it is shown, because that is usually why the user clears the
    /// form.</summary>
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        ArtistBox.Text = "";
        CatnoBox.Text = "";
        BarcodeBox.Text = "";
        YearBox.Text = "";
        CountryBox.Text = "";

        if (CodeFieldsPanel.Visibility == Visibility.Visible)
            CatnoBox.Focus(FocusState.Programmatic);
        else
            SearchBox.Focus(FocusState.Programmatic);
    }

    private async Task SearchAsync()
    {
        var q = Blank(SearchBox.Text);
        var artist = Blank(ArtistBox.Text);
        var catno = Blank(CatnoBox.Text);
        var barcode = Blank(BarcodeBox.Text);

        // An album, an artist, a catalogue number or a barcode is each a search on its own. Any one is
        // enough. Year, country and medium only narrow a search. They are not a search by themselves,
        // because on their own they would match every record pressed in Britain in 1997.
        if (q is null && artist is null && catno is null && barcode is null)
        {
            // Say what is missing, and remove the last search's results and criteria line too. Rows for
            // the album that was just cleared would look like the answer to the question on screen.
            _rows.Clear();
            SearchSummaryPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = Loc.Get("Lookup_NeedSearch");
            PaintResults(Asked.Nobody);
            return;
        }

        // The last search may still be running. It answers a question the user has moved on from.
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        uint? year = uint.TryParse(YearBox.Text?.Trim(), out var y) && y > 0 ? y : null;
        var query = new ReleaseQuery(
            q ?? "",
            Artist: artist,
            Media: SelectedMedia,
            Limit: 25,
            CatalogNumber: catno,
            Country: Blank(CountryBox.Text),
            Year: year,
            Barcode: barcode);

        ShowSearchSummary(query);

        _rows.Clear();
        var available = _catalog.Search.Providers.Where(p => p.IsAvailable).ToList();
        foreach (var vm in _sources)
        {
            if (available.Any(p => p.Id == vm.ProviderId)) vm.SetBusy();
            else vm.SetUnavailable();
        }

        // Nothing is left to ask. The caller asks for the account before this window opens, so reaching this
        // point means the session ended while the window was open. Saying "no releases" would blame the
        // catalogues for a cause the app knows.
        if (available.Count == 0)
        {
            // Nobody was asked, so the column must not say "no results" under this line.
            StatusText.Text = Loc.Get("Lookup_SignInNeeded");
            PaintResults(Asked.Unanswered);
            return;
        }

        StatusText.Text = Loc.Get("Lookup_Searching");
        PaintResults(Asked.Running);

        // The column shows "no results" only when every catalogue that was asked has answered.
        var ended = Asked.Answered;
        try
        {
            // Each catalogue runs on its own task, so its progress chip appears when it answers, not when
            // the slower one does. A catalogue that did not answer says so on its chip.
            var tasks = available.Select(async p =>
            {
                var res = await _catalog.Search.SearchOneAsync(p, query, cts.Token);
                _dispatcher.TryEnqueue(() =>
                {
                    if (cts.Token.IsCancellationRequested)
                        return;
                    if (res.Answers(out var found, out _))
                        SourceFor(p.Id)?.SetDone(found.Count);
                    else
                        SourceFor(p.Id)?.SetFailed();
                });
                return res;
            }).ToList();

            var answers = await Task.WhenAll(tasks);
            if (cts.Token.IsCancellationRequested)
                return;

            var all = answers
                .SelectMany(a => a.Answers(out var found, out _) ? found : Array.Empty<ReleaseSearchResult>())
                .ToList();
            var someUnanswered = answers.Any(a => !a.Answers(out _, out _));

            var ordered = ReleaseSearch.Order(all, query);

            // The rows appear with whatever the search itself returned. For Discogs that includes a
            // thumbnail address. Nothing is fetched here. A sleeve that has to be resolved costs one
            // request per row, and waiting for twenty-five of those before the first row appears would
            // take seconds. The pictures catch up later (see FillCoversAsync).
            foreach (var result in ordered)
            {
                _rows.Add(ReleaseLookupRow.From(new RankedRelease(result, result.CoverUrl, null)));
                if (_rows.Count == 1)
                    PaintResults(Asked.Running);   // Rows are shown now, so the wait mark is hidden
            }

            if (_rows.Count == 0)
            {
                // "No releases" is a claim that only an answer can make. If a catalogue did not answer,
                // the line says the search did not finish, and the column stays empty.
                if (someUnanswered)
                {
                    StatusText.Text = Loc.Get("Lookup_SearchUnanswered");
                    ended = Asked.Unanswered;
                }
                else
                {
                    StatusText.Text = Loc.Get("Search_NoReleases");
                }
                return;
            }
            StatusText.Text = Loc.Format("Search_Found", _rows.Count);

            // Next, score the shortlist. Each Match appears as its listing arrives. The sleeves are
            // fetched separately, because that is a slower job.
            _ = RankAsync(ordered, cts.Token);
            _ = FillCoversAsync([.. _rows], cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusText.Text = Loc.Format("Search_Failed", Faults.Note("lookup", "searching the catalogues", ex).Say());
            ended = Asked.Unanswered;
        }
        finally
        {
            // Only the search that is still current owns the column. A cancelled search was cancelled by
            // the next one, and the next one already shows the waiting mark.
            if (ReferenceEquals(_searchCts, cts))
                PaintResults(ended);
        }
    }

    /// <summary>What has been asked of the catalogues. An empty results column cannot tell these apart by
    /// itself. No rows can mean "nobody has searched", "still searching", or "searched, nothing found".
    /// Those are three different messages.</summary>
    private enum Asked
    {
        /// <summary>Nothing has been searched for yet, or the criteria have just been cleared.</summary>
        Nobody,

        /// <summary>A search is in flight.</summary>
        Running,

        /// <summary>A search has run, and this is what it returned.</summary>
        Answered,

        /// <summary>A search has run and a catalogue did not answer. An empty column then says nothing,
        /// because "no results" would be a claim about a catalogue that was never heard from. The status
        /// line says what happened.</summary>
        Unanswered,
    }

    /// <summary>
    /// The results column when it has nothing in it. It shows the spinning record while the catalogues
    /// are asked, the arrow at the form before anything has been asked, or the struck-out magnifier once
    /// they have answered with nothing.
    /// </summary>
    /// <remarks>All three depend on <see cref="_rows"/> as well as on <paramref name="asked"/>. Rows on
    /// screen hide all three, so no two can be shown together.</remarks>
    private void PaintResults(Asked asked)
    {
        // Unanswered shows none of the three. The status line says what happened.
        var empty = _rows.Count == 0;
        ResultsLoader.Spinning = empty && asked == Asked.Running;
        EmptyHint.Visibility = Shown(empty && asked == Asked.Nobody);
        NoResults.Visibility = Shown(empty && asked == Asked.Answered);

        static Visibility Shown(bool yes) => yes ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Writes the header above the results: the criteria this search actually ran with. Each
    /// filled field is shown as "Label: value", joined with dots. A stale seed, such as a CD's wrong
    /// auto-lookup still sitting in the fields, is then obvious, and the user does not have to guess it
    /// from odd hits.</summary>
    private void ShowSearchSummary(ReleaseQuery query)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(query.Album)) parts.Add($"{Loc.Get("Lookup_Album")}: {query.Album}");
        if (query.Artist is { Length: > 0 }) parts.Add($"{Loc.Get("Lookup_Artist")}: {query.Artist}");
        if (query.CatalogNumber is { Length: > 0 }) parts.Add($"{Loc.Get("Lookup_CatalogNumber")}: {query.CatalogNumber}");
        if (query.Barcode is { Length: > 0 }) parts.Add($"{Loc.Get("Lookup_Barcode")}: {query.Barcode}");
        if (query.Year is > 0) parts.Add($"{Loc.Get("Review_ColYear")}: {query.Year}");
        if (query.Country is { Length: > 0 }) parts.Add($"{Loc.Get("Lookup_Country")}: {query.Country.ToUpperInvariant()}");
        if (query.Media != ReleaseMedia.Any)
            parts.Add($"{Loc.Get("Lookup_Media")}: {MediaChoice.Label(query.Media)}");

        SearchSummaryText.Text = string.Join("   ·   ", parts);
        SearchSummaryPanel.Visibility = parts.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task RankAsync(IReadOnlyList<ReleaseSearchResult> results, CancellationToken ct)
    {
        if (_detected.Count == 0)
            return;

        foreach (var row in _rows.Take(_catalog.Search.ShortlistSize))
            row.MatchPending = true;

        try
        {
            await _catalog.Search.RankAsync(results, _detected, _catalog.ArtProviders,
                onRanked: ranked => _dispatcher.TryEnqueue(() =>
                {
                    if (ct.IsCancellationRequested)
                        return;
                    var row = _rows.FirstOrDefault(r => r.Release.Ref == ranked.Release.Ref);
                    if (row is not null)
                    {
                        row.Detail = ranked.Detail;
                        row.Match = ranked.Match;
                    }
                }),
                ct: ct);
        }
        catch (OperationCanceledException) { }
        catch { /* a score that never arrives leaves the row saying what it knows */ }
    }

    /// <summary>How many sleeves are fetched at once. Each one needs an address to be resolved and then a
    /// picture to be downloaded, and both ends limit the rate. Four lanes fill a screen quickly without
    /// sending twenty-five requests at once to an API that is not meant for that.</summary>
    private const int CoverLanes = 4;

    /// <summary>
    /// The sleeves, fetched after the rows are already on screen.
    ///
    /// <para>A row without a picture is still a readable row. The year, the country, the format and the
    /// catalogue number tell two pressings apart. So the list must not wait for the pictures, and the
    /// pictures must not arrive in a burst that stalls it either.</para>
    ///
    /// <para>Results can arrive in any order, and the user may have moved on from the window. So nothing
    /// is written by position. Each task holds its own row and writes only to that row, on the UI thread,
    /// and only while the search that started it is still the current one. A row that has since been
    /// removed from the list cannot be reached by the write.</para>
    /// </summary>
    private async Task FillCoversAsync(IReadOnlyList<ReleaseLookupRow> rows, CancellationToken ct)
    {
        var next = -1;
        var lanes = Enumerable.Range(0, Math.Min(CoverLanes, rows.Count)).Select(async _ =>
        {
            while (true)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= rows.Count || ct.IsCancellationRequested)
                    return;

                var row = rows[index];
                try
                {
                    var url = row.CoverUrl is { Length: > 0 } known
                        ? known
                        : await CoverUrlAsync(row.Release, ct);
                    if (url is not { Length: > 0 })
                        continue;

                    // This is kept on the row, not just used and then dropped. Many rows arrive with no
                    // picture address at all. That is why this lane had to look one up. The sleeve fetch
                    // later falls back to the search result's own address when the archive will not
                    // answer a second time. Throwing this address away would leave exactly the releases
                    // whose thumbnail was still on its way with no cover.
                    row.CoverUrl = url;

                    // The picture is decoded at the size the row draws it, twice over for Retina
                    // displays. The archives hand over a full-size scan later, when a pressing is
                    // actually chosen. A row with no sleeve is drawn the same way whether the host has
                    // none or did not answer. Nothing is written down in either case.
                    if ((await RemoteImage.LoadAsync(url, ct)).Match(picture => picture, _ => null) is not { } bitmap)
                        continue;

                    _dispatcher.TryEnqueue(() =>
                    {
                        if (!ct.IsCancellationRequested)
                            row.Cover = bitmap;
                    });
                }
                catch (OperationCanceledException) { return; }
                catch { /* a sleeve that will not come is a row without a picture, not a failed search */ }
            }
        });

        await Task.WhenAll(lanes);
    }

    private async Task<string?> CoverUrlAsync(ReleaseSearchResult result, CancellationToken ct)
    {
        if (result.CoverUrl is { Length: > 0 })
            return result.CoverUrl;
        // A row without a picture is drawn the same way whether the archive has none or did not answer.
        // Nothing is written down in either case.
        foreach (var art in _catalog.ArtProviders.Where(a => a.Owns(result.Ref)))
        {
            try
            {
                if ((await art.FrontCoverUrlAsync(result.Ref, 250, ct)).Answers(out var url, out _)
                    && url is { Length: > 0 })
                    return url;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                /* the next archive, or no picture */
            }
        }
        return null;
    }

    private void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UseButton.IsEnabled = ResultList.SelectedItem is ReleaseLookupRow;

    private void ResultList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        => Take(ResultList.SelectedItem as ReleaseLookupRow);

    private void Use_Click(object sender, RoutedEventArgs e)
        => Take(ResultList.SelectedItem as ReleaseLookupRow);

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>The tick that decides what the primary button does, and whether there is a second page to
    /// show a ribbon for. Nothing is written by it, so it can be changed at any time.</summary>
    private void TrackStepBox_Click(object sender, RoutedEventArgs e)
    {
        PaintPrimary();
        PaintSteps();
    }

    /// <summary>What the primary button says, and where it goes: on to the tracks, or out of the window
    /// with a pressing.</summary>
    private void PaintPrimary()
    {
        UseText.Text = Loc.Get(Onward ? "Lookup_Next" : "Lookup_Use");
        UseGlyph.Glyph = Onward ? "" : "";   // arrow-right, check
    }

    /// <summary>
    /// The step ribbon. It is shown only while there are two steps.
    ///
    /// <para>It follows the tick, not the offer. An unticked window is a one-page window that ends at
    /// "Use this release". A ribbon that promised a second step the window will not go to would
    /// contradict the button.</para>
    /// </summary>
    private void PaintSteps()
    {
        if (Onward)
            Shell.SetSteps(Loc.Get("Lookup_StepRelease"), Loc.Get("Lookup_StepTracks"));
        else
            Shell.SetSteps();
    }

    /// <summary>Whether the window goes on to the tracks. That is true when it was offered and the offer
    /// stands. A window opened straight onto the tracks was never offered anything, because it is the
    /// second step itself.</summary>
    private bool Onward => _settled is not null || (_step.Offered && TrackStepBox.IsChecked == true);

    /// <summary>The chosen pressing. It is either the window's answer, or the way in to the second
    /// page.</summary>
    private void Take(ReleaseLookupRow? row)
    {
        if (row is null)
            return;

        // The list's own tasks end as soon as a pressing is chosen, and they must actually STOP. Up to
        // four lanes of thumbnail fetches and a shortlist of listings all wait behind the same throttled
        // catalogue that the sleeve fetch is about to call. If they kept running, they would take the
        // sleeve fetch's place in the queue. The archive would then say no for now, the fetch would
        // swallow that and fall back to whatever address the search had. For a release the search had no
        // picture for, that means no picture at all.
        _searchCts?.Cancel();

        var picked = Picked(row);
        if (Onward)
        {
            _ = ToTracksAsync(picked.Ref, picked, row.Detail);
            return;
        }

        _result = new PickedRelease(picked, row.Detail, null, OverwriteArtBox.IsChecked == true);
        Hide();
    }

    /// <summary>The chosen release, as the rest of the app should see it. It carries whatever picture
    /// address the list found for it. The search result itself may not have carried one.</summary>
    internal static ReleaseSearchResult Picked(ReleaseLookupRow row)
        => row.Release.CoverUrl is { Length: > 0 } || row.CoverUrl is not { Length: > 0 }
            ? row.Release
            : row.Release with { CoverUrl = row.CoverUrl };

    /// <summary>
    /// The second page, once a pressing has been picked.
    ///
    /// <para><b>The page comes up first, and the listing arrives into it.</b> The picker carries a
    /// listing only for releases it RANKED. Ranking needs track lengths, and the ways that open this
    /// window on a record have none. So this is nearly always a throttled lookup, which takes seconds.
    /// Fetching before switching pages would leave the user on a search list that had stopped answering
    /// the button just pressed. That looks like a hung window, not a wait.</para>
    ///
    /// <para>Seconds is also long enough for the user to press Back, press Next on another row, or close
    /// the window. Each of those cancels this fetch. A listing that arrives for a pressing nobody is
    /// looking at any more must not paint a grid over the one on screen.</para>
    /// </summary>
    /// <param name="release">The search row it came from, when it came from one. Null for a pressing the
    /// record was already linked to. That pressing was never searched for.</param>
    /// <param name="carried">The listing the picker already fetched, when it has one.</param>
    private async Task ToTracksAsync(
        ReleaseRef pressing, ReleaseSearchResult? release, ReleaseDetail? carried)
    {
        _listingCts?.Cancel();
        var cts = new CancellationTokenSource();
        _listingCts = cts;

        WaitingForListing(release?.Title ?? SearchBox.Text);
        try
        {
            var detail = carried;
            string? unanswered = null;
            if (detail is not { Tracks.Count: > 0 })
            {
                var provider = _catalog.Search.Providers.FirstOrDefault(p =>
                    string.Equals(p.Id, pressing.Provider, StringComparison.OrdinalIgnoreCase));
                if (provider is not null)
                {
                    if ((await provider.GetReleaseAsync(pressing.Id, cts.Token)).Answers(out var fetched, out var why))
                        detail = fetched ?? detail;
                    else
                        unanswered = why;
                }
            }

            if (cts.IsCancellationRequested || !ReferenceEquals(_listingCts, cts))
                return;

            if (unanswered is not null)
            {
                // This is not "this release has no track listing". Nobody said that. The listing did not
                // arrive. Trying again, or picking another pressing, is what the user can do about it.
                App.Services.GetRequiredService<ILoggerFactory>()
                   .CreateLogger("RPM.ReleaseLookup")
                   .LogInformation("[lookup] {Provider} {Id} listing not answered: {Why}",
                                   pressing.Provider, pressing.Id, unanswered);
                StatusText.Text = Loc.Get("Lookup_ListingUnanswered");
                _settled = null;
                PaintPage(tracks: false);
                PaintSteps();
                return;
            }

            if (detail is not { Tracks.Count: > 0 })
            {
                // A pressing with no tracklist has nothing to fill the grid from. The page would be a
                // grid of empty boxes. So the window goes back to the search. For a record already linked
                // to this pressing, the search is the only thing left to offer.
                App.Services.GetRequiredService<ILoggerFactory>()
                   .CreateLogger("RPM.ReleaseLookup")
                   .LogInformation("[lookup] {Provider} {Id} has no tracklist", pressing.Provider, pressing.Id);
                StatusText.Text = Loc.Get("Lookup_NoListing");
                _settled = null;
                PaintPage(tracks: false);
                PaintSteps();
                return;
            }

            ShowTracks(release, detail);
        }
        // This is our own cancellation, and only ours. The user pressed Back, pressed Next on another
        // row, or closed the window, and there is nothing to report for any of those. An HttpClient that
        // gives up after its twenty-second timeout throws the same TaskCanceledException, from a token
        // that nobody here cancelled. Catching that as "the user moved on" would leave the record turning
        // forever, with no message and no way forward. That is worse than saying the catalogue did not
        // answer.
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!ReferenceEquals(_listingCts, cts))
                return;

            App.Services.GetRequiredService<ILoggerFactory>()
               .CreateLogger("RPM.ReleaseLookup")
               .LogWarning(ex, "[lookup] no track listing for {Provider} {Id}",
                           pressing.Provider, pressing.Id);

            // A timeout gets its own sentence. It says to try again. A catalogue that answered with
            // nothing says to pick something else.
            StatusText.Text = ex is OperationCanceledException
                ? Loc.Get("Lookup_ListingTimedOut")
                : Loc.Format("Search_Failed", Faults.Note("lookup", "reading a track listing", ex).Say());
            _settled = null;
            PaintPage(tracks: false);
            PaintSteps();
        }
    }

    /// <summary>The second page with nothing on it yet. The record turns where the grid will be, and
    /// there is no Apply, because there is nothing to apply.</summary>
    private void WaitingForListing(string? name)
    {
        _fill = null;
        _showing = null;
        TrackFillList.ItemsSource = null;
        TracksReleaseText.Text = string.IsNullOrWhiteSpace(name) ? "" : name;

        PaintPage(tracks: true);
        PaintLoading(true);
        StatusText.Text = string.IsNullOrWhiteSpace(name)
            ? Loc.Get("Lookup_Searching") : Loc.Format("Search_Fetching", name);
    }

    /// <summary>Which view of page two is up: the wait mark, or the grid.</summary>
    private void PaintLoading(bool loading)
    {
        TracksLoader.Spinning = loading;
        AlbumFieldsBox.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        TracksHeaderGrid.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        TrackFillScroll.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
        ApplyButton.IsEnabled = !loading;
    }

    /// <summary>Lays the listing against the caller's files and shows the grid.</summary>
    /// <param name="release">The search row the pressing came from. It is null when the window was opened
    /// straight onto the tracks, and the listing is all there is.</param>
    private void ShowTracks(ReleaseSearchResult? release, ReleaseDetail listing)
    {
        _showing = release is null
            ? null
            : new PickedRelease(release, listing, null, OverwriteArtBox.IsChecked == true);
        _fill = ReleaseTrackFill.Pair(
            _step.Rows, listing.Tracks, ReleaseTrackFill.AlbumFieldsOf(release, listing));

        TrackFillList.ItemsSource = _fill.Lines;
        TracksSelectAllBox.IsChecked = true;

        var name = string.Join("   ·   ", new[] { release?.Title ?? listing.Title, release?.Artist ?? listing.Artist }
            .Where(v => !string.IsNullOrWhiteSpace(v)));
        TracksReleaseText.Text = Loc.Format("Lookup_TracksFrom", name, listing.Tracks.Count);
        AlbumFieldsValues.Text = "   " + _fill.AlbumFieldsSummary;

        // How much of the record the release covers is shown before Apply, not after it. "18 of 20" tells
        // the user they picked the wrong pressing.
        StatusText.Text = Loc.Format("Lookup_TracksMatched", _fill.MatchedCount, _fill.Lines.Count);
        PaintLoading(false);
        PaintPage(tracks: true);
    }

    /// <summary>Goes back to the search with the pressing still selected. The grid is dropped, not kept.
    /// Next builds it again from whatever is picked then, so a page half-edited against one pressing can
    /// never be applied to another.</summary>
    private void Back_Click(object sender, RoutedEventArgs e)
    {
        _listingCts?.Cancel();
        _fill = null;
        _showing = null;
        TrackFillList.ItemsSource = null;
        PaintLoading(false);
        StatusText.Text = _rows.Count > 0 ? Loc.Format("Search_Found", _rows.Count) : "";
        PaintPage(tracks: false);
    }

    /// <summary>Which page the card shows, and what the footer offers on it.</summary>
    private void PaintPage(bool tracks)
    {
        SearchPage.Visibility = tracks ? Visibility.Collapsed : Visibility.Visible;
        TracksPage.Visibility = tracks ? Visibility.Visible : Visibility.Collapsed;

        TrackStepBox.Visibility = !tracks && _step.Offered ? Visibility.Visible : Visibility.Collapsed;
        OverwriteArtBox.Visibility = !tracks && _ask != ArtAsk.None ? Visibility.Visible : Visibility.Collapsed;
        UseButton.Visibility = tracks ? Visibility.Collapsed : Visibility.Visible;
        ApplyButton.Visibility = tracks ? Visibility.Visible : Visibility.Collapsed;
        // There is nothing to go back to when the window opened on the tracks. The pressing was settled
        // before the window showed, and there is no search behind this page.
        BackButton.Visibility = tracks && _settled is null
            ? Visibility.Visible : Visibility.Collapsed;

        Shell.Title = Loc.Get(tracks ? "Lookup_TracksHeading" : "Lookup_Heading");
        Shell.TitleGlyph = tracks ? "" : "";   // record, magnifier
        Shell.CurrentStep = tracks ? 1 : 0;
    }

    /// <summary>The header tick: every line the release has an answer for, all on or all off
    /// together.</summary>
    private void TracksSelectAll_Click(object sender, RoutedEventArgs e)
        => _fill?.TickAll(TracksSelectAllBox.IsChecked == true);

    /// <summary>
    /// Writes the grid onto the rows and closes.
    ///
    /// <para>The write to disk does NOT happen here. This method puts the grid onto the rows, which is
    /// what the window is for. Carrying that onto two hundred files is the caller's own task, see
    /// <see cref="TrackMetadataStep.CommitAsync"/>. Doing it inside a dialog that is busy closing itself
    /// would leave a long write with no window to report progress from.</para>
    /// </summary>
    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_fill is not { } fill)
            return;

        _applied = fill.Apply(AlbumFieldsBox.IsChecked == true);
        if (_showing is { } showing)
            _result = showing with { Fill = _applied };
        Hide();
    }

    private ProviderProgressVm? SourceFor(string id) => _sources.FirstOrDefault(v => v.ProviderId == id);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>
/// One medium in the media picker. It is a value with a name, not the enum itself. The list must read in
/// the user's language. <c>ToString</c> is what the stock ComboBox item shows. This keeps the choice as a
/// value the code compares, not an index into a hand-written list.
/// </summary>
[RpmBindable]
public sealed partial class MediaChoice(ReleaseMedia media)
{
    public ReleaseMedia Media { get; } = media;

    public override string ToString() => Label(Media);

    /// <summary>What this medium is called on screen.</summary>
    public static string Label(ReleaseMedia media) => Loc.Get($"Media_{media}");
}

/// <summary>One catalogue's line in the progress strip: what it is, and how its search is going. The
/// states are idle, searching, a count, or "not connected" for one the app cannot ask yet.</summary>
[RpmBindable]
public sealed partial class ProviderProgressVm : INotifyPropertyChanged
{
    private enum SourceState { Idle, Busy, Done, Empty, Unavailable, Failed }

    private SourceState _state;
    private int _count;

    public ProviderProgressVm(string providerId, string displayName)
    {
        ProviderId = providerId;
        DisplayName = displayName;
        // A source starts hidden. It earns a line only once a search touches it, with a spinner, a count,
        // or "not connected". An idle name before any search is just clutter.
        _state = SourceState.Idle;
    }

    public string ProviderId { get; }
    public string DisplayName { get; }

    public void SetBusy() => Set(SourceState.Busy, 0);
    public void SetUnavailable() => Set(SourceState.Unavailable, 0);
    public void SetDone(int count) => Set(count > 0 ? SourceState.Done : SourceState.Empty, count);

    /// <summary>The catalogue was asked and did not answer. That is not "none", because "none" would be
    /// an answer.</summary>
    public void SetFailed() => Set(SourceState.Failed, 0);

    private void Set(SourceState state, int count)
    {
        _state = state;
        _count = count;
        OnChanged(nameof(Label));
        OnChanged(nameof(IsBusy));
        OnChanged(nameof(ChipVisibility));
        OnChanged(nameof(GlyphVisibility));
        OnChanged(nameof(StateGlyph));
        OnChanged(nameof(StateBrush));
    }

    /// <summary>Hidden until a search touches this source. Shown once it is searching, done, empty or
    /// unavailable.</summary>
    public Visibility ChipVisibility => _state == SourceState.Idle ? Visibility.Collapsed : Visibility.Visible;

    public bool IsBusy => _state == SourceState.Busy;

    public Visibility GlyphVisibility =>
        _state is SourceState.Done or SourceState.Empty or SourceState.Unavailable or SourceState.Failed
            ? Visibility.Visible : Visibility.Collapsed;

    public string StateGlyph => _state switch
    {
        SourceState.Done => "\uf00c",         // check
        SourceState.Empty => "\uf068",        // minus
        SourceState.Unavailable => "\uf05e",  // ban
        SourceState.Failed => "\uf071",       // exclamation-triangle
        _ => "",
    };

    public Brush StateBrush => new SolidColorBrush(_state switch
    {
        SourceState.Done => Color.FromArgb(0xFF, 0x4C, 0xD9, 0x64),
        _ => Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF),
    });

    public string Label => _state switch
    {
        SourceState.Busy => Loc.Format("Lookup_SourceSearching", DisplayName),
        SourceState.Done => Loc.Format("Lookup_SourceFound", DisplayName, _count),
        SourceState.Empty => Loc.Format("Lookup_SourceNone", DisplayName),
        SourceState.Unavailable => Loc.Format("Lookup_SourceUnavailable", DisplayName),
        SourceState.Failed => Loc.Format("Lookup_SourceFailed", DisplayName),
        _ => DisplayName,
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
