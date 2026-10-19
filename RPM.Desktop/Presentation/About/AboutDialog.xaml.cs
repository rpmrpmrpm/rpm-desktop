using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Desktop.Presentation.Main.LeftPane.Controls;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Core.Cloud;
using RPM.Desktop.Presentation.Controls;
using RPM.Desktop.Strings;
using Windows.System;
using Windows.UI;

namespace RPM.Desktop.Presentation.About;

/// <summary>
/// What RPM is, and everything in it that someone else made.
///
/// <para>The card has two views, and only one is shown at a time: the list, and one licence in full. The
/// licence covers the list rather than sitting beside it. The texts run to about 25,000 words and are meant
/// to be read. A two-pane card would also need to be wide enough for the longest line of the GPL.</para>
///
/// <para>The list is built in code, not in the markup. The data is generated from scripts/credits.json, and
/// it has about 120 entries. The count changes when a genre photograph or a shelf's typeface changes. So
/// the list cannot be written out by hand and stay correct.</para>
///
/// <para>The list is NOT virtualised, on purpose. The rows differ in height by a factor of four. A section
/// heading with a note above it is much taller than a photographer's name. An ItemsRepeater with a
/// StackLayout estimates its total height from the average of the rows it has realised. With rows of such
/// different heights, the scrollbar jumps while the user drags it. A modal dialog with a bounded list is
/// the case where building every row up front is correct.</para>
/// </summary>
public sealed partial class AboutDialog : ContentDialog
{
    /// <summary>Every row, built once, with the words it is searched on. The rows are kept because the
    /// filter hides and shows them. Rebuilding about 120 rows on each keystroke would make the filter box
    /// slow.</summary>
    private readonly List<(FrameworkElement Row, string Words, bool IsHeading)> _rows = [];

    public AboutDialog()
    {
        InitializeComponent();

        Shell.Title = Loc.Get("About_Title");
        Shell.AttachResize(this, minWidth: 700, minHeight: 520, maxWidth: 1100, defaultHeight: 700,
            defaultWidth: 820);

        VersionText.Text = Loc.Format("About_Version", App.VersionText);
        RightsText.Text = $"© {DateTime.Now.Year} rpm. {Loc.Get("About_Rights")}";

        CloseLabel.Text = Loc.Get("About_Close");

        Build();
    }

    public static Task ShowAsync(XamlRoot xamlRoot)
        => DialogHost.ShowAsync(new AboutDialog { XamlRoot = xamlRoot });

    // ── the list ────────────────────────────────────────────────────────────────────────────

    private void Build()
    {
        foreach (var section in Credits.Sections)
        {
            Add(Heading(section), Loc.Get(section.TitleKey), isHeading: true);

            foreach (var entry in section.Entries)
                Add(Row(entry), Words(entry), isHeading: false);
        }
    }

    private void Add(FrameworkElement row, string words, bool isHeading)
    {
        Entries.Children.Add(row);
        _rows.Add((row, words.ToLowerInvariant(), isHeading));
    }

    /// <summary>The words that a row matches in the filter box: its name, what it is for, who holds it,
    /// and under which licence. The licence name is included on purpose. People open this screen to ask,
    /// for example, about the GPL.</summary>
    private static string Words(CreditEntry entry)
    {
        var parts = new List<string> { entry.Name };

        if (entry.Detail is { } detail)
            parts.Add(detail);
        if (entry.UseKey is { } use)
            parts.Add(Loc.Get(use));
        if (entry.Copyright is { } copyright)
            parts.Add(copyright);

        foreach (var key in entry.Licenses)
            parts.Add(Credits.Licenses.TryGetValue(key, out var licence) ? licence.Name : key);

        return string.Join(" ", parts);
    }

