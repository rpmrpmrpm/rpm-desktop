using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Diagnostics;
using RPM.Desktop.Services.Audio;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// The message shown when a play is refused because this computer's sound output is not working:
/// what is wrong in one sentence, then the steps for that cause, numbered as the problem screen numbers
/// them (<c>LibraryProblemPage</c>), and a button to the system's sound settings where they can help.
/// </summary>
public static class OutputTroubleDialog
{
    /// <summary>Shows the message and, when asked, opens the system's sound settings. Returns once the
    /// message is closed. Must be called on the UI thread, like every dialog
    /// (<see cref="DialogHost"/>).</summary>
    /// <param name="root">The tree to show over. Null shows nothing.</param>
    public static async Task ShowAsync(XamlRoot? root, OutputTrouble trouble)
    {
        var advice = OutputHelp.For(trouble, OutputHelp.Here);

        var answer = await PromptDialog.ShowAsync(
            root,
            Loc.Get("AudioOut_Title"),
            Body(advice, trouble),
            primaryText: advice.SettingsUri is null ? null : Loc.Get("AudioOut_OpenSoundSettings"),
            closeText: Loc.Get("Common_Ok"),
            defaultButton: ContentDialogButton.Close);

        if (answer == ContentDialogResult.Primary && advice.SettingsUri is { } uri)
            OpenSettings(uri);
    }

    private static StackPanel Body(OutputHelp.Advice advice, OutputTrouble trouble)
    {
        var body = new StackPanel { Spacing = 14 };

        var sentence = trouble is OutputTrouble.Refused { Device.Length: > 0 } refused
            ? Loc.Format(advice.Sentence, refused.Device)
            : Loc.Get(advice.Sentence);
        body.Children.Add(Text(sentence, 13.5, 0xD9));

        var heading = Text(Loc.Get("LibProblem_StepsHeading"), 11, 0x8F);
        heading.CharacterSpacing = 120;
        heading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;

        var steps = new StackPanel { Spacing = 10 };
        for (int i = 0; i < advice.Steps.Count; i++)
            steps.Children.Add(Step(i + 1, StepText(advice.Steps[i])));

        var trying = new StackPanel { Spacing = 12 };
        trying.Children.Add(heading);
        trying.Children.Add(steps);
        body.Children.Add(trying);

        return body;
    }

    /// <summary>A step's words. The one that names rpm's own settings is given the section's name from
    /// the table, so the two cannot drift apart in any language.</summary>
    private static string StepText(string key) => key == "AudioOut_StepPickOther"
        ? Loc.Format(key, Loc.Get("Settings_Sound"))
        : Loc.Get(key);

    /// <summary>One numbered step, drawn the same way as the steps on the problem screen.</summary>
    private static Grid Step(int number, string text)
    {
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x26, 0x04, 0xC8, 0xFF)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0x04, 0xC8, 0xFF)),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["RpmCyanBrush"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        Grid.SetColumn(badge, 0);
        grid.Children.Add(badge);

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

    /// <summary>Opens the system's sound page. The shell opens these URI schemes on Windows and on macOS
    /// (where it uses <c>open</c>). The supporter links rely on the same behaviour.</summary>
    private static void OpenSettings(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Faults.Note("audio", "opening the sound settings", e);
        }
    }
}
