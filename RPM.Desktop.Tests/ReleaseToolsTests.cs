using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace RPM.Desktop.Tests;

/// <summary>
/// Every release publishes RPM.Desktop. Nothing else checks that ffmpeg and fpcalc are in the release, so a
/// failed download could ship an app that decodes nothing on a machine without Homebrew. PublishRpmTools in
/// RPM.Desktop.csproj fails the publish unless both binaries are present at the SHA-256 that
/// scripts/tool-versions.txt pins for the RID. These tests run that target through MSBuild, against a
/// scratch tools folder and a scratch pins file. So they need neither the real binaries nor the network.
/// </summary>
public sealed class ReleaseToolsTests : IDisposable
{
    private const string Rid = "osx-arm64";

    private readonly string _scratch = Directory.CreateTempSubdirectory("rpm-release-tools-").FullName;

    private string Tools => Path.Combine(_scratch, "tools");

    public void Dispose() => Directory.Delete(_scratch, recursive: true);

    [Fact]
    public void A_publish_with_both_pinned_binaries_ships_them()
    {
        var ffmpeg = WriteTool("ffmpeg", "the pinned ffmpeg");
        var fpcalc = WriteTool("fpcalc", "the pinned fpcalc");
        WritePins(("FFMPEG", ffmpeg), ("FPCALC", fpcalc));

        var (exit, output) = Publish(Rid);

        Assert.True(exit == 0, output);
        Assert.Contains($"Publishing ffmpeg for {Rid}", output);
        Assert.Contains($"Publishing fpcalc for {Rid}", output);
    }

    [Fact]
    public void A_publish_without_fpcalc_fails()
    {
        var ffmpeg = WriteTool("ffmpeg", "the pinned ffmpeg");
        WritePins(("FFMPEG", ffmpeg), ("FPCALC", Sha256("an fpcalc that was never downloaded")));

        var (exit, output) = Publish(Rid);

        Assert.NotEqual(0, exit);
        Assert.Contains($"{Path.Combine(Tools, "fpcalc")} is missing", output);
    }

    [Fact]
    public void A_publish_with_a_binary_that_is_not_the_pinned_one_fails()
    {
        WriteTool("ffmpeg", "some other ffmpeg");
        var fpcalc = WriteTool("fpcalc", "the pinned fpcalc");
        var pinned = Sha256("the pinned ffmpeg");
        WritePins(("FFMPEG", pinned), ("FPCALC", fpcalc));

        var (exit, output) = Publish(Rid);

        Assert.NotEqual(0, exit);
        Assert.Contains($"pins FFMPEG_BINARY_SHA256_OSX_ARM64={pinned}", output);
        Assert.DoesNotContain("Publishing ffmpeg", output);
    }

    [Fact]
    public void A_publish_for_a_rid_with_no_pin_fails()
    {
        var ffmpeg = WriteTool("ffmpeg", "the pinned ffmpeg");
        var fpcalc = WriteTool("fpcalc", "the pinned fpcalc");
        WritePins(("FFMPEG", ffmpeg), ("FPCALC", fpcalc));

        var (exit, output) = Publish("osx-x64");

        Assert.NotEqual(0, exit);
        Assert.Contains("pins no FFMPEG_BINARY_SHA256_OSX_X64", output);
    }

    /// <summary>A publish tells ToolPaths that the app ships its tools, so it never looks on PATH; a
    /// build does not, so a development build without the tools still finds the machine's.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Only_a_publish_sets_the_switch_that_keeps_the_tools_to_the_app(bool publishing)
    {
        var (exit, output) = MsBuild("-getItem:RuntimeHostConfigurationOption", $"-p:_IsPublishing={publishing}");

        Assert.True(exit == 0, output);
        Assert.Equal(publishing, output.Contains($"\"Identity\": \"{RPM.Fingerprint.ToolPaths.ShipsToolsSwitch}\""));
    }

    /// <summary>A RID the fetch scripts can download for and tool-versions.txt does not pin is a fetch
    /// that always fails and a publish that always stops, found at release time rather than here.</summary>
    [Fact]
    public void Every_rid_the_fetch_scripts_know_has_both_binaries_pinned()
    {
        var scripts = Path.Combine(RepoRoot(), "scripts");
        var bash = Regex.Matches(File.ReadAllText(Path.Combine(scripts, "fetch-tools.sh")),
                @"^\s*([a-z]+-[a-z0-9]+)\)\s+FPCALC_ASSET=", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).Order().ToList();
        var powershell = Regex.Matches(File.ReadAllText(Path.Combine(scripts, "fetch-tools.ps1")),
                @"^\s*'([a-z]+-[a-z0-9]+)'\s*=\s*""chromaprint-fpcalc-", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).Order().ToList();
        var pins = File.ReadAllText(Path.Combine(scripts, "tool-versions.txt"));

        Assert.NotEmpty(bash);
        Assert.Equal(bash, powershell);
        var missing = bash
            .SelectMany(rid => new[] { "FFMPEG", "FPCALC" }.Select(tool => $"{tool}_BINARY_SHA256_{Key(rid)}"))
            .Where(key => !Regex.IsMatch(pins, $@"(?m)^{key}=[0-9a-f]{{64}}\s*$"))
            .ToList();
        Assert.True(missing.Count == 0, "Not pinned in tool-versions.txt: " + string.Join(", ", missing));
    }

    private string WriteTool(string name, string content)
    {
        Directory.CreateDirectory(Tools);
        File.WriteAllText(Path.Combine(Tools, name), content);
        return Sha256(content);
    }

    /// <summary>A pins file for <see cref="Rid"/>. The comment carries an apostrophe because the real
    /// file does, and the csproj reads the file in a way that a quote cannot break.</summary>
    private void WritePins(params (string Tool, string Sha256)[] pins) =>
        File.WriteAllLines(Path.Combine(_scratch, "tool-versions.txt"),
            pins.Select(p => $"{p.Tool}_BINARY_SHA256_{Key(Rid)}={p.Sha256}").Prepend("# Chromaprint's build"));

    private (int Exit, string Output) Publish(string rid) =>
        MsBuild("-t:PublishRpmTools",
            "-p:RpmSkipToolFetch=true",
            $"-p:RpmToolsRid={rid}",
            $"-p:RpmToolsDir={Tools}{Path.DirectorySeparatorChar}",
            $"-p:RpmToolVersions={Path.Combine(_scratch, "tool-versions.txt")}");

    internal static (int Exit, string Output) MsBuild(params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[]
                 {
                     "msbuild", Path.Combine(RepoRoot(), "RPM.Desktop", "RPM.Desktop.csproj"),
                     "-nologo", "-v:m", "-nodeReuse:false", "-p:TargetFramework=net10.0-desktop",
                 }.Concat(args))
            start.ArgumentList.Add(arg);

        using var process = Process.Start(start)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr.Result);
    }

    private static string Key(string rid) => rid.ToUpperInvariant().Replace('-', '_');

    private static string Sha256(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)));

    /// <summary>Walks up from the test binaries to the folder holding RPM.sln.</summary>
    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RPM.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
