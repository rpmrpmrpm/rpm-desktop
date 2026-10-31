using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Metadata.Providers;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Controls;
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
    /// <summary>The logo's box: its width, and the height the article runs beside before it carries on
    /// underneath. The plate's padding is inside both.</summary>
    private const double LabelLogoWidth = 144;

    private const double LabelLogoHeight = 96;

    /// <summary>The plate's padding around the logo, as the XAML sets it.</summary>
    private const double LabelLogoInset = 10;

    private LabelProfile? _label;
    private bool _labelExpanded;

    /// <summary>The label a record's tags name, and its logo, cut to the size the card draws it at. Never
    /// throws: a label that cannot be found or fetched is no card.</summary>
    /// <param name="rasterScale">The display's scale, so the logo is cut to physical pixels.</param>
    private static async Task<(LabelProfile? Label, ImageSource? Logo)> LabelFor(string? tag, double rasterScale)
    {
        if (EncyclopediaLookup.LabelName(tag) is null)
            return (null, null);

        try
        {
            if (await Dossiers.LabelAsync(tag) is not { } label)
                return (null, null);

            ImageSource? logo = null;
            if (label.LogoUrl is { Length: > 0 } url
                && (await RemoteImage.LoadToFitAsync(url, LabelLogoWidth - 2 * LabelLogoInset,
                                                     LabelLogoHeight - 2 * LabelLogoInset, rasterScale))
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
    private void PaintLabel(LabelProfile? label, ImageSource? logo)
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

        // No logo, no plate: the column goes, and the text has the card's whole width.
        LabelLogo.Source = logo;
        LabelLogoColumn.Width = new GridLength(logo is null ? 0 : LabelLogoWidth);
        LabelTop.ColumnSpacing = logo is null ? 0 : 14;
        LabelLogoPlate.Visibility = logo is null ? Visibility.Collapsed : Visibility.Visible;

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
        var picture = LabelLogoPlate.Visibility == Visibility.Visible ? LabelLogoHeight : double.PositiveInfinity;
        FlowBeside(text, picture, LabelBeside, [LabelName, LabelYears, LabelGenres], LabelProse, LabelProseBelow);
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
