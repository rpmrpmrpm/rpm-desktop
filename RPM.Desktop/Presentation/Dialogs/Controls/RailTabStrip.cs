using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Strings;
using Windows.UI;

namespace RPM.Desktop.Presentation.Dialogs.Controls;

/// <summary>The left pane's function rail, laid on its side. It is a frosted strip of buttons, each
/// with a glyph and a label. One of them is washed azure.
///
/// <para>It works like the rail: the user picks one of these. It is the same object, including the
/// wash. The wash comes from <see cref="SelectedSegment"/>, so it has the same colour as the library's
/// list, wall and table switch. Only the layout differs. The rail is an 82 px column, and it stacks the
/// glyph over the label. A dialog's tab strip has no height to spare, so it sets them side by
/// side.</para>
///
/// <para>The strip also owns the panels, not only the buttons. A tab strip that only reported clicks
/// would make every window repeat the same show-one-and-hide-the-rest loop, and those loops could drift
/// apart.</para>
/// </summary>
internal sealed partial class RailTabStrip : BorderBase
{
    /// <summary>One tab. It has the key that a panel is registered under, the <see cref="Loc"/> key
    /// for its label, and a Font Awesome code point such as "\uf02b". The label is translated, so it is
    /// a key and never a caption.
    /// </summary>
    /// <remarks>This is a plain struct, not a record. Uno's KeyEquality generator treats any record
    /// with a Key or Id member as needing its whole nest to be partial. Nothing here compares tabs for
    /// equality, so a record would add that cost for no gain.
    /// </remarks>
    internal readonly struct Tab
    {
        public Tab(string id, string labelKey, string glyph)
            => (Id, LabelKey, Glyph) = (id, labelKey, glyph);

        public string Id { get; }
        public string LabelKey { get; }
        public string Glyph { get; }
    }

    private readonly Dictionary<string, Button> _buttons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FrameworkElement> _panels = new(StringComparer.Ordinal);

    /// <summary>Which tab is up. Empty until the first <see cref="Select"/>.</summary>
    public string Current { get; private set; } = "";

    /// <summary>Raised after the shown panel has changed. Use it when a window has to do more than swap
    /// panels, such as loading a picture the first time its tab opens.</summary>
    public event Action<string>? SelectionChanged;

    public RailTabStrip(params Tab[] tabs)
    {
        ArgumentNullException.ThrowIfNull(tabs);

        Background = new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00));
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        // The tabs sit edge to edge, with no inset. A selected tab's wash then runs into the strip's
        // own border, and does not float inside it. The two end tabs round the strip's corners
        // themselves (see RoundEnds).
        Padding = new Thickness(0);
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;

        // Use the rail's own hover and pressed colours, so a tab lights up the same way as a rail
        // button.
        Resources["ButtonBackgroundPointerOver"] = new SolidColorBrush(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
        Resources["ButtonBackgroundPressed"] = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        Resources["ButtonForegroundPointerOver"] = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        Resources["ButtonForegroundPressed"] = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));

        _row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        foreach (var tab in tabs)
        {
            var button = Build(tab);
            var key = tab.Id;
            button.Click += (_, _) => Select(key);
            _buttons[key] = button;
            _row.Children.Add(button);
        }
        Child = _row;
        RoundEnds();
    }

    private readonly StackPanel _row;

    /// <summary>The strip's own corner. The tabs on each end carry it. The other tabs have square
    /// corners, because the tabs touch, and an inner corner would cut a notch out of the neighbouring
    /// wash.</summary>
    private void RoundEnds()
    {
        const double r = 7;   // The strip's radius is 8, less its 1 px border

        var shown = _row.Children.OfType<Button>()
            .Where(b => b.Visibility == Visibility.Visible)
            .ToList();

        for (var i = 0; i < shown.Count; i++)
        {
            var first = i == 0;
            var last = i == shown.Count - 1;
            shown[i].CornerRadius = new CornerRadius(
                first ? r : 0, last ? r : 0, last ? r : 0, first ? r : 0);
        }
    }

    private static Button Build(Tab tab)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon
        {
            Glyph = tab.Glyph,
            Style = DenseForm.Res<Style>("FaIcon"),
            FontSize = 13,
        });
        content.Children.Add(new TextBlock
        {
            Text = Loc.Get(tab.LabelKey),
            FontSize = 11.5,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });

        return new Button
        {
            Content = content,
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),   // Set on the end tabs by RoundEnds
            Height = 36,
            MinHeight = 36,
            MinWidth = 0,
            Padding = new Thickness(14, 0, 14, 0),
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
        };
    }

    /// <summary>Ties a panel to a tab. The strip shows exactly one of them from then on.</summary>
    public void Register(string key, FrameworkElement panel)
    {
        ArgumentNullException.ThrowIfNull(panel);
        _panels[key] = panel;
        panel.Visibility = string.Equals(key, Current, StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>Shows one tab and paints the strip for it. An unknown key is ignored, so a window that
    /// hides a tab does not need to check each call that names that tab.</summary>
    public void Select(string key)
    {
        if (!_buttons.ContainsKey(key))
            return;

        Current = key;
        foreach (var (name, button) in _buttons)
            SelectedSegment.Paint(button, string.Equals(name, key, StringComparison.Ordinal));
        foreach (var (name, panel) in _panels)
            panel.Visibility = string.Equals(name, key, StringComparison.Ordinal)
                ? Visibility.Visible
                : Visibility.Collapsed;

        SelectionChanged?.Invoke(key);
    }

    /// <summary>Removes a tab from the strip, for a caller that has nothing to show under it.</summary>
    public void Hide(string key)
    {
        if (_buttons.TryGetValue(key, out var button))
            button.Visibility = Visibility.Collapsed;
        if (_panels.TryGetValue(key, out var panel))
            panel.Visibility = Visibility.Collapsed;

        // Hiding an end tab removes the corner it carried. RoundEnds moves the corner to the tab that
        // is now at the end.
        RoundEnds();
    }
}
