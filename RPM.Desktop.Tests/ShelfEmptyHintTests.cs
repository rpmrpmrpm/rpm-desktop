using RPM.Desktop.Presentation.Main.Library;
using System.Text.Json;

namespace RPM.Desktop.Tests;

/// <summary>
/// What the album wall and the album list put up when there is nothing on them. Nothing on screen has
/// two causes and they do not read alike: a collection nobody has filled yet is an invitation, and a
/// search that matched none of it is an answer.
/// </summary>
public class ShelfEmptyHintTests
{
    [Fact]
    public void An_empty_collection_is_asked_to_be_filled()
    {
        var (glyph, key) = ShelfEmptyHint.Face(ShelfEmpty.Collection);

        Assert.Equal("\uf87d", glyph);   // the note struck through: there is no music here
        Assert.Equal("Library_Empty", key);
    }

    /// <summary>A search that found nothing is not a library with nothing in it, so it neither strikes
    /// the note through nor tells the listener to go and add music they already have.</summary>
    [Fact]
    public void A_search_that_matched_nothing_says_so()
    {
        var (glyph, key) = ShelfEmptyHint.Face(ShelfEmpty.Search);

        Assert.Equal("\uf001", glyph);
        Assert.Equal("Library_NoSearchResults", key);
    }

    /// <summary>The keys are handed out here rather than written at a <c>Loc.Get</c> call, so the
    /// repo-wide guard over <c>strings.json</c> cannot see them: they are checked against the same file
    /// instead. A key nobody wrote renders on screen as the key itself.</summary>
    [Fact]
    public void Both_lines_are_in_the_string_table()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SharedFiles.Path("strings.json")));

        foreach (var empty in new[] { ShelfEmpty.Collection, ShelfEmpty.Search })
        {
            var key = ShelfEmptyHint.Face(empty).Key;

            Assert.True(doc.RootElement.TryGetProperty(key, out var entry), $"{key} is not in strings.json");
            Assert.True(entry.TryGetProperty("en", out var en) && en.GetString()?.Length > 0, $"{key} has no words");
        }
    }

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
