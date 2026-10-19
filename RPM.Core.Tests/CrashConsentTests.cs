using System.Text.Json;
using RPM.Core.Crashes;

namespace RPM.Core.Tests;

public class CrashConsentTests
{
    [Theory]
    [InlineData(CrashConsent.NotAsked, 0, CrashStep.Nothing)]
    [InlineData(CrashConsent.Send, 0, CrashStep.Nothing)]
    [InlineData(CrashConsent.DontSend, 0, CrashStep.Nothing)]
    [InlineData(CrashConsent.NotAsked, 2, CrashStep.Ask)]
    [InlineData(CrashConsent.Send, 2, CrashStep.Send)]
    [InlineData(CrashConsent.DontSend, 2, CrashStep.Discard)]
    public void What_happens_at_launch(CrashConsent consent, int pending, CrashStep expected)
    {
        Assert.Equal(expected, CrashConsentRule.Next(consent, pending));
    }

    /// <summary>The page fires Toggled when it draws the switch; that is not an answer.</summary>
    [Theory]
    [InlineData(false, CrashConsent.NotAsked, null)]
    [InlineData(false, CrashConsent.DontSend, null)]
    [InlineData(true, CrashConsent.Send, null)]
    [InlineData(true, CrashConsent.NotAsked, CrashConsent.Send)]
    [InlineData(true, CrashConsent.DontSend, CrashConsent.Send)]
    [InlineData(false, CrashConsent.Send, CrashConsent.DontSend)]
    public void The_switch_changes_the_setting_only_when_it_disagrees_with_it(bool isOn, CrashConsent current, CrashConsent? expected)
    {
        Assert.Equal(expected, CrashConsentRule.FromSwitch(isOn, current));
    }

    /// <summary>The answer is saved in settings.json by name, so adding a value cannot change what a
    /// saved answer means.</summary>
    [Fact]
    public void The_answer_is_saved_by_name()
    {
        Assert.Equal("\"DontSend\"", JsonSerializer.Serialize(CrashConsent.DontSend));
        Assert.Equal(CrashConsent.Send, JsonSerializer.Deserialize<CrashConsent>("\"Send\""));
    }
}
