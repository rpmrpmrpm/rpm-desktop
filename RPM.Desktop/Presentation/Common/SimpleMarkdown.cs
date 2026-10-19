using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// A deliberately small Markdown renderer. It is enough to make an album review easy to read, without
/// a full Markdown engine. It handles headings (<c>#</c> to <c>###</c>), bullet and numbered lists,
/// blank-line paragraph breaks, inline <c>**bold**</c>, <c>*italic*</c> and <c>_italic_</c>, and
/// <c>`code`</c>. Anything it does not recognise is shown as it is, so a review is never mangled.
///
/// It writes into a <see cref="TextBlock"/> instead of a RichTextBlock. Uno does not implement
/// RichTextBlock, and its Blocks never reach the screen, so a review rendered into one is blank. A
/// TextBlock's inlines have the same per-run weight, style and size. Only Block.Margin is lost, and a
/// line break stands in for the paragraph gap.
/// </summary>
public static partial class SimpleMarkdown
{
    /// <summary>Renders <paramref name="markdown"/> into <paramref name="target"/>, replacing whatever
    /// it held.</summary>
    public static void Render(TextBlock target, string? markdown)
    {
        ArgumentNullException.ThrowIfNull(target);

        target.Inlines.Clear();
        target.TextWrapping = TextWrapping.Wrap;

        var lines = (markdown ?? "").Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            // Every line but the first opens with the break that ended the one before it, so a blank
            // line in the source becomes the empty line that separates two paragraphs.
            if (i > 0)
                target.Inlines.Add(new LineBreak());

            var line = lines[i].TrimEnd();
            if (line.Length == 0)
                continue;

            if (HeadingRe().Match(line) is { Success: true } h)
            {
                var level = h.Groups["h"].Value.Length;   // 1..3
                target.Inlines.Add(new Run
                {
                    Text = h.Groups["t"].Value,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = level == 1 ? 20 : level == 2 ? 17 : 15,
                });
                continue;
            }

            if (BulletRe().Match(line) is { Success: true } b)
            {
                target.Inlines.Add(new Run { Text = "•   " });
                AppendInlines(target.Inlines, b.Groups["t"].Value);
                continue;
            }

            if (NumberRe().Match(line) is { Success: true } n)
            {
                target.Inlines.Add(new Run { Text = $"{n.Groups["n"].Value}.   " });
                AppendInlines(target.Inlines, n.Groups["t"].Value);
                continue;
            }

            AppendInlines(target.Inlines, line);
        }
    }

    /// <summary>Splits a line into styled runs on <c>**bold**</c>, <c>*italic*</c>/<c>_italic_</c> and
    /// <c>`code`</c>, leaving anything unmatched as plain text.</summary>
    private static void AppendInlines(InlineCollection inlines, string text)
    {
        int pos = 0;
        foreach (Match m in InlineRe().Matches(text))
        {
            if (m.Index > pos)
                inlines.Add(new Run { Text = text[pos..m.Index] });

            if (m.Groups["b"].Success)
                inlines.Add(new Run { Text = m.Groups["b"].Value, FontWeight = FontWeights.SemiBold });
            else if (m.Groups["c"].Success)
                inlines.Add(new Run
                {
                    Text = m.Groups["c"].Value,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas, monospace"),
                });
            else if (m.Groups["i"].Success)
                inlines.Add(new Run { Text = m.Groups["i"].Value, FontStyle = Windows.UI.Text.FontStyle.Italic });
            else if (m.Groups["u"].Success)
                inlines.Add(new Run { Text = m.Groups["u"].Value, FontStyle = Windows.UI.Text.FontStyle.Italic });

            pos = m.Index + m.Length;
        }
        if (pos < text.Length)
            inlines.Add(new Run { Text = text[pos..] });
    }

    [GeneratedRegex(@"^(?<h>#{1,3})\s+(?<t>.+)$")]
    private static partial Regex HeadingRe();

    [GeneratedRegex(@"^\s*[-*]\s+(?<t>.+)$")]
    private static partial Regex BulletRe();

    [GeneratedRegex(@"^\s*(?<n>\d+)\.\s+(?<t>.+)$")]
    private static partial Regex NumberRe();

    [GeneratedRegex(@"\*\*(?<b>.+?)\*\*|`(?<c>.+?)`|\*(?<i>.+?)\*|_(?<u>.+?)_")]
    private static partial Regex InlineRe();
}
