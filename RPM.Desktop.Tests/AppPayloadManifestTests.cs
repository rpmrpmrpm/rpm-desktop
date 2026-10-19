using System.Xml.Linq;

using RPM.Core.Startup;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Tests;

/// <summary>
/// What the installer has to install, and whether each entry is Required or Optional.
///
/// <para>Severity is the main content of this manifest and it is one word per entry, so it is easy
/// to change by accident and not notice until a later release. Required means the app refuses to
/// start without it; Optional means it starts and silently does less. Both are right for some
/// entries, and a mistake is costly either way: an Optional that should be Required ships a build
/// that looks healthy and behaves wrongly, and a Required that should be Optional refuses to open
/// because of a feature nobody was using.</para>
/// </summary>
public class AppPayloadManifestTests
{
    private static PreflightItem Item(string name) =>
        AppPayload.Manifest.Single(i => i.Name == name);

    [Fact]
    public void TheBeatModelIsRequired()
    {
        // Every DJ feature is timed against its grid, and there is no other way to get one.
        Assert.Equal(PreflightSeverity.Required, Item("beat_this.onnx").Severity);
    }

    /// <summary>
    /// Every model the preflight requires is one the build includes and does not exclude. If the
    /// shipped beat model changes, a preflight that still asks for the other file would refuse to
    /// open every release, and no other test would notice before a release did.
    /// </summary>
    [Fact]
    public void Every_required_model_is_one_the_build_ships()
    {
        var csproj = XDocument.Load(Path.Combine(ReleaseToolsTests.RepoRoot(), "RPM.Desktop", "RPM.Desktop.csproj"));
        var included = csproj.Descendants()
            .Select(e => e.Attribute("Include")?.Value.Replace('\\', '/'))
            .OfType<string>()
            .Select(Path.GetFileName)
            .ToHashSet();
        var excluded = csproj.Descendants("RpmAssetsKeptOut")
            .Where(e => e.Attribute("Condition") is null && e.Parent?.Attribute("Condition") is null)
            .SelectMany(e => e.Value.Split(';'))
            .Select(Path.GetFileName)
            .ToHashSet();

        var required = AppPayload.Manifest
            .Where(i => i.Severity == PreflightSeverity.Required && i.Name.EndsWith(".onnx", StringComparison.Ordinal))
            .Select(i => i.Name)
            .ToList();

        Assert.NotEmpty(required);
        Assert.All(required, name =>
        {
            Assert.Contains(name, included);
            Assert.DoesNotContain(name, excluded);
        });
    }

    [Fact]
    public void TheKeyModelIsRequired()
    {
        Assert.Equal(PreflightSeverity.Required, Item("skey.onnx").Severity);
    }

    [Fact]
    public void TheFaceModelIsRequired()
    {
        // Not the same trade-off as saliency, which is the point of this test. Faces decide WHICH
        // backdrop an artist header gets (BackdropPreselector scores the shortlist on them); without
        // them the ranking uses only shape and the header picks the wrong photograph, with nothing
        // on screen to show it.
        Assert.Equal(PreflightSeverity.Required, Item("ultraface.onnx").Severity);
    }

    [Fact]
    public void TheSaliencyModelIsOptional()
    {
        // The other side of that line, checked here so the two cannot be confused: this one is
        // applied to a picture already chosen, so losing it costs a crop and not a choice.
        Assert.Equal(PreflightSeverity.Optional, Item("u2netp.onnx").Severity);
    }

    [Fact]
    public void TheAlignerIsNotInTheManifestAtAll()
    {
        // wav2vec2 times a lyric sheet's words to the singer rather than per line, and OnnxAligner
        // is built to work without it: the sheet keeps the line-level timing it already had. Listing
        // it here would make the app refuse to open because of a decoration.
        Assert.DoesNotContain(AppPayload.Manifest, i => i.Name.StartsWith("wav2vec2"));
    }

    [Fact]
    public void EveryEntryIsNamedOnce()
    {
        // Two entries with one name give a report that lists the same failure twice, and a manifest
        // whose second copy can be edited while the first one is still the one used.
        Assert.Equal(
            AppPayload.Manifest.Count,
            AppPayload.Manifest.Select(i => i.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryEntryHasAFloorWorthCheckingAndALineToShow()
    {
        // MinBytes catches a file that IS there but is not usable: a zero-byte placeholder, or the
        // empty file an antivirus leaves behind. Left at the default it would accept a 2 KB stub as an
        // 80 MB model. And an entry with no purpose key fails with a blank line beside it.
        foreach (var item in AppPayload.Manifest)
        {
            Assert.True(item.MinBytes >= 100_000, $"{item.Name} has no meaningful floor");
            Assert.False(string.IsNullOrWhiteSpace(item.PurposeKey), $"{item.Name} has no purpose line");
        }
    }
}
