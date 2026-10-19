using RPM.Core.Net;
using System.Collections.ObjectModel;
using RPM.Core.Metadata.Providers;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Dialogs.Models;
using RPM.Desktop.Services.Art;
using RPM.Desktop.Strings;
using RPM.Fingerprint.Catalog;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>Looks for a cover in many places at once.
///
/// <para>The user types an artist and an album. The window then shows what seven or eight services hold
/// for those words. These are the Cover Art Archive's photographs of pressings, Discogs' collector
/// scans, the catalogue art from Apple, Spotify and Deezer, the hand-drawn HD sleeves from fanart.tv,
/// and the open web through Google. Each tile shows where it came from and how big it is, because those
/// two things decide which one to take. When the user picks one, it is downloaded and handed back to
/// the window that opened this one.</para>
///
/// <para>The result is <see cref="Picked"/>: the bytes of the chosen picture, or null when the window
/// was cancelled. This window writes nothing. The album editor that opened it owns the album's artwork,
/// and it stays the only thing that changes it.</para>
///
/// <para><b>What arrives on its own clock here, and why each one is safe:</b></para>
/// <list type="number">
/// <item><description><b>The search.</b> One call, on a background thread, that fans out to seven
/// services. A new search cancels the previous one (<c>_searchCts</c>). So an old fan-out that lands
/// late cannot fill the grid with the previous album. This is safe because the rows it writes are only
/// added under a token check on the UI thread.</description></item>
/// <item><description><b>Sixty thumbnails</b>, each on its own host's timing, in lanes. Each task holds
/// its <i>own row object</i> and writes only to that row. This is what makes it safe. A row removed
/// from the list is an orphan, so a late write reaches nothing. If the writes went by index into the
/// collection instead, the same code would paint the wrong pictures onto the next
/// search.</description></item>
/// <item><description><b>The user searching again</b> between any two of the above. Items (1) and (2)
/// handle this. The grid is also cleared on the UI thread before the new search starts, so old rows and
/// new rows are never in the list together.</description></item>
/// <item><description><b>The user picking</b>, which starts a download of the full picture. That can
/// take seconds. The candidate is read at the moment the button is pressed and held in a local
/// variable. It is not read from the selection afterwards, because a search that finishes during the
/// download rebuilds the grid and the selection with it.</description></item>
/// <item><description><b>The window closing</b> during a download. The button is disabled for the
/// duration, so the only way out is the framework's own dismiss. The download uses the window's token.
/// A cancelled download never sets <see cref="Picked"/>.</description></item>
/// </list>
/// <para>Not verified: whether the framework can deliver a <c>DoubleTapped</c> to a container that was
/// recycled while a search finishes in the same frame. The result would be the wrong cover, not a
/// crash. This only happens when the grid is rebuilt in the middle of a gesture.</para>
/// </summary>
public sealed partial class CoverArtSearchDialog : ContentDialog
{
    private readonly ICoverSearchApi _covers;
    private readonly DispatcherQueue _dispatcher;
    private readonly ObservableCollection<CoverResultRow> _rows = new();
    private readonly ObservableCollection<CoverSourceToggle> _sources = new();

    private readonly CancellationTokenSource _closing = new();
    private CancellationTokenSource? _searchCts;
    private bool _busy;

    /// <summary>The picture that the user chose, already downloaded. Null when the window was
    /// cancelled. It is also null when the chosen picture could not be fetched. In that case the window
    /// stays open and says so. This is read only after the window closes.</summary>
    public byte[]? Picked { get; private set; }

    /// <summary>The disc that came with the chosen sleeve, where the source had one and it could be
    /// fetched.
    ///
    /// <para>Null in a disc search, where the disc is <see cref="Picked"/>. Null when the chosen result
    /// had no disc, which is most results. Null also when the disc's own download failed. That is
    /// deliberately not treated as the pick failing. The user asked for a cover and got one. Losing the
    /// record's sleeve because a bonus picture failed to download would make no sense.</para>
    /// </summary>
    public byte[]? PickedDisc { get; private set; }

