using System.Diagnostics;
using System.Text.Json;

namespace RPM.Desktop.Tests;

/// <summary>
/// The models a build ships come from the commit, never from what the building machine happens to have.
/// Each model is either committed, or it is an RpmModel pinned by SHA-256 that FetchRpmModels downloads.
/// The wav2vec2 lyric timing files must be one of these. If they were on disk only, a Mac build made on a
/// machine that had them would ship word-by-word lyric timing, and a Windows build would not.
///
/// <para>These tests evaluate the project on this machine, so a model that ships only because it is on disk
/// here is seen here. The first test catches a csproj that lacks the pinned wav2vec2 files, on any
/// machine that has those files on disk.</para>
/// </summary>
public sealed class ShippedModelsTests
{
    private const string Desktop = "net10.0-desktop";
    private const string Windows = "net10.0-windows10.0.19041";

    [Theory]
    [InlineData(Desktop)]
    [InlineData(Windows)]
    public void Every_shipped_model_is_committed_or_fetched_by_hash(string targetFramework)
    {
        var (shipped, fetched) = Models(targetFramework);
        var committed = Committed();

        Assert.NotEmpty(shipped);
        var fromTheMachine = shipped.Where(m => !committed.Contains(m) && !fetched.Contains(m)).ToList();
        Assert.True(fromTheMachine.Count == 0,
            $"These models ship in a {targetFramework} build and are neither committed nor an RpmModel in " +
            "RPM.Desktop.csproj, so they ship only from a machine that has them. Upload each under its " +
            "SHA-256 and add it as an RpmModel ('Changing a model' in Assets/OnnxModels/README.md):\n  " +
            string.Join("\n  ", fromTheMachine));
    }

    [Fact]
    public void Both_heads_ship_the_same_models()
    {
        var (desktop, _) = Models(Desktop);
        var (windows, _) = Models(Windows);

        Assert.Equal(desktop, windows);
    }

    /// <summary>The lyric timing pair, named, so a change to the list that drops them is a decision
    /// and not a side effect: the weights without the vocabulary align nothing.</summary>
    [Fact]
    public void The_lyric_timing_weights_ship_with_their_vocabulary()
    {
        var (_, fetched) = Models(Desktop);

        Assert.Contains("wav2vec2.int8.onnx", fetched);
        Assert.Contains("wav2vec2.vocab.json", fetched);
    }

    /// <summary>The file names under Assets/OnnxModels that the build copies as content, and the ones
    /// it fetches by hash.</summary>
    private static (SortedSet<string> Shipped, SortedSet<string> Fetched) Models(string targetFramework)
    {
        var (exit, output) = ReleaseToolsTests.MsBuild($"-p:TargetFramework={targetFramework}",
            "-getItem:Content", "-getItem:RpmModel");
        Assert.True(exit == 0, output);

        using var json = JsonDocument.Parse(output);
        var items = json.RootElement.GetProperty("Items");
        return (InModels(items.GetProperty("Content")), InModels(items.GetProperty("RpmModel")));
    }

    private static SortedSet<string> InModels(JsonElement items)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in items.EnumerateArray())
        {
            var path = item.GetProperty("Identity").GetString()!.Replace('\\', '/');
            if (path.StartsWith("Assets/OnnxModels/", StringComparison.Ordinal))
                names.Add(path["Assets/OnnxModels/".Length..]);
        }
        return names;
    }

    /// <summary>The files under Assets/OnnxModels that git tracks.</summary>
    private static HashSet<string> Committed()
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.Combine(ReleaseToolsTests.RepoRoot(), "RPM.Desktop", "Assets", "OnnxModels"),
        };
        start.ArgumentList.Add("ls-files");
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(".");

        using var git = Process.Start(start)!;
        var stderr = git.StandardError.ReadToEndAsync();
        var stdout = git.StandardOutput.ReadToEnd();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, stderr.Result);
        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
    }
}
