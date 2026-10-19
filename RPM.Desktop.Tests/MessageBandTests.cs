using System.Text.RegularExpressions;

namespace RPM.Desktop.Tests;

/// <summary>The message band's colours are named in markup and asked for by name from code. The compiler
/// does not check this join. A band that lost its fill is an example: the keys moved to the application's
/// dictionary, but the page's code-behind still asked the page for them. A miss there is not an error. It
/// gives a null brush and a bare band. These tests guard the join.</summary>
public class MessageBandTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "RPM.Desktop")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found from the test binary");
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([Root, "RPM.Desktop", .. parts]));

    /// <summary>Every voice must have all four colours. If one is missing, the framework returns a default
    /// for it, and part of the band is painted with that default.</summary>
    [Fact]
    public void EveryVoiceHasItsFourColours()
    {
        var band = Read("Themes", "MessageBand.xaml");
        foreach (var voice in new[] { "BandError", "BandWarning", "BandInfo" })
            foreach (var role in new[] { "Fill", "Edge", "Mark", "Ink" })
                Assert.Contains($"x:Key=\"{voice}{role}\"", band);
    }

    /// <summary>The colours are app-wide, not page-level, because the mass-edit window also shows this
    /// band.</summary>
    [Fact]
    public void TheAppMergesTheBandDictionary()
    {
        Assert.Contains("Themes/MessageBand.xaml", Read("App.xaml"));
    }

    /// <summary>
    /// StaticResource in markup finds the application's resources on its own. A lookup from code on a page
    /// does not. Asking a page for a band colour returns nothing, and the band paints bare.
    ///
    /// <para>The check scans every file, not one named page. A test that reads one fixed path stops
    /// guarding anything when that file is moved or split.</para>
    /// </summary>
    [Fact]
    public void NoBandColourIsAskedOfThePage()
    {
        var pageLookup = new Regex(@"(?<!Application\.Current\.)Resources\[[^\]]*""Band");

        var offenders = Directory
            .EnumerateFiles(Path.Combine(Root, "RPM.Desktop"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(path => pageLookup.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(Root, path))
            .ToList();

        Assert.Empty(offenders);
    }
}
