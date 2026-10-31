using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Metadata.Providers;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Controls;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>
/// The conductor's card, under the album's: their photograph, who they are, when they lived, the opening
/// of their Wikipedia article, and where else they are written about.
///
/// <para><b>Part of the papers.</b> The person and their photograph are fetched beside the papers and
/// handed to the build with them (see LookUpAsync), and the card is painted when the album's layer goes
/// up, in the same section. So the section arrives whole, and the card is never the one block that turns
/// up late. An opera credits one conductor on every track, and the answer is kept by credit (see
/// TrackInfoService.ConductorAsync), so the next track asks nothing.</para>
/// </summary>
public sealed partial class TrackInfoView
{
    /// <summary>The height of the photograph, which is how far the article runs beside it before it carries
    /// on underneath.</summary>
    private const double ConductorPhotoHeight = 148;

    private ConductorProfile? _conductor;
    private bool _conductorExpanded;

    /// <summary>The conductor a track credits, and their photograph, decoded. Never throws: a conductor who
    /// cannot be found or fetched is no card, and the papers go up without one.</summary>
    private static async Task<(ConductorProfile? Who, ImageSource? Photo)> ConductorFor(string? credit)
    {
        if (EncyclopediaLookup.ConductorName(credit) is null)
            return (null, null);

        try
        {
            if (await Dossiers.ConductorAsync(credit) is not { } who)
                return (null, null);

            ImageSource? photo = null;
            if (who.PhotoUrl is { Length: > 0 } url
                && (await RemoteImage.LoadAsync(url)).Answers(out var picture, out _))
                photo = picture;
            return (who, photo);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>Paints the card from what the errand brought. Synchronous, like every layer's paint: nothing
    /// is fetched here.</summary>
    private void PaintConductor(ConductorProfile? person, ImageSource? photo)
    {
        _conductor = person;
        _conductorExpanded = false;
        if (person is null)
        {
            ConductorBlock.Visibility = Visibility.Collapsed;
            return;
        }

        ConductorName.Text = person.Name;

        // Wikidata writes its occupations in lower case ("conductor"); a line of them on a card starts each
        // with a capital, as a label does.
        var culture = CultureInfo.CurrentUICulture;
        ConductorRoles.Text = string.Join("  ·  ", person.Roles.Select(r =>
            r.Length == 0 ? r : char.ToUpper(r[0], culture) + r[1..]));
        ConductorRoles.Visibility = person.Roles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        ConductorYears.Text = (person.Born, person.Died) switch
        {
            ({ } born, { } died) => $"{born} – {died}",
            ({ } born, null) => Loc.Format("TrackInfo_ConductorBorn", born),
            (null, { } died) => $"– {died}",
            _ => "",
        };
        ConductorYears.Visibility = ConductorYears.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        ConductorPhoto.Source = photo;
        ConductorPlaceholder.Visibility = photo is null ? Visibility.Visible : Visibility.Collapsed;

        ConductorLinks.Children.Clear();
        foreach (var link in person.Links)
        {
            var door = new RpmLink { Content = SiteName(link), FontSize = 11 };
            ToolTipService.SetToolTip(door, link.Url);
            door.Click += (_, _) => Open(link.Url);
            ConductorLinks.Children.Add(door);
        }

        ConductorBlock.Visibility = Visibility.Visible;
        PaintConductorArticle();
    }

    /// <summary>The article's text and its two links, as the album card shows its own, then the text
    /// measured into its two places.</summary>
    private void PaintConductorArticle()
    {
        var article = _conductor?.Article;
        ConductorMoreButton.Visibility = article is { HasMore: true } ? Visibility.Visible : Visibility.Collapsed;
        ConductorMoreButton.Content = Loc.Get(_conductorExpanded ? "TrackInfo_ShowLess" : "TrackInfo_ShowMore");
        ConductorSourceButton.Visibility = article is null ? Visibility.Collapsed : Visibility.Visible;
        ConductorSourceButton.Content = Loc.Get("TrackInfo_ReadOnWikipedia");
        FlowConductorProse();
    }

    /// <summary>
    /// Runs the article around the photograph: as many words as fit beside it, under the name and the
    /// years, and the rest underneath at the card's full width.
    ///
    /// <para>Measured rather than left to the text engine, because WinUI has no text that flows around a
    /// picture, and RichTextBlockOverflow, the one way to split a text across two boxes, is not drawn by the
    /// Skia head. A box of the same style is measured at the column's width for a growing number of words, by
    /// halves, and the split falls on the last word that still fits. Done again whenever the column changes
    /// width, and when the reader asks for more.</para>
    /// </summary>
    private void FlowConductorProse()
    {
        var text = _conductor?.Article is { } article ? (_conductorExpanded ? article.Extract : article.Lead) : "";
        var width = ConductorBeside.ActualWidth;

        if (text.Length == 0)
        {
            ConductorProse.Text = ConductorProseBelow.Text = "";
            ConductorProse.Visibility = ConductorProseBelow.Visibility = Visibility.Collapsed;
            return;
        }

        // Before the first layout there is no width to measure against. Everything goes beside the photograph
        // for that frame, and the column's first SizeChanged flows it properly.
        if (width <= 0)
        {
            Split(text, "");
            return;
        }

        // A line belongs beside the photograph if it STARTS before the photograph ends. The last one may hang a
        // little below the picture's foot; the text under it then carries on from that line, with no gap, so the
        // paragraph reads straight through the join.
        var room = ConductorPhotoHeight - HeadHeight(width) - ConductorProse.Margin.Top
                   + ConductorProse.LineHeight - 4;
        var words = text.Split(' ');
        var probe = new TextBlock { Style = ConductorProse.Style, Width = width };

        double Height(int count)
        {
            probe.Text = string.Join(' ', words, 0, count);
            probe.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
            return probe.DesiredSize.Height;
        }

        var fits = 0;
        if (room > 0)
        {
            int low = 0, high = words.Length;
            while (low < high)
            {
                var mid = (low + high + 1) / 2;
                if (Height(mid) <= room) low = mid; else high = mid - 1;
            }
            fits = low;
        }

        Split(string.Join(' ', words, 0, fits), string.Join(' ', words, fits, words.Length - fits).TrimStart());

        void Split(string beside, string below)
        {
            ConductorProse.Text = beside;
            ConductorProse.Visibility = beside.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            ConductorProseBelow.Text = below;
            ConductorProseBelow.Visibility = below.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>How tall the name, the roles and the years stand at this width, with the gaps between them
    /// and the one under them.</summary>
    private double HeadHeight(double width)
    {
        var height = 0.0;
        var shown = 0;
        foreach (var line in new[] { ConductorName, ConductorRoles, ConductorYears })
        {
            if (line.Visibility != Visibility.Visible)
                continue;
            line.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
            height += line.DesiredSize.Height;
            shown++;
        }
        return height + ConductorBeside.Spacing * shown;
    }

    private double _conductorBesideWidth;

    private void ConductorBeside_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Only a change of width moves the split. A change of height is the split itself landing.
        if (Math.Abs(e.NewSize.Width - _conductorBesideWidth) < 0.5)
            return;
        _conductorBesideWidth = e.NewSize.Width;
        FlowConductorProse();
    }

    private void ConductorMore_Click(object sender, RoutedEventArgs e)
    {
        _conductorExpanded = !_conductorExpanded;
        PaintConductorArticle();
    }

    private void ConductorSource_Click(object sender, RoutedEventArgs e) => Open(_conductor?.Article?.Url);
}