    private static StackPanel Heading(CreditSection section)
    {
        var block = new StackPanel { Spacing = 4, Margin = new Thickness(2, 22, 2, 10) };

        block.Children.Add(new TextBlock
        {
            Text = Loc.Get(section.TitleKey),
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = White(0xE0),
        });

        if (section.NoteKey is { } note)
            block.Children.Add(new TextBlock
            {
                Text = Loc.Get(note),
                FontSize = 12,
                Foreground = White(0x88),
                TextWrapping = TextWrapping.Wrap,
            });

        return block;
    }

    /// <summary>
    /// One entry: its name, what RPM does with it, whose it is, and the links to its licence.
    ///
    /// <para>The licence is a link, not plain text, because it is the one thing on the row that a user may
    /// act on. A row can have more than one.</para>
    /// </summary>
    private Border Row(CreditEntry entry)
    {
        var lines = new StackPanel { Spacing = 3 };

        var title = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        title.Children.Add(new TextBlock
        {
            Text = entry.Name,
            FontSize = 13.5,
            Foreground = White(0xF0),
            VerticalAlignment = VerticalAlignment.Center,
        });

        if (entry.Version is { } version)
            title.Children.Add(Faint(version, 11.5));

        // The detail, for example the shelf that a photograph is shown on. It is used only here. It comes
        // after the name, because the list is read down the names.
        if (entry.Detail is { } detail)
            title.Children.Add(Faint(detail, 11.5));

        lines.Children.Add(title);

        if (entry.UseKey is { } use)
            lines.Children.Add(new TextBlock
            {
                Text = Loc.Get(use),
                FontSize = 12,
                Foreground = White(0x99),
                TextWrapping = TextWrapping.Wrap,
            });

        if (entry.Copyright is { } copyright)
            lines.Children.Add(new TextBlock
            {
                Text = copyright,
                FontSize = 11,
                Foreground = White(0x66),
                TextWrapping = TextWrapping.Wrap,
            });

        var links = new WrapRow
        {
            HorizontalSpacing = 14,
            VerticalSpacing = 4,
            Margin = new Thickness(0, 4, 0, 0),
        };

        foreach (var key in entry.Licenses)
        {
            if (!Credits.Licenses.TryGetValue(key, out var licence))
                continue;

            // A licence whose text ships opens here. A licence whose text does not ship is a published
            // set of terms that only its publisher may host. It opens in a browser. The row shows which
            // kind it is by where the link sends the reader. A notices document ships, but it still opens
            // in a browser. It is hundreds of kilobytes of licences, and this reader would put the whole
            // text in one block.
            links.Children.Add(licence.File is not null && !licence.IsNotices
                ? Link(licence.Name, () => Open(entry, licence))
                : Link(licence.Name, () => Browse(entry.TermsUrl ?? licence.Url)));
        }

        if (entry.Url is { Length: > 0 } url)
            links.Children.Add(Link(Host(url), () => Browse(url)));

        if (entry.Source is { Length: > 0 } source)
            links.Children.Add(Link(Loc.Get("About_SourceCode"), () => Browse(source)));

        lines.Children.Add(links);

        return new Border
        {
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 0, 0, 6),
            CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0x0E, 0xFF, 0xFF, 0xFF)),
            Child = lines,
        };
    }

    /// <summary>The app's link, which is <see cref="RpmLink"/>. It is not built here. It is cyan, has no
    /// plate, shows the hand cursor, and uses the same size and colour as every other link in the app. Do
    /// not build a different kind of link here. A row made from a glyph and a HyperlinkButton would look
    /// like another kind of button.</summary>
    private static FrameworkElement Link(string text, Action go)
    {
        var link = new RpmLink { Content = text };
        link.Click += (_, _) => go();
        return link;
    }

    private static TextBlock Faint(string text, double size) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = White(0x66),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static SolidColorBrush White(byte alpha)
        => new(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));

    /// <summary>The label of a link is the site name, not the full address. A long address such as
    /// "github.com/mono/taglib-sharp" in a column of small text is noise. "github.com" is enough to show
    /// where the link goes.</summary>
    private static string Host(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.Host.StartsWith("www.", StringComparison.Ordinal) ? uri.Host[4..] : uri.Host
            : url;

    // ── filtering ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Hides what does not match, and hides a heading whose whole section went with it.
    ///
    /// <para>A heading is kept only when something under it survived, which is why this walks the
    /// rows backwards: at each heading, whether anything below it is still showing has by then
    /// already been decided.</para>
    /// </summary>
    private void Filter_Changed(object sender, TextChangedEventArgs e)
    {
        var want = FilterBox.Text.Trim().ToLowerInvariant();
        var showAll = want.Length == 0;
        var shown = 0;
        var sinceHeading = 0;

        for (var i = _rows.Count - 1; i >= 0; i--)
        {
            var (row, words, isHeading) = _rows[i];

            if (isHeading)
            {
                row.Visibility = sinceHeading > 0 ? Visibility.Visible : Visibility.Collapsed;
                sinceHeading = 0;
                continue;
            }

            var keep = showAll || words.Contains(want, StringComparison.Ordinal);
            row.Visibility = keep ? Visibility.Visible : Visibility.Collapsed;

            if (keep)
            {
                shown++;
                sinceHeading++;
            }
        }

        NoMatchText.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── the licence ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Shows one licence in full, with the holder's own copyright written into it.
    ///
    /// <para>This is async, and the caller does not await it. The text is read from a file. The first read
    /// of the GPL is 35 KB from disk. The view is switched BEFORE the read, so the card does not stay on
    /// the list for a frame after the press.</para>
    /// </summary>
    private async void Open(CreditEntry entry, CreditLicense licence)
    {
        ReaderTitle.Text = licence.Name;
        ReaderSubtitle.Text = entry.Copyright is { Length: > 0 } copyright
            ? $"{entry.Name}. {copyright}"
            : entry.Name;
        ReaderText.Text = string.Empty;

        Shell.Title = Loc.Get("About_License");
        CloseLabel.Text = Loc.Get("About_Back");
        CloseGlyph.Glyph = "\uf060";
        ListView.Visibility = Visibility.Collapsed;
        ReaderView.Visibility = Visibility.Visible;

        try
        {
            ReaderText.Text = await Credits.TextAsync(licence, entry.Copyright);
        }
        catch
        {
            // The text is missing, or could not be read. The licence is still named on the row behind
            // this, and its address is still a link. So the reader is sent to that address, rather than
            // shown a blank card.
            ReaderText.Text = licence.Url ?? string.Empty;
        }

        ReaderScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void BackToList()
    {
        ReaderView.Visibility = Visibility.Collapsed;
        ListView.Visibility = Visibility.Visible;
        Shell.Title = Loc.Get("About_Title");
        CloseLabel.Text = Loc.Get("About_Close");
        CloseGlyph.Glyph = "\uf00d";
    }

    // ── the way out ─────────────────────────────────────────────────────────────────────────

    /// <summary>The footer has one button. It means Back while a licence is open. A second button that is
    /// disabled most of the time looks broken.</summary>
    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (ReaderView.Visibility == Visibility.Visible)
            BackToList();
        else
            Hide();
    }

    private void Website_Click(object sender, RoutedEventArgs e) => Browse(RpmSite.Home);

    private void Support_Click(object sender, RoutedEventArgs e) => Browse(RpmSite.Support);

    private void Privacy_Click(object sender, RoutedEventArgs e) => Browse(RpmSite.Privacy);

    private void Terms_Click(object sender, RoutedEventArgs e) => Browse(RpmSite.Terms);

    private void LgplSource_Click(object sender, RoutedEventArgs e) => Browse(RpmSite.LgplSource);

    private static async void Browse(string? url)
    {
        if (url is not { Length: > 0 } || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return;

        try
        {
            await Launcher.LaunchUriAsync(uri);
        }
        catch
        {
            // There is no browser, or the shell refused the request. Nothing on this screen depends on
            // it working. An error dialog about a failed link would be worse than silence.
        }
    }
}
