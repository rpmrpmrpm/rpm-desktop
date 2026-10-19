namespace RPM.Core.Diagnostics;

/// <summary>
/// The app's environment switches (<c>RPM_API_URL</c>, <c>RPM_WALL_DIAG</c>, <c>RPM_BEAT_MODEL</c> and
/// the rest). Every read of one goes through <see cref="Read"/>; build rule RPM017 refuses a direct
/// <c>Environment.GetEnvironmentVariable</c> in the app's projects.
///
/// <para><b>Off in a Release build of the app.</b> The Release configuration of RPM.Desktop sets the
/// AppContext switch <see cref="AppContextName"/> to false, and then every switch reads as unset.
/// Without this gate, about 20 of them would work in the shipped app, and <c>RPM_API_URL</c> would
/// send the sign-in token to whatever host it named.</para>
///
/// <para><b>On everywhere else</b>: a Debug build of the app, the tests, and the tools. The gate is the
/// app's own setting and not <c>#if DEBUG</c> here, because TempoBench runs in Release and pins the
/// beat model with <c>RPM_BEAT_MODEL</c>, which is read inside RPM.Onnx. A library compiled in Release
/// would ignore the switch and measure the other model without saying so.</para>
///
/// <para><c>ToolPaths</c> in RPM.Fingerprint, which cannot see this type, reads the same AppContext
/// switch for <c>RPM_FFMPEG</c> and <c>RPM_FPCALC</c>.</para>
/// </summary>
public static class DevSwitch
{
    /// <summary>The AppContext switch the app's Release configuration sets to false
    /// (RuntimeHostConfigurationOption in RPM.Desktop.csproj, written into the runtime config of a
    /// Native AOT binary).</summary>
    public const string AppContextName = "RPM.DevSwitches";

    /// <summary>Whether this process reads its environment switches at all.</summary>
    public static bool Honoured => !(AppContext.TryGetSwitch(AppContextName, out var on) && !on);

    /// <summary>The switch's value, or null when it is unset or this is a Release build of the app.</summary>
    public static string? Read(string name) => Honoured ? Environment.GetEnvironmentVariable(name) : null;

    /// <summary>Whether the switch is set to <c>1</c> or <c>true</c>.</summary>
    public static bool IsOn(string name) => Read(name) is "1" or "true";
}
