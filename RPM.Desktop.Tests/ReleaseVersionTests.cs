namespace RPM.Desktop.Tests;

/// <summary>
/// A release's version numbers must all come from one place: its tag. The tag is rpm-v&lt;X.Y.Z&gt;, and
/// nothing else sets the version. The csproj says 0.0.0, which no release has. Both release paths pass the
/// tag's version as the same pair of properties, and the Mac's Info.plist carries it in both of its version
/// keys. scripts/test_release_version.py covers reading the tag and refusing a checkout that the tag does
/// not name.
/// </summary>
public sealed class ReleaseVersionTests
{
    private const string Release = "0.1.10";

    [Fact]
    public void A_build_that_is_not_a_release_is_version_0_0_0()
    {
        // The app reads this through App.VersionText for the About dialog, the Profile page and every
        // error and crash report, so a report from a development build is never filed under a release.
        Assert.Equal(new Version(0, 0, 0, 0), typeof(App).Assembly.GetName().Version);
    }

    [Fact]
    public void The_project_says_0_0_0_until_a_release_passes_its_own()
    {
        var (exit, output) = ReleaseToolsTests.MsBuild("-getProperty:Version", "-getProperty:ApplicationDisplayVersion");
        Assert.True(exit == 0, output);
        Assert.Contains("\"Version\": \"0.0.0\"", output);
        Assert.Contains("\"ApplicationDisplayVersion\": \"0.0.0\"", output);

        (exit, output) = ReleaseToolsTests.MsBuild($"-p:Version={Release}", $"-p:ApplicationDisplayVersion={Release}",
            "-getProperty:Version", "-getProperty:ApplicationDisplayVersion");
        Assert.True(exit == 0, output);
        Assert.Contains($"\"Version\": \"{Release}\"", output);
        Assert.Contains($"\"ApplicationDisplayVersion\": \"{Release}\"", output);
    }

    /// <summary>Uno's SDK sets Version from ApplicationDisplayVersion, so a release that passes Version
    /// alone leaves ApplicationDisplayVersion at the project's 0.0.0. That is why both paths pass
    /// both.</summary>
    [Fact]
    public void Version_alone_leaves_the_display_version_at_the_projects()
    {
        var (exit, output) = ReleaseToolsTests.MsBuild($"-p:Version={Release}",
            "-getProperty:Version", "-getProperty:ApplicationDisplayVersion");
        Assert.True(exit == 0, output);
        Assert.Contains($"\"Version\": \"{Release}\"", output);
        Assert.Contains("\"ApplicationDisplayVersion\": \"0.0.0\"", output);
    }
}
