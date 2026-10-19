using System.Text.Json;
using System.Text.RegularExpressions;
using RPM.Core.Audio;

namespace RPM.Core.Tests;

/// <summary>
/// What the player says about a cast that failed or changed by itself. Every message is a localised
/// string: no hard-coded English, no <c>ex.Message</c> on the end, and no dashes in the Dolby notice.
/// </summary>
public class CastTroubleTests
{
    private static CastTrouble[] All(string device) =>
    [
        new CastTrouble.Disconnected(device), new CastTrouble.NeverFetched(device),
        new CastTrouble.StreamRefused(device), new CastTrouble.StoppedPlaying(device),
        new CastTrouble.StreamBroke(device), new CastTrouble.DolbyOff(device),
        new CastTrouble.NoSurround(device), new CastTrouble.Unreachable(device),
        new CastTrouble.ConnectFailed(device), new CastTrouble.InPair(device),
        new CastTrouble.CommandFailed(device), new CastTrouble.ReceiverError(device),
    ];

    [Fact]
    public void Only_the_two_stereo_fallbacks_are_notices()
    {
        var notices = All("Kitchen").Where(t => t.IsNotice).Select(t => t.GetType()).ToList();

        Assert.Equal([typeof(CastTrouble.DolbyOff), typeof(CastTrouble.NoSurround)], notices);
    }

    [Fact]
    public void Every_case_names_its_speaker()
    {
        Assert.All(All("Kitchen"), t => Assert.Equal("Kitchen", t.Device));
    }

    /// <summary>Keys are reached through <see cref="CastTrouble.StringKey"/>, which LocalizationKeyTests
    /// cannot see. Each one takes the speaker's name as {0} and nothing else.</summary>
    [Fact]
    public void Every_cast_sentence_is_in_every_language_with_the_speaker_as_its_one_argument()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(StringsJson()));

        var problems = new List<string>();
        foreach (var key in All("x").Select(t => t.StringKey).Distinct())
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
                    problems.Add($"{key}: no {locale}");
                else if (text.IndexOfAny(['—', '–']) >= 0)
                    problems.Add($"{key}: {locale} has a dash");
                else if (Regex.Matches(text, @"\{(\d+)\}").Select(m => m.Value).Distinct().SingleOrDefault() != "{0}")
                    problems.Add($"{key}: {locale} must use {{0}} and nothing else");
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static string StringsJson() => SharedFiles.Path("strings.json");
}
