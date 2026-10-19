using System.Text.RegularExpressions;

namespace RPM.Desktop.Tests;

/// <summary>
/// Every type wearing <c>[RpmBindable]</c> is declared <c>partial</c>.
///
/// <para>On the WinUI head that attribute is
/// <c>WinRT.GeneratedBindableCustomPropertyAttribute</c>, and CsWinRT's generator answers it by adding
/// a partial declaration to the type: an <c>IBindableCustomPropertyImplementation</c> whose accessors
/// are static lambdas over the properties themselves. A trimmed or NativeAOT build keeps those lambdas,
/// and that is their purpose, because a property named only by a string in XAML is not a reference and
/// would otherwise be trimmed away.</para>
///
/// <para><b>Why a test and not the compiler.</b> A type that is not partial is skipped SILENTLY. No
/// error, no warning, nothing in the build log: the generator skips it and that type falls back to the
/// reflection path, which this attribute exists to avoid. Removing the keyword from one row removes it
/// from the generated file while the build still succeeds. Under AOT the result is a binding that
/// resolves to nothing, which shows as an empty cell, not an exception, so nothing else would report
/// it.</para>
///
/// <para>Read from the source rather than the assembly because <c>partial</c> is a source-only word:
/// it leaves no trace in metadata for reflection to find. This project also targets the Skia head,
/// where the generated interface does not exist to assert against.</para>
/// </summary>
public class BindableRowsTests
{
    /// <summary>The declaration that follows the attribute, however it is written: a class, a record,
    /// a record struct, with or without a primary constructor.</summary>
    private static readonly Regex Declaration = new(
        @"^\s*(?:public|internal|private|protected|sealed|abstract|static|partial|\s)*\b(class|record|struct)\b\s+(\w+)",
        RegexOptions.Compiled);

    [Fact]
    public void EveryBindableRowIsPartial()
    {
        var offenders = new List<string>();
        var seen = 0;

        foreach (var file in Sources())
        {
            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim() is not "[RpmBindable]")
                    continue;

                // The declaration is the next line that looks like one: another attribute can be
                // between them, and a long one can be wrapped.
                for (var j = i + 1; j < Math.Min(i + 5, lines.Length); j++)
                {
                    var match = Declaration.Match(lines[j]);
                    if (!match.Success)
                        continue;

                    seen++;
                    if (!lines[j].Contains("partial", StringComparison.Ordinal))
                    {
                        offenders.Add(
                            $"{match.Groups[2].Value} is not partial "
                            + $"({Path.GetFileName(file)}:{j + 1})");
                    }

                    break;
                }
            }
        }

        // A separate count, because a scan that matches nothing would pass this test while checking
        // nothing about the app.
        Assert.True(seen > 50, $"only {seen} [RpmBindable] declarations found; the scan is wrong");

        Assert.True(
            offenders.Count == 0,
            "These types wear [RpmBindable] but are not partial, so CsWinRT skips them without a "
            + "word and their bindings fall back to reflection, which AOT trims away:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    private static IEnumerable<string> Sources()
    {
        var app = Path.Combine(RepoRoot(), "RPM.Desktop");

        return Directory.EnumerateFiles(app, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal)
                     && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                            StringComparison.Ordinal));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RPM.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
