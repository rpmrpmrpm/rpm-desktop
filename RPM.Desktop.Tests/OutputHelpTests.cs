using System.Text.Json;
using System.Text.RegularExpressions;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Services.Audio;

namespace RPM.Desktop.Tests;

/// <summary>
/// The message shown when a play is refused because the sound output is not working. These tests
/// check that every cause, on every desk, points to steps that exist in all five languages.
/// </summary>
public class OutputHelpTests
{
    private static readonly string[] Locales = ["en", "fr", "es", "de", "zh-Hans"];

    /// <summary>The keys that take an argument, and how many. Everything else the message uses takes
    /// none.</summary>
    private static readonly Dictionary<string, int> Arguments = new()
    {
        ["AudioOut_Refused"] = 1,
        ["AudioOut_StepPickOther"] = 1,
    };

    /// <summary>One of each case, built by reflection. A case added later without its advice fails
    /// here, not as an exception on a user's screen.</summary>
    public static IEnumerable<object[]> Cases()
    {
        var cases = typeof(OutputTrouble).GetNestedTypes()
            .Where(t => t.IsSubclassOf(typeof(OutputTrouble)))
            .ToList();
        Assert.NotEmpty(cases);

        foreach (var type in cases)
        {
            var ctor = type.GetConstructors().Single();
            var args = ctor.GetParameters().Select(p => (object)"Speakers (USB Audio)").ToArray();
            var trouble = (OutputTrouble)ctor.Invoke(args);

            foreach (var desk in Enum.GetValues<OutputHelp.Desk>())
                yield return [trouble, desk];
        }

        // A device with no name gives a refusal that reads differently.
        foreach (var desk in Enum.GetValues<OutputHelp.Desk>())
            yield return [new OutputTrouble.Refused(""), desk];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Every_cause_has_a_sentence_and_steps_in_every_language(OutputTrouble trouble, OutputHelp.Desk desk)
    {
        var advice = OutputHelp.For(trouble, desk);

        Assert.InRange(advice.Steps.Count, 2, 4);
        Assert.Equal(advice.Steps.Count, advice.Steps.Distinct().Count());

        using var doc = Strings();
        var problems = new List<string>();
        foreach (var key in advice.Steps.Prepend(advice.Sentence))
            problems.AddRange(Check(doc, key));

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void The_settings_button_appears_where_the_system_settings_can_help(OutputTrouble trouble, OutputHelp.Desk desk)
    {
        var advice = OutputHelp.For(trouble, desk);

        // A missing library is fixed by reinstalling. No settings page brings it back. There is also no
        // settings page to open on a desk rpm does not ship for.
        var wanted = trouble is not OutputTrouble.LibraryMissing && desk != OutputHelp.Desk.Other;
        Assert.Equal(wanted, advice.SettingsUri is not null);

        if (desk == OutputHelp.Desk.Windows && wanted)
            Assert.StartsWith("ms-settings:", advice.SettingsUri);
        if (desk == OutputHelp.Desk.Mac && wanted)
            Assert.StartsWith("x-apple.systempreferences:", advice.SettingsUri);
    }

    [Fact]
    public void A_device_that_refused_is_named_and_one_with_no_name_is_not()
    {
        Assert.Equal("AudioOut_Refused", OutputHelp.For(new OutputTrouble.Refused("Kitchen"), OutputHelp.Desk.Windows).Sentence);
        Assert.Equal("AudioOut_RefusedUnnamed", OutputHelp.For(new OutputTrouble.Refused(""), OutputHelp.Desk.Windows).Sentence);
    }

    [Fact]
    public void The_steps_name_the_system_the_listener_is_on()
    {
        var none = new OutputTrouble.NoDevice();
        Assert.Contains("AudioOut_StepWindowsSound", OutputHelp.For(none, OutputHelp.Desk.Windows).Steps);
        Assert.Contains("AudioOut_StepMacSound", OutputHelp.For(none, OutputHelp.Desk.Mac).Steps);
        Assert.Contains("AudioOut_StepOtherSound", OutputHelp.For(none, OutputHelp.Desk.Other).Steps);
    }

    /// <summary>Every AudioOut_ string is used by the message. The two strings the dialog adds itself (the
    /// title and the button) must also exist. A key nobody reads is a sentence that was meant to be
    /// shown.</summary>
    [Fact]
    public void Every_sound_output_string_is_shown_somewhere()
    {
        var used = Cases()
            .Select(c => OutputHelp.For((OutputTrouble)c[0], (OutputHelp.Desk)c[1]))
            .SelectMany(a => a.Steps.Prepend(a.Sentence))
            .Concat(["AudioOut_Title", "AudioOut_OpenSoundSettings"])
            .ToHashSet();

        using var doc = Strings();
        var inTable = doc.RootElement.EnumerateObject()
            .Select(p => p.Name)
            .Where(n => n.StartsWith("AudioOut_", StringComparison.Ordinal))
            .ToHashSet();

        Assert.True(inTable.SetEquals(used),
            "In strings.json and never shown: " + string.Join(", ", inTable.Except(used)) +
            "\nShown and not in strings.json: " + string.Join(", ", used.Except(inTable)));

        var problems = new List<string>();
        foreach (var key in new[] { "AudioOut_Title", "AudioOut_OpenSoundSettings", "LibProblem_StepsHeading", "Common_Ok", "Settings_Sound" })
            problems.AddRange(Check(doc, key));
        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    private static IEnumerable<string> Check(JsonDocument doc, string key)
    {
        if (!doc.RootElement.TryGetProperty(key, out var entry))
        {
            yield return $"{key} is not in strings.json";
            yield break;
        }

        var args = Arguments.GetValueOrDefault(key);
        foreach (var locale in Locales)
        {
            var text = entry.TryGetProperty(locale, out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(text))
            {
                yield return $"{key}: no {locale}";
                continue;
            }

            if (text.IndexOfAny(['—', '–']) >= 0)
                yield return $"{key}: {locale} has a dash";

            var placeholders = Regex.Matches(text, @"\{(\d+)\}").Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
            if (!placeholders.SetEquals(Enumerable.Range(0, args)))
                yield return $"{key}: {locale} placeholders do not match {args} argument(s)";
        }
    }

    private static JsonDocument Strings() =>
        JsonDocument.Parse(File.ReadAllText(SharedFiles.Path("strings.json")));

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
