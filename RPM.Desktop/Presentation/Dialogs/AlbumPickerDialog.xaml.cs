using RPM.Core.Library;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>One record on the picker's list, flattened for the template.</summary>
[RpmBindable]
public sealed partial class AlbumPickerRow
{
    // Plain defaults, not required. Uno's bindable-type generator creates these with no arguments to
    // map the template's bindings. A required member would make that a compile error.
    public string Folder { get; init; } = "";
    public string Album { get; init; } = "";

    /// <summary>The artist, and the year when the library has it. These two tell one "Greatest Hits"
    /// from another.</summary>
    public string Line { get; init; } = "";

    public string Tracks { get; init; } = "";
}

/// <summary>Choosing a record that the library already holds.
///
/// <para>This is the other half of the track editor's lookup. A stray file usually belongs to a record
/// that is already in the library. It may have been ripped separately, or it may have come from somewhere
/// else with its tags wrong. In that case a catalogue search is a slow way to find something the
/// library can already answer. Pick the album, and its details come with it: the record's name, artist,
/// year, label and cover. The file then moves itself into that folder on save, because the naming
/// template puts it where its tags say it belongs.</para>
///
/// <para>There is no network, no rate limit, and no chance of picking the wrong pressing. The user
/// points at the record they already own.</para>
/// </summary>
public sealed partial class AlbumPickerDialog : ContentDialog
{
    private readonly List<AlbumPickerRow> _all = new();
    private string? _result;

    public AlbumPickerDialog(string? seedSearch)
    {
        InitializeComponent();
        Shell.AttachResize(this, minWidth: 560, minHeight: 420, maxWidth: 900, defaultHeight: 560);

        LoadAlbums();
        // Start with the text the track already has. The record it belongs to is then usually the first
        // item on the list, not something to search for.
        SearchBox.Text = seedSearch ?? "";
        Filter(SearchBox.Text);
        Loaded += (_, _) => SearchBox.Focus(FocusState.Programmatic);
    }

    private void LoadAlbums()
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen)
            return;

        var db = library.Db;
        var counts = db.QueryAlbumFacts()
            .ToDictionary(a => a.Folder, a => a, StringComparer.OrdinalIgnoreCase);

        foreach (var facts in counts.Values.OrderBy(a => a.Album, StringComparer.CurrentCultureIgnoreCase))
        {
            var tracks = db.FolderTracks(facts.Folder);
            if (tracks.Count == 0)
                continue;

            // The year is read from the tracks, not from the folder summary. The summary is used for
            // ordering and totals, and it has no year.
            var year = tracks.Select(t => t.Year).FirstOrDefault(y => y > 0);
            var line = new List<string>(2);
            if (facts.AlbumArtist is { Length: > 0 } artist)
                line.Add(artist);
            if (year > 0)
                line.Add(year.ToString());

            _all.Add(new AlbumPickerRow
            {
                Folder = facts.Folder,
                Album = facts.Album is { Length: > 0 } album ? album : facts.Folder,
                Line = string.Join("  ·  ", line),
                Tracks = Loc.Format("AlbumPicker_Tracks", tracks.Count),
            });
        }
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => Filter(SearchBox.Text);

    private void Filter(string search)
    {
        var terms = search.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = terms.Length == 0
            ? _all
            : [.. _all.Where(r => terms.All(t =>
                r.Album.Contains(t, StringComparison.CurrentCultureIgnoreCase)
                || r.Line.Contains(t, StringComparison.CurrentCultureIgnoreCase)))];

        ResultList.ItemsSource = shown;
        EmptyText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ResultList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UseButton.IsEnabled = ResultList.SelectedItem is AlbumPickerRow;

    private void ResultList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        => Commit();

    private void Use_Click(object sender, RoutedEventArgs e) => Commit();

    private void Commit()
    {
        if (ResultList.SelectedItem is not AlbumPickerRow row)
            return;
        _result = row.Folder;
        Hide();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>Opens the picker and returns the library-relative folder of the record chosen, or null.
    /// </summary>
    /// <param name="seedSearch">What the track says it is, so the list opens on the likely
    /// answer.</param>
    public static async Task<string?> ShowAsync(XamlRoot xamlRoot, string? seedSearch = null)
    {
        var dialog = new AlbumPickerDialog(seedSearch) { XamlRoot = xamlRoot };
        await DialogHost.ShowAsync(dialog);
        return dialog._result;
    }
}
