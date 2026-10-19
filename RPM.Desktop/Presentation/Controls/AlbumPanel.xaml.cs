using RPM.Core.Import;
using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Models;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>The album detail: cover, play controls, favourite and Discogs state, tracklist, star rating and
/// the Markdown review. It is a reusable pane control. An album is a folder, given as a path relative to
/// the library root and joined with '/'. It is used by the Now Playing pane (fed from playback) and by the
/// full-screen <see cref="AlbumPage"/>.
/// </summary>
public sealed partial class AlbumPanel : UserControl
{
    private static readonly Color Gold = Color.FromArgb(0xFF, 0xF2, 0xC1, 0x4E);

    private string _folder = "";
    private IReadOnlyList<TrackInfo> _tracks = [];

    public AlbumPanel()
    {
        InitializeComponent();
        ShowEmpty();   // Start empty. The host calls Load() to fill it.
    }

    private static LibraryService Library => App.Services.GetRequiredService<LibraryService>();
    private static RPaMpService Amp => App.Services.GetRequiredService<RPaMpService>();

    /// <summary>The album currently shown, or "" when the empty state is up.</summary>
    public string Folder => _folder;

    /// <summary>Shows the detail for an album folder, given relative to the library root. If the folder is
    /// blank, or has no tracks in the library database, this shows the empty state instead.</summary>
    public void Load(string folder)
    {
        var library = Library;
        if (string.IsNullOrEmpty(folder) || !library.IsOpen || library.Root is not { } root)
        {
            ShowEmpty();
            return;
        }

        var db = library.Db;
        // Disc order, not file-name order. Otherwise a multi-disc album mixes its discs (both track 1s,
        // then both track 2s). Playing the album uses the same order.
        _tracks = AlbumTracklist.InDiscOrder(db.FolderTracks(folder).Select(t => t.ToTrackInfo(root)).ToList());
        if (_tracks.Count == 0)
        {
            ShowEmpty();
            return;
        }

        _folder = folder;
        ContentScroller.Visibility = Visibility.Visible;
        EmptyState.Visibility = Visibility.Collapsed;

        // The title and artist come from the album's own tags. If a tag is missing, the folder path is
        // used.
        var first = _tracks[0];
        var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        TitleText.Text = !string.IsNullOrWhiteSpace(first.Album) ? first.Album!
            : parts.Length > 0 ? parts[^1] : library.Root;
        var artist = first.AlbumArtist ?? first.Artist ?? (parts.Length >= 2 ? parts[^2] : "");

        // artist · year · FLAC · 5.1. Each part is shown only when it has a value. The format is read from
        // the audio stream, not from the tags (see Metadata/AudioFormat). Stereo is left out, because
        // nearly every record is stereo and the label would tell the reader nothing. The format comes from
        // the album's first track, because one release uses one encoding. If the tracks differ, the folder
        // needs sorting out, so the line is not hedged.
        var line = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(artist)) line.Add(artist);
        if (first.Year > 0) line.Add(first.Year.ToString());
        line.AddRange(AudioFormat.Describe(
            first.Extension, first.Channels, first.CodecDescription,
            first.BitsPerSample, first.SampleRate, first.BitrateKbps));

        SubtitleText.Text = string.Join("  ·  ", line);

        LoadCover(root);
        PlayButton.IsEnabled = _tracks.Count > 0;
        ShuffleButton.IsEnabled = _tracks.Count > 0;
        var meta = db.GetAlbumMeta(folder);
        TrackList.ItemsSource = AlbumTrackItem.Build(_tracks, meta.Classical == true);

