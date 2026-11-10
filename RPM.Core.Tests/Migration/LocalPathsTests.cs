using System.Text;
using RPM.Core.Migration;

namespace RPM.Core.Tests;

/// <summary>
/// Finding a file the other app lists in Unicode normal form C when the disk holds the name decomposed
/// (a file copied from a Mac to Windows or Linux). macOS opens either form, so these tests use a
/// listing of their own.
/// </summary>
public class LocalPathsTests
{
    private static readonly string Decomposed = "/music/Cafe\u0301 Society/01 Cafe\u0301.flac";

    private static string? Find(string path, params string[] files)
    {
        var dirs = files.SelectMany(Parents).ToHashSet(StringComparer.Ordinal);
        return LocalPaths.Find(path, files.Contains, dirs.Contains,
            dir => files.Concat(dirs).Where(e => e != dir && Path.GetDirectoryName(e) == dir));
    }

    private static IEnumerable<string> Parents(string path)
    {
        for (var dir = Path.GetDirectoryName(path); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
            yield return dir;
    }

    [Fact]
    public void A_name_stored_decomposed_is_found_from_its_composed_form()
    {
        var composed = Decomposed.Normalize(NormalizationForm.FormC);
        Assert.NotEqual(composed, Decomposed);

        Assert.Equal(Decomposed, Find(composed, Decomposed));
    }

    [Fact]
    public void A_path_that_is_there_as_written_is_returned_as_it_is() =>
        Assert.Equal("/music/a.flac", Find("/music/a.flac", "/music/a.flac"));

    [Fact]
    public void A_missing_file_is_null() =>
        Assert.Null(Find("/music/Café Society/02 Other.flac", Decomposed));
}
