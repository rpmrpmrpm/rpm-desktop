using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Library;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Dialogs.Controls;
using RPM.Desktop.Presentation.Dialogs.Models;
using RPM.Desktop.Services.Art;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// The mood window's last step: the picture under the cover's frame. It has the cover window's layout and three of its
/// tabs (records, picture, made figures), and a fourth that is the default: the picture the app's own list of the mood
/// wears. Whatever is chosen, the preview and the saved cover are drawn under the frame (<see cref="MoodCovers"/>).
///
/// <para><b>What arrives on its own clock here, and why each is safe.</b> Every field is written and read on the UI
/// thread; the work below runs off it and comes back through an <c>await</c> begun on the UI thread or through
/// <c>_dispatcher</c>.</para>
/// <list type="number">
/// <item><description><b>The records</b> (<c>FillRecordsAsync</c>), read from the recipe as it stands whenever the
/// step is entered with different filters than the last fill (<c>_recordsKey</c>). A newer fill cancels the older
/// one's sleeve lanes and takes a new generation; an older answer arriving late is dropped by the generation check.
/// Picks that are not among the new records are dropped, so the saved choice never names a record the list does not
/// hold. Safe because of the generation check and the cancel; remove either and stale rows can land.</description></item>
/// <item><description><b>The sleeves</b>, lane-parallel disk reads, each writing only its own row and
/// <c>_recordSleeves</c> through the dispatcher, under the fill's token. A record can be picked only once its sleeve is
/// in, as in the cover window.</description></item>
/// <item><description><b>The figures</b> (<c>FillFigures</c>), redrawn when the colours button asks; the earlier
/// fill is cancelled first and its rows are no longer in <c>_figures</c>, so a late tile changes nothing seen.</description></item>
/// <item><description><b>The preview</b> (<c>RepaintPreview</c>), on every choice and on entering the step. A
/// generation counter: only the newest repaint is shown.</description></item>
/// <item><description><b>Save</b> draws the cover again from the recipe, waiting for any picked sleeve not yet in,
/// and hands nothing back if the window closed meanwhile (<c>_closing</c>).</description></item>
/// </list>
/// </summary>
public sealed partial class MoodPlaylistDialog
{
    private const int PreviewEdge = 220;
    private const double SleeveEdge = 96;
    private const double FigureEdge = 132;
    private const int Lanes = 6;

    private readonly DispatcherQueue _dispatcher;
    private readonly CancellationTokenSource _closing = new();
    private bool _saving;

    private RailTabStrip? _tabs;
    private readonly PictureWell _preview = new(PreviewEdge);

    private readonly ObservableCollection<CoverChoiceRow> _records = [];
    private readonly ObservableCollection<CoverChoiceRow> _figures = [];
    private readonly List<string> _picked = [];
    private readonly Dictionary<string, byte[]> _recordSleeves = new(StringComparer.OrdinalIgnoreCase);
    private string? _recordsKey;
    private int _recordsGen;
    private CancellationTokenSource? _recordsCts;
    private CancellationTokenSource? _figuresCts;

    private byte[]? _picture;
    private CoverChoiceRow? _figure;
    private uint _roll;
    private long _previewGen;

    /// <summary>The tab the step opens on and what it holds, from the list's stored choice.</summary>
    private void SetUpCover(MoodCover current, byte[]? currentPicture)
    {
        PreviewHost.Child = _preview.Root;
        _tabs = new RailTabStrip(
            new RailTabStrip.Tab("mood", "MoodMaker_CoverMood", ""),
            new RailTabStrip.Tab("records", "Playlists_CoverRecords", ""),
            new RailTabStrip.Tab("picture", "Playlists_CoverPicture", ""),
            // Not the cover window's "Moods": beside this window's own Mood tab the two read as one.
            new RailTabStrip.Tab("figure", "MoodMaker_CoverDesigns", ""));
        TabStripHost.Child = _tabs;
        _tabs.Register("mood", MoodPanel);
        _tabs.Register("records", RecordsPanel);
        _tabs.Register("picture", PicturePanel);
        _tabs.Register("figure", FigurePanel);
        RecordsGrid.ItemsSource = _records;
        FiguresGrid.ItemsSource = _figures;

        _roll = current.Source is MoodCoverSource.Figure ? current.Seed : (uint)Environment.TickCount;
        if (current.Source is MoodCoverSource.Records)
            _picked.AddRange(current.Folders.Take(PlaylistArt.Sleeves));
        if (current.Source is MoodCoverSource.Figure)
            _figure = new CoverChoiceRow(current.Style, current.Seed) { Edge = FigureEdge };
        if (currentPicture is { Length: > 0 })
        {
            _picture = currentPicture;
            CopiedText.Visibility = Visibility.Visible;
        }

        _tabs.Select(current.Source switch
        {
            MoodCoverSource.Records => "records",
            // A picture the caller could not hand back cannot be shown or drawn again; the mood's own stands in.
            MoodCoverSource.Picture => _picture is null ? "mood" : "picture",
            MoodCoverSource.Figure => "figure",
            MoodCoverSource.Mood => "mood",
        });
        _tabs.SelectionChanged += _ => RepaintPreview();
    }

