using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// The small questions: OK, OK/Cancel, and "are you sure" before something is thrown away. They are drawn
/// in the app's own style, not in the style of the framework dialog.
///
/// <para>
/// The look matches the view chooser: a dark card with 8 px corners, a darker band across the top with
/// the question's name in it, the body on the card, and the app's own buttons at the bottom. The stock
/// ContentDialog underneath is transparent. It is kept only for the backdrop, the modal wait and the
/// light-dismiss handling. Its own buttons are not used, so the answer is recorded here.
/// <see cref="ShowAsync(Microsoft.UI.Xaml.XamlRoot?, string?, object?, string?, string?, string?, ContentDialogButton)"/>
/// returns that answer. It does not return what <c>ContentDialog.ShowAsync</c> gives back, which is
/// always <c>None</c> for a dialog closed by <c>Hide</c>.
/// </para>
/// </summary>
public sealed partial class PromptDialog : ContentDialog
{
    private ContentDialogResult _result = ContentDialogResult.None;
    private ContentDialogButton _default = ContentDialogButton.Close;

    /// <summary>The typed confirmation this dialog is asking, or null for an ordinary question.</summary>
    private Typed? _typed;

    /// <summary>True while the typed confirmation's action runs. The dialog cannot be closed meanwhile,
    /// because the action has already started and its answer is what the user waits for.</summary>
    private bool _working;

    /// <param name="MustType">The text the box must contain. It is compared without case or spaces at the
    /// ends.</param>
    /// <param name="Act">The action. Returning null means success. Returning a message shows it in red
    /// under the box, and the dialog stays open for another try.</param>
    /// <param name="FailedText">Shown when the action throws instead of returning.</param>
    private sealed record Typed(
        string MustType, TextBox Box, TextBlock Error, string ActionText, string WorkingText,
        string FailedText, Func<CancellationToken, Task<string?>> Act)
    {
        public bool Matches() =>
            string.Equals(Box.Text.Trim(), MustType.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private PromptDialog()
    {
        InitializeComponent();
        Opened += OnOpened;
        KeyDown += OnKeyDown;
    }

    /// <summary>
    /// Shows the dialog and waits for an answer. It returns the button that ended the dialog. Escape and
    /// the close button both return <see cref="ContentDialogResult.None"/>. A caller checks for the
    /// answer it wants and treats anything else as no. This is the same result shape the framework dialog
    /// had.
    /// </summary>
    /// <param name="root">The tree to show over. Null means there is no window, so the call returns
    /// <c>None</c> without showing anything.</param>
    /// <param name="title">The text in the title band. Empty hides the band, for a note with no
    /// name.</param>
    /// <param name="body">A string is set as body copy. An element is placed as it is.</param>
    public static async Task<ContentDialogResult> ShowAsync(
        XamlRoot? root,
        [Shown] string? title,
        [Shown] object? body,
        [Shown] string? primaryText = null,
        [Shown] string? secondaryText = null,
        [Shown] string? closeText = null,
        ContentDialogButton defaultButton = ContentDialogButton.Close)
    {
        if (root is null)
            return ContentDialogResult.None;

        var dialog = new PromptDialog { XamlRoot = root, _default = defaultButton };

        if (string.IsNullOrEmpty(title))
            dialog.TitleBand.Visibility = Visibility.Collapsed;
        else
            dialog.TitleText.Text = title;

        // A bare string gets the app's body copy: 13 px, near-white, wrapping. Anything already built (a
        // panel of warnings, a text box to type in) is used as it is.
        dialog.BodyHost.Content = body is string text
            ? new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
            }
            : body;

        Fill(dialog.PrimaryButton, primaryText);
        Fill(dialog.SecondaryButton, secondaryText);
        Fill(dialog.CloseButton, closeText);

        await DialogHost.ShowAsync(dialog);
        return dialog._result;

        static void Fill(Button button, string? text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            button.Content = text;
            button.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// The "type it to confirm" question, for actions that cannot be undone, such as deleting an account.
    /// </summary>
    /// <remarks>
    /// <para>The action's button is red and stays disabled until the box holds
    /// <paramref name="mustType"/>. When the user presses it, or presses Enter, <paramref name="act"/>
    /// runs. The dialog stays open meanwhile, and the button shows <paramref name="workingText"/>.
    /// Success closes the dialog. A failure is shown under the box. Escape and the cancel button do
    /// nothing while the action runs.</para>
    /// <para>Enter runs the action here. In ordinary questions Enter picks the default button, which is
    /// usually Cancel. Here the user has just typed the requested text, so Enter confirms it.</para>
    /// </remarks>
    /// <returns>True when the action ran and succeeded.</returns>
    public static async Task<bool> ConfirmByTypingAsync(
        XamlRoot? root,
        string title,
        string body,
        string typePrompt,
        string mustType,
        string actionText,
        string workingText,
        string failedText,
        string cancelText,
        Func<CancellationToken, Task<string?>> act)
    {
        if (root is null)
            return false;

        var dialog = new PromptDialog { XamlRoot = root };
        dialog.TitleText.Text = title;

        var box = new TextBox { PlaceholderText = mustType, IsSpellCheckEnabled = false };
        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["RpmRedBrush"],
            Visibility = Visibility.Collapsed,
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(BodyCopy(body));
        panel.Children.Add(BodyCopy(typePrompt));
        panel.Children.Add(box);
        panel.Children.Add(error);
        dialog.BodyHost.Content = panel;

        dialog._typed = new Typed(mustType, box, error, actionText, workingText, failedText, act);
        dialog.DangerButton.Content = actionText;
        dialog.DangerButton.Visibility = Visibility.Visible;
        dialog.CloseButton.Content = cancelText;
        dialog.CloseButton.Visibility = Visibility.Visible;

        box.TextChanged += (_, _) => dialog.DangerButton.IsEnabled = !dialog._working && dialog._typed.Matches();

        await DialogHost.ShowAsync(dialog);
        return dialog._result == ContentDialogResult.Primary;
    }

    /// <summary>The body copy a bare string gets: 13 px, near-white, wrapping.</summary>
    private static TextBlock BodyCopy(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 13,
        Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
            Windows.UI.Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)),
    };

    private async void Danger_Click(object sender, RoutedEventArgs e) => await RunTypedAsync();

    private async Task RunTypedAsync()
    {
        if (_typed is not { } typed || _working || !typed.Matches())
            return;

        _working = true;
        typed.Box.IsEnabled = false;
        CloseButton.IsEnabled = false;
        DangerButton.IsEnabled = false;
        DangerButton.Content = typed.WorkingText;
        typed.Error.Visibility = Visibility.Collapsed;

        string? failure;
        try { failure = await typed.Act(CancellationToken.None); }
        catch (Exception) { failure = typed.FailedText; }

        _working = false;

        if (failure is null)
        {
            _result = ContentDialogResult.Primary;
            Hide();
            return;
        }

        typed.Error.Text = failure;
        typed.Error.Visibility = Visibility.Visible;
        typed.Box.IsEnabled = true;
        CloseButton.IsEnabled = true;
        DangerButton.Content = typed.ActionText;
        DangerButton.IsEnabled = typed.Matches();
        typed.Box.Focus(FocusState.Programmatic);
    }

    /// <summary>The button that gets focus when the dialog opens, so Enter and Space work straight away.
    /// A typed confirmation puts focus in its text box instead.</summary>
    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        if (_typed is { } typed)
            typed.Box.Focus(FocusState.Programmatic);
        else
            LandingButton().Focus(FocusState.Programmatic);
    }

