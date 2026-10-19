using System.Text.Json;
using RPM.Core.Import;
using RPM.Desktop.Services.Audio;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Tests;

/// <summary>
/// The names of the built-in presets the pickers offer: the equalizer curves and the file naming
/// templates. Each name is a translation key, so each language shows its own word.
///
/// <para>Their keys are handed out by a table rather than written at a <c>Loc.Get</c> call, so the
/// repo-wide guard over <c>strings.json</c> cannot see them. Each is checked here against the file.</para>
/// </summary>
public class PresetNameTests
{
    private static readonly string[] Locales = ["en", "fr", "es", "de", "zh-Hans"];

    [Fact]
    public void Every_equalizer_preset_has_a_name_in_every_language()
        => AssertInTable(EqService.Presets.Select(p => p.Key));

    /// <summary>A built-in template left out of <see cref="NamingText"/> shows its stored English name
    /// in every language, which is the failure this is here to catch.</summary>
    [Fact]
    public void Every_built_in_naming_template_has_a_name_in_every_language()
    {
        var keys = NamingTemplate.Presets.Select(t => NamingText.Key(t.Name)).ToList();

        Assert.All(keys, Assert.NotNull);
        AssertInTable(keys.Select(k => k!));
    }

    /// <summary>A template the user made is shown with the name they typed.</summary>
    [Fact]
    public void A_template_the_user_made_has_no_key()
        => Assert.Null(NamingText.Key("My vinyl rips"));

    private static void AssertInTable(IEnumerable<string> keys)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(SharedFiles.Path("strings.json")));

        var problems = new List<string>();
        foreach (var key in keys)
        {
            if (!doc.RootElement.TryGetProperty(key, out var entry))
            {
                problems.Add($"{key} is not in strings.json");
                continue;
            }

            foreach (var locale in Locales)
                if (!entry.TryGetProperty(locale, out var text) || string.IsNullOrWhiteSpace(text.GetString()))
                    problems.Add($"{key}: no {locale}");
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
