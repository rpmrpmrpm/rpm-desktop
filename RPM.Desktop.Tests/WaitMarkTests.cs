using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// The app has one wait mark: the turning record (Controls/RecordSpinner). Every loader uses it. The
/// platform's ProgressRing is easy to reach for in a new screen, and nothing else would notice one coming back.
/// </summary>
public class WaitMarkTests
{
    [Fact]
    public void No_screen_shows_the_platform_ring()
    {
        var app = Path.Combine(RepoRoot(), "RPM.Desktop");
        var ring = new Regex(@"<ProgressRing\b|new ProgressRing\b");

        var found = Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                           && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(path => ring.IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(app, path))
            .ToList();

        Assert.True(found.Count == 0, "a ProgressRing where the turning record belongs: " + string.Join(", ", found));
    }

    /// <summary>Walks up from the test binaries to the folder holding RPM.sln.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RPM.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