    /// <summary>The picture under the frame as the step stands: the tab showing, if it holds a choice, else the mood's
    /// own. Read before the step is set up too (the recipe asks for it), where it is the mood's own.</summary>
    private MoodCover CoverChoice() => _tabs?.Current switch
    {
        "records" when _picked.Count > 0 => MoodCover.OfRecords(_picked),
        "picture" when _picture is { Length: > 0 } => MoodCover.OfPicture,
        "figure" when _figure is { } f => MoodCover.OfFigure(f.Style, f.Seed),
        _ => MoodCover.OfMood,
    };

    private void EnterCover()
    {
        if (_figures.Count == 0)
            FillFigures();

        // The records hang off the filters, not the cover; filled again only when the filters changed since.
        var recipe = Recipe();
        var key = recipe with { Cover = MoodCover.OfMood };
        var keyText = key.ToJson();
        if (keyText != _recordsKey)
        {
            _recordsKey = keyText;
            _ = FillRecordsAsync(recipe);
        }
        RepaintPreview();
    }

    private void CloseCover()
    {
        _closing.Cancel();
        _recordsCts?.Cancel();
        _figuresCts?.Cancel();
    }

    // ── records ──

    private async Task FillRecordsAsync(MoodRecipe recipe)
    {
        var gen = ++_recordsGen;
        _recordsCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _recordsCts = cts;

        IReadOnlyList<(string Folder, string Album, string Artist)> found;
        try
        {
            var db = _db;
            found = await Task.Run(() => db.MoodRecipeRecords(recipe), cts.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException or ObjectDisposedException)
        {
            return;
        }
        if (cts.IsCancellationRequested || gen != _recordsGen)
            return;

        _records.Clear();
        foreach (var r in found)
            _records.Add(new CoverChoiceRow { Folder = r.Folder, Album = r.Album, Artist = r.Artist, Edge = SleeveEdge });

        var held = found.Select(r => r.Folder).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _picked.RemoveAll(f => !held.Contains(f));
        Renumber();
        NoRecordsText.Visibility = _records.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RepaintPreview();

        await FillSleevesAsync([.. _records], cts.Token);
    }

    private async Task FillSleevesAsync(IReadOnlyList<CoverChoiceRow> rows, CancellationToken ct)
    {
        var edge = ArtScaler.DecodeEdgeFor(SleeveEdge, XamlRoot?.RasterizationScale ?? 1);
        var next = -1;
        var lanes = Enumerable.Range(0, Math.Min(Lanes, rows.Count)).Select(async _ =>
        {
            while (true)
            {
                var index = Interlocked.Increment(ref next);
                if (index >= rows.Count || ct.IsCancellationRequested)
                    return;
                var row = rows[index];

                byte[]? bytes;
                try { bytes = await AlbumCovers.LoadAsync(row.Folder, ct, ArtScaler.RowEdge, ownCollection: true); }
                catch (OperationCanceledException) { return; }
                catch { bytes = null; }

                ImageSource? shown = null;
                if (bytes is { Length: > 0 })
                {
                    try { shown = await RemoteImage.DecodeSmoothAsync(bytes, edge, RemoteImage.Cut.Photograph); }
                    catch { shown = null; }
                }

                _dispatcher.TryEnqueue(() =>
                {
                    if (ct.IsCancellationRequested)
                        return;
                    if (shown is null || bytes is not { Length: > 0 })
                    {
                        row.Unavailable = true;
                        return;
                    }
                    row.Thumb = shown;
                    _recordSleeves[row.Folder] = bytes;
                    if (_picked.Contains(row.Folder, StringComparer.OrdinalIgnoreCase))
                        RepaintPreview();
                });
            }
        });
        await Task.WhenAll(lanes);
    }

    private void RecordsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CoverChoiceRow row || row.Folder.Length == 0)
            return;
        var at = _picked.FindIndex(f => string.Equals(f, row.Folder, StringComparison.OrdinalIgnoreCase));
        if (at >= 0)
            _picked.RemoveAt(at);
        else if (row.Thumb is not null && _picked.Count < PlaylistArt.Sleeves)
            _picked.Add(row.Folder);
        else
            return;
        Renumber();
        RepaintPreview();
    }

    private void Renumber()
    {
        foreach (var row in _records)
        {
            var at = _picked.FindIndex(f => string.Equals(f, row.Folder, StringComparison.OrdinalIgnoreCase));
            row.Number = at >= 0 ? at + 1 : null;
        }
    }

    // ── picture ──

    private async void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = Services.Shell.Pickers.OpenFile(
                Windows.Storage.Pickers.PickerLocationId.PicturesLibrary, ".jpg", ".jpeg", ".png");
            if (await picker.PickSingleFileAsync() is not { } file)
                return;
            // Squared on the centre and no larger than the largest edge a cover is composed at, as the cover window
            // takes a picture.
            var bytes = await Task.Run(() => PlaylistArt.Bleed(File.ReadAllBytes(file.Path), ArtScaler.DisplayEdge));
            if (_closing.IsCancellationRequested)
                return;
            _picture = bytes;
            CopiedText.Visibility = Visibility.Visible;
            RepaintPreview();
        }
        catch
        {
            // A file that will not open is a choice not made. The tab keeps whatever it had.
        }
    }

    // ── made figures ──

    private void FillFigures()
    {
        _figuresCts?.Cancel();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _figuresCts = cts;

        _figures.Clear();
        for (var style = 0; style < PlaylistArt.Styles; style++)
            _figures.Add(new CoverChoiceRow(style, _roll) { Edge = FigureEdge });
        foreach (var row in _figures)
            row.Selected = _figure is { } f && f.Style == row.Style && f.Seed == row.Seed;

        _ = FillFigureThumbsAsync([.. _figures], cts.Token);
    }

    private async Task FillFigureThumbsAsync(IReadOnlyList<CoverChoiceRow> rows, CancellationToken ct)
    {
        var edge = ArtScaler.DecodeEdgeFor(FigureEdge, XamlRoot?.RasterizationScale ?? 1);
        foreach (var row in rows)
        {
            if (ct.IsCancellationRequested)
                return;
            byte[]? bytes;
            try { bytes = await Task.Run(() => PlaylistArt.Figure(row.Style, row.Seed, CoverMark.None, mood: null).Cover, ct); }
            catch { continue; }
            if (bytes is not { Length: > 0 })
                continue;
            ImageSource? shown;
            try { shown = await RemoteImage.DecodeSmoothAsync(bytes, edge, RemoteImage.Cut.Drawing); }
            catch { continue; }
            if (!ct.IsCancellationRequested)
                row.Thumb = shown;
        }
    }

    private void FiguresGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CoverChoiceRow row)
            return;
        foreach (var r in _figures)
            r.Selected = ReferenceEquals(r, row);
        _figure = row;
        RepaintPreview();
    }

    /// <summary>Another colourway for all eight. A pick moves onto its own family in the new colours, so the preview
    /// and the saved cover are the tile that is lit.</summary>
    private void Colours_Click(object sender, RoutedEventArgs e)
    {
        _roll = (_roll * 1664525u) + 1013904223u;
        var style = _figure?.Style;
        FillFigures();
        if (style is { } s)
        {
            _figure = _figures.First(r => r.Style == s);
            foreach (var r in _figures)
                r.Selected = ReferenceEquals(r, _figure);
        }
        RepaintPreview();
    }

    // ── the preview and the saved cover ──

    private void RepaintPreview()
    {
        if (_step is not Step.Cover)
            return;
        var gen = Interlocked.Increment(ref _previewGen);
        _ = RepaintPreviewAsync(gen, Recipe(), NameBox.Text.Trim());
    }

    private async Task RepaintPreviewAsync(long gen, MoodRecipe recipe, string name)
    {
        PlaylistArt.Drawn drawn;
        try { drawn = await DrawCoverAsync(recipe, name, finalDraw: false); }
        catch { return; }
        if (_closing.IsCancellationRequested || gen != Interlocked.Read(ref _previewGen))
            return;

        ImageSource? shown = null;
        if (drawn.Cover is { Length: > 0 } bytes)
        {
            try { shown = await RemoteImage.DecodeAsync(bytes); }
            catch { shown = null; }
        }
        if (!_closing.IsCancellationRequested && gen == Interlocked.Read(ref _previewGen))
            _preview.Show(shown);
    }

    /// <summary>The framed cover for <paramref name="recipe"/>. The preview draws with the sleeves already in hand;
    /// <paramref name="finalDraw"/> waits for any picked sleeve still being read, so the saved cover never leaves a
    /// chosen record out.</summary>
    private async Task<PlaylistArt.Drawn> DrawCoverAsync(MoodRecipe recipe, string name, bool finalDraw)
    {
        var sleeves = new List<byte[]>();
        foreach (var folder in recipe.Cover.Folders)
        {
            if (_recordSleeves.TryGetValue(folder, out var known))
            {
                sleeves.Add(known);
                continue;
            }
            if (!finalDraw)
                continue;
            byte[]? bytes;
            try { bytes = await AlbumCovers.LoadAsync(folder, _closing.Token, ArtScaler.RowEdge, ownCollection: true); }
            catch (Exception e) when (e is not OperationCanceledException) { bytes = null; }
            if (bytes is { Length: > 0 })
            {
                _recordSleeves[folder] = bytes;
                sleeves.Add(bytes);
            }
        }
        return await MoodCovers.DrawAsync(recipe.Kind, recipe.Cover, name, sleeves, _picture, _closing.Token);
    }
}
