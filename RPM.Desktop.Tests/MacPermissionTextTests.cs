using System.Text.Json;

namespace RPM.Desktop.Tests;

/// <summary>
/// The Mac's microphone and local network sentences come from the string table, in every language.
/// The Mac release writes them with scripts/infoplist_strings.py. These tests check that the table has
/// them in every language; ReleaseScriptTests checks that the release asks for them.
/// </summary>
public class MacPermissionTextTests
{
    private static readonly string[] Locales = ["en", "fr", "es", "de", "zh-Hans"];

    [Theory]
    [InlineData("Mac_MicrophoneUsage")]
    [InlineData("Mac_LocalNetworkUsage")]
    public void Each_sentence_is_in_the_table_in_every_language(string key)
    {
        using var table = JsonDocument.Parse(File.ReadAllText(
            SharedFiles.Path("strings.json")));

        var entry = table.RootElement.GetProperty(key);
        foreach (var locale in Locales)
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty(locale).GetString()), $"{key} has no {locale}");
    }
}
