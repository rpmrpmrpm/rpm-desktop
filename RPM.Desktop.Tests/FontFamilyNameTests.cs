using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace RPM.Desktop.Tests;

/// <summary>
/// The family name after the <c>#</c> in the address of an app font. WinUI puts each font file in a
/// collection of its own and looks this name up with FindFamilyName, which needs an exact match and
/// reports a miss as "not found", not as an error. The collection groups fonts by typographic family
/// (name ID 16 of the font, or name ID 1 when it has no ID 16). A font's other names, such as
/// "Font Awesome 5 Pro Solid", are not family names in that collection. With one of those, Windows
/// up to build 20348, which includes all of Windows 10, draws the text in the default font. That font
/// has none of the icons, so every icon is an empty box.
///
/// <para>Windows build 26100 still draws the icons with a wrong name, and the Skia head does not use
/// this name for a font file with one face, which all our fonts are. So a wrong name looks correct on
/// a new Windows PC and on macOS and Linux. This test is what sees it there.</para>
/// </summary>
public class FontFamilyNameTests
{
    /// <summary>The projects whose code names an app font by address.</summary>
    private static readonly string[] AppProjects = ["RPM.Desktop", "RPM.DataGrid"];

    private const ushort WindowsPlatform = 3;
    private const ushort UsEnglish = 0x409;
    private const ushort FamilyNameId = 1;
    private const ushort TypographicFamilyNameId = 16;

    private static readonly Regex FontAddress =
        new(@"ms-appx:///Assets/Fonts/(?<file>[^#""<]+)#(?<family>[^""<]+)", RegexOptions.Compiled);

    [Fact]
    public void Every_app_font_is_addressed_by_its_typographic_family()
    {
        var root = ReleaseToolsTests.RepoRoot();
        var fonts = Path.Combine(root, "RPM.Desktop", "Assets", "Fonts");
        var wrong = new SortedSet<string>(StringComparer.Ordinal);
        var checkedFiles = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in Sources(root))
        {
            foreach (Match address in FontAddress.Matches(File.ReadAllText(source)))
            {
                var file = address.Groups["file"].Value;
                var path = Path.Combine(fonts, file);

                // Font Awesome Pro is not in a contributor's checkout (RpmIconFonts in RPM.Desktop.csproj).
                if (!File.Exists(path))
                    continue;

                checkedFiles.Add(file);
                var family = address.Groups["family"].Value;
                var expected = TypographicFamily(File.ReadAllBytes(path));
                if (family != expected)
                    wrong.Add($"{file}#{family} should be #{expected}");
            }
        }

        Assert.NotEmpty(checkedFiles);
        Assert.True(wrong.Count == 0,
            "These font addresses name something other than the font's typographic family, so WinUI " +
            "finds no such family and draws the text in the default font:\n  " + string.Join("\n  ", wrong));
    }

    private static IEnumerable<string> Sources(string root) =>
        AppProjects
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, project), "*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    /// <summary>The family a font belongs to in a typographic collection, from its name table: name ID
    /// 16, or name ID 1 when it has no ID 16.</summary>
    private static string TypographicFamily(byte[] font)
    {
        var names = NameTable(font);
        var strings = names + U16(font, names + 4);
        string? family = null, typographic = null;

        for (var i = 0; i < U16(font, names + 2); i++)
        {
            var record = names + 6 + i * 12;
            if (U16(font, record) != WindowsPlatform || U16(font, record + 4) != UsEnglish)
                continue;

            var text = Encoding.BigEndianUnicode.GetString(
                font, strings + U16(font, record + 10), U16(font, record + 8));
            var id = U16(font, record + 6);
            if (id == FamilyNameId)
                family = text;
            else if (id == TypographicFamilyNameId)
                typographic = text;
        }

        return typographic ?? family ?? throw new InvalidDataException("The font has no family name.");
    }

    /// <summary>Where the font's name table starts.</summary>
    private static int NameTable(byte[] font)
    {
        for (var i = 0; i < U16(font, 4); i++)
        {
            var entry = 12 + i * 16;
            if (Encoding.ASCII.GetString(font, entry, 4) == "name")
                return checked((int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(entry + 8)));
        }

        throw new InvalidDataException("The font has no name table.");
    }

    private static ushort U16(byte[] font, int offset) => BinaryPrimitives.ReadUInt16BigEndian(font.AsSpan(offset));
}
