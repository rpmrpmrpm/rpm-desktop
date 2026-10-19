using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Dialogs.Controls;

/// <summary>A dense data-entry form, built from the pieces below. The sizes come from
/// Themes/DenseForm.xaml, which the dialog merges so that its inputs shrink. The labels, headers and
/// inline buttons come from Themes/Generic.xaml. This layout depends on those sizes.
///
/// <para>Every form uses one shape: a group name over a thin rule, then rows of [tick] [label] [input]
/// on a 24 px pitch. The label column has a fixed width, so every input in a window starts on the same
/// vertical line, whichever group it is in. A field that is empty, or holds an unusual value, shows as
/// a break in that line, so it is easy to find.</para>
/// </summary>
internal static class DenseForm
{
    public static T Res<T>(string key) => (T)Application.Current.Resources[key];

    public static double RowSpacing => Res<double>("DenseRowSpacing");
    public static double LabelWidth => Res<double>("DenseLabelWidth");

    /// <summary>A year or a BPM. These are short values, so a full-width box would look
    /// wrong.</summary>
    public static double NarrowWidth => Res<double>("DenseNarrowWidth");

    /// <summary>A disc or track number. It has two digits.</summary>
    public static double TinyWidth => Res<double>("DenseTinyWidth");

    /// <summary>The width that an editable ComboBox uses for its arrow and its inner text padding. A
    /// short field is made this much wider, so that its placeholder text still fits.</summary>
    public const double ComboChromeWidth = 52;

    public static TextBlock Label(string key) =>
        new() { Text = Loc.Get(key), Style = Res<Style>("DenseFieldLabelStyle") };

    /// <summary>A word between two inputs on one row, such as "of" in "Disc 1 of 2".</summary>
    public static TextBlock Joiner(string key) => new()
    {
        Text = Loc.Get(key),
        FontSize = 11.5,
        Foreground = Res<Brush>("DenseLabelInkBrush"),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A row that holds two short boxes, with a word between them.</summary>
    public static StackPanel Pair(params UIElement[] parts)
    {
        var line = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var part in parts)
            line.Children.Add(part);
        return line;
    }

    /// <summary>Sets a control to a short field width. An editable ComboBox gets the width of its arrow
    /// added on top, so that a short ComboBox is not squeezed down to the width of the
    /// number.</summary>
    public static T Narrow<T>(this T control, double width) where T : Control
    {
        control.Width = control is ComboBox ? width + ComboChromeWidth : width;
        control.HorizontalAlignment = HorizontalAlignment.Left;
        return control;
    }

    /// <summary>Makes a box accept digits only, and shows a numeric keypad where the device has one.
    ///
    /// <para>This filters the input rather than using a <c>NumberBox</c>. A NumberBox holds a double
    /// and formats it, so a year comes back as "1,977", and an empty field becomes NaN. Every one of
    /// these fields is a <c>uint</c> that is legitimately blank when no value has been set. Filtering
    /// keeps the value a string all the way through the catalogue, which is what <see cref="TagField"/>
    /// uses.</para>
    ///
    /// <para><c>TextChanging</c> is used rather than <c>TextChanged</c>. It runs before the text is
    /// drawn, so a rejected character never appears and then disappears.</para>
    /// </summary>
    public static TextBox AcceptDigitsOnly(this TextBox box)
    {
        ArgumentNullException.ThrowIfNull(box);

        box.InputScope = new InputScope
        {
            Names = { new InputScopeName(InputScopeNameValue.Number) },
        };

        box.TextChanging += (sender, _) =>
        {
            var digits = new string([.. sender.Text.Where(char.IsAsciiDigit)]);
            if (digits == sender.Text)
                return;

            // Keep the caret where the user is typing rather than throwing it to the end, which is what
            // assigning Text does on its own.
            int caret = Math.Max(0, sender.SelectionStart - (sender.Text.Length - digits.Length));
            sender.Text = digits;
            sender.SelectionStart = Math.Min(caret, digits.Length);
        };

        return box;
    }
}

