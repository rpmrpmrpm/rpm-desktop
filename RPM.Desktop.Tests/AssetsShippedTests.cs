using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RPM.Desktop.Tests;

/// <summary>
/// What ships from RPM.Desktop/Assets. Uno and the Windows App SDK put every file under Assets in the
/// build unless RpmAssetsKeptOut says otherwise, so a template leftover or a design file added there
/// would ship without anybody choosing it (for example template notes, other apps' logos, a GIMP
/// document or an unused backdrop).
///
/// <para><b>Named</b> means the file's name appears in a string literal in the app's C#, anywhere in
/// its XAML outside a comment, or in an <c>Include</c> of RPM.Desktop.csproj, which is how the folders
/// read whole (GenrePhotos, PlaylistPhotos, Licenses) are named. A scaled image
/// (<c>x.scale-200.png</c>) is named by its plain name. <b>Kept out</b> means an unconditional
/// <c>RpmAssetsKeptOut</c> entry matches it. A conditional entry, such as each head's
/// icon or the float32 lyric model when the int8 one is present, ships in some build and is treated as
/// shipped.</para>
/// </summary>
public class AssetsShippedTests
{
    /// <summary>The projects whose code runs in the desktop app and reads Assets by path or URI.</summary>
    private static readonly string[] AppProjects = ["RPM.Desktop", "RPM.Core", "RPM.Onnx", "RPM.Cast", "RPM.DataGrid", "RPM.Fingerprint"];

    /// <summary>Files that ship without the app reading them, each with the reason.</summary>
    private static readonly Dictionary<string, string> ShippedUnread = new(StringComparer.Ordinal)
    {
        ["Fonts/Doto-OFL.txt"] = "the SIL Open Font License asks for the licence to travel with the font",
    };

    /// <summary>Files kept out of the package that the app still reads when a working copy has them,
    /// each with the reason. A build never has them, so the app's fallback for their absence is the
    /// shipped behaviour.</summary>
    private static readonly Dictionary<string, string> ExcludedButRead = new(StringComparer.Ordinal)
    {
        ["OnnxModels/beat_this.int8.onnx"] = "the eight-bit beat model, for RPM_BEAT_MODEL=int8 on a working copy; " +
            "every build ships beat_this.onnx and BeatModelChoice opens nothing else unasked",
        ["OnnxModels/wav2vec2.onnx"] = "the float32 lyric timing model: the quantiser's source and LyricBench's " +
            "weights; every build ships wav2vec2.int8.onnx, which OnnxAligner opens first",
    };

    /// <summary>Folders Uno's build reads as inputs and does not copy: Resizetizer turns
    /// Assets/Splash into the splash_screen images beside the binary.</summary>
    private static readonly string[] BuildInputs = ["Splash/"];

    [Fact]
    public void Every_shipped_asset_is_named_by_the_app()
    {
        var root = ReleaseToolsTests.RepoRoot();
        var (excluded, _) = Excludes(root);
        var includes = CsprojIncludes(root);
        var sources = Sources(root);

        var unnamed = AssetFiles(root)
            .Where(f => !excluded.Any(rx => rx.IsMatch(f)))
            .Where(f => !BuildInputs.Any(f.StartsWith))
            .Where(f => !ShippedUnread.ContainsKey(f))
            .Where(f => !includes.Any(rx => rx.IsMatch(f)) && !IsNamed(sources, PlainName(f)))
            .ToList();

        Assert.True(unnamed.Count == 0,
            "These files under RPM.Desktop/Assets ship, and nothing in the app names them. Delete them, " +
            "or add them to RpmAssetsKeptOut in RPM.Desktop.csproj if they are kept as sources:\n  "
            + string.Join("\n  ", unnamed));
    }

    [Fact]
    public void No_asset_kept_out_of_the_package_is_loaded()
    {
        var root = ReleaseToolsTests.RepoRoot();
        var (excluded, _) = Excludes(root);
        var sources = Sources(root);

        var loaded = AssetFiles(root)
            .Where(f => excluded.Any(rx => rx.IsMatch(f)))
            .Where(f => !ExcludedButRead.ContainsKey(f))
            .Where(f => IsNamed(sources, PlainName(f)))
            .ToList();

        Assert.True(loaded.Count == 0,
            "These files are kept out of the package by RpmAssetsKeptOut in RPM.Desktop.csproj, and the " +
            "app names them, so it would look for a file that is not there:\n  " + string.Join("\n  ", loaded));
    }

    /// <summary>The setup itself: the exclude list is read, and it keeps out the design sources and
    /// models listed below.</summary>
    [Fact]
    public void The_design_sources_are_kept_out()
    {
        var root = ReleaseToolsTests.RepoRoot();
        var (excluded, _) = Excludes(root);

        foreach (var kept in new[] { "Brand/design.xcf", "Svg/discogs.svg", "Brand/rpm-mark-dark-512.png", "OnnxModels/README.md", "OnnxModels/beat_this.int8.onnx", "OnnxModels/wav2vec2.onnx" })
            Assert.Contains(excluded, rx => rx.IsMatch(kept));

        // Whether a file ships must not depend on which files the building machine happens to have, so
        // no exclude reads the disk. A condition on the head being built (each head's icon) is allowed:
        // every build of that head answers it the same way.
        var csproj = XDocument.Load(Path.Combine(root, "RPM.Desktop", "RPM.Desktop.csproj"));
        var onDisk = csproj.Descendants("RpmAssetsKeptOut")
            .Where(e => ((string?)e.Attribute("Condition") + (string?)e.Parent?.Attribute("Condition")).Contains("Exists(", StringComparison.Ordinal))
            .Select(e => e.Value.Trim())
            .ToList();
        Assert.Empty(onDisk);
    }