    /// <summary>Which of the record's two pictures this window is looking for. It is fixed for the life
    /// of the window. The window is opened from one picture pane or the other, and that decides the
    /// question.</summary>
    private readonly CoverKind _kind;

    /// <summary>How many results to ask each source for. Twelve from seven sources fills this window
    /// about twice. That is as far as most people scroll before they retype the words.</summary>
    private const int PerSource = 12;

    /// <summary>How many thumbnails are fetched at once. Six fills the visible grid quickly. It does
    /// not open sixty connections to a dozen CDNs when the window appears.</summary>
    private const int ThumbLanes = 6;

    /// <summary>The square the grid draws each result in, in points. This matches the number in the
    /// markup (see this window's ItemTemplate). A picture that has to be fetched at full size is
    /// decoded at this size, not whole. See <see cref="ThumbAsync"/>.</summary>
    private const double TileEdge = 152;

    public CoverArtSearchDialog(
        ICoverSearchApi covers, string? artist, string? album, CoverKind kind = CoverKind.Cover)
    {
        InitializeComponent();
        _covers = covers;
        _kind = kind;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        Shell.Title = Loc.Get(kind == CoverKind.Disc ? "CoverSearch_HeadingDisc" : "CoverSearch_Heading");
        // A record for a disc search, a picture frame for a sleeve.
        Shell.TitleGlyph = kind == CoverKind.Disc ? "\uf51f" : "\uf03e";

        // Wide enough for the form's column and five tiles beside it. Tall enough for three rows of
        // tiles. At that height the grid reads as a grid, not as a list.
        Shell.AttachResize(this, minWidth: 820, minHeight: 480, maxWidth: 1400, defaultWidth: 1020,
            defaultHeight: 660);

        ResultGrid.ItemsSource = _rows;
        SourceList.ItemsSource = _sources;

        ArtistBox.Text = artist ?? "";
        AlbumBox.Text = album ?? "";

        EmptyText.Text = Loc.Get("CoverSearch_EmptyStart");
        UseText.Text = Loc.Get(kind == CoverKind.Disc ? "CoverSearch_UseDisc" : "CoverSearch_Use");

        Opened += OnOpened;
        Closed += OnClosed;
    }

    private async void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        // The source filter is built from the sources that can really be reached, so it must be fetched
        // before it is shown. Until the answer arrives there are no tick boxes. That is correct. A list
        // that the user could act on before it is real would be worse than a moment of nothing.
        await LoadSourcesAsync();

