using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// The note that greets a closed beta tester: a thank you, and what the beta asks and gives. Shown once
/// per account on each machine, over the library, after the server has said the account is marked as a
/// tester (see <c>ShellPage.OfferBetaWelcome</c>).
/// </summary>
public static class BetaWelcomeDialog
{
    /// <summary>Shows the note. Returns once it is closed. Must be called on the UI thread.</summary>
    public static Task ShowAsync(XamlRoot? root) => PromptDialog.ShowAsync(
        root,
        Loc.Get("Beta_WelcomeTitle"),
        Body(),
        primaryText: Loc.Get("Common_Ok"),
        defaultButton: ContentDialogButton.Primary);

    /// <summary>
    /// The account the note is due for, or null when it is due for nobody.
    ///
    /// <para>Due when someone is signed in, the standing says they are a closed beta tester, and this
    /// machine has not shown them the note yet. Every input is passed in, and none has a default, so the
    /// rule can be tested without a window, a settings file or a network.</para>
    /// </summary>
    /// <param name="accountId">The signed-in account, or null when nobody is signed in.</param>
    /// <param name="standing">What is believed about the account (<see cref="SupporterService.Standing"/>).
    /// Null when nothing is known yet, which is not a tester.</param>
    /// <param name="welcomed">The accounts already shown the note on this machine.</param>
    public static string? DueFor(
        string? accountId, SupporterService.Status? standing, IReadOnlyList<string> welcomed)
    {
        if (string.IsNullOrEmpty(accountId) || standing is not { IsTester: true })
            return null;

        return welcomed.Contains(accountId, StringComparer.Ordinal) ? null : accountId;
    }

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
