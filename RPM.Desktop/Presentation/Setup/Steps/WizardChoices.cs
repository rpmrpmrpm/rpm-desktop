using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Setup.Steps;

/// <summary>What every step shares: how many there are, and the mark's tagline.</summary>
internal static class WizardSteps
{
    /// <summary>The number of steps, for the kicker over each heading and the dots at the foot.</summary>
    public const int Count = 7;

    /// <summary>"Step 2 of 7".</summary>
    public static string Kicker(int step) => Loc.Format("Wizard_StepOf", step, Count);

    /// <summary>
    /// The tagline under the mark, split the way the artwork header splits it.
    /// </summary>
    /// <remarks>The leading "re-" is shown a shade darker, and only where the translation really
    /// starts with it. The hyphen is an English style choice. Other languages join the prefix to the
    /// word, or say it another way, so they get the line in one tone rather than an arbitrary few
    /// characters darkened. The rule is the same as <c>ArtDisplay.SyncTagline</c>, because it is the
    /// same line under the same mark. Two rules would eventually disagree.
    /// </remarks>
    public static void Tagline(Run head, Run tail)
    {
        var line = Loc.Get("Art_ParkedTagline");
        var split = line.StartsWith("re-", StringComparison.Ordinal) ? 3
                  : line.StartsWith("re", StringComparison.Ordinal) ? 2
                  : 0;

        head.Text = line[..split];
        tail.Text = line[split..];
    }
}

/// <summary>
/// A row of tiles to pick one from: a picture (or none), a name and a line under it, with the picked one
/// ringed in the app's cyan. The look step and the layout step use it for every choice that is easier to
/// see than to read.
/// </summary>
internal sealed class WizardChoices<T>(Panel host, T picked)
{
    private static readonly SolidColorBrush Ring = new(Windows.UI.Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF));
    private static readonly SolidColorBrush Rim = new(Windows.UI.Color.FromArgb(0x2B, 0xFF, 0xFF, 0xFF));

    private readonly List<(Border Tile, T Value)> _tiles = [];

    /// <summary>The value of the ringed tile.</summary>
    public T Picked { get; private set; } = picked;

    /// <summary>Adds a tile.</summary>
    /// <param name="value">What picking it means.</param>
    /// <param name="caption">Its name.</param>
    /// <param name="detail">A line under the name, or null.</param>
    /// <param name="image">An ms-appx picture of it, or null for a tile of words.</param>
    /// <param name="width">The tile's width.</param>
    /// <param name="imageHeight">The picture's height in the tile.</param>
    public void Add(T value, string caption, string? detail, string? image, double width, double imageHeight)
    {
        var body = new StackPanel { Spacing = 6 };

        if (image is not null)
        {
            body.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                Height = imageHeight,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x26, 0x00, 0x00, 0x00)),
                Child = new Image
                {
                    Stretch = Stretch.UniformToFill,
                    VerticalAlignment = VerticalAlignment.Top,
                    Source = new BitmapImage(new Uri(image)) { DecodePixelWidth = (int)(width * 2) },
                },
            });
        }

        body.Children.Add(new TextBlock
        {
            Text = caption,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
            Margin = new Thickness(4, image is null ? 2 : 4, 4, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        if (detail is not null)
        {
            body.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 12,
                LineHeight = 17,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 0, 4, 4),
            });
        }

        var tile = new Border
        {
            Width = width,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(2),
            Padding = new Thickness(6),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x13, 0xFF, 0xFF, 0xFF)),
            Child = body,
        };
        tile.Tapped += (_, _) => Pick(value);

        _tiles.Add((tile, value));
        host.Children.Add(tile);
        Paint();
    }

    private void Pick(T value)
    {
        Picked = value;
        Paint();
    }

    private void Paint()
    {
        foreach (var (tile, value) in _tiles)
            tile.BorderBrush = EqualityComparer<T>.Default.Equals(value, Picked) ? Ring : Rim;
    }
}