/// <summary>The marker in a row's last column. It is used on a field that describes the whole record,
/// not only this file.
///
/// <para>It has two jobs, and one glyph shows both. In the per-file editor it is a switch. When it is
/// off, an edit changes only this file. Use it to repair a record whose tracks have different values.
/// When it is on, the value is written to every track on the album. In the album editor there is no
/// switch, because everything there is written to all the tracks. So the marker only reports.</para>
///
/// <para>In either editor it turns amber when the other tracks do not match the value in the box. This
/// makes a split album easy to see.</para>
/// </summary>
internal sealed partial class AlbumMarker : Button
{
    private static readonly Brush Idle = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Armed = new SolidColorBrush(Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF));
    private static readonly Brush Disagrees = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xC0, 0x61));

    private readonly FontIcon _glyph;
    private readonly string? _disagreement;
    private readonly bool _canArm;

    /// <summary>True when this field is to be written to every track on the album.</summary>
    public bool IsArmed { get; private set; }

    /// <param name="disagreement">What the other tracks say, when they do not all match this box's
    /// value. Null when the record agrees with itself.</param>
    /// <param name="canArm">False in the album editor, where every field already goes to every
    /// track.</param>
    public AlbumMarker(string? disagreement, bool canArm)
    {
        _disagreement = disagreement;
        _canArm = canArm;
        _glyph = new FontIcon
        {
            Style = DenseForm.Res<Style>("FaIcon"),
            Glyph = "",   // A record glyph: this field belongs to the album, not to the file
            FontSize = 11,
        };

        Content = _glyph;
        Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Width = 20;
        Height = 20;
        MinWidth = 0;
        MinHeight = 0;
        VerticalAlignment = VerticalAlignment.Center;
        IsHitTestVisible = canArm;   // In the album editor this only shows the state

        if (canArm)
            Click += (_, _) => { IsArmed = !IsArmed; Refresh(); };
        Refresh();
    }

    private void Refresh()
    {
        _glyph.Foreground = IsArmed ? Armed : _disagreement is null ? Idle : Disagrees;
        ToolTipService.SetToolTip(this, IsArmed
            ? Loc.Get("Edit_WillApplyToAlbum")
            : _disagreement ?? (_canArm ? Loc.Get("Edit_ApplyToAlbum") : null));
    }
}

/// <summary>A named group of rows. Add rows to it, then call <see cref="Build"/> to place it in a
/// column. <see cref="Build"/> returns null when no row was added. So a group whose fields the caller
/// did not offer is left out, and no empty heading is shown.
/// </summary>
internal sealed class DenseFormGroup
{
    private readonly Grid _grid;
    private readonly bool _ticks;

    /// <param name="ticks">True when each row is armed by a checkbox, as in the mass edit. False when
    /// every row is always written, as in the single-track editor. False removes the tick column, so
    /// the labels sit against the card's edge and not beside an empty gap.</param>
    public DenseFormGroup(bool ticks = false)
    {
        _ticks = ticks;
        _grid = new Grid { ColumnSpacing = 8, RowSpacing = DenseForm.RowSpacing };
        if (ticks)
            _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DenseForm.LabelWidth) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // The last column holds a marker for the row. The marker shows that the rest of the album
        // disagrees with the row's value. The column is always there, and it is usually empty. So the
        // inputs of a group end on one line, whether or not a row has a marker.
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
    }

    public int RowCount => _grid.RowDefinitions.Count;

    /// <param name="tick">In a ticked group, null means the row has no tick of its own. For example,
    /// the second half of a pair has its tick placed beside its box.</param>
    /// <param name="topAlign">For a row taller than one line, such as a comment box. Its label is
    /// placed at the top of the row, not centred against it.</param>
    /// <param name="trailing">The content of the row's last column, past the input, such as the
    /// disagreement marker. Null leaves that column empty.</param>
    public void Add(
        UIElement? tick, FrameworkElement label, UIElement input, bool topAlign = false,
        UIElement? trailing = null)
    {
        int r = _grid.RowDefinitions.Count;
        _grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (topAlign)
        {
            label.VerticalAlignment = VerticalAlignment.Top;
            label.Margin = new Thickness(0, 4, 0, 0);
            if (tick is FrameworkElement fe)
            {
                fe.VerticalAlignment = VerticalAlignment.Top;
                fe.Margin = new Thickness(0, 4, 0, 0);
            }
        }

        int column = 0;
        if (_ticks)
        {
            if (tick is not null)
                Place(tick, r, column);
            column++;
        }
        Place(label, r, column);
        Place(input, r, column + 1);
        if (trailing is not null)
            Place(trailing, r, column + 2);

        void Place(UIElement element, int row, int col)
        {
            // Grid.SetRow and Grid.SetColumn are declared on FrameworkElement in WinUI and on UIElement
            // in Uno. Every element a form row places is a FrameworkElement, so the cast is safe.
            var placed = (FrameworkElement)element;
            Grid.SetRow(placed, row);
            Grid.SetColumn(placed, col);
            _grid.Children.Add(element);
        }
    }

    /// <summary>The rows alone, with no heading above them. Use this where the surrounding surface
    /// already names these fields. For example, the import workbench's tag panel has its own header. A
    /// group heading would repeat the same word.
    /// </summary>
    public UIElement? BuildBare() => RowCount == 0 ? null : _grid;

    /// <summary>The finished group: its name over a thin rule, then its rows. Null when there are no
    /// rows.</summary>
    public UIElement? Build(string headerKey)
    {
        if (RowCount == 0)
            return null;

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = Loc.Get(headerKey),
            Style = DenseForm.Res<Style>("DenseGroupHeaderStyle"),
        });
        panel.Children.Add(new Border
        {
            Height = 1,
            Background = DenseForm.Res<Brush>("DenseRuleBrush"),
            Margin = new Thickness(0, 3, 0, 5),
        });
        panel.Children.Add(_grid);
        return panel;
    }

    /// <summary>Builds the group and adds it to a column, if it has rows.</summary>
    public void AddTo(Panel column, string headerKey)
    {
        if (Build(headerKey) is { } group)
            column.Children.Add(group);
    }
}