        // The caller opened this window because the record's artwork is wrong. It already knows what
        // the record is called. So the search starts at once, using those words.
        if (ArtistBox.Text.Trim().Length > 0 || AlbumBox.Text.Trim().Length > 0)
            await SearchAsync();
        else
            ArtistBox.Focus(FocusState.Programmatic);
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        _searchCts?.Cancel();
        _closing.Cancel();
    }

    private async Task LoadSourcesAsync()
    {
        IReadOnlyList<string> names;
        try
        {
            // A question that was not answered is reported as such below. It is never reported as a
            // service with no sources. Getting this wrong would make the window blame the user's
            // Discogs account for an API that cannot be reached.
            names = (await _covers.SourcesAsync(_kind, _closing.Token)).Answers(out var answered, out _)
                ? answered
                : [];
        }
        catch (OperationCanceledException) { return; }
        catch { names = []; }

        foreach (var name in names)
            _sources.Add(new CoverSourceToggle { Name = name });

        // Four of the sources need no key. So a server that answers always names at least those four.
        // No names means the question did not get through, whatever the reason.
        if (names.Count == 0)
        {
            HintText.Text = Loc.Get("CoverSearch_NoSources");
            HintText.Visibility = Visibility.Visible;
            return;
        }

        // A disc search has a shorter list. The user cannot work out why, so the window gives the
        // reason instead of the Discogs line. Discogs is one of the sources missing from this list.
        // Telling the user to connect Discogs in order to find disc art would be misleading. Its
        // release photographs are not typed, so only a person looking at them can pick out the disc.
        if (_kind == CoverKind.Disc)
        {
            HintText.Text = Loc.Get("CoverSearch_DiscSourcesShort");
            HintText.Visibility = Visibility.Visible;
            return;
        }

        // Discogs is the source most likely to hold the sleeve of the record the user actually has. It
        // is also the only source that needs the user to do something first. So the hint is worth
        // showing.
        var hasDiscogs = names.Any(n => n.Equals("Discogs", StringComparison.OrdinalIgnoreCase));
        HintText.Text = Loc.Get("CoverSearch_ConnectDiscogs");
        HintText.Visibility = hasDiscogs ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── searching ─────────────────────────────────────────────────────────────

    private async void Search_Click(object sender, RoutedEventArgs e) => await SearchAsync();

    private async void Field_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter)
            return;
        e.Handled = true;
        await SearchAsync();
    }

    private async Task SearchAsync()
    {
        var artist = ArtistBox.Text.Trim();
        var album = AlbumBox.Text.Trim();
        if (artist.Length == 0 && album.Length == 0)
        {
            EmptyText.Text = Loc.Get("CoverSearch_EmptyStart");
            return;
        }

        // The previous search, and every thumbnail it started, stops here. It is cancelled before the
        // grid is cleared. A lane that runs once more between the two steps writes to a row that is
        // about to be dropped, which is harmless. A lane that runs once more after the new rows are
        // added would write the old album's pictures into the new album's grid. So the cancel comes
        // first.
        _searchCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _searchCts = cts;

        _rows.Clear();
        SetBusy(true);
        Spinner.Spinning = true;
        EmptyState.Visibility = Visibility.Visible;
        EmptyText.Text = Loc.Get("CoverSearch_Searching");
        StatusText.Text = "";
        UseButton.IsEnabled = false;
        OpenPageButton.IsEnabled = false;

        // Unticked sources are left out of the request, not filtered out of the answer. Each source is
        // an upstream call, and Google's calls are metered.
        var wanted = _sources.Where(s => s.IsOn).Select(s => s.Name).ToList();
        var query = new CoverSearchQuery(album, artist, PerSource,
            wanted.Count == _sources.Count ? null : wanted, _kind);

        CoverSearchResponse? answer;
        try
        {
            answer = (await _covers.SearchAsync(query, cts.Token)).Answers(out var answered, out _)
                ? answered
                : null;
        }
        catch (OperationCanceledException) { return; }
        catch
        {
            answer = null;
        }

        // No answer means the search could not be completed. It never means that no covers were found.
        if (answer is null)
        {
            if (!cts.IsCancellationRequested && ReferenceEquals(_searchCts, cts))
            {
                SetBusy(false);
                Spinner.Spinning = false;
                EmptyText.Text = Loc.Get("CoverSearch_Failed");
            }
            return;
        }

        // The user may have searched again while this call was running. The newer search now owns the
        // grid.
        if (cts.IsCancellationRequested || !ReferenceEquals(_searchCts, cts))
            return;

        SetBusy(false);
        Spinner.Spinning = false;

        foreach (var candidate in answer.Results)
            _rows.Add(CoverResultRow.From(candidate));

        EmptyState.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = Loc.Get(_kind == CoverKind.Disc ? "CoverSearch_NoDiscs" : "CoverSearch_NoResults");
        StatusText.Text = _rows.Count == 0
            ? ""
            : Loc.Format("CoverSearch_Found", _rows.Count,
                answer.Results.Select(r => r.Source).Distinct().Count());

        // The tiles are already on screen. The pictures, and then the sizes, load after them. This is
        // not awaited. The grid is usable as soon as it has rows, and a user who sees the right title
        // can click it before its thumbnail arrives. The decode size depends on the screen. It is read
        // here, once, on the UI thread, and not by each lane.
        var settled = _rows.ToList();
        var edge = ArtScaler.DecodeEdgeFor(TileEdge, XamlRoot?.RasterizationScale ?? 1);
        _ = FillThumbsAsync(settled, edge, cts.Token)
            .ContinueWith(_ => MeasureAsync(settled, cts.Token),
                          cts.Token, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>The thumbnails, loaded after the tiles are already up.
    ///
    /// <para>The lanes share one cursor. This is better than one task per row. Sixty results across a
    /// dozen hosts would be too many requests at once, and the window would stop responding at the
    /// moment it appears.</para>
    ///
    /// <para>Every write here goes to the row that the task holds, on the UI thread, and to no other
    /// row. This is why a search that the user has moved on from is harmless. Its rows are no longer in
    /// the collection, so writing to them changes nothing that anyone can see. If this were changed to
    /// index into <c>_rows</c>, it would become wrong without looking any different.</para>
    /// </summary>
    private async Task FillThumbsAsync(IReadOnlyList<CoverResultRow> rows, int edge, CancellationToken ct)
    {
        var next = -1;
        var lanes = Enumerable.Range(0, Math.Min(ThumbLanes, rows.Count)).Select(async _ =>
        {
            while (true)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= rows.Count || ct.IsCancellationRequested)
                    return;

                var row = rows[index];
                try
                {
                    var bitmap = await ThumbAsync(row.Candidate, edge, ct);
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (ct.IsCancellationRequested)
                            return;
                        if (bitmap is not null)
                            row.Thumb = bitmap;
                        else
                            row.Unavailable = true;
                    });
                }
                catch (OperationCanceledException) { return; }
                catch
                {
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (!ct.IsCancellationRequested)
                            row.Unavailable = true;
                    });
                }
            }
        });

        await Task.WhenAll(lanes);
    }

    /// <summary>One tile's picture: the small copy where the host serves one, and the full picture
    /// where it does not.
    ///
    /// <para>The fallback is needed for fanart.tv. Every other source gives its own thumbnail address.
    /// fanart.tv gives one address, and the API derives the small copy's address from it (see
    /// <c>FanartClient.Preview</c> in RPM.Lambda.Api). An address that the app worked out, and that the
    /// host will not serve, is a bug in the app, not a missing picture. The picture is there, at the
    /// address that the Use button will fetch. Marking the tile as broken would be the worst possible
    /// answer.</para>
    ///
    /// <para>The picture is decoded at the size it is drawn at, not whole. A fallback is a full-size
    /// cover, which is a large file with far more pixels than the tile needs.</para>
    ///
    /// <para>This costs one extra request, and only for a tile that would otherwise show the broken
    /// mark. A source whose two addresses are the same is not asked twice.</para>
    /// </summary>
    private static async Task<Microsoft.UI.Xaml.Media.Imaging.BitmapImage?> ThumbAsync(
        CoverCandidate candidate, int edge, CancellationToken ct)
    {
        // Try the thumbnail first. If it does not arrive, whether the host has none or could not be
        // reached, try the full picture. The full address is a second chance either way.
        if ((await RemoteImage.LoadAsync(candidate.ThumbUrl, ct)).Match(picture => picture, _ => null) is { } small)
            return small;

        if (string.IsNullOrWhiteSpace(candidate.FullUrl)
            || string.Equals(candidate.FullUrl, candidate.ThumbUrl, StringComparison.OrdinalIgnoreCase))
            return null;

        // Neither one arrived. The tile shows the broken mark. The mark means the picture cannot be
        // shown, and that is true of a failed fetch too. Nothing is kept.
        if ((await RemoteImage.BytesAsync(candidate.FullUrl, ct)).Match(found => found, _ => null) is not { } bytes)
            return null;

        try
        {
            return await RemoteImage.DecodeAsync(bytes, edge);
        }
        catch
        {
            return null;   // Bytes that are not a picture the app can read
        }
    }

    /// <summary>How big each picture actually is, for the sources that do not say.
    ///
    /// <para>Only three of the eight sources state their dimensions, and they are not the three whose
    /// results are worth taking. The record catalogues (the Cover Art Archive, Discogs and Apple) give
    /// an address and nothing else. A grid that showed only what the sources state would leave the size
    /// blank on exactly the tiles where the size decides the choice.</para>
    ///
    /// <para>The size is measured, not downloaded. Every image format states its dimensions in a header
    /// near the start of the file. So a ranged request for the first 64 KB is enough. Sixty of those
    /// come to a couple of megabytes. Sixty full pictures would be about a hundred.</para>
    ///
    /// <para>Measuring runs after the thumbnails, not beside them. Both need the same connections to
    /// the same few CDNs. A size shown under a tile that is still a grey square helps nobody. The
    /// picture is what the user is waiting for, and the size is only a caption on it.</para>
    ///
    /// <para>It follows the same rules as the thumbnails, for the same reason. Each task holds its own
    /// row and writes only to that row, on the UI thread, under the search's token.</para>
    /// </summary>
    private async Task MeasureAsync(IReadOnlyList<CoverResultRow> rows, CancellationToken ct)
    {
        // A tile whose picture never arrived is not measured. The host has already refused it once.
        // Some of the sixty results will refuse again for the same reason.
        var pending = rows.Where(r => r.NeedsMeasuring && !r.Unavailable).ToList();
        if (pending.Count == 0)
            return;

        var next = -1;
        var lanes = Enumerable.Range(0, Math.Min(ThumbLanes, pending.Count)).Select(async _ =>
        {
            while (true)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= pending.Count || ct.IsCancellationRequested)
                    return;

                var row = pending[index];
                try
                {
                    if (await _covers.MeasureAsync(row.Candidate, ct) is not { } measured)
                        continue;
                    _dispatcher.TryEnqueue(() =>
                    {
                        if (!ct.IsCancellationRequested)
                            row.Measured = measured;
                    });
                }
                catch (OperationCanceledException) { return; }
                catch
                {
                    // A picture that cannot be measured keeps a blank caption. There is nothing to
                    // report and nothing to retry.
                }
            }
        });

        await Task.WhenAll(lanes);
    }

    // ── choosing ──────────────────────────────────────────────────────────────

    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = ResultGrid.SelectedItem as CoverResultRow;
        UseButton.IsEnabled = row is not null && !_busy;
        OpenPageButton.IsEnabled = row is { HasPage: true };
    }

    private async void Results_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (ResultGrid.SelectedItem is CoverResultRow row)
            await TakeAsync(row);
    }

    private async void Use_Click(object sender, RoutedEventArgs e)
    {
        if (ResultGrid.SelectedItem is CoverResultRow row)
            await TakeAsync(row);
    }

    /// <summary>Fetch the full picture and close with it.
    ///
    /// <para>The row is passed in as an argument, not read from the selection inside this method. This
    /// is the safety point. The method waits for a download of several megabytes, and a search that
    /// finishes during the download rebuilds the grid and the selection. So what the user pressed the
    /// button on is decided at the moment they press it.</para>
    ///
    /// <para>A picture that cannot be fetched leaves the window open. This is the honest result. Some
    /// web results sit on servers that serve them only to their own pages. The only useful thing to do
    /// is let the user pick a different one.</para>
    ///
    /// <para>When the sleeve brings a disc with it, both are fetched and both are handed back. A
    /// failure of the disc is not a failure of the pick. The user asked for a cover, and one arrived.
    /// Throwing it away because a second picture failed would lose the thing they chose.</para>
    /// </summary>
    private async Task TakeAsync(CoverResultRow row)
    {
        if (_busy)
            return;

        var waiting = Loc.Get(row.Candidate.HasDisc ? "CoverSearch_FetchingBoth" : "CoverSearch_Fetching");
        SetBusy(true);
        StatusText.Text = waiting;
        ShowVeil(waiting);
        try
        {
            var bytes = await _covers.DownloadAsync(row.Candidate, _closing.Token);
            if (bytes is not { Length: > 0 })
            {
                row.Unavailable = true;
                StatusText.Text = Loc.Get("CoverSearch_CouldNotFetch");
                return;
            }

            // The disc that goes with it comes from the same source and release. It is fetched after
            // the sleeve, not beside it. So a slow disc never delays the picture the user asked for.
            // Any failure with the disc happens when the sleeve is already in hand.
            if (row.Candidate.AsDisc is { } disc)
                PickedDisc = await StampAsync(
                    await _covers.DownloadAsync(disc, _closing.Token), row.Candidate);

            // In a disc search the result is the disc itself, so it is the thing that gets cut out.
            // Stamping never returns nothing. A picture with no disc to find is returned as it was. The
            // record's own border already clips it round, and a flat picture is better than an empty
            // pane.
            Picked = _kind == CoverKind.Disc ? await StampAsync(bytes, row.Candidate) : bytes;
            Hide();
        }
        catch (OperationCanceledException) { }
        catch
        {
            row.Unavailable = true;
            StatusText.Text = Loc.Get("CoverSearch_CouldNotFetch");
        }
        finally
        {
            HideVeil();
            SetBusy(false);
        }
    }

    /// <summary>Shown while a picture is fetched and cut. This is the one part of the window with no
    /// progress of its own. A search fills the grid as it goes, and the thumbnails arrive tile by tile,
    /// so both show activity. Taking a picture is one wait of a few seconds. A sleeve from a source is
    /// several megabytes, and stamping a disc decodes all of it. Nothing shows the wait except a button
    /// that has gone grey.
    ///
    /// <para>The veil covers the grid, not the space beside it. The results are no longer what the user
    /// is looking at. The veil also stops a second tile from being double-tapped into a second download
    /// while the first is still coming.</para>
    /// </summary>
    private void ShowVeil(string message)
    {
        BusyText.Text = message;
        BusyVeil.Visibility = Visibility.Visible;
        BusySpinner.Spinning = true;
    }

    private void HideVeil()
    {
        BusySpinner.Spinning = false;
        BusyVeil.Visibility = Visibility.Collapsed;
    }

    /// <summary>Cuts the round disc out of a photograph, here, at the moment it is chosen.
    ///
    /// <para>Disc art is meant to be a transparent PNG of the record itself. That is what the turntable
    /// draws over its own pressing. Two of the three sources hold exactly that, and they are handed
    /// back untouched. The Cover Art Archive does not. Its images are a collector's photograph of a CD
    /// on a table, corners and all. A JPEG cannot carry transparency at all. So the transparency is
    /// made here, not looked for.</para>
    ///
    /// <para>This is done as the window closes, not on save. This way the user sees what was chosen.
    /// The pane behind shows the stamped disc as soon as this window closes. So a bad cut can be
    /// replaced by picking again, not by saving, looking, and coming back.</para>
    ///
    /// <para>A picture with no disc to find is kept as it is, not refused. The record's own border
    /// already clips it round. A flat picture in the pane is a better result than an empty pane with a
    /// message.</para>
    /// </summary>
    private static async Task<byte[]?> StampAsync(byte[]? bytes, CoverCandidate candidate)
    {
        if (bytes is not { Length: > 0 })
            return bytes;

        // Whether only the middle is replaced. A record's label is stamped at the label's own size, and
        // the app's own pressing shows around it. A CD replaces the drawn record entirely. The pressing
        // says which one it is, and the source writes that in the note.
        var vinyl = candidate.Note is { } note
                 && (note.Contains("Vinyl", StringComparison.OrdinalIgnoreCase)
                     || note.Contains("LP", StringComparison.OrdinalIgnoreCase));

        // Both calls run off the UI thread, not only the stamp. Checking whether a picture has
        // transparency is not a cheap header read. It decodes the image and looks at the pixels,
        // because a PNG can have an alpha channel that is opaque everywhere. A photograph saved that
        // way is an example. On a full-size sleeve this takes most of a second. It must not run on the
        // thread that draws the window.
        return await Task.Run(() => DiscStamp.HasAlpha(bytes) ? bytes : DiscStamp.Stamp(bytes, vinyl) ?? bytes);
    }

    private async void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        // http or https only: the page address came from a cover search source (see WebLink).
        if (ResultGrid.SelectedItem is not CoverResultRow { Candidate.PageUrl: var url }
            || RPM.Core.Net.WebLink.Parse(url) is not { } link)
            return;
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(link.Uri);
        }
        catch
        {
            // A browser that will not open is not something this window can do anything about.
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>One flag for "a call is out". Every button in the window means something different
    /// while a call is out. Search would start a second fan-out, and Use would start a second
    /// download.</summary>
    private void SetBusy(bool busy)
    {
        _busy = busy;
        SearchButton.IsEnabled = !busy;
        UseButton.IsEnabled = !busy && ResultGrid.SelectedItem is not null;
    }
}
