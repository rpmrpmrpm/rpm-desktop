using System.Runtime.InteropServices;

namespace RPM.Desktop.Services.Updates;

/// <summary>Which head this copy of the app is. Not every platform the app runs on has a release channel.
/// Linux is built from source, and <see cref="UpdateHead.Other"/> is how that is stated.</summary>
public enum UpdateHead
{
    Other,
    MacOS,
    Windows,
}

/// <summary>
/// Which update feed a copy of the app reads.
/// </summary>
/// <remarks>
/// There is one prefix per published build, all under one host. Each prefix holds a Velopack feed (the
/// releases file and the packages it names). It also holds the installers under <c>latest/</c>, beside
/// them. The site links to those installers.
///
/// <para>The channel is decided by the head and by the ARCHITECTURE OF THIS PROCESS, not by the machine. An
/// Intel build that runs on Apple silicon under Rosetta reports x64. That is correct. It must be offered
/// the next Intel build, not a package that it cannot run.</para>
///
/// <para>A pair that nothing is published for returns null, and null means the app never asks. Returning
/// the nearest feed instead would offer somebody a package built for another architecture, based only on a
/// guess.</para>
/// </remarks>
public static class UpdateChannel
{
    /// <summary>Where the feeds live. This is RPM's own host, not a release page. RPM's site, its API and
    /// its accounts are all on this domain.</summary>
    public const string Host = "https://updates.rpmrpmrpm.com";

    /// <summary>The channel for a head and an architecture, or null where nothing is published for that
    /// pair.</summary>
    public static string? Name(UpdateHead head, Architecture arch) => (head, arch) switch
    {
        (UpdateHead.MacOS, Architecture.Arm64) => "osx-arm64",
        (UpdateHead.MacOS, Architecture.X64) => "osx-x64",
        (UpdateHead.Windows, Architecture.X64) => "win-x64",
        _ => null,
    };

    /// <summary>Whether this copy was built by the release (<c>-p:RpmUpdates=true</c>, see RPM.Desktop.csproj).
    /// Only those copies read a feed. A copy built from source is not one of the published builds, so an
    /// update would replace it with something else.</summary>
    public static bool Released =>
#if RPM_UPDATES
        true;
#else
        false;
#endif

    /// <summary>This copy's channel, or null where nothing is published for it or it was not built by the
    /// release.</summary>
    public static string? Current => !Released ? null : Name(
        OperatingSystem.IsMacOS() ? UpdateHead.MacOS
        : OperatingSystem.IsWindows() ? UpdateHead.Windows
        : UpdateHead.Other,
        RuntimeInformation.ProcessArchitecture);

    /// <summary>The feed that a channel is read from. It ends in a slash, because what Velopack adds after
    /// it is a file name.</summary>
    public static string Url(string channel) => $"{Host}/{channel}/";
}
