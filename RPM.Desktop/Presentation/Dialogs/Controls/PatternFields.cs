using RPM.Core.Import;
using RPM.Desktop.Presentation.Dialogs.Controls;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.RegularExpressions;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>One line of a preset list: what the pattern does, in words, and the pattern itself.
///
/// <para>This type is public and at namespace level, like <see cref="TagPreviewRow"/>. The binding
/// engine's generated metadata only covers types declared at namespace level.</para>
/// </summary>
[RpmBindable]
public sealed partial record TagPresetRow(string Name, string Pattern);

/// <summary>A window that asks for a pattern has two jobs for the person typing it. It shows the
/// available fields as buttons that write a token into the box, instead of a text list of them. It
/// shows a preset in the app's own field names, not in braces.
///
/// <para>This is shared by the tags-from-filename and the tags-from-tags windows. They are the same
/// window with two sources. One reads its pattern from the file name, and the other reads it from the
/// file's tags. So the pattern controls are written once, and both windows use them.</para>
/// </summary>
internal static partial class PatternFields
{
    /// <summary>The tokens that a file name can hold. A name contains what a ripper put in it: the
    /// artist, the title and the track number. The rest of the catalogue, such as the comment, the
    /// publisher or the movement, is never in a file name. Offering those here would offer a pattern
    /// that cannot match.</summary>
    public static readonly IReadOnlyList<string> InNames =
        ["artist", "albumartist", "album", "title", "track", "disc", "year", "genre"];

    /// <summary>Every token. Used by the window that builds one tag from the others. Its source is the
    /// file's own tags, so any field in the catalogue can be read.</summary>
    public static IReadOnlyList<string> All => [.. TagFields.Tokens.Keys];

    [GeneratedRegex(@"\{(\w+)(?::[^}]*)?\}")]
    private static partial Regex Token();

    /// <summary>A grid of keys, one per token. Each key writes <c>{token}</c> into <paramref
    /// name="target"/> at the caret.
    ///
    /// <para>The grid has a fixed number of columns, not a flowing layout. The caller knows how many
    /// tokens it offers and how wide its window is, and a grid given both lays out the same way on
    /// every pass. The keys stretch to fill their cells, so the block reads as one keypad of fields,
    /// not a ragged line.</para>
    /// </summary>
    public static Grid Chips(TextBox target, IReadOnlyList<string> tokens, int columns)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(tokens);

        var grid = new Grid { ColumnSpacing = 4, RowSpacing = 4 };
        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r * columns < tokens.Count; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var key = new Button
            {
                Content = new TextBlock
                {
                    Text = FieldName(token),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextWrapping = TextWrapping.NoWrap,
                },
                Style = DenseForm.Res<Style>("DenseButtonStyle"),
                Padding = new Thickness(6, 0, 6, 0),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            ToolTipService.SetToolTip(key, "{" + token + "}   " + Loc.Get("Tags_FieldTooltip"));
            key.Click += (_, _) => Insert(target, "{" + token + "}");

            Grid.SetRow(key, i / columns);
            Grid.SetColumn(key, i % columns);
            grid.Children.Add(key);
        }

        return grid;
    }

    /// <summary>Describes the pattern in the app's own words. "{track} - {title}" reads back as "Track
    /// # - Title", so a preset explains itself to someone who has never seen a brace.</summary>
    public static string Describe(string pattern) =>
        Token().Replace(pattern ?? "", m => FieldName(m.Groups[1].Value));

    /// <summary>The presets as a list that a picker can show. The last line has an empty pattern
    /// and is for a custom pattern. It is the line the list shows when the user types a pattern that
    /// matches none of the presets.</summary>
    public static List<TagPresetRow> Rows(IReadOnlyList<string> patterns) =>
    [
        .. patterns.Select(p => new TagPresetRow(Describe(p), p)),
        new TagPresetRow(Loc.Get("Tags_Custom"), ""),
    ];

    /// <summary>The app's name for the field that a token names, or the token itself when the catalogue
    /// does not know it. This is public because a preview also shows these names. "Artist: Radiohead"
    /// is a line a user can check. "artist: Radiohead" shows the internal machinery.</summary>
    public static string FieldName(string token) =>
        TagFields.Tokens.TryGetValue(token, out var kind)
            ? Loc.Get(TagFields.Get(kind).LabelKey)
            : "{" + token + "}";

    private static void Insert(TextBox box, string token)
    {
        var text = box.Text ?? "";
        var at = Math.Clamp(box.SelectionStart, 0, text.Length);
        box.Text = string.Concat(text.AsSpan(0, at), token, text.AsSpan(at));
        box.SelectionStart = at + token.Length;
        box.Focus(FocusState.Programmatic);
    }
}
