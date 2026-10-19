using System.Text.Json.Serialization;

namespace RPM.Core.Crashes;

/// <summary>Whether crash reports are sent. The person is asked once, at the first crash, and the answer
/// is remembered. It is saved by name, so adding a value in the middle cannot change a saved
/// answer.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CrashConsent>))]
public enum CrashConsent
{
    NotAsked,
    Send,
    DontSend,
}

/// <summary>What the launch does about pending crash reports.</summary>
public enum CrashStep
{
    Nothing,
    Ask,
    Send,
    Discard,
}

public static class CrashConsentRule
{
    public static CrashStep Next(CrashConsent consent, int pending) =>
        pending == 0 ? CrashStep.Nothing : consent switch
        {
            CrashConsent.Send => CrashStep.Send,
            CrashConsent.DontSend => CrashStep.Discard,
            _ => CrashStep.Ask,
        };

    /// <summary>The setting that the Settings switch asks for, or null when the switch only shows what
    /// is already set. Setting <c>IsOn</c> while the page draws also fires <c>Toggled</c>. This check
    /// tells that apart from the user. So drawing the switch off for <see cref="CrashConsent.NotAsked"/>
    /// does not record an answer that nobody gave.
    /// </summary>
    public static CrashConsent? FromSwitch(bool isOn, CrashConsent current) =>
        isOn == (current == CrashConsent.Send) ? null
        : isOn ? CrashConsent.Send
        : CrashConsent.DontSend;
}
