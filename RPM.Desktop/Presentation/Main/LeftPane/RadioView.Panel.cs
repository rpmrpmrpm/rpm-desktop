using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Main.LeftPane;

public sealed partial class RadioView
{
    /// <summary>
    /// A dropped panel: a list of entries that scrolls after twenty lines, with a button fixed under it
    /// that does not scroll with them.
    ///
    /// <para><b>Two things use one of these</b>: the filters for the dial, and the country the dial is
    /// listening from. They share one class so they also share one look. A country picked from a combo
    /// box beside a panel of ticks would be two answers to the same kind of question, drawn two
    /// different ways.</para>
    ///
    /// <para><b>Nothing here stores what is chosen.</b> Each entry is given a way to ask whether it is the
    /// one in force. Every entry is asked again after every press. So a panel cannot show a tick against
    /// a value the model has since changed. A panel that stays open while it is used is correct by
    /// design, not because of careful coding.</para>
    ///
    /// <para><b>The pinned button is the way back out</b>: it clears the filters or the country. A way out
    /// that has scrolled off the top of a list of thirty is hard to find, so it stays at the foot of the
    /// panel. It is disabled while there is nothing to undo.</para>
    /// </summary>
    /// <param name="icons">The pane's icon font, for the tick an entry in force wears.</param>
    /// <param name="width">How wide the panel is. Each panel has its own width, because the widest label
    /// decides it, and the two panels carry different lists.</param>
    /// <param name="staysOpen">Whether a press leaves the panel open. True where more than one thing can
    /// be set from it (the filters are three questions in three sections). False where one press is the
    /// whole answer, and anything after it would be a second trip.</param>
    private sealed class Panel(FontFamily icons, double width, bool staysOpen)
    {
        /// <summary>How tall one line is, and how many are shown before the list scrolls. The height is
        /// for a LINE, not for an entry. Section headings are lines too, so "twenty rows" counts
        /// them.</summary>
        private const double RowHeight = 26;

        private const int Rows = 20;

        private readonly StackPanel _list = new() { Margin = new Thickness(0, 4, 0, 4) };

        /// <summary>What every line on the panel does when the thing it shows changes: it asks again. It is
        /// kept as a list of actions, not as a remembered answer. See the class note.</summary>
        private readonly List<Action> _reread = [];

        private Flyout? _shown;

        /// <summary>The name of a section. It is a label on the list below, not an item to pick. It has the
        /// same height as an entry, so "twenty rows" is a true measurement.</summary>
        public void Heading(string text) => _list.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            Opacity = 0.6,
            Height = RowHeight,
            Padding = new Thickness(12, 7, 12, 0),
        });

        /// <summary>One entry: its text, how to tell whether it is the one in force, and what a press
        /// does.</summary>
        public void Entry(string text, Func<bool> chosen, Action press)
        {
            var tick = new TextBlock
            {
                Text = "\uf00c",   // a tick, shown only on the entry in force
                FontFamily = icons,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            var label = new TextBlock
            {
                Text = text,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            var line = new Grid { ColumnSpacing = 6 };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(tick, 0);
            Grid.SetColumn(label, 1);
            line.Children.Add(tick);
            line.Children.Add(label);

            var row = Flat(new Button
            {
                Content = line,
                Height = RowHeight,
                Padding = new Thickness(10, 0, 10, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            });

            row.Click += (_, _) =>
            {
                press();
                if (staysOpen)
                    Reread();
                else
                    _shown?.Hide();
            };

            // Every entry has a tick, and only the one in force shows it. A hidden tick keeps every label on
            // the same left edge, which makes a long list easier to read down.
            _reread.Add(() => tick.Opacity = chosen() ? 1 : 0);
            _list.Children.Add(row);
        }

        /// <summary>Opens the panel under <paramref name="at"/>, with the button that undoes what it sets
        /// fixed beneath the list.</summary>
        public void Show(FrameworkElement at, [Shown] string footer, Func<bool> undoable, Action undo)
        {
            // Exactly twenty lines deep. It is a MAXIMUM: on a short window the panel may be shorter, so it
            // does not hang off the bottom of the screen.
            var scroll = new ScrollViewer
            {
                Content = _list,
                MaxHeight = Rows * RowHeight,
                HorizontalScrollMode = ScrollMode.Disabled,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };

            var button = new Button
            {
                Content = footer,
                FontSize = 12,
                Height = 26,
                MinHeight = 0,
                Margin = new Thickness(8, 6, 8, 8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            button.Click += (_, _) =>
            {
                undo();
                if (staysOpen)
                    Reread();
                else
                    _shown?.Hide();
            };
            _reread.Add(() => button.IsEnabled = undoable());

            // A thin line over the button, so the foot of the panel looks like a footer rather than one more
            // entry drawn differently.
            var foot = new Border
            {
                BorderThickness = new Thickness(0, 1, 0, 0),
                BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
                Child = button,
            };

            var body = new Grid { Width = width };
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(scroll, 0);
            Grid.SetRow(foot, 1);
            body.Children.Add(scroll);
            body.Children.Add(foot);

            _shown = new Flyout
            {
                Content = body,
                Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
                // The panel does its own padding and scrolling. The presenter's own would add a second
                // scrollbar around the first, and a margin around entries that should run to the panel's
                // edges.
                FlyoutPresenterStyle = Presenter,
            };
            _shown.ShowAt(at);
            Reread();
        }

        private void Reread()
        {
            foreach (var again in _reread)
                again();
        }

        /// <summary>The flat style of the album wall's sort button: no background until the pointer is over
        /// it, then the app's two greys.</summary>
        private static Button Flat(Button button)
        {
            button.MinHeight = 0;
            button.MinWidth = 0;
            button.CornerRadius = new CornerRadius(0);
            button.BorderThickness = new Thickness(0);
            button.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.Resources["ButtonBackground"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.Resources["ButtonBackgroundPointerOver"] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));
            button.Resources["ButtonBackgroundPressed"] =
                new SolidColorBrush(Windows.UI.Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF));
            return button;
        }

        /// <summary>The panel's frame. It has no padding and no scrolling of its own. The list inside does
        /// both. A presenter that also scrolled would add a second bar beside the first.</summary>
        private static Style Presenter
        {
            get
            {
                var style = new Style(typeof(FlyoutPresenter));
                style.Setters.Add(new Setter(FlyoutPresenter.PaddingProperty, new Thickness(0)));
                style.Setters.Add(new Setter(FlyoutPresenter.MinWidthProperty, 0d));
                style.Setters.Add(new Setter(FlyoutPresenter.MaxWidthProperty, 400d));
                style.Setters.Add(new Setter(FlyoutPresenter.CornerRadiusProperty, new CornerRadius(6)));
                style.Setters.Add(new Setter(ScrollViewer.VerticalScrollModeProperty, ScrollMode.Disabled));
                style.Setters.Add(new Setter(ScrollViewer.VerticalScrollBarVisibilityProperty,
                                             ScrollBarVisibility.Disabled));
                return style;
            }
        }
    }
}
