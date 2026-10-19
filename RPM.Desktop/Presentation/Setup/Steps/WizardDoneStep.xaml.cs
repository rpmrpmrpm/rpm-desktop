using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using RPM.Desktop.Presentation.Controls;
using RPM.Desktop.Services.Account;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Setup.Steps;

/// <summary>
/// Step three: what was imported, and the supporter ask.
///
/// <para>The perks are built from <see cref="SupporterPerks.All"/>. The supporter window prints the
/// same list. The two layouts differ, because the two surfaces have different shapes. The content does
/// not differ, because it is read from one place.</para>
/// </summary>
public sealed partial class WizardDoneStep : UserControl
{
    /// <summary>Done with the wizard, whichever button said so.</summary>
    public event EventHandler? Finished;

    /// <summary>Nothing was imported and the listener has changed their mind. Back to step two.</summary>
    public event EventHandler? ImportRequested;

    /// <summary>Whether this screen was reached without importing anything. The second button depends
    /// on this: there is no collection to open yet.</summary>
    private bool _empty;

    /// <summary>The glyph's size in the list. It is small, because it sits beside the words and is not
    /// meant to be looked at on its own.</summary>
    private const double GlyphSize = 15;

    /// <summary>The width of the glyph's column. Every label starts at the same position, whatever
    /// width its own glyph is drawn.</summary>
    private const double GlyphColumn = 22;

    private static readonly Windows.UI.Color Cyan = Windows.UI.Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF);
    private static readonly Windows.UI.Color Gold = Windows.UI.Color.FromArgb(0xFF, 0xEF, 0xAF, 0x12);

    public WizardDoneStep()
    {
        InitializeComponent();
        Kicker.Text = Loc.Format("Wizard_StepOf", 3, 3);
        WizardRecord.Dress(Disc, size: 48);
        BuildPerks();
    }

    /// <summary>
    /// The import's result, from the step before.
    ///
    /// <para><b>When nothing was imported, no count is shown.</b> A line that says "Nothing imported
    /// yet" would only repeat the absence of the count. The second button carries that news instead, by
    /// being the action to take.</para>
    /// </summary>
    public void Show(ImportOutcome outcome)
    {
        _empty = outcome.Albums <= 0;

        AlbumsText.Text = _empty ? "" : Loc.Format("Wizard_DoneAlbums", outcome.Albums);
        TracksText.Text = _empty ? "" : Loc.Format("Wizard_DoneTracks", outcome.Tracks);

        // Opening an empty collection would open nothing. With no music filed, the useful step is to go and
        // import some.
        FinishButton.Content = Loc.Get(_empty ? "Wizard_GoImport" : "Wizard_Finish");
    }

    /// <summary>Called by the host once this step is on screen. There is nothing to re-read yet. It
    /// exists because the host says what has arrived, rather than the step guessing from a visibility
    /// change.</summary>
    public void Arrived() { }

    /// <summary>How many columns the perks are laid out in.</summary>
    private const int PerkColumns = 2;

    /// <summary>
    /// The eight perks, in two columns.
    /// </summary>
    /// <remarks>
    /// <para>The perks are built in code, not in markup. The list is <see cref="SupporterPerks.All"/>,
    /// which the supporter window reads too. A copy in markup would be a second list to keep in step.
    /// That is why the type exists: there is one list.</para>
    ///
    /// <para><b>Down the first column, then the second</b>, which is how a list reads. The declaration
    /// order is deliberate: storage first, community last. Filling across the rows would keep that
    /// order only for a reader who reads the grid like a table.</para>
    /// </remarks>
    private void BuildPerks()
    {
        PerkGrid.Children.Clear();
        PerkGrid.ColumnDefinitions.Clear();
        PerkGrid.RowDefinitions.Clear();

        var perks = SupporterPerks.All;
        int rows = (perks.Count + PerkColumns - 1) / PerkColumns;

        for (var c = 0; c < PerkColumns; c++)
            PerkGrid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var r = 0; r < rows; r++)
            PerkGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < perks.Count; i++)
        {
            var line = Row(perks[i]);
            Grid.SetColumn(line, i / rows);
            Grid.SetRow(line, i % rows);
            PerkGrid.Children.Add(line);
        }
    }

    private static Grid Row(SupporterPerk perk)
    {
        var ink = perk.Gold ? Gold : Cyan;

        var line = new Grid { ColumnSpacing = 0 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GlyphColumn) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var glyph = new FontIcon
        {
            Glyph = perk.Glyph,
            FontSize = GlyphSize,
            FontFamily = FaSolid,
            Foreground = new SolidColorBrush(ink),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(glyph, 0);

        var label = new TextBlock
        {
            Text = Loc.Get(perk.StringKey),
            FontSize = 13,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
        };
        Grid.SetColumn(label, 1);

        line.Children.Add(glyph);
        line.Children.Add(label);
        return line;
    }

    /// <summary>The bundled Font Awesome face. It is named here, not taken from the FaIcon style,
    /// because these icons are built in code and a Style is applied in markup.</summary>
    private static readonly FontFamily FaSolid =
        new("ms-appx:///Assets/Fonts/fa-solid-900.ttf#Font Awesome 5 Pro Solid");

    private void Support_Click(object sender, RoutedEventArgs e)
    {
        SupporterLinks.OpenBecomeSupporter();

        // Then on to the collection. The purchase finishes in a browser, and the app cannot see it happen.
        // Waiting on this screen for it would wait for something that never reports back.
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (_empty)
            ImportRequested?.Invoke(this, EventArgs.Empty);
        else
            Finished?.Invoke(this, EventArgs.Empty);
    }
}
