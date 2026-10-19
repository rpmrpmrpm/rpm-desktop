using RPM.Core.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>The album review editor: a 1 to 5 star rating and a Markdown body, with a live preview
/// beside it. Reviews are only saved on this device for now. The row they are saved into is the basis
/// of the album's future social data, and a later change will make reviews shareable.
/// </summary>
public sealed partial class AlbumReviewDialog : ContentDialog
{
    private const int MaxStars = 5;
    private static readonly Color Gold = Color.FromArgb(0xFF, 0xF2, 0xC1, 0x4E);
    private static readonly Color Empty = Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);

    private readonly FontIcon[] _stars = new FontIcon[MaxStars];
    private int _rating;
    private bool _saved;

    public AlbumReviewDialog(string subtitle, AlbumMeta meta)
    {
        InitializeComponent();
        Shell.Subtitle = subtitle;
        Shell.AttachResize(this, minWidth: 560, minHeight: 380, maxWidth: 900, defaultHeight: 520);

        BuildStars();
        _rating = Math.Clamp(meta.Rating, 0, MaxStars);
        Editor.Text = meta.ReviewMarkdown ?? "";
        RefreshStars();
        RenderPreview();
    }

    private void BuildStars()
    {
        for (int i = 0; i < MaxStars; i++)
        {
            var icon = new FontIcon
            {
                Glyph = "\uf005",
                FontFamily = (FontFamily)Resources["Fa"],
                FontSize = 20,
            };
            _stars[i] = icon;

            var button = new Button
            {
                Content = icon,
                Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(2, 0, 2, 0),
                MinWidth = 0,
                Tag = i + 1,
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                button, i == 0 ? Loc.Get("Review_OneStar") : Loc.Format("Review_Stars", i + 1));
            button.Click += Star_Click;
            StarPanel.Children.Add(button);
        }
    }

    private void Star_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: int value })
        {
            // Clicking the star that is already the rating sets it one lower. This is the quick way to
            // remove a rating.
            _rating = _rating == value ? value - 1 : value;
            RefreshStars();
        }
    }

    private void ClearRating_Click(object sender, RoutedEventArgs e)
    {
        _rating = 0;
        RefreshStars();
    }

    private void RefreshStars()
    {
        for (int i = 0; i < MaxStars; i++)
            _stars[i].Foreground = new SolidColorBrush(i < _rating ? Gold : Empty);
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e) => RenderPreview();

    private void RenderPreview() => SimpleMarkdown.Render(Preview, Editor.Text);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Hide();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _saved = true;
        Hide();
    }

    /// <summary>Opens the editor seeded from <paramref name="meta"/>. Returns the new rating and
    /// markdown when the user saves, or null when they cancel.</summary>
    public static async Task<AlbumReviewResult?> ShowAsync(XamlRoot xamlRoot, string subtitle, AlbumMeta meta)
    {
        var dialog = new AlbumReviewDialog(subtitle, meta) { XamlRoot = xamlRoot };
        await DialogHost.ShowAsync(dialog);
        if (!dialog._saved)
            return null;

        var text = dialog.Editor.Text.Trim();
        return new AlbumReviewResult(dialog._rating, text.Length == 0 ? null : text);
    }
}

/// <summary>The outcome of the review editor: the chosen star rating and markdown body.</summary>
public sealed record AlbumReviewResult(int Rating, string? Markdown);
