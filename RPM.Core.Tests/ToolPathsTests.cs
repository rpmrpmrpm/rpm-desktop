using RPM.Fingerprint;

namespace RPM.Core.Tests;

/// <summary>
/// Where ffmpeg and fpcalc are found. A published app uses only the copies it ships, so a missing
/// one shows in the launch check instead of being replaced by Homebrew's. A development build may
/// fall back to the machine's copy.
/// </summary>
public sealed class ToolPathsTests : IDisposable
{
    private readonly string _own = Directory.CreateTempSubdirectory("rpm-tools-own-").FullName;
    private readonly string _machine = Directory.CreateTempSubdirectory("rpm-tools-machine-").FullName;

    private static string Exe => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

    public void Dispose()
    {
        Directory.Delete(_own, recursive: true);
        Directory.Delete(_machine, recursive: true);
    }

    [Fact]
    public void A_published_app_does_not_use_a_tool_from_PATH()
    {
        Place(_machine);

        Assert.Equal("ffmpeg", ToolPaths.Resolve("ffmpeg", null, _own, ToolSource.OwnFolder, _machine));
    }

    [Fact]
    public void A_development_build_falls_back_to_PATH()
    {
        var onPath = Place(_machine);

        Assert.Equal(onPath, ToolPaths.Resolve("ffmpeg", null, _own, ToolSource.OwnFolderThenMachine, _machine));
    }

    [Fact]
    public void The_shipped_tool_comes_before_PATH_in_both_kinds_of_build()
    {
        var shipped = Place(_own);
        Place(_machine);

        Assert.Equal(shipped, ToolPaths.Resolve("ffmpeg", null, _own, ToolSource.OwnFolder, _machine));
        Assert.Equal(shipped, ToolPaths.Resolve("ffmpeg", null, _own, ToolSource.OwnFolderThenMachine, _machine));
    }

    [Fact]
    public void An_explicit_override_wins_in_a_published_app()
    {
        Place(_own);
        var chosen = Place(_machine);

        Assert.Equal(chosen, ToolPaths.Resolve("ffmpeg", chosen, _own, ToolSource.OwnFolder, null));
    }

    private static string Place(string folder)
    {
        var path = Path.Combine(folder, Exe);
        File.WriteAllText(path, "");
        return path;
    }
}
