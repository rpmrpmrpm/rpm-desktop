using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Services.Diagnostics;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Shell;

/// <summary>
/// The shell's part in feedback a user writes: the Send feedback item in the menu, the window it
/// opens, and the screenshot that goes with it. The window takes both a problem and a suggestion,
/// which is what the kind list at the top of it picks.
/// </summary>
public sealed partial class ShellPage
{
    /// <summary>
    /// Completed when the hamburger menu has gone from the screen.
    ///
    /// <para>The menu's Click arrives while the menu is still up, so a shot taken there has the menu
    /// over the app. Waiting on this task is order-independent: the menu's Closed handler completes
    /// it, and a task that is already completed returns at once.</para>
    /// </summary>
    private TaskCompletionSource? _menuGone;

    private void Menu_Closed(object sender, object e) => _menuGone?.TrySetResult();

    private async void Feedback_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is not { } root)
            return;

        var png = await ShotWithoutTheMenuAsync();
        await ReportAsync(root, png);
    }

    /// <summary>The app as the tester had it, with the menu gone. Null when the frame could not be
    /// read; the window then offers no picture.</summary>
    private async Task<byte[]?> ShotWithoutTheMenuAsync()
    {
        if (_menuGone is { Task: var gone })
            await gone;

        // Closed fires while the menu is still on the last drawn frame. One more layout pass puts the
        // app on screen without it, which is what the shot has to show.
        await Task.Yield();

        return await WindowShot.PngAsync(this);
    }

    /// <summary>
    /// Asks what the user wants to say and sends it.
    ///
    /// <para>The message is the report: a queue of rows that say only "something is broken" cannot be
    /// worked through, so an empty box is refused here and the window can be opened again. Everything
    /// else travels because the user left it on.</para>
    /// </summary>
    private async Task ReportAsync(XamlRoot root, byte[]? png)
    {
        var kinds = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 0,
        };
        foreach (var (_, key, _) in FeedbackKind.All)
            kinds.Items.Add(Loc.Get(key));

        var areas = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 0,
        };
        foreach (var (_, key) in FeedbackArea.All)
            areas.Items.Add(Loc.Get(key));

        var message = new TextBox
        {
            PlaceholderText = Loc.Get(FeedbackKind.All[0].PlaceholderKey),
            // This is prose a person reads, so it is spell checked. The implicit style turns spell
            // check off for the name-and-path fields that make up the rest of the app.
            IsSpellCheckEnabled = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 120,
            MaxLength = 4000,
        };

        // The question in the empty box follows the kind, so "What went wrong?" is never left over
        // the box after the user has said they are suggesting something.
        kinds.SelectionChanged += (_, _) =>
            message.PlaceholderText = Loc.Get(Chosen(kinds, FeedbackKind.All).PlaceholderKey);

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(BodyCopy(Loc.Get("Feedback_Body")));
        body.Children.Add(Label(Loc.Get("Feedback_KindLabel")));
        body.Children.Add(kinds);
        body.Children.Add(Label(Loc.Get("Feedback_AreaLabel")));
        body.Children.Add(areas);
        body.Children.Add(message);

        // The switch is only offered when there is a picture to offer. A switch over an empty box
        // would ask the tester about something that is not there.
        CheckBox? include = null;
        if (png is not null)
        {
            include = new CheckBox { Content = Loc.Get("Feedback_Screenshot"), IsChecked = true };
            body.Children.Add(include);

            if (await PreviewAsync(png) is { } preview)
                body.Children.Add(preview);
        }

        body.Children.Add(BodyCopy(Loc.Get("Feedback_Sends"), dim: true));

        // Only a tester's account opens this window, so there is always an address to reply to.
        if (Auth?.Email is { Length: > 0 } email)
            body.Children.Add(BodyCopy(Loc.Format("Feedback_RepliesTo", email), dim: true));

        var answer = await PromptDialog.ShowAsync(
            root,
            Loc.Get("Feedback_Title"),
            body,
            primaryText: Loc.Get("Feedback_Send"),
            closeText: Loc.Get("Common_Cancel"),
            defaultButton: ContentDialogButton.Primary);

        if (answer != ContentDialogResult.Primary)
            return;

        var words = message.Text.Trim();
        if (words.Length == 0)
        {
            await Note(root, Loc.Get("Feedback_NeedWords"));
            return;
        }

        var kind = Chosen(kinds, FeedbackKind.All).Value;
        var area = Chosen(areas, FeedbackArea.All).Value;
        var picture = include?.IsChecked == true ? png : null;

        var feedback = App.Services.GetRequiredService<FeedbackService>();
        var sent = await feedback.SendAsync(kind, area, words, picture, activity: true, CancellationToken.None);

        await Note(root, Loc.Get(sent ? "Feedback_Sent" : "Feedback_Failed"));
    }

    /// <summary>The row a list is showing. A ComboBox reports -1 before anything is picked and while
    /// its items are being filled, so the first row is the answer when the index is not a row.</summary>
    private static T Chosen<T>(ComboBox list, T[] rows) =>
        rows[Math.Clamp(list.SelectedIndex, 0, rows.Length - 1)];

    /// <summary>The shot, small, so the tester can see what is about to be sent.</summary>
    private static async Task<Image?> PreviewAsync(byte[] png)
    {
        try
        {
            var source = new BitmapImage { DecodePixelWidth = 320 };
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await stream.WriteAsync(png.AsBuffer());
            stream.Seek(0);
            source.SetSource(stream);

            return new Image
            {
                Source = source,
                Stretch = Stretch.Uniform,
                MaxHeight = 200,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            // The report does not need the preview. The picture still travels.
            return null;
        }
    }

    private static TextBlock BodyCopy(string text, bool dim = false) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = dim ? 12 : 13,
        Foreground = new SolidColorBrush(dim
            ? Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)
            : Windows.UI.Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
    };

    private static TextBlock Label(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
        Margin = new Thickness(0, 2, 0, -6),
    };

    private static Task Note(XamlRoot root, string text) =>
        PromptDialog.ShowAsync(root, title: null, text, closeText: Loc.Get("Common_OK"));
}