        ApplyMeta(meta);
    }

    /// <summary>Drops the album and shows the empty state (Now Playing with nothing playing).</summary>
    public void ShowEmpty()
    {
        _folder = "";
        _tracks = [];
        ContentScroller.Visibility = Visibility.Collapsed;
        EmptyState.Visibility = Visibility.Visible;
    }

    /// <summary>Overrides the empty-state caption. For example, the full-screen host has no "nothing
    /// playing".</summary>
    public void SetEmptyText(string text) => EmptyText.Text = text;

    private void LoadCover(string root)
    {
        var fullFolder = Path.Combine(root, _folder.Replace('/', Path.DirectorySeparatorChar));
        if (FolderArtWriter.FindFolderImage(fullFolder) is { } image)
        {
            // A small thumbnail is loaded in the background. The square does not need the full-size image.
            var bitmap = new BitmapImage();
            Cover.Source = bitmap;
            CoverPlaceholder.Visibility = Visibility.Collapsed;
            _ = FillCoverAsync(bitmap, image);
        }
        else
        {
            Cover.Source = null;
            CoverPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private static async Task FillCoverAsync(BitmapImage bitmap, string path)
    {
        try
        {
            var thumb = await Task.Run(() => ThumbCache.Get(File.ReadAllBytes(path), ArtScaler.TileEdge));
            var stream = Presentation.Common.BitmapBytes.Stream(thumb);
            await bitmap.SetSourceAsync(stream);
        }
        catch { /* The cover is decoration. If it fails, the square stays empty and the panel keeps working. */ }
    }

    private void ApplyMeta(AlbumMeta meta)
    {
        // Star rating, read-only. It shows the stars from the review. Hidden when the album has no rating.
        RatingRow.Children.Clear();
        RatingRow.Visibility = meta.Rating > 0 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < meta.Rating; i++)
            RatingRow.Children.Add(new FontIcon
            {
                Glyph = "\uf005",
                FontFamily = (FontFamily)Resources["Fa"],
                FontSize = 14,
                Foreground = new SolidColorBrush(Gold),
            });

        // Favourite: filled heart when on, outline when off.
        FavoriteIcon.FontFamily = (FontFamily)Resources[meta.IsFavorite ? "Fa" : "FaRegular"];

        DiscogsButton.Visibility = string.IsNullOrEmpty(meta.DiscogsReleaseId)
            ? Visibility.Collapsed : Visibility.Visible;

        var hasBody = !string.IsNullOrWhiteSpace(meta.ReviewMarkdown);
        ReviewBody.Visibility = hasBody ? Visibility.Visible : Visibility.Collapsed;
        ReviewEmpty.Visibility = hasBody ? Visibility.Collapsed : Visibility.Visible;
        if (hasBody)
            SimpleMarkdown.Render(ReviewBody, meta.ReviewMarkdown);
    }

    // ── actions ──

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_tracks.Count > 0)
            Amp.AddTracks(_tracks, play: true);
    }

    private void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        if (_tracks.Count > 0)
            Amp.AddTracks([.. _tracks.OrderBy(_ => Random.Shared.Next())], play: true);
    }

    private void Track_Click(object sender, RoutedEventArgs e)
    {
        // Clicking a track plays the album from that track, then the rest of the tracks.
        if ((sender as FrameworkElement)?.Tag is AlbumTrackItem { Info: { } info })
            Amp.PlayAlbumFrom(info);
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        var library = Library;
        if (!library.IsOpen || _folder.Length == 0) return;
        var db = library.Db;
        var next = !db.GetAlbumMeta(_folder).IsFavorite;
        db.SetAlbumFavorite(_folder, next);
        FavoriteIcon.FontFamily = (FontFamily)Resources[next ? "Fa" : "FaRegular"];
    }

    private async void Discogs_Click(object sender, RoutedEventArgs e)
    {
        var id = Library.IsOpen && _folder.Length > 0 ? Library.Db.GetAlbumMeta(_folder).DiscogsReleaseId : null;
        if (!string.IsNullOrEmpty(id))
            await Windows.System.Launcher.LaunchUriAsync(new Uri($"https://www.discogs.com/release/{id}"));
    }

    private async void WriteReview_Click(object sender, RoutedEventArgs e)
    {
        var library = Library;
        if (!library.IsOpen || _folder.Length == 0 || XamlRoot is null) return;
        var db = library.Db;

        var subtitle = string.IsNullOrWhiteSpace(SubtitleText.Text) ? TitleText.Text
            : $"{TitleText.Text}  ·  {SubtitleText.Text}";
        var result = await AlbumReviewDialog.ShowAsync(XamlRoot, subtitle, db.GetAlbumMeta(_folder));
        if (result is null)
            return;

        db.SetAlbumReview(_folder, result.Rating, result.Markdown, DateTime.UtcNow.Ticks);
        ApplyMeta(db.GetAlbumMeta(_folder));
    }
}