    /// <summary>
    /// The kept-out list applies to both heads. Uno's Assets glob (the Skia head) honours
    /// DefaultItemExcludes but the Windows App SDK's (the WinUI head) does not, so without a separate
    /// exclude every listed file would ship on Windows. This evaluates each head's Content on this
    /// machine. A Mac restore has no Windows App SDK for the WinUI target, so that half is only
    /// meaningful on Windows.
    /// </summary>
    [Theory]
    [InlineData("net10.0-desktop")]
    [InlineData("net10.0-windows10.0.19041")]
    public void No_kept_out_file_is_content_in_either_head(string targetFramework)
    {
        var (exit, output) = ReleaseToolsTests.MsBuild($"-p:TargetFramework={targetFramework}", "-getItem:Content");
        Assert.True(exit == 0, output);

        var (excluded, _) = Excludes(ReleaseToolsTests.RepoRoot());
        using var json = System.Text.Json.JsonDocument.Parse(output);
        var shipped = json.RootElement.GetProperty("Items").GetProperty("Content").EnumerateArray()
            .Select(item => item.GetProperty("Identity").GetString()!.Replace('\\', '/'))
            .Where(path => path.StartsWith("Assets/", StringComparison.Ordinal))
            .Select(path => path["Assets/".Length..])
            .Where(path => excluded.Any(rx => rx.IsMatch(path)))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(shipped.Count == 0,
            $"These files are in RpmAssetsKeptOut and still Content in a {targetFramework} build:\n  " +
            string.Join("\n  ", shipped));
    }

    /// <summary>Paths relative to Assets, with forward slashes. Dot files are never content.</summary>
    private static List<string> AssetFiles(string root)
    {
        var assets = Path.Combine(root, "RPM.Desktop", "Assets");
        return Directory.EnumerateFiles(assets, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(assets, f).Replace('\\', '/'))
            .Where(f => !f.Split('/').Any(part => part.StartsWith('.')))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The RpmAssetsKeptOut entries, unconditional and conditional apart.</summary>
    private static (List<Regex> Unconditional, List<Regex> Conditional) Excludes(string root)
    {
        var doc = XDocument.Load(Path.Combine(root, "RPM.Desktop", "RPM.Desktop.csproj"));
        var unconditional = new List<Regex>();
        var conditional = new List<Regex>();
        foreach (var e in doc.Descendants("RpmAssetsKeptOut"))
        {
            var isConditional = e.Attribute("Condition") is not null || e.Parent?.Attribute("Condition") is not null;
            foreach (var entry in e.Value.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                var path = entry.Replace('\\', '/');
                if (path.StartsWith("Assets/", StringComparison.Ordinal))
                    (isConditional ? conditional : unconditional).Add(Glob(path["Assets/".Length..]));
            }
        }
        Assert.NotEmpty(unconditional);
        return (unconditional, conditional);
    }

    /// <summary>Every Include of RPM.Desktop.csproj under Assets, as a pattern over Assets-relative paths.</summary>
    private static List<Regex> CsprojIncludes(string root)
    {
        var doc = XDocument.Load(Path.Combine(root, "RPM.Desktop", "RPM.Desktop.csproj"));
        return doc.Descendants()
            .Select(e => e.Attribute("Include")?.Value)
            .OfType<string>()
            .SelectMany(v => v.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .Select(v => v.Replace('\\', '/'))
            .Where(v => v.StartsWith("Assets/", StringComparison.Ordinal))
            .Select(v => Glob(v["Assets/".Length..]))
            .ToList();
    }

    /// <summary>The text a name can be found in: string literals of the C#, and the XAML with its
    /// comments taken out. A name in a comment is a note, not a load.</summary>
    private static List<string> Sources(string root)
    {
        var literal = new Regex("\"[^\"\\r\\n]*\"", RegexOptions.Compiled);
        var comment = new Regex("<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
        var texts = new List<string>();
        foreach (var project in AppProjects)
        {
            foreach (var file in SourceFiles(Path.Combine(root, project)))
            {
                if (file.EndsWith(".cs", StringComparison.Ordinal))
                    texts.Add(string.Join('\n', literal.Matches(File.ReadAllText(file)).Select(m => m.Value)));
                else if (file.EndsWith(".xaml", StringComparison.Ordinal))
                    texts.Add(comment.Replace(File.ReadAllText(file), ""));
            }
        }
        Assert.NotEmpty(texts);
        return texts;
    }

    /// <summary>The project's files, without walking into build output or the assets themselves.</summary>
    private static IEnumerable<string> SourceFiles(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir))
            yield return file;
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (name is "bin" or "obj" or "Assets" or "NativeLibs" || name.StartsWith('.'))
                continue;
            foreach (var file in SourceFiles(sub))
                yield return file;
        }
    }

    /// <summary>Whether a file name appears in any of the texts as a whole name: not as the end of a
    /// longer one (<c>apple-touch-icon.png</c> does not name <c>icon.png</c>).</summary>
    private static bool IsNamed(List<string> texts, string name)
    {
        var rx = new Regex(@"(?<![\w.\-])" + Regex.Escape(name) + @"(?![\w.\-])");
        return texts.Any(rx.IsMatch);
    }

    /// <summary>The name an image is asked for by: <c>x.scale-200.png</c> is loaded as <c>x.png</c>.</summary>
    private static string PlainName(string relativePath)
        => Regex.Replace(Path.GetFileName(relativePath), @"\.scale-\d+(?=\.)", "");

    private static Regex Glob(string pattern)
        => new("^" + Regex.Escape(pattern).Replace(@"\*\*/", "(.*/)?").Replace(@"\*\*", ".*").Replace(@"\*", "[^/]*") + "$");
}
