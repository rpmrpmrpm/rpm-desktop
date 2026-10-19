using System.Text.RegularExpressions;

namespace RPM.Desktop.Tests;

/// <summary>
/// A track dragged to a new place in a playlist does not move. The rows stay where they are, and their
/// FACES change places (PlaylistTrackRow.Show, driven by CarriedReorder). A PropertyChanged is the only
/// thing that tells a line it now shows a different song. So a face field bound OneTime is a line that
/// reads its song once and never hears the exchange.
///
/// <para>This is not a subtle wrong picture. The gesture is inverted. The offsets carry on under the frozen
/// text, so the row under the pointer is the one dragged OVER, not the one picked up. The drag then ends
/// with nothing apparently moved. x:Bind is OneTime unless told otherwise, so this mistake is easy to make
/// by default.</para>
///
/// <para>Driven off <c>Show</c> itself rather than a list written out here: what the exchange announces is
/// the definition of a face field, so a field added there is covered the day it is added.</para>
/// </summary>
public class PlaylistRowFaceBindingTests
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

    /// <summary>The names <see cref="RPM.Desktop.Presentation.Main.LeftPane.Models.PlaylistTrackRow.Show"/>
    /// announces, read off the array it announces them from.</summary>
    private static HashSet<string> Announced()
    {
        var source = Read("Presentation", "Main", "LeftPane", "Models", "PlaylistModel.Detail.cs");
        var at = source.IndexOf("public void Show(PlaylistTrackRow other)", StringComparison.Ordinal);
        Assert.True(at > 0, "PlaylistTrackRow.Show was renamed — this guard is looking for the exchange.");

        var body = source[at..];
        var list = body.IndexOf("foreach (var name in new[]", StringComparison.Ordinal);
        Assert.True(list > 0, "Show no longer announces its fields from one array — re-point this guard.");

        var end = body.IndexOf("})", list, StringComparison.Ordinal);
        var names = Regex.Matches(body[list..end], @"nameof\((\w+)\)").Select(m => m.Groups[1].Value).ToHashSet();
        Assert.NotEmpty(names);
        return names;
    }

    /// <summary>The row template, and only it: the page binds other things to other shapes.</summary>
    private static string RowTemplate()
    {
        var page = Read("Presentation", "Main", "RightPane", "PlaylistView.xaml");
        var at = page.IndexOf("<DataTemplate x:DataType=\"model:PlaylistTrackRow\">", StringComparison.Ordinal);
        Assert.True(at > 0, "the playlist's row template was renamed or moved");
        var end = page.IndexOf("</DataTemplate>", at, StringComparison.Ordinal);
        return page[at..end];
    }

    /// <summary>Every field the exchange announces is bound OneWay wherever the row template shows
    /// it.</summary>
    [Fact]
    public void EveryFaceFieldTheTemplateShowsIsOneWay()
    {
        var announced = Announced();

        foreach (Match bind in Regex.Matches(RowTemplate(), @"\{x:Bind\s+(\w+)([^}]*)\}"))
        {
            var name = bind.Groups[1].Value;
            if (!announced.Contains(name))
                continue;

            Assert.True(bind.Groups[2].Value.Contains("Mode=OneWay", StringComparison.Ordinal),
                        $"{name} is a face field and is bound OneTime: a drag would freeze the list and " +
                        "carry the wrong row. Add Mode=OneWay.");
        }
    }

    /// <summary>The other half of the same rule. A row's number says where the SLOT stands in the list, so
    /// it must not travel with the face. It is left OneTime on purpose. The exchange must keep saying
    /// nothing about it, or the column would count 1, 2, 2, 4 during a drag.</summary>
    [Fact]
    public void ThePositionColumnStaysWithItsSlot()
    {
        Assert.DoesNotContain("Position", Announced());
        Assert.Contains("{x:Bind Position}", RowTemplate());
    }
}
