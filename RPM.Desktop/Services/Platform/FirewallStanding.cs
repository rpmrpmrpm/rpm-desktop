namespace RPM.Desktop.Services.Platform;

/// <summary>
/// What Windows Firewall does with connections from the local network to this copy of rpm, on the
/// networks the computer is connected to now.
///
/// <para>Casting and library sharing both need incoming connections. A speaker fetches the stream from
/// <c>CastServer</c>, and another computer reads the library from <c>LibraryServer</c>. Both kinds of
/// discovery receive multicast replies. When the firewall blocks rpm, none of this arrives, and nothing
/// fails on this computer. Instead the cast ends with "did not start playing" after 20 seconds, the
/// speaker list can stay empty, and other computers never see this one.</para>
/// </summary>
public enum FirewallStanding
{
    /// <summary>The firewall could not be read, its rules are set by the organization, or this is
    /// not Windows. Nothing is said.</summary>
    Unknown,

    /// <summary>Connections reach rpm: an allow rule covers it, or the firewall is off.</summary>
    Open,

    /// <summary>No rule for rpm on this kind of network. Windows asks the user the first time rpm
    /// listens, so rpm does not ask as well.</summary>
    NotDecided,

    /// <summary>A rule blocks rpm. This is usually made when the user clicks Cancel on Windows' own prompt.
    /// It can also happen when nothing allows rpm and Windows will not ask. An allow rule fixes it: see
    /// <see cref="WindowsFirewall.AllowAsync"/>.</summary>
    Blocked,

    /// <summary>The firewall blocks every incoming connection on this network, allowed apps included.
    /// No rule changes that; only the setting in Windows Security does.</summary>
    BlocksEverything,
}

/// <summary>One firewall rule, reduced to what <see cref="FirewallJudge"/> reads.</summary>
/// <param name="Program">The program the rule names. Empty for a rule that covers every program.</param>
/// <param name="Profiles">The network types it applies to, as <see cref="FirewallJudge.Domain"/> and
/// the others.</param>
/// <param name="Protocol">IANA protocol number, or <see cref="FirewallJudge.AnyProtocol"/>.</param>
internal readonly record struct FirewallRule(
    string Program, bool Inbound, bool Enabled, bool Allows, int Profiles, int Protocol);

/// <summary>One network type's settings, for a type the computer is connected through now.</summary>
/// <param name="Type"><see cref="FirewallJudge.Domain"/>, <see cref="FirewallJudge.Private"/> or
/// <see cref="FirewallJudge.Public"/>.</param>
/// <param name="On">The firewall is on for this type.</param>
/// <param name="BlocksEverything">"Block all incoming connections, including those in the list of
/// allowed apps" is set.</param>
/// <param name="Notifies">Windows shows its own prompt when a program with no rule listens.</param>
/// <param name="AllowsByDefault">A connection that no rule matches is allowed.</param>
internal readonly record struct FirewallProfile(
    int Type, bool On, bool BlocksEverything, bool Notifies, bool AllowsByDefault);

/// <summary>
/// Works out a <see cref="FirewallStanding"/> from the rules and settings, without a real firewall, so
/// the logic can be tested on its own.
/// </summary>
internal static class FirewallJudge
{
    public const int Domain = 1, Private = 2, Public = 4;
    public const int AllProfiles = 0x7FFFFFFF;
    public const int Tcp = 6, Udp = 17, AnyProtocol = 256;

    /// <summary>The standing on every network type in <paramref name="current"/>, taking the worst.
    /// </summary>
    /// <remarks>
    /// Only rules that name <paramref name="program"/> are read. Rules for all programs are left out.
    /// The ports rpm listens on are chosen by the system at each start, so no port rule is about rpm.
    /// <para>The worst standing is taken, because a speaker can be on any of the connected networks.
    /// The allow rule that rpm adds covers all of them, so one fix settles every case.</para>
    /// </remarks>
    public static FirewallStanding Judge(
        string program, IReadOnlyList<FirewallProfile> current, IReadOnlyList<FirewallRule> rules)
    {
        if (current.Count == 0)
            return FirewallStanding.Unknown;

        var worst = FirewallStanding.Open;
        foreach (var profile in current)
        {
            var standing = JudgeOne(program, profile, rules);
            if (Severity(standing) > Severity(worst))
                worst = standing;
        }

        return worst;
    }

    private static FirewallStanding JudgeOne(
        string program, FirewallProfile profile, IReadOnlyList<FirewallRule> rules)
    {
        if (!profile.On)
            return FirewallStanding.Open;

        if (profile.BlocksEverything)
            return FirewallStanding.BlocksEverything;

        bool allowed = false;
        foreach (var rule in rules)
        {
            if (!rule.Inbound || !rule.Enabled || (rule.Profiles & profile.Type) == 0)
                continue;
            if (rule.Protocol is not (Tcp or Udp or AnyProtocol))
                continue;
            if (!SameProgram(rule.Program, program))
                continue;

            // A block rule wins over any allow rule, which is how the firewall itself decides. A block
            // on UDP alone still counts: discovery replies arrive over UDP.
            if (!rule.Allows)
                return FirewallStanding.Blocked;

            // Both servers are TCP, so an allow rule for UDP alone does not open rpm.
            if (rule.Protocol is Tcp or AnyProtocol)
                allowed = true;
        }

        if (allowed || profile.AllowsByDefault)
            return FirewallStanding.Open;

        return profile.Notifies ? FirewallStanding.NotDecided : FirewallStanding.Blocked;
    }

    /// <summary>The standing to use when a speaker has taken the stream but never fetched it. If there is
    /// no rule yet, Windows asks by itself and rpm says nothing. After a failed fetch, Windows has either
    /// been answered or was never asked. A rule from rpm is what would help, so NotDecided is treated as
    /// Blocked.
    /// </summary>
    public static FirewallStanding AfterNoFetch(FirewallStanding standing) =>
        standing == FirewallStanding.NotDecided ? FirewallStanding.Blocked : standing;

    private static int Severity(FirewallStanding standing) => standing switch
    {
        FirewallStanding.Open => 0,
        FirewallStanding.NotDecided => 1,
        FirewallStanding.Blocked => 2,
        FirewallStanding.BlocksEverything => 3,
        _ => 0,
    };

    /// <summary>
    /// Whether a rule's program is this one. Rules made by Windows' prompt store the path in lower
    /// case, and rules made by hand can use environment variables, so both are compared expanded and
    /// without case.
    /// </summary>
    public static bool SameProgram(string? ruleProgram, string program)
    {
        if (string.IsNullOrWhiteSpace(ruleProgram))
            return false;

        return string.Equals(Normal(ruleProgram), Normal(program), StringComparison.OrdinalIgnoreCase);

        static string Normal(string path)
        {
            var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            try { return Path.GetFullPath(expanded); }
            catch { return expanded; }
        }
    }
}
