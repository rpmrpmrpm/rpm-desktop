using RPM.Desktop.Services;
using RPM.Desktop.Services.Platform;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace RPM.Desktop.Presentation.Common;

/// <summary>Gives every text box a working right-click menu and a working Ctrl+V.
///
/// <para>WinUI's TextBox has a built-in cut, copy and paste flyout, and its own clipboard shortcuts.
/// Uno implements TextBox separately for each host, and the Skia hosts do not agree. On macOS the menu
/// and the shortcuts both work. On the Win32 Skia host the right-click menu does not appear and paste
/// does not arrive, but copy does. That makes it look like a clipboard fault rather than a missing
/// menu. Copy only needs the control to read its own selection. Paste needs the key to reach the
/// control.</para>
///
/// <para>The cause of the dead paste is repaired at startup by <see cref="OemCodePageFix"/>. So the
/// Ctrl+V fallback here is installed only when that repair could not be made. The menu is always this
/// class's own. It is built for each text box when it is opened, and styled like every other menu in
/// the app. It does not depend on what each host's TextBox provides.</para>
/// </summary>
public static class TextBoxClipboard
{
    /// <summary>Set true on a TextBox, via the implicit style in Generic.xaml, to attach the menu
    /// and the shortcut handling.</summary>
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(TextBoxClipboard),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject o) => (bool)o.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject o, bool value) => o.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
            return;

        box.RemoveHandler(UIElement.KeyDownEvent, (KeyEventHandler)OnKeyDown);

        if (e.NewValue is not true)
            return;

        // Windows only, on both counts. macOS's TextBox already has a working menu and a working paste, and
        // replacing them there would only add risk.
        if (!Win32Clipboard.IsNeeded)
            return;

        // Only needed while the host's own paste is still broken. Uno's TextBox claims Ctrl+V and marks
        // the key handled before its paste throws inside the clipboard. So the keystroke is seen only
        // by listening with handledEventsToo. If OemCodePageFix has made the clipboard readable, this
        // handler is not added, because it would paste a second time on top of the normal paste.
        if (!OemCodePageFix.CodePageResolves)
            box.AddHandler(UIElement.KeyDownEvent, (KeyEventHandler)OnKeyDown, handledEventsToo: true);

        // ContextFlyout rather than the ContextRequested event. Uno's TextBox raises and consumes that
        // event to show its own flyout, so a handler cannot show a different menu. A flyout assigned to
        // the control is what it shows instead. There is one instance per box, because a FlyoutBase
        // attaches to a single element at a time.
        var menu = new MenuFlyout();
        menu.Opening += (_, _) => BuildMenu(menu, box);
        box.ContextFlyout = menu;
    }

    // ── the menu ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Fills the menu when it opens, so each item reflects the selection and the clipboard as
    /// they are at this click, not as they were when the box was created.</summary>
    private static void BuildMenu(MenuFlyout menu, TextBox box)
    {
        var hasSelection = box.SelectionLength > 0;
        var hasText = box.Text.Length > 0;
        var canPaste = !box.IsReadOnly && Win32Clipboard.HasText();

        menu.Items.Clear();
        // Undo and Redo come first, as in every Windows text box. The host's own menu had them, so this
        // one does too.
        menu.Items.Add(Item("Clipboard_Undo", "\uf0e2", box.CanUndo && !box.IsReadOnly, () => box.Undo()));
        menu.Items.Add(Item("Clipboard_Redo", "\uf01e", box.CanRedo && !box.IsReadOnly, () => box.Redo()));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Clipboard_Cut", "\uf0c4", hasSelection && !box.IsReadOnly, () =>
        {
            SetClipboardText(box.SelectedText);
            box.SelectedText = "";
        }));
        menu.Items.Add(Item("Clipboard_Copy", "\uf0c5", hasSelection, () => SetClipboardText(box.SelectedText)));
        menu.Items.Add(Item("Clipboard_Paste", "\uf0ea", canPaste, () => _ = PasteIntoAsync(box)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Clipboard_SelectAll", "\uf247", hasText, box.SelectAll));
    }

    private static MenuFlyoutItem Item(string locKey, string glyph, bool enabled, Action action)
    {
        var item = new MenuFlyoutItem
        {
            Text = Loc.Get(locKey), Icon = FaIcon(glyph), IsEnabled = enabled,
            FontSize = 12, MinHeight = 26, Padding = new Thickness(11, 3, 11, 4),
        };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Matches the menu icons the rest of the app draws. FaMenuIcon (in Generic.xaml) boxes
    /// the glyph in 16x16. That stops the row's Viewbox from stretching each glyph to a different
    /// size.</summary>
    private static FontIcon FaIcon(string glyph) => new()
    {
        Glyph = glyph,
        Style = (Style)Application.Current.Resources["FaMenuIcon"],
    };

    // ── the shortcut ─────────────────────────────────────────────────────────────────────────────

    private static void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Not gated on e.Handled, on purpose. Uno's TextBox has already claimed this key and failed to
        // paste (see Win32Clipboard). This handler runs only in that case.
        if (sender is not TextBox box || box.IsReadOnly)
            return;

        if (e.Key != VirtualKey.V)
            return;

        if (!HeldKeys.Now.HasFlag(Modifier.Control))
            return;

        e.Handled = true;
        _ = PasteIntoAsync(box);
    }

    // ── the clipboard itself ─────────────────────────────────────────────────────────────────────

    private static void SetClipboardText(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        // Another process may own the clipboard during the copy. Then SetContent throws instead of
        // returning false. A failed copy must not bring down the dialog.
        try { Clipboard.SetContent(package); } catch { }
    }

    /// <summary>The clipboard's text, from whichever path works on this host.</summary>
    private static async Task<string?> ClipboardTextAsync()
    {
        if (Win32Clipboard.IsNeeded)
            return Win32Clipboard.GetText();

        try
        {
            var content = Clipboard.GetContent();
            if (!content.Contains(StandardDataFormats.Text))
                return null;
            return await content.GetTextAsync();
        }
        catch { return null; }
    }

    /// <summary>Replaces the selection with the clipboard text. The caret is left after the pasted
    /// text, as it would be after typing the same characters.</summary>
    private static async Task PasteIntoAsync(TextBox box)
    {
        var text = await ClipboardTextAsync();
        if (string.IsNullOrEmpty(text))
            return;

        // A single-line box takes a multi-line paste as one line. It does not drop everything after the
        // first newline. A user who pastes a copied title wants the whole title on one line.
        if (!box.AcceptsReturn)
            text = text.ReplaceLineEndings(" ").Trim();

        if (box.MaxLength > 0)
        {
            var room = box.MaxLength - (box.Text.Length - box.SelectionLength);
            if (room <= 0) return;

            // Cut to fit, but never through a character. An emoji is two chars long, and a box that
            // holds half of one would break the app. See TextBoxSurrogates.Fits.
            if (text.Length > room)
                text = text[..TextBoxSurrogates.Fits(text, room)];
        }

        var start = box.SelectionStart;
        box.SelectedText = text;
        box.SelectionStart = start + text.Length;
        box.SelectionLength = 0;
    }
}
