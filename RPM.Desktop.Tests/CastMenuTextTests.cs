using System.Text.Json;
using System.Text.RegularExpressions;

namespace RPM.Desktop.Tests;

/// <summary>
/// The desktop cast menu shows text in the user's language. All visible text comes from the string table.
/// Text written directly into the XAML or the code would always be English.
/// </summary>
public class CastMenuTextTests
{
    /// <summary>A literal string would show the same English in every language. Only glyphs
    /// (<c>&amp;#x…;</c>) and bindings are allowed in a <c>Text</c> here.</summary>
    [Fact]
    public void The_cast_menu_has_no_text_written_into_it()
    {
        var xaml = File.ReadAllText(Path.Combine(RepoRoot(), "RPM.Desktop", "Presentation", "RPaMp", "CastFlyoutContent.xaml"));

        var literals = Regex.Matches(xaml, @"\b(?:Text|Content|ToolTipService\.ToolTip)=""([^""]*)""")
            .Select(m => m.Groups[1].Value)
            .Where(v => !v.StartsWith('{') && !v.StartsWith("&#x", StringComparison.Ordinal))
            .ToList();

        Assert.True(literals.Count == 0, "Written into CastFlyoutContent.xaml: " + string.Join(", ", literals));
    }

    /// <summary>The keys the cast menu's model picks by state. The repo-wide guard cannot find them,
    /// because they are not the first argument of a <c>Loc</c> call. Several are shared with the iPhone's
    /// speaker picker, so both apps use the same words.</summary>
    [Fact]
    public void Every_cast_menu_sentence_is_in_every_language()
    {
        (string Key, int Args)[] keys =
        [
            ("Rpm_Cast_Title", 0), ("Rpm_Cast_Searching", 0), ("Rpm_Cast_None", 0), ("Rpm_Cast_Group", 0),
            ("Rpm_Cast_Playing", 1), ("Rpm_Cast_PlayOn", 0), ("Rpm_Cast_LookAgain", 0),
            ("Cast_ThisComputer", 0), ("Cast_SoundCard", 0), ("Cast_Lossless", 0), ("Cast_LosslessHint", 0),
            ("Cast_Surround", 0), ("Cast_SurroundHint", 0), ("Cast_ScreenOff", 0), ("Cast_ScreenOffHint", 0),
            ("Cast_TooltipCasting", 1),
        ];

        using var doc = JsonDocument.Parse(File.ReadAllText(SharedFiles.Path("strings.json")));

        var problems = new List<string>();
        foreach (var (key, args) in keys)
        {
            if (!doc.RootElement.TryGetProperty(key, out var entry))
            {
                problems.Add($"{key} is not in strings.json");
                continue;
            }

            foreach (var locale in new[] { "en", "fr", "es", "de", "zh-Hans" })
            {
                var text = entry.TryGetProperty(locale, out var t) ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(text))
                {
                    problems.Add($"{key}: no {locale}");
                    continue;
                }

                if (text.IndexOfAny(['—', '–']) >= 0)
                    problems.Add($"{key}: {locale} has a dash");

                var used = Regex.Matches(text, @"\{(\d+)\}").Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
                if (!used.SetEquals(Enumerable.Range(0, args)))
                    problems.Add($"{key}: {locale} placeholders do not match {args} argument(s)");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

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
