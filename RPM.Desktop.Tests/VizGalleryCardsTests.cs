using System.Text.RegularExpressions;

namespace RPM.Desktop.Tests;

/// <summary>
/// The two things a new visualizer can be added without, both of which fail quietly. A card whose still is
/// missing shows a black rectangle in the gallery, and a card whose id the full-screen host has no branch
/// for launches the ribbon instead: the gallery is the only caller, so an unknown id is read there as a
/// catalogue that has moved on rather than as a mistake.
///
/// Read from the source rather than from <c>VizCatalog.All</c>: the catalogue resolves its words through
/// the resource loader, which is the running app's and not a test host's. The words themselves are guarded by
/// <c>LocalizationKeyTests</c> against strings.json.
/// </summary>
public class VizGalleryCardsTests
{
    [Fact]
    public void Every_card_has_its_still()
    {
        var stills = Regex.Matches(CatalogSource(), @"ms-appx:///Assets/Viz/([A-Za-z0-9\-\.]+)")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(stills);
        foreach (var still in stills)
        {
            var path = Path.Combine(RepoRoot(), "RPM.Desktop", "Assets", "Viz", still);
            Assert.True(File.Exists(path), $"{still} is on a gallery card but not in Assets/Viz");
        }
    }

    [Fact]
    public void Every_catalog_id_is_launched_by_a_stage_of_its_own()
    {
        var host = File.ReadAllText(Path.Combine(
            RepoRoot(), "RPM.Desktop", "Presentation", "Viz", "VizFullScreen.xaml.cs"));

        var ids = Regex.Matches(CatalogSource(), @"public const string ([A-Za-z0-9_]+) = ""[a-z0-9\-]+"";")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Contains("Butterfly", ids);
        foreach (var id in ids)
        {
            // RibbonSea is the else, so it is named by falling through rather than by a branch of its own.
            if (id == "RibbonSea")
                continue;
            Assert.True(host.Contains($"VizCatalog.{id}", StringComparison.Ordinal),
                $"{id} is in the gallery but the full-screen host has no stage for it");
        }
    }

    private static string CatalogSource() => File.ReadAllText(Path.Combine(
        RepoRoot(), "RPM.Desktop", "Presentation", "Viz", "VizCatalog.cs"));

    /// <summary>Walks up from the test binaries to the folder holding RPM.sln.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RPM.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir.FullName;
    }
}
