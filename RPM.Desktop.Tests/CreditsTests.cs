using System.Text.Json;
using System.Text.RegularExpressions;
using RPM.Desktop.Presentation.About;

namespace RPM.Desktop.Tests;

/// <summary>
/// The acknowledgements screen makes legal statements, so an error there is a legal problem, not only a
/// visual one. Nothing shows the error at runtime. A licence that did not ship renders as a blank pane. A
/// string key that is not in the table renders as its own name. Both look like a rough app.
///
/// <para>The list itself is generated, and scripts/gen_credits.py checks it when it is generated. These
/// tests check what the generator cannot see. They check that the licence files reached the build output,
/// that the strings reached the resource table, and that the credited versions are the versions actually
/// referenced. A generator run that is out of date passes its own checks, and the list is still
/// wrong.</para>
/// </summary>
public class CreditsTests
{
    /// <summary>Where the app's own assets sit beside the test binary.</summary>
    private static string Licenses =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "Licenses");

    /// <summary>The repo, found by walking up to the file that only its root has.</summary>
    private static string Root
    {
        get
        {
            var at = new DirectoryInfo(AppContext.BaseDirectory);
            while (at is not null && !File.Exists(Path.Combine(at.FullName, "Directory.Packages.props")))
                at = at.Parent;

            Assert.NotNull(at);
            return at!.FullName;
        }
    }

    private static IEnumerable<CreditEntry> Everything =>
        Credits.Sections.SelectMany(section => section.Entries);

    [Fact]
    public void EveryLicenceAnEntryNamesExists()
    {
        foreach (var entry in Everything)
        {
            Assert.True(entry.Licenses.Count > 0, $"{entry.Name} is credited under no licence");

            foreach (var key in entry.Licenses)
                Assert.True(Credits.Licenses.ContainsKey(key),
                    $"{entry.Name} names the licence {key}, which is not in the map");
        }
    }

    [Fact]
    public void EveryLicenceTextReachedTheBuild()
    {
        foreach (var (key, licence) in Credits.Licenses)
        {
            if (licence.File is not { } file)
                continue;

            var path = Path.Combine(Licenses, file);
            Assert.True(File.Exists(path), $"{key} names {file}, which is not in the output");

            // Long enough to be a licence, not a truncated download. The shortest text that ships is
            // SQLite's blessing, at 244 bytes.
            Assert.True(new FileInfo(path).Length > 200, $"{file} is too short to be a licence");
        }
    }

    [Fact]
    public void ALicenceWithNoTextOffersAnAddressInstead()
    {
        foreach (var entry in Everything)
        {
            foreach (var key in entry.Licenses)
            {
                var licence = Credits.Licenses[key];
                if (licence.File is not null)
                    continue;

                Assert.True(
                    licence.Url is { Length: > 0 } || entry.TermsUrl is { Length: > 0 },
                    $"{entry.Name} is under {key}, which ships no text and names no address");
            }
        }
    }

    [Fact]
    public void NoTextShipsThatNothingIsUnder()
    {
        var named = Credits.Licenses.Values
            .Select(licence => licence.File)
            .Where(file => file is not null)
            .ToHashSet();

        foreach (var path in Directory.GetFiles(Licenses, "*.txt"))
            Assert.True(named.Contains(Path.GetFileName(path)),
                $"{Path.GetFileName(path)} ships and nothing on the screen is under it");
    }

    /// <summary>
    /// A notices document ships its text and is still read at an address.
    ///
    /// <para>These two carry the notices that libSkiaSharp and onnxruntime require to travel with them.
    /// That is why the file must be in the app. The files are 140 KB and 345 KB, so the screen links to the
    /// website instead of showing them in one block of text. A notices licence needs both halves. With no
    /// file it ships nothing, and with no address it cannot be read at all.</para>
    /// </summary>
    [Fact]
    public void ANoticesDocumentShipsAndIsStillLinked()
    {
        var notices = Credits.Licenses.Where(pair => pair.Value.IsNotices).ToList();
        Assert.NotEmpty(notices);

        foreach (var (key, licence) in notices)
        {
            Assert.True(licence.File is { Length: > 0 },
                $"{key} is a notices document and ships no text");
            Assert.True(File.Exists(Path.Combine(Licenses, licence.File!)),
                $"{key} names {licence.File}, which is not in the output");
            Assert.True(
                Uri.TryCreate(licence.Url, UriKind.Absolute, out var address)
                && address.Scheme == Uri.UriSchemeHttps,
                $"{key} is a notices document and gives no https address to read it at");
        }
    }

    [Fact]
    public void NoEntryIsListedTwice()
    {
        var seen = new HashSet<string>();

        foreach (var entry in Everything)
            Assert.True(seen.Add(entry.Name + entry.Detail),
                $"{entry.Name} is listed twice");
    }

    [Fact]
    public void EveryLinkIsAnAddress()
    {
        foreach (var entry in Everything)
        {
            foreach (var url in new[] { entry.Url, entry.Source, entry.TermsUrl })
            {
                if (url is not { Length: > 0 })
                    continue;

                Assert.True(
                    Uri.TryCreate(url, UriKind.Absolute, out var address)
                    && address.Scheme == Uri.UriSchemeHttps,
                    $"{entry.Name} gives {url}, which is not an https address");
            }
        }
    }

    /// <summary>
    /// The versions on the screen are the versions in the build.
    ///
    /// <para>This is the check that matters most. Everything else here would still pass a year after a
    /// package was raised while the credits were not. The screen would then name a version that nobody
    /// ships. The version is read from Directory.Packages.props, where the numbers are kept. This applies
    /// only to the entries that name a package by a version.</para>
    /// </summary>
    [Theory]
    [InlineData("SkiaSharp", "SkiaSharp")]
    [InlineData("TagLib#", "TagLibSharp")]
    [InlineData("ONNX Runtime", "Microsoft.ML.OnnxRuntime")]
    [InlineData("Velopack", "Velopack")]
    [InlineData("Protocol Buffers", "Google.Protobuf")]
    [InlineData("Zeroconf", "Zeroconf")]
    [InlineData("net-mdns", "Makaretu.Dns.Multicast.New")]
    [InlineData("Microsoft.Data.Sqlite", "Microsoft.Data.Sqlite")]
    [InlineData("SQLitePCLRaw", "SQLitePCLRaw.lib.e_sqlite3")]
    [InlineData("Win2D", "Microsoft.Graphics.Win2D")]
    [InlineData("OpenAL Soft", "Silk.NET.OpenAL.Soft.Native")]
    [InlineData("Silk.NET", "Silk.NET.OpenAL")]
    public void TheCreditedVersionIsTheReferencedVersion(string credited, string package)
    {
        var props = File.ReadAllText(Path.Combine(Root, "Directory.Packages.props"));
        var pattern = "Include=\"" + Regex.Escape(package) + "\"\\s+Version=\"([^\"]+)\"";
        var match = Regex.Match(props, pattern);

        Assert.True(match.Success, $"{package} is not in Directory.Packages.props");

        var entry = Everything.FirstOrDefault(e => e.Name == credited);
        Assert.True(entry is not null, $"{credited} is not on the About screen");

        Assert.Equal(match.Groups[1].Value, entry!.Version);
    }

    /// <summary>
    /// The command-line tools, and what is built into them, are credited at the versions in
    /// scripts/tool-versions.txt. The build, the fetch scripts and the transcoder read the same file.
    /// gen_credits.py writes the credits from it. This test catches a version that was raised without
    /// running the generator again.
    /// </summary>
    [Theory]
    [InlineData("FFmpeg", "FFMPEG_VERSION")]
    [InlineData("LAME", "LAME_VERSION")]
    [InlineData("zlib", "ZLIB_VERSION")]
    [InlineData("Chromaprint", "CHROMAPRINT_VERSION")]
    public void TheCreditedToolIsThePinnedTool(string credited, string key)
    {
        var pinned = File.ReadAllText(Path.Combine(Root, "scripts", "tool-versions.txt"));
        var match = Regex.Match(pinned, "^" + Regex.Escape(key) + "=(\\S+)", RegexOptions.Multiline);

        Assert.True(match.Success, $"{key} is not in scripts/tool-versions.txt");

        var entry = Everything.FirstOrDefault(e => e.Name == credited);
        Assert.True(entry is not null, $"{credited} is not on the About screen");

        Assert.Equal(match.Groups[1].Value, entry!.Version);
    }

    /// <summary>
    /// The FFmpeg inside fpcalc is credited at its own version, not at rpm's.
    ///
    /// <para>This is separate from the theory above, because two entries are called FFmpeg. rpm builds one,
    /// and Chromaprint links the other into the fpcalc that rpm ships. They are different versions under
    /// the same licence. Each needs its own source link, so this test finds the entry by its detail.</para>
    /// </summary>
    [Fact]
    public void TheFfmpegInsideFpcalcIsCreditedAtItsOwnVersion()
    {
        var pinned = File.ReadAllText(Path.Combine(Root, "scripts", "tool-versions.txt"));
        var match = Regex.Match(pinned, "^FPCALC_FFMPEG_VERSION=(\\S+)", RegexOptions.Multiline);

        Assert.True(match.Success, "FPCALC_FFMPEG_VERSION is not in scripts/tool-versions.txt");

        var entry = Everything.FirstOrDefault(
            e => e.Name == "FFmpeg" && e.Detail == "inside fpcalc");
        Assert.True(entry is not null, "the FFmpeg inside fpcalc is not on the About screen");

        Assert.Equal(match.Groups[1].Value, entry!.Version);
        Assert.NotEqual(Everything.First(e => e.Name == "FFmpeg").Version, entry.Version);

        // The LGPL requires the source of the version that is shipped. So the link must carry this version,
        // not the other one.
        Assert.Contains(entry.Version!, entry.Source);
    }

    /// <summary>Uno's version is in global.json, not in the package table, because the SDK carries
    /// it.</summary>
    [Fact]
    public void TheCreditedUnoIsTheReferencedUno()
    {
        var json = File.ReadAllText(Path.Combine(Root, "global.json"));

        // global.json has comments in it, which System.Text.Json refuses by default.
        using var document = JsonDocument.Parse(json,
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });

        var referenced = document.RootElement
            .GetProperty("msbuild-sdks").GetProperty("Uno.Sdk").GetString();

        var entry = Everything.First(e => e.Name == "Uno Platform");
        Assert.Equal(referenced, entry.Version);
    }

    /// <summary>
    /// Every typeface the app ships is credited.
    ///
    /// <para>The fonts are read from the folder, not from a list here. A font placed in Assets/Fonts and
    /// used by a style ships whether or not anyone remembered this screen. That is exactly the omission
    /// this test catches.</para>
    /// </summary>
    [Fact]
    public void EveryShippedTypefaceIsCredited()
    {
        var credited = Everything.Select(e => e.Name).ToList();

        // The file name as it is on disk, and the family as the screen names it. Font Awesome ships as
        // three files under one name, so the check is a contains and not an exact match.
        var expected = new Dictionary<string, string>
        {
            ["Cabin-Medium.ttf"] = "Cabin",
            ["Cabin-Bold.ttf"] = "Cabin",
            ["Doto.ttf"] = "Doto",
            ["ThiamineRPM.otf"] = "Thiamine",
            ["fa-solid-900.ttf"] = "Font Awesome",
            ["fa-regular-400.ttf"] = "Font Awesome",
            ["fa-brands-400.ttf"] = "Font Awesome",
        };

        var fonts = Path.Combine(Root, "RPM.Desktop", "Assets", "Fonts");

        foreach (var path in Directory.GetFiles(fonts, "*.ttf").Concat(Directory.GetFiles(fonts, "*.otf")))
        {
            var name = Path.GetFileName(path);
            Assert.True(expected.TryGetValue(name, out var family),
                $"{name} ships and this test does not know what it is credited as");
            Assert.Contains(family, credited);
        }
    }

    /// <summary>
    /// Every model the app ships is credited.
    ///
    /// <para>The reasoning is the same as for the typefaces, and it matters more here. The models are the
    /// part of the app most likely to be swapped for a better one. A model file is a licensed work, whether
    /// or not the swap changed this screen.</para>
    /// </summary>
    [Fact]
    public void EveryShippedModelIsCredited()
    {
        var credited = Everything.Select(e => e.Name).ToList();

        var expected = new Dictionary<string, string>
        {
            ["u2netp.onnx"] = "U^2-Net",
            ["ultraface.onnx"] = "UltraFace",
            ["beat_this.onnx"] = "Beat This!",
            ["beat_this.int8.onnx"] = "Beat This!",
            ["skey.onnx"] = "S-KEY",
            ["wav2vec2.onnx"] = "wav2vec 2.0 (base, 960h)",
            ["wav2vec2.int8.onnx"] = "wav2vec 2.0 (base, 960h)",
        };

        var models = Path.Combine(Root, "RPM.Desktop", "Assets", "OnnxModels");
        if (!Directory.Exists(models))
            return;   // A working copy that has not run scripts/fetch-models.sh.

        foreach (var path in Directory.GetFiles(models, "*.onnx"))
        {
            var name = Path.GetFileName(path);
            Assert.True(expected.TryGetValue(name, out var model),
                $"{name} ships and this test does not know what it is credited as");
            Assert.Contains(model, credited);
        }
    }

    /// <summary>
    /// Every genre photograph on the wall has its photographer on this screen.
    ///
    /// <para>Both lists come from scripts/genre-photos.json, so this test cannot catch a wrong name. It
    /// catches a generator that has not been run. For example, a photograph is swapped in the JSON and the
    /// asset is rebuilt, but the credits table still names the photographer of the old one.</para>
    /// </summary>
    [Fact]
    public void EveryGenrePhotographHasItsPhotographer()
        => EveryPhotographIsCredited("genre-photos.json", "genre", what => $"the photograph heading {what}");

    /// <summary>The same check, for the photograph behind each generated playlist's frame. This is a second
    /// table under the same heading. Those pictures are shipped with the app, not fetched.</summary>
    [Fact]
    public void EveryPlaylistPhotographHasItsPhotographer()
        => EveryPhotographIsCredited("playlist-photos.json", "list", what => $"the photograph behind {what}");

    /// <summary>
    /// One photograph table against the credits screen.
    /// </summary>
    /// <remarks>
    /// The match uses the photograph's own PAGE, not what the picture is of. Two of these tables share one
    /// heading, and the subject of a picture is not unique across them. For example, the pooled Classical
    /// playlist and the Classical shelf are both called "Classical". Keying by that subject would throw on
    /// the duplicate. A Pexels page names one photograph and nothing else.
    /// </remarks>
    private void EveryPhotographIsCredited(string file, string subject, Func<string, string> says)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(SharedFiles.Path(file)));

        var credited = Credits.Sections
            .SelectMany(section => section.Entries)
            .Where(entry => entry.Url is not null)
            .GroupBy(entry => entry.Url!)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var photo in document.RootElement.GetProperty("photos").EnumerateArray())
        {
            var what = photo.GetProperty(subject).GetString()!;
            var by = photo.GetProperty("by").GetString()!;
            var page = photo.GetProperty("page").GetString()!;

            Assert.True(credited.TryGetValue(page, out var entry),
                $"{says(what)} is credited to nobody (run python scripts/gen_credits.py)");
            Assert.Equal(by, entry!.Name);
            Assert.Equal(what, entry.Detail);
        }
    }

    /// <summary>The genres wall's typefaces, with the same reasoning. The outlines of these faces are
    /// embedded in the app. The Open Font License permits that on the condition that the licence travels
    /// with them.</summary>
    [Fact]
    public void EveryWordmarkTypefaceIsCredited()
    {
        var source = SharedFiles.Path("wordmarks.json");
        using var document = JsonDocument.Parse(File.ReadAllText(source));

        var credited = Everything.Select(entry => entry.Name).ToHashSet();

        foreach (var shelf in document.RootElement.GetProperty("genres").EnumerateArray())
        {
            var family = shelf.GetProperty("font").GetString()!;
            Assert.True(credited.Contains(family),
                $"the wall is lettered in {family} and does not credit it "
                + "(run python3 scripts/gen_credits.py)");
        }
    }

    /// <summary>
    /// What a package the desktop head restores is credited as, or why it ships nothing.
    ///
    /// <para>One line per package in the restore graph, including the ones nobody chose. Most of what ships
    /// arrives through Uno Platform rather than through Directory.Packages.props. A prefix stands for a
    /// family that is credited under one name. The other packages are named one by one. A package that is
    /// in neither list fails the test on purpose, so that someone has to say what it is.</para>
    ///
    /// <para>The right-hand side is a name on the About screen, or <see cref="NotShipped"/> with a reason.
    /// "Not shipped" is a claim about the build output. So each reason says what the package does instead
    /// of shipping.</para>
    /// </summary>
    private const string NotShipped = "";

    private static readonly (string Prefix, string Credited)[] Families =
    [
        // Families the screen credits under one name.
        ("Microsoft.Extensions.", "Microsoft.Extensions"),
        ("Microsoft.Data.Sqlite", "Microsoft.Data.Sqlite"),
        ("Microsoft.ML.OnnxRuntime", "ONNX Runtime"),
        ("SQLitePCLRaw.", "SQLitePCLRaw"),
        ("SkiaSharp", "SkiaSharp"),
        ("HarfBuzzSharp", "HarfBuzzSharp"),
        ("Svg.", "Svg.Skia"),
        ("Common.Logging", "Common.Logging"),
        ("DirectNAot", "DirectNAot"),
        ("Tmds.DBus.Protocol", "Tmds.DBus"),
        ("Silk.NET.OpenAL.Soft.Native", "OpenAL Soft"),
        ("Silk.NET.", "Silk.NET"),
        ("Uno.icu-", "ICU"),

        // The WinUI head's own packages. Only a restore that has the Windows target lists them, so a Mac
        // run of this test never sees them. The Windows App SDK arrives as one umbrella package and its
        // parts (Base, Foundation, WinUI, ML, AI, and so on). They get one credit.
        ("Microsoft.WindowsAppSDK", "Windows App SDK"),

        // Build-time only, by family: the ahead-of-time compiler, the trimmer, the Win32 source generator
        // and its metadata, and Uno's build tasks and hot-reload server. None of them puts a file in the
        // app.
        ("Microsoft.DotNet.ILCompiler", NotShipped),
        ("runtime.", NotShipped),
        ("Microsoft.NET.ILLink.", NotShipped),
        ("Microsoft.Windows.", NotShipped),
        ("Tmds.DBus.Generator", NotShipped),
        ("Uno.Resizetizer", NotShipped),
        ("Uno.Sdk.", NotShipped),
        ("Uno.Settings.DevServer", NotShipped),
        ("Uno.UI.App.Mcp", NotShipped),
        ("Uno.UI.HotDesign", NotShipped),
        ("Uno.WinUI.DevServer", NotShipped),

        // Everything else from Uno is the framework itself, credited as Uno Platform. It comes last, so the
        // lines above take priority.
        ("Uno.", "Uno Platform"),
    ];

    private static readonly Dictionary<string, string> Packages = new()
    {
        ["CommonServiceLocator"] = "CommonServiceLocator",
        ["CommunityToolkit.Mvvm"] = "CommunityToolkit.Mvvm",
        ["ExCSS"] = "ExCSS",
        ["Google.Protobuf"] = "Protocol Buffers",
        ["LibVLCSharp"] = "LibVLCSharp",
        ["Makaretu.Dns.Multicast.New"] = "net-mdns",
        ["Makaretu.Dns.New"] = "net-dns",
        ["NuGet.Versioning"] = "NuGet.Versioning",
        ["Refit"] = "Refit",
        ["ShimSkiaSharp"] = "Svg.Skia",
        ["System.Linq.Async"] = "System.Linq.Async",
        ["System.Reactive"] = "System.Reactive",
        ["TagLibSharp"] = "TagLib#",
        ["Velopack"] = "Velopack",
        ["WebView2Aot"] = "WebView2Aot",

        // The WinUI head's, like the Windows App SDK above.
        ["Microsoft.AI.DirectML"] = "DirectML",
        // Windows ML, restored with the Windows App SDK. Its onnxruntime.dll, DirectML.dll and managed ONNX
        // Runtime are excluded by RPM.Desktop.csproj (ExcludeAssets). So the ONNX Runtime and DirectML
        // credited above are the ones that ship. It is named here because the "Microsoft.Windows." family
        // above is for build-time packages, and this package is not one of them.
        ["Microsoft.Windows.AI.MachineLearning"] = NotShipped,
        ["Microsoft.Graphics.Win2D"] = "Win2D",
        ["Microsoft.Web.WebView2"] = "Microsoft Edge WebView2",
        ["Zeroconf"] = "Zeroconf",

        // Typefaces that Uno brings. They are credited as faces, not as Uno Platform, because the licensed
        // part is the letterforms.
        ["Uno.Fonts.Fluent"] = "Uno Fluent icons",
        ["Uno.Fonts.OpenSans"] = "Open Sans",

        // Parts of .NET itself, under the one ".NET" line. They share a publisher and a licence, and
        // listing each one would say nothing that the one line does not.
        ["Microsoft.Bcl.AsyncInterfaces"] = ".NET",
        ["Microsoft.DotNet.PlatformAbstractions"] = ".NET",
        ["System.Diagnostics.EventLog"] = ".NET",
        ["System.Json"] = ".NET",
        ["System.Linq.AsyncEnumerable"] = ".NET",
        ["System.Numerics.Tensors"] = ".NET",

        // Restored and not shipped.
        ["Uno.Fonts.Roboto"] = NotShipped,   // Uno's Material theme carries it; rpm uses its own.
        ["Uno.Wasm.WebSockets"] = NotShipped,   // WebAssembly only, and there is no web head.
    };

    /// <summary>The desktop head's restore graph, which is where a transitive package is visible at
    /// all. Written by restore, so a working copy that has not restored skips this.</summary>
    private static JsonDocument? RestoreGraph()
    {
        var path = Path.Combine(Root, "RPM.Desktop", "obj", "project.assets.json");
        return File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path)) : null;
    }

    private static IEnumerable<(string Name, string Version)> RestoredPackages(JsonDocument graph)
    {
        foreach (var library in graph.RootElement.GetProperty("libraries").EnumerateObject())
        {
            if (library.Value.GetProperty("type").GetString() != "package")
                continue;

            var at = library.Name.LastIndexOf('/');
            yield return (library.Name[..at], library.Name[(at + 1)..]);
        }
    }

    private static string? CreditedAs(string package)
    {
        if (Packages.TryGetValue(package, out var named))
            return named;

        foreach (var (prefix, credited) in Families)
            if (package.StartsWith(prefix, StringComparison.Ordinal))
                return credited;

        return null;
    }

    [Fact]
    public void EveryRestoredPackageIsCreditedOrAccountedFor()
    {
        using var graph = RestoreGraph();
        if (graph is null)
            return;

        var credited = Everything.Select(entry => entry.Name).ToHashSet();
        var unknown = new List<string>();
        var uncredited = new List<string>();

        foreach (var (package, _) in RestoredPackages(graph))
        {
            switch (CreditedAs(package))
            {
                case null:
                    unknown.Add(package);
                    break;
                case NotShipped:
                    break;
                case var name when !credited.Contains(name):
                    uncredited.Add($"{package} is credited as {name}, which is not on the screen");
                    break;
            }
        }

        Assert.True(unknown.Count == 0,
            "these packages are in the restore graph and this test does not know what they are. "
            + "Credit them in scripts/credits.json, or record here why they ship nothing: "
            + string.Join(", ", unknown));

        Assert.True(uncredited.Count == 0, string.Join("; ", uncredited));
    }

    /// <summary>
    /// The version credited for a package nobody asked for is the version that was restored.
    ///
    /// <para>These arrive through Uno Platform, so their versions are not in Directory.Packages.props.
    /// Raising the Uno SDK can change any of them. The version is read from the restore graph, which is the
    /// only place the resolved version is written down.</para>
    /// </summary>
    [Theory]
    [InlineData("CommunityToolkit.Mvvm", "CommunityToolkit.Mvvm")]
    [InlineData("Refit", "Refit")]
    [InlineData("System.Reactive", "System.Reactive")]
    [InlineData("System.Linq.Async", "System.Linq.Async")]
    [InlineData("Svg.Skia", "Svg.Skia")]
    [InlineData("ExCSS", "ExCSS")]
    [InlineData("Common.Logging", "Common.Logging")]
    [InlineData("CommonServiceLocator", "CommonServiceLocator")]
    [InlineData("NuGet.Versioning", "NuGet.Versioning")]
    [InlineData("DirectNAot", "DirectNAot")]
    [InlineData("WebView2Aot", "WebView2Aot")]
    [InlineData("Tmds.DBus.Protocol", "Tmds.DBus")]
    [InlineData("LibVLCSharp", "LibVLCSharp")]
    [InlineData("Makaretu.Dns.New", "net-dns")]
    public void TheCreditedTransitiveVersionIsTheRestoredVersion(string package, string credited)
    {
        using var graph = RestoreGraph();
        if (graph is null)
            return;

        var restored = RestoredPackages(graph)
            .Where(p => p.Name == package)
            .Select(p => p.Version)
            .FirstOrDefault();

        Assert.True(restored is not null, $"{package} is not in the restore graph");

        var entry = Everything.FirstOrDefault(e => e.Name == credited);
        Assert.True(entry is not null, $"{credited} is not on the About screen");

        Assert.Equal(restored, entry!.Version);
    }
}
