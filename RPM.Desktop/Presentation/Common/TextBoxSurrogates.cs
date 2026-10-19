using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Common;

/// <summary>Lets a character too big for one keystroke be typed into a text box, instead of ending the
/// app.
///
/// <para><b>What arrives.</b> Windows sends one WM_CHAR for each UTF-16 code unit. A character outside
/// the basic plane, such as an emoji or a mathematical italic letter, is typed as TWO keystrokes: first
/// the high half, then the low half. The Win32 Skia host passes each keystroke on by itself. Uno's
/// TextBox inserts each one by itself too. So after the first, the box holds half a character, and it
/// is measured before the other half arrives. Measuring runs <c>Char.ConvertToUtf32</c> over the line,
/// and that throws on a half. The throw happens inside a key event and inside the window procedure,
/// where an unhandled managed exception ends the process. One emoji typed into any field closes the
/// app.</para>
///
/// <para><b>What this does.</b> It refuses the half and holds it. When the other half arrives, it
/// writes the WHOLE character. So the box is never asked to measure a fragment, and the character the
/// typist meant to type appears. Text that has a half with nothing to pair with, such as a paste cut
/// through a character, loses that fragment. Dropping it is the only sensible result for half a
/// character.</para>
///
/// <para><b>It works through the coercion, not the keystroke.</b> <c>BeforeTextChanging</c> is raised
/// while the Text property is being coerced. That is before the value is stored and before anything is
/// drawn. It is raised for every route into the box: typing, pasting, and the app's own assignments.
/// The keystroke cannot be used. The character it carries is internal to Uno, and by the time a handler
/// of ours could see the key, the insert has already happened.</para>
///
/// <para>It is attached by the implicit TextBox style in Generic.xaml, as <see
/// cref="TextBoxClipboard"/> is. The keyed styles that replace the implicit style set it again.</para>
/// </summary>
public static class TextBoxSurrogates
{
    /// <summary>Set true on a TextBox, through the implicit style in Generic.xaml, to hold half
    /// characters back until they are whole.</summary>
    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(TextBoxSurrogates),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject o) => (bool)o.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject o, bool value) => o.SetValue(IsEnabledProperty, value);

    /// <summary>The half this box is holding, if it is in the middle of a character. It is stored on
    /// the box and not in one static field. The focused box is not the only one whose text changes,
    /// because the app writes to other boxes while someone types. A half belongs to the box it was
    /// typed into.</summary>
    private static readonly DependencyProperty HeldProperty =
        DependencyProperty.RegisterAttached(
            "Held", typeof(object), typeof(TextBoxSurrogates), new PropertyMetadata(null));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box)
            return;

        box.BeforeTextChanging -= OnBeforeTextChanging;

        if (e.NewValue is true)
            box.BeforeTextChanging += OnBeforeTextChanging;
    }

    private static void OnBeforeTextChanging(TextBox box, TextBoxBeforeTextChangingEventArgs args)
    {
        var read = Read(args.NewText, box.GetValue(HeldProperty) as char?);
        box.SetValue(HeldProperty, read.Held);

        // Whole characters all the way through, which is nearly every change ever made to a text box.
        if (string.Equals(read.Text, args.NewText, StringComparison.Ordinal))
            return;

        args.Cancel = true;

        // The half is being held and there is nothing else to say yet: the box keeps the text it had,
        // and the character appears when its other half is typed.
        if (string.Equals(read.Text, box.Text, StringComparison.Ordinal))
            return;

        // The text is written on the next dispatcher tick. This method runs during the coercion of the Text
        // property, and assigning Text here would re-enter the change that is being refused.
        var text = read.Text;
        var caret = read.Caret;
        box.DispatcherQueue?.TryEnqueue(() =>
        {
            box.Text = text;
            if (caret >= 0 && caret <= box.Text.Length)
            {
                box.SelectionStart = caret;
                box.SelectionLength = 0;
            }
        });
    }

    /// <summary>How much of <paramref name="text"/> fits in <paramref name="room"/> characters without
    /// cutting through one.
    /// </summary>
    /// <remarks>A character outside the basic plane is two chars long. A box left holding half of one
    /// is what this file exists to prevent. So a cut that would land between the two halves takes one
    /// char less. A paste that is one char shorter still fits. A paste that splits a character would
    /// break the app.
    /// </remarks>
    internal static int Fits(string text, int room)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (room >= text.Length)
            return text.Length;
        if (room <= 0)
            return 0;

        return char.IsHighSurrogate(text[room - 1]) ? room - 1 : room;
    }

    /// <param name="Text">The text as it may safely stand. Every character in it is whole.</param>
    /// <param name="Held">The half to keep back, if the text carried one. This is the first keystroke
    /// of a character whose second keystroke is still to come.</param>
    /// <param name="Caret">Where the caret belongs once <paramref name="Text"/> is written: after the
    /// character just completed. Minus one when nothing was completed, which means the caret stays
    /// where it is.</param>
    internal readonly record struct Repaired(string Text, char? Held, int Caret);

    /// <summary>Reads one proposed text, with whatever half the box was holding.
    /// </summary>
    /// <remarks>The scan is skipped for text with no halves anywhere in it. That is every line of
    /// every ordinary edit. This runs on every keystroke in every box in the app, so the ordinary case
    /// has to cost one vectorised search and nothing more.
    /// </remarks>
    internal static Repaired Read(string? proposed, char? held)
    {
        var text = proposed ?? "";

        if (held is null && !text.AsSpan().ContainsAnyInRange('\uD800', '\uDFFF'))
            return new Repaired(text, null, -1);

        var whole = new StringBuilder(text.Length + 1);
        char? hold = null;
        var caret = -1;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (char.IsHighSurrogate(c))
            {
                // A whole character, written as it was found.
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    whole.Append(c).Append(text[i + 1]);
                    i++;
                    continue;
                }

                // A half on its own is the first keystroke of a character being typed. It is held
                // rather than written, so the box never measures a fragment. It is held at any position
                // in the text, not only at the end. Titles are often edited in the middle, and the rest
                // of the line then follows the held half.
                hold = c;
                continue;
            }

            if (char.IsLowSurrogate(c))
            {
                // The second keystroke. The character is whole now, and it is written where it was typed.
                if (held is { } first)
                {
                    whole.Append(first).Append(c);
                    caret = whole.Length;
                    held = null;
                    continue;
                }

                continue;   // a second half with no first half is not a character
            }

            whole.Append(c);
        }

        return new Repaired(whole.ToString(), hold, caret);
    }
}
