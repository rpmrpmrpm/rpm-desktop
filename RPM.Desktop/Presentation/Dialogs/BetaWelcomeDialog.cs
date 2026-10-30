using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// The note that greets a new closed-beta tester once setup is done: a thank you, what the beta asks and
/// gives, and that the tour of the app follows. Shown once, over the library the wizard opened on (see
/// <c>MainPage.AfterSetup</c>). The first-run hints wait for it to close, so the tour starts as the note
/// says it will.
/// </summary>
public static class BetaWelcomeDialog
{
    /// <summary>Shows the note. Returns once it is closed. Must be called on the UI thread.</summary>
    public static Task ShowAsync(XamlRoot? root) => PromptDialog.ShowAsync(
        root,
        Loc.Get("Beta_WelcomeTitle"),
        Body(),
        primaryText: Loc.Get("Beta_WelcomeTour"),
        defaultButton: ContentDialogButton.Primary);

    private static StackPanel Body()
    {
        var body = new StackPanel { Spacing = 16, MaxWidth = 520 };

        body.Children.Add(Text(Loc.Get("Beta_WelcomeIntro"), 13.5, 0xD9));

        var heading = Text(Loc.Get("Beta_WelcomeWhatHeading"), 11, 0x8F);
        heading.CharacterSpacing = 120;
        heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;

        var points = new StackPanel { Spacing = 10 };
        points.Children.Add(Point("",Loc.Get("Beta_WelcomeUse")));
        points.Children.Add(Point("", Loc.Get("Beta_WelcomeTell")));
        points.Children.Add(Point("", Loc.Get("Beta_WelcomeGet")));

        var what = new StackPanel { Spacing = 12 };
        what.Children.Add(heading);
        what.Children.Add(points);
        body.Children.Add(what);

        body.Children.Add(Text(Loc.Get("Beta_WelcomeTourLine"), 13.5, 0xD9));
        return body;
    }

    /// <summary>One point: a glyph in the app's cyan, and its sentence beside it.</summary>
    private static Grid Point(string glyph, string text)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new FontIcon
        {
            Style = (Style)Application.Current.Resources["FaIcon"],
            Glyph = glyph,
            FontSize = 14,
            Foreground = (Brush)Application.Current.Resources["RpmCyanBrush"],
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 3, 0, 0),
        });

        var words = Text(text, 13.5, 0xCC);
        words.LineHeight = 21;
        Grid.SetColumn(words, 1);
        grid.Children.Add(words);
        return grid;
    }

    private static TextBlock Text(string text, double size, byte alpha) => new()
    {
        Text = text,
        FontSize = size,
        TextWrapping = TextWrapping.Wrap,
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF)),
    };
}
