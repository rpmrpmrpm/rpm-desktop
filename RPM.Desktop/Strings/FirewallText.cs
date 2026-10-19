using RPM.Desktop.Services.Platform;

namespace RPM.Desktop.Strings;

/// <summary>
/// A <see cref="FirewallStanding"/> in the user's language, for the line and button the cast menu and
/// the sharing settings put under it. One place, so the two cannot say different things about the same
/// firewall.
/// </summary>
internal static class FirewallText
{
    /// <summary>The line for <paramref name="standing"/>, or null when there is nothing to say.</summary>
    /// <param name="blockedKey">What a block stops, in the place the line is shown:
    /// <c>Firewall_CastBlocked</c> or <c>Firewall_ShareBlocked</c>.</param>
    public static string? Line(FirewallStanding standing, string blockedKey) => standing switch
    {
        FirewallStanding.Blocked => Loc.Get(WindowsFirewall.HasTriedToAllow ? "Firewall_StillBlocked" : blockedKey),
        FirewallStanding.BlocksEverything => Loc.Get("Firewall_BlocksEverything"),
        _ => null,
    };

    /// <summary>The button under the line. A rule fixes a block; only Windows Security can turn off
    /// "block all incoming connections".</summary>
    public static string Button(FirewallStanding standing) =>
        Loc.Get(standing == FirewallStanding.BlocksEverything ? "Firewall_OpenSecurity" : "Firewall_Allow");
}
