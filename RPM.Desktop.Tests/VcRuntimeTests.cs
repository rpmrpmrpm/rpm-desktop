using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace RPM.Desktop.Tests;

/// <summary>
/// The Visual C++ runtime that the Windows publish copies next to rpm.exe (PublishVcRuntime in
/// RPM.Desktop.csproj). soft_oal.dll and onnxruntime.dll import it, and Windows does not carry it.
/// Without this copy, a PC that has never installed the runtime plays no audio. These tests read the
/// import table and linker version of every win-x64 native in this test's output, which comes from the
/// same packages as the app's. The WinUI head's own natives (the Windows App SDK, Win2D) are not in this
/// output, and none of them import the runtime.
/// </summary>
public class VcRuntimeTests
{
    private static readonly string NativeFolder = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");

    /// <summary>The Visual C++ runtime's DLL names, for any version of the runtime.</summary>
    private static readonly Regex VcRuntimeName = new(@"^(msvcp|vcruntime|concrt|vccorlib|vcomp|vcamp|mfc)1\d0", RegexOptions.IgnoreCase);

    /// <summary>A runtime DLL that is imported but not published is a fault: Windows refuses to load the
    /// importer on a PC without the runtime.</summary>
    [Fact]
    public void Every_runtime_dll_a_native_imports_is_published()
    {
        var published = PublishedFiles();
        var problems = new List<string>();
        foreach (var native in WindowsNatives())
            foreach (var name in native.Imports.Where(n => VcRuntimeName.IsMatch(n)))
            {
                // A package that carries its own renamed copy (WPF's vcruntime140_cor3.dll) satisfies it.
                if (published.Contains(name) || File.Exists(Path.Combine(NativeFolder, name)))
                    continue;
                problems.Add($"{Relative(native.Path)} imports {name}, which RpmVcRuntimeFile does not list");
            }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>Microsoft's rule: the runtime must be at least as new as the newest toolset any
    /// component was linked with. The publish refuses a runtime older than the minimum, so the minimum
    /// has to keep up with the natives.</summary>
    [Fact]
    public void No_native_was_linked_with_a_newer_toolset_than_the_runtime_minimum()
    {
        var minimum = Version.Parse(Property("RpmVcRuntimeMinimum"));
        var published = PublishedFiles();
        var problems = WindowsNatives()
            .Where(n => n.Imports.Any(published.Contains))
            .Where(n => n.Linker > minimum)
            .Select(n => $"{Relative(n.Path)} was linked with MSVC {n.Linker}, newer than RpmVcRuntimeMinimum {minimum}. Raise it in RPM.Desktop.csproj.")
            .ToList();

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>Keeps the two tests above from passing because the scan found nothing.</summary>
    [Fact]
    public void The_audio_output_and_the_model_runtime_are_among_the_natives_read()
    {
        var natives = WindowsNatives().ToList();
        foreach (var name in new[] { "soft_oal.dll", "onnxruntime.dll" })
        {
            var native = natives.SingleOrDefault(n => Path.GetFileName(n.Path).Equals(name, StringComparison.OrdinalIgnoreCase));
            Assert.True(native.Path is not null, $"{name} is not in {NativeFolder}");
            Assert.Contains("vcruntime140.dll", native.Imports, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static IEnumerable<(string Path, List<string> Imports, Version Linker)> WindowsNatives()
    {
        foreach (var path in Directory.EnumerateFiles(NativeFolder, "*.dll"))
        {
            using var reader = new PEReader(File.OpenRead(path), PEStreamOptions.PrefetchEntireImage);
            var header = reader.PEHeaders.PEHeader!;
            var imports = new List<string>();
            ReadDllNames(reader, header.ImportTableDirectory.RelativeVirtualAddress, descriptorSize: 20, nameAt: 12, imports);
            ReadDllNames(reader, header.DelayImportTableDirectory.RelativeVirtualAddress, descriptorSize: 32, nameAt: 4, imports);
            yield return (path, imports, new Version(header.MajorLinkerVersion, header.MinorLinkerVersion));
        }
    }

    /// <summary>Reads the DLL names out of an import or delay-import table: fixed-size descriptors,
    /// each holding the address of its DLL's name at a fixed offset, ended by a descriptor of
    /// zeros.</summary>
    private static void ReadDllNames(PEReader reader, int table, int descriptorSize, int nameAt, List<string> into)
    {
        if (table == 0)
            return;

        for (var at = table; ; at += descriptorSize)
        {
            var descriptor = reader.GetSectionData(at).GetReader();
            descriptor.Offset = nameAt;
            var name = descriptor.ReadInt32();
            if (name == 0)
                return;

            var text = reader.GetSectionData(name).GetReader();
            var bytes = new List<byte>();
            for (byte b; (b = text.ReadByte()) != 0;)
                bytes.Add(b);
            into.Add(Encoding.ASCII.GetString(bytes.ToArray()));
        }
    }

    private static HashSet<string> PublishedFiles() =>
        Project().Descendants("RpmVcRuntimeFile")
            .SelectMany(e => ((string?)e.Attribute("Include") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string Property(string name)
    {
        var value = Project().Descendants(name).Select(e => e.Value.Trim()).SingleOrDefault();
        Assert.False(string.IsNullOrEmpty(value), $"{name} is not set in RPM.Desktop.csproj");
        return value!;
    }

    private static XDocument Project() =>
        XDocument.Load(Path.Combine(RepoRoot(), "RPM.Desktop", "RPM.Desktop.csproj"));

    private static string Relative(string path) => Path.GetRelativePath(AppContext.BaseDirectory, path);

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
