using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using RPM.Core.Metadata.Providers;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Controls;
using RPM.Desktop.Services.Art;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>
/// The record label's card, under the conductor's, or under the album's on a record with no conductor: its
/// name, when it was active, what it is known for, its logo, the opening of its Wikipedia article, and where
/// else it is written about.
///
/// <para><b>Part of the papers, as the conductor is.</b> The label and its logo are fetched beside the
/// papers and handed to the build with them (see LookUpAsync), so the card goes up with the album's and never
/// arrives on its own. The answer is kept by the tag's name (see TrackInfoService.LabelAsync), so the next
/// record on the same label asks nothing.</para>
/// </summary>
public sealed partial class TrackInfoView
{
    /// <summary>The box the logo fits inside: at most this wide, and at most this tall, which is as far as
    /// the article runs beside it before it carries on underneath.</summary>
    private const double LabelLogoWidth = 144;

    private const double LabelLogoHeight = 96;

    private LabelProfile? _label;
    private bool _labelExpanded;

    /// <summary>The label a record's tags name, and its logo, cut to the size the card draws it at. Never
    /// throws: a label that cannot be found or fetched is no card.</summary>
    /// <param name="rasterScale">The display's scale, so the logo is cut to physical pixels.</param>
    /// <param name="ink">The card's writing colour, which a logo in one dark colour is repainted in.</param>
    private static async Task<(LabelProfile? Label, BitmapImage? Logo)> LabelFor(
        string? tag, double rasterScale, Windows.UI.Color ink)
    {
        if (EncyclopediaLookup.LabelName(tag) is null)
            return (null, null);

        try
        {
            if (await Dossiers.LabelAsync(tag) is not { } label)
                return (null, null);

            var pen = new SkiaSharp.SKColor(ink.R, ink.G, ink.B, ink.A);
            BitmapImage? logo = null;
            if (label.LogoUrl is { Length: > 0 } url
                && (await RemoteImage.LoadToFitAsync(url, LabelLogoWidth, LabelLogoHeight, rasterScale,
                                                     cut => ArtScaler.InkMark(cut, pen)))
                    .Answers(out var picture, out _))
                logo = picture;
            return (label, logo);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>Paints the card from what the errand brought. Synchronous, like every layer's paint.</summary>
    private void PaintLabel(LabelProfile? label, BitmapImage? logo)
    {
        _label = label;
        _labelExpanded = false;
        if (label is null)
        {
            HideCard(LabelMotion);
            return;
        }

        // A "Show more" still growing on the last record's label is stopped and its height let go, or the new
        // card would open at the old one's height.
        LabelMotion.Board?.Stop();
        LabelPage.Height = double.NaN;

        LabelName.Text = label.Name;

        LabelYears.Text = (label.Founded, label.Ended) switch
        {
            ({ } founded, { } ended) => Loc.Format("TrackInfo_LabelFromTo", founded, ended),
            ({ } founded, null) => Loc.Format("TrackInfo_LabelSince", founded),
            (null, { } ended) => Loc.Format("TrackInfo_LabelClosed", ended),
            _ => "",
        };
        LabelYears.Visibility = LabelYears.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Wikidata writes its genres in lower case ("classical music"); a line of them on a card starts each
        // with a capital, as the conductor's roles do.
        var culture = System.Globalization.CultureInfo.CurrentUICulture;
        LabelGenres.Text = string.Join("  ·  ", label.Genres.Select(g =>
            g.Length == 0 ? g : char.ToUpper(g[0], culture) + g[1..]));
        LabelGenres.Visibility = label.Genres.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        // The logo at the size its own shape gives inside the box, and its column no wider than it, so a tall
        // mark leaves the text more room than a wide one. No logo, no column: the text has the whole width.
        var (wide, tall) = LogoSize(logo);
        _labelLogoHeight = tall;
        LabelLogo.Source = logo;
        LabelLogo.Width = wide;
        LabelLogo.Height = tall;
        LabelLogoColumn.Width = new GridLength(wide);
        LabelTop.ColumnSpacing = logo is null ? 0 : 14;
        LabelLogo.Visibility = logo is null ? Visibility.Collapsed : Visibility.Visible;

        LabelLinks.Children.Clear();
        foreach (var link in label.Links)
        {
            var door = new RpmLink { Content = SiteName(link), FontSize = 11 };
            ToolTipService.SetToolTip(door, link.Url);
            door.Click += (_, _) => Open(link.Url);
            LabelLinks.Children.Add(door);
        }

        LabelBlock.Visibility = Visibility.Visible;
        PaintLabelArticle();
    }

    /// <summary>The article's text and its two links, as the conductor's card shows its own.</summary>
    private void PaintLabelArticle()
    {
        var article = _label?.Article;
        LabelMoreButton.Visibility = article is { HasMore: true } ? Visibility.Visible : Visibility.Collapsed;
        LabelMoreButton.Content = Loc.Get(_labelExpanded ? "TrackInfo_ShowLess" : "TrackInfo_ShowMore");
        LabelSourceButton.Visibility = article is null ? Visibility.Collapsed : Visibility.Visible;
        LabelSourceButton.Content = Loc.Get("TrackInfo_ReadOnWikipedia");
        FlowLabelProse();
    }

    /// <summary>The article beside the logo and then under it, as the conductor's runs around the
    /// photograph. With no logo there is nothing to run around.</summary>
    private void FlowLabelProse()
    {
        var text = _label?.Article is { } article ? (_labelExpanded ? article.Extract : article.Lead) : "";
        var picture = LabelLogo.Visibility == Visibility.Visible ? _labelLogoHeight : double.PositiveInfinity;
        FlowBeside(text, picture, LabelBeside, [LabelName, LabelYears, LabelGenres], LabelProse, LabelProseBelow);
    }

    /// <summary>How tall the logo on screen stands: what the article runs beside.</summary>
    private double _labelLogoHeight;

    /// <summary>The logo fitted whole inside the box, by its own proportions. Nothing for no logo; the whole
    /// box for one whose size is not known yet.</summary>
    private static (double Width, double Height) LogoSize(BitmapImage? logo)
    {
        if (logo is null)
            return (0, 0);
        if (logo.PixelWidth <= 0 || logo.PixelHeight <= 0)
            return (LabelLogoWidth, LabelLogoHeight);

        var aspect = (double)logo.PixelWidth / logo.PixelHeight;
        var width = Math.Min(LabelLogoWidth, LabelLogoHeight * aspect);
        return (Math.Round(width), Math.Round(width / aspect));
    }

    private double _labelBesideWidth;

    private void LabelBeside_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Only a change of width moves the split. A change of height is the split itself landing.
        if (Math.Abs(e.NewSize.Width - _labelBesideWidth) < 0.5)
            return;
        _labelBesideWidth = e.NewSize.Width;
        FlowLabelProse();
    }

    /// <summary>Grows into the rest of the article and shrinks back out of it, as the other cards
    /// do.</summary>
    private void LabelMore_Click(object sender, RoutedEventArgs e)
    {
        var was = LabelPage.ActualHeight;

        _labelExpanded = !_labelExpanded;
        PaintLabelArticle();
        MoveCard(LabelMotion, LabelBlock.Visibility == Visibility.Visible, way: 0, was);
    }

    private void LabelSource_Click(object sender, RoutedEventArgs e) => Open(_label?.Article?.Url);

    private CardMotion? _labelMotion;
    private CardMotion LabelMotion => _labelMotion ??= new CardMotion(LabelBlock, LabelPage, LabelPageShift);
}
