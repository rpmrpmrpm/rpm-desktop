using Microsoft.Extensions.DependencyInjection;
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
        Kicker.Text = WizardSteps.Kicker(WizardSteps.Count);
        BuildPerks();

        // Hidden until the account's standing is read on arrival (see Arrived).
        ShowAsk(false);
    }

    /// <summary>
    /// Whether the last step asks for support: the card with the perks, and the Become a supporter
    /// button.
    ///
    /// <para>It asks an account that the server has said is not a supporter, and it asks when nobody is
    /// signed in, because then there is no account that could already support rpm. A closed beta tester
    /// has the supporter features, so is not asked, as on the cloud settings page.</para>
    ///
    /// <para><b>An unknown standing does not ask.</b> A supporter who is asked to support rpm because a
    /// request failed has been told something that is not true.</para>
    /// </summary>
    /// <param name="signedIn">Whether there is an account. No default: signed out asks, and an account
    /// with no answer does not, so every caller must say which it is.</param>
    /// <param name="standing">What this session has heard from the server, or null.</param>
    public static bool AsksForSupport(bool signedIn, SupporterService.Status? standing) =>
        !signedIn || standing is { IsSupporter: false };

    /// <summary>
    /// The import's result, from the step before.
    ///
    /// <para><b>When nothing was imported, no count is shown.</b> A line that says "Nothing imported
    /// yet" would only repeat the absence of the count. The second button carries that news instead, by
    /// being the action to take.</para>
    /// </summary>
    /// <summary>For a library made from music the person already had. Nothing was imported, so there is
    /// nothing to count yet: the catalogue read that fills the library starts when it opens.</summary>
    public void ShowAdopted()
    {
        _empty = false;
        AlbumsText.Text = Loc.Get("Wizard_DoneAdopted");
        TracksText.Text = "";
        FinishButton.Content = Loc.Get("Wizard_Finish");
    }

    public void Show(ImportOutcome outcome)
    {
        _empty = outcome.Albums <= 0;

        AlbumsText.Text = _empty ? "" : Loc.Format("Wizard_DoneAlbums", outcome.Albums);
        TracksText.Text = _empty ? "" : Loc.Format("Wizard_DoneTracks", outcome.Tracks);

        // Opening an empty collection would open nothing. With no music filed, the useful step is to go and
        // import some.
        FinishButton.Content = Loc.Get(_empty ? "Wizard_GoImport" : "Wizard_Finish");
    }

    /// <summary>Called by the host once this step is on screen. The step reads the account's standing
    /// here, rather than the step guessing from a visibility change, so the ask follows the account
    /// signed in now.</summary>
    public void Arrived() => _ = ReadStandingAsync();

    /// <summary>Counts the reads of the standing. An answer that arrives after a newer read has started
    /// is dropped, so a slow answer cannot hide or show the ask over a newer one.</summary>
    private int _reads;

    private async Task ReadStandingAsync()
    {
        var read = ++_reads;
        var auth = App.Services.GetRequiredService<AuthService>();
        var supporter = App.Services.GetRequiredService<SupporterService>();

        // What this session has already heard draws at once; the answer to the new request follows.
        ShowAsk(AsksForSupport(auth.IsLoggedIn, supporter.Current));
        if (!auth.IsLoggedIn)
            return;

        // GetAsync gives null on any failure. The standing heard before is kept then.
        var heard = await supporter.GetAsync() ?? supporter.Current;
        if (read != _reads)
            return;

        ShowAsk(AsksForSupport(auth.IsLoggedIn, heard));
    }

    private void ShowAsk(bool asks)
    {
        var shown = asks ? Visibility.Visible : Visibility.Collapsed;
        SupportCard.Visibility = shown;
        SupportButton.Visibility = shown;
    }

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
        new("ms-appx:///Assets/Fonts/fa-solid-900.ttf#Font Awesome 5 Pro");

    private void Support_Click(object sender, RoutedEventArgs e)
    {
        _ = SupporterLinks.OpenBecomeSupporterAsync();

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
