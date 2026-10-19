using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Library;
using Windows.UI;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// Draws a line's bracketed asides at seven tenths of the ink the rest of it is written in:
/// <c>ui:AsideInk.Line="{Binding Title}"</c> in place of <c>Text="{Binding Title}"</c>.
///
/// <para><b>Why an attached property and not a converter.</b> A converter returns a string, and a string
/// is drawn in one colour. Two colours are needed on one line. It has to be one line, because these are
/// trimmed, capped and ellipsised lines in a virtualising list. Two TextBlocks side by side would each trim
/// to their own width, and the ellipsis would appear in the wrong half.</para>
///
/// <para><b>The cost, and when it applies.</b> A line with no aside to fade is given plain
/// <see cref="TextBlock.Text"/>. That is the same single string as before, with no inlines and no extra
/// objects. Only a line that has an aside creates runs. On a wall with a few hundred lines on screen, this
/// difference is the reason the check is done here and not in the markup.</para>
///
/// <para><b>What it masks.</b> A line passes through here on its way to the screen, and nowhere else. So
/// the rule that the lyric sheet uses to mask swear words is applied here too. See
/// <see cref="TitleCensor"/>. If a track list showed a word in full while the lyric sheet stars it, one
/// screen would disagree with another. The models on either side of this still hold the name as it was
/// tagged.</para>
///
/// <para><b>Where the faint colour comes from.</b> It comes from the block's own
/// <see cref="TextBlock.Foreground"/>, with its alpha multiplied. The colour is not fixed. These lines use
/// four different inks across the two views. A fixed grey would be lighter than the name in one view and
/// darker in another. Reading the ink when the value is applied is safe for this reason. Each call site
/// sets a literal brush while XAML parses the element. The text arrives later, through a <c>{Binding}</c>,
/// because it cannot resolve until the DataContext is set. If the ink is not a flat colour, the block gets
/// the plain string at full strength. That is legible, and it avoids guessing how much to fade it.</para>
/// </summary>
public static class AsideInk
{
    /// <summary>The share of the line's ink that is kept for an aside, which is only a note about the name.
    /// Seven tenths: this is faint enough that a reader can scan down the names past a venue repeated on
    /// every line. It is still readable when the reader wants it.</summary>
    private const double Faint = 0.7;

    public static readonly DependencyProperty LineProperty = DependencyProperty.RegisterAttached(
        "Line", typeof(string), typeof(AsideInk), new PropertyMetadata(null, OnLineChanged));

    public static string GetLine(TextBlock block) => (string)block.GetValue(LineProperty);

    public static void SetLine(TextBlock block, string value) => block.SetValue(LineProperty, value);

    private static void OnLineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
            return;

        // The mask is applied before the line is split, not after. The mask keeps the punctuation and
        // the length of the line, so the brackets that mark an aside are still in the same place. See
        // TitleCensor. This is one of the two routes a title takes to the screen. That is why the rule is
        // applied here, and not at each of the four call sites.
        var text = TitleCensor.Shown(e.NewValue as string) ?? "";
        var parts = TitleAsides.Split(text);

        // This covers the common line and a recycled container. Setting Text replaces any inlines that
        // the container still holds from the last item it showed. Without that, a row that showed an
        // aside would keep it after it is reused for a track that has none.
        if (parts.Count <= 1 || Faded(block) is not { } faint)
        {
            block.Text = text;
            return;
        }

        block.Inlines.Clear();
        foreach (var part in parts)
        {
            var run = new Run { Text = part.Text };
            if (part.Aside)
                run.Foreground = faint;
            block.Inlines.Add(run);
        }
    }

    /// <summary>The block's ink at <see cref="Faint"/>, or null when the ink is not a flat colour. In that
    /// case there is no single colour to take a fraction of.</summary>
    private static Brush? Faded(TextBlock block)
    {
        if (block.Foreground is not SolidColorBrush ink)
            return null;

        var colour = ink.Color;
        var faded = Color.FromArgb((byte)Math.Round(colour.A * Faint), colour.R, colour.G, colour.B);

        // Brushes are kept, not made for each line. The two views draw in a few inks between them.
        // Without this, each of a few hundred lines would create its own brush for one of about four
        // colours. The lock is needed because containers are realised off the UI thread, in bursts.
        lock (Kept)
        {
            if (Kept.TryGetValue(faded, out var kept))
                return kept;

            var brush = new SolidColorBrush(faded);
            Kept[faded] = brush;
            return brush;
        }
    }

    private static readonly Dictionary<Color, SolidColorBrush> Kept = [];
}
