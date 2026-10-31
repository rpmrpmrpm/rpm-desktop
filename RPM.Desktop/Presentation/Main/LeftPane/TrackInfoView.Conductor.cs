using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RPM.Core.Metadata.Providers;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Controls;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Main.LeftPane;

/// <summary>
/// The conductor's card, at the foot of what the pane says about the record: their photograph, who they
/// are, when they lived, the opening of their Wikipedia article, and where else they are written about.
///
/// <para><b>Asked by credit, and kept by credit.</b> An opera credits one conductor on all of its tracks,
/// so moving from one track to the next asks nothing and redraws nothing: the card is already about the
/// right person. A track with no conductor, or one the encyclopedias do not know, has no card.</para>
/// </summary>
public sealed partial class TrackInfoView
{
    /// <summary>The credit the card is about now, so the next track with the same conductor leaves it be.</summary>
    private string? _conductorCredit;

    private ConductorProfile? _conductor;
    private bool _conductorExpanded;
    private CancellationTokenSource? _conductorLookup;

    /// <summary>The track's conductor, looked up beside the papers. It answers on its own clock: the card
    /// arrives when the encyclopedias do, and nothing else on the pane waits for it.</summary>
    private async Task LoadConductorAsync(PaneBuild build, string? credit)
    {
        var name = EncyclopediaLookup.ConductorName(credit);
        if (string.Equals(name, _conductorCredit, StringComparison.OrdinalIgnoreCase)
            && (name is null || _conductor is not null))
            return;

        _conductorLookup?.Cancel();
        _conductorCredit = name;
        _conductor = null;
        ConductorBlock.Visibility = Visibility.Collapsed;
        if (name is null)
            return;

        var lookup = new CancellationTokenSource();
        _conductorLookup = lookup;

        ConductorProfile? found;
        try
        {
            found = await Dossiers.ConductorAsync(credit, lookup.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (lookup.IsCancellationRequested || !ReferenceEquals(_build, build) || found is null)
            return;

        _conductor = found;
        _conductorExpanded = false;
        PaintConductor(found, lookup.Token);
    }

    private void PaintConductor(ConductorProfile person, CancellationToken ct)
    {
        ConductorName.Text = person.Name;

        // Wikidata writes its occupations in lower case ("conductor"); a line of them on a card starts
        // each with a capital, as a label does.
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

        ApplyArticle(person.Article, _conductorExpanded, ConductorProse, ConductorMoreButton, ConductorSourceButton);

        ConductorLinks.Children.Clear();
        foreach (var link in person.Links)
        {
            var door = new RpmLink { Content = SiteName(link), FontSize = 11 };
            ToolTipService.SetToolTip(door, link.Url);
            door.Click += (_, _) => Open(link.Url);
            ConductorLinks.Children.Add(door);
        }

        ConductorPhoto.Source = null;
        ConductorPlaceholder.Visibility = Visibility.Visible;
        ConductorBlock.Visibility = Visibility.Visible;

        _ = LoadConductorPhotoAsync(person, ct);
    }

    /// <summary>The photograph, through the app's picture cache (see RemoteImage), which identifies the app
    /// the way Wikimedia asks. A photograph that does not come leaves the figure in its place.</summary>
    private async Task LoadConductorPhotoAsync(ConductorProfile person, CancellationToken ct)
    {
        if (person.PhotoUrl is not { Length: > 0 } url)
            return;

        try
        {
            if ((await RemoteImage.LoadAsync(url, ct)).Answers(out var picture, out _)
                && picture is not null && !ct.IsCancellationRequested && ReferenceEquals(_conductor, person))
            {
                ConductorPhoto.Source = picture;
                ConductorPlaceholder.Visibility = Visibility.Collapsed;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ConductorMore_Click(object sender, RoutedEventArgs e)
    {
        _conductorExpanded = !_conductorExpanded;
        ApplyArticle(_conductor?.Article, _conductorExpanded, ConductorProse, ConductorMoreButton, ConductorSourceButton);
    }

    private void ConductorSource_Click(object sender, RoutedEventArgs e) => Open(_conductor?.Article?.Url);
}