    private Button LandingButton() => _default switch
    {
        ContentDialogButton.Primary when PrimaryButton.Visibility == Visibility.Visible => PrimaryButton,
        ContentDialogButton.Secondary when SecondaryButton.Visibility == Visibility.Visible => SecondaryButton,
        _ when CloseButton.Visibility == Visibility.Visible => CloseButton,
        _ when PrimaryButton.Visibility == Visibility.Visible => PrimaryButton,
        _ => SecondaryButton,
    };

    // The keyboard behaviour every dialog needs. The framework handled it through its buttons, which this
    // dialog does not use. So Enter picks the default button and Escape closes the dialog. The exception
    // is a multi-line text box, where Enter is part of the text.
    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_typed is not null)
        {
            switch (e.Key)
            {
                case VirtualKey.Enter:
                    e.Handled = true;
                    _ = RunTypedAsync();
                    break;
                case VirtualKey.Escape:
                    e.Handled = true;
                    if (!_working) Answer(CloseButton);
                    break;
            }
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Enter when Typing() is not TextBox { AcceptsReturn: true }:
                e.Handled = true;
                Answer(LandingButton());
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                Answer(CloseButton);
                break;
        }
    }

    /// <summary>Whatever has keyboard focus, or null before the tree is ready.</summary>
    private object? Typing() => XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;

    private void Primary_Click(object sender, RoutedEventArgs e) => Answer(PrimaryButton);
    private void Secondary_Click(object sender, RoutedEventArgs e) => Answer(SecondaryButton);
    private void Close_Click(object sender, RoutedEventArgs e) => Answer(CloseButton);

    private void Answer(Button button)
    {
        _result =
            ReferenceEquals(button, PrimaryButton) ? ContentDialogResult.Primary :
            ReferenceEquals(button, SecondaryButton) ? ContentDialogResult.Secondary :
            ContentDialogResult.None;
        Hide();
    }
}
