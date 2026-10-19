using System.Text.Json;
using System.Text.RegularExpressions;
using RPM.Desktop.Services.Platform;

namespace RPM.Desktop.Tests;

/// <summary>
/// Reading Windows Firewall for rpm. <see cref="FirewallJudge"/> holds the reasoning and has no firewall
/// behind it, so these tests run on any machine. The COM reading was checked against the rules on a
/// development machine.
/// </summary>
public class FirewallTests
{
    private const string Rpm = @"C:\Users\x\AppData\Local\rpm\current\rpm.exe";

    private static readonly FirewallProfile PublicOn =
        new(FirewallJudge.Public, On: true, BlocksEverything: false, Notifies: true, AllowsByDefault: false);

    private static FirewallRule Rule(
        bool allows, int protocol = FirewallJudge.AnyProtocol, int profiles = FirewallJudge.AllProfiles,
        string program = Rpm, bool inbound = true, bool enabled = true) =>
        new(program, inbound, enabled, allows, profiles, protocol);

    private static FirewallStanding Judge(params FirewallRule[] rules) =>
        FirewallJudge.Judge(Rpm, [PublicOn], rules);

    [Fact]
    public void An_allow_rule_opens_rpm() =>
        Assert.Equal(FirewallStanding.Open, Judge(Rule(allows: true, FirewallJudge.Tcp)));

    [Fact]
    public void A_block_rule_wins_over_an_allow_rule() =>
        Assert.Equal(FirewallStanding.Blocked,
            Judge(Rule(allows: true), Rule(allows: false, FirewallJudge.Tcp)));

    /// <summary>Discovery replies arrive over UDP, so a block there stops sharing even with TCP
    /// open.</summary>
    [Fact]
    public void A_block_on_udp_alone_still_blocks() =>
        Assert.Equal(FirewallStanding.Blocked,
            Judge(Rule(allows: true, FirewallJudge.Tcp), Rule(allows: false, FirewallJudge.Udp)));

    /// <summary>Both servers use TCP, so a rule for UDP alone does not open rpm.</summary>
    [Fact]
    public void An_allow_rule_for_udp_alone_does_not_open_rpm() =>
        Assert.Equal(FirewallStanding.NotDecided, Judge(Rule(allows: true, FirewallJudge.Udp)));

    /// <summary>Windows shows its own prompt when rpm listens, so rpm says nothing.</summary>
    [Fact]
    public void No_rule_with_notifications_on_is_not_decided() =>
        Assert.Equal(FirewallStanding.NotDecided, Judge());

    [Fact]
    public void No_rule_with_notifications_off_is_blocked() =>
        Assert.Equal(FirewallStanding.Blocked,
            FirewallJudge.Judge(Rpm, [PublicOn with { Notifies = false }], []));

    [Fact]
    public void No_rule_where_the_default_allows_is_open() =>
        Assert.Equal(FirewallStanding.Open,
            FirewallJudge.Judge(Rpm, [PublicOn with { AllowsByDefault = true, Notifies = false }], []));

    [Fact]
    public void A_firewall_that_is_off_blocks_nothing() =>
        Assert.Equal(FirewallStanding.Open,
            FirewallJudge.Judge(Rpm, [PublicOn with { On = false }], [Rule(allows: false)]));

    /// <summary>"Block all incoming connections" overrides allowed apps, so a rule from rpm would not help.
    /// The button opens Windows Security instead.</summary>
    [Fact]
    public void Block_all_incoming_overrides_an_allow_rule() =>
        Assert.Equal(FirewallStanding.BlocksEverything,
            FirewallJudge.Judge(Rpm, [PublicOn with { BlocksEverything = true }], [Rule(allows: true)]));

    [Fact]
    public void Rules_that_do_not_apply_are_ignored()
    {
        var standing = Judge(
            Rule(allows: false, program: @"C:\Other\rpm.exe"),
            Rule(allows: false, enabled: false),
            Rule(allows: false, inbound: false),
            Rule(allows: false, profiles: FirewallJudge.Private),
            Rule(allows: false, protocol: 1),
            Rule(allows: false, program: ""));

        Assert.Equal(FirewallStanding.NotDecided, standing);
    }

    /// <summary>Rules made by Windows' own prompt for the Public profile read back as 0xFFFF0004, not 4. So
    /// the profile must be tested by bit.</summary>
    [Fact]
    public void Profiles_are_tested_by_bit() =>
        Assert.Equal(FirewallStanding.Blocked,
            Judge(Rule(allows: false, FirewallJudge.Tcp, profiles: unchecked((int)0xFFFF0004))));

    /// <summary>Rules made by Windows' prompt store the path in lower case.</summary>
    [Fact]
    public void A_rule_names_rpm_whatever_the_case_of_its_path() =>
        Assert.Equal(FirewallStanding.Blocked, Judge(Rule(allows: false, program: Rpm.ToLowerInvariant())));

    [Fact]
    public void A_rule_names_rpm_through_an_environment_variable()
    {
        var local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrEmpty(local))
            return;

        var program = Path.Combine(local, "rpm", "current", "rpm.exe");
        var standing = FirewallJudge.Judge(program, [PublicOn],
            [Rule(allows: false, program: @"%LOCALAPPDATA%\rpm\current\rpm.exe")]);

        Assert.Equal(FirewallStanding.Blocked, standing);
    }

    /// <summary>The speaker can be on any connected network, and rpm's rule covers every type.</summary>
    [Fact]
    public void The_worst_network_decides()
    {
        var domain = PublicOn with { Type = FirewallJudge.Domain };
        var standing = FirewallJudge.Judge(Rpm, [PublicOn, domain],
            [Rule(allows: true, profiles: FirewallJudge.Public), Rule(allows: false, profiles: FirewallJudge.Domain)]);

        Assert.Equal(FirewallStanding.Blocked, standing);
    }

    /// <summary>If a speaker took the stream but never fetched it, a firewall rule does not fix the
    /// problem. The other readings stay as they are.</summary>
    [Fact]
    public void After_a_failed_fetch_no_rule_counts_as_blocked()
    {
        Assert.Equal(FirewallStanding.Blocked, FirewallJudge.AfterNoFetch(FirewallStanding.NotDecided));
        Assert.Equal(FirewallStanding.Open, FirewallJudge.AfterNoFetch(FirewallStanding.Open));
        Assert.Equal(FirewallStanding.Unknown, FirewallJudge.AfterNoFetch(FirewallStanding.Unknown));
        Assert.Equal(FirewallStanding.BlocksEverything, FirewallJudge.AfterNoFetch(FirewallStanding.BlocksEverything));
    }

    [Fact]
    public void No_network_is_unknown() =>
        Assert.Equal(FirewallStanding.Unknown, FirewallJudge.Judge(Rpm, [], [Rule(allows: false)]));

    /// <summary>Main calls this before anything else, so every ordinary start goes through it.</summary>
    [Fact]
    public void The_helper_leaves_an_ordinary_start_alone()
    {
        WindowsFirewall.ServeIfAsked([]);
        WindowsFirewall.ServeIfAsked(["--gallery"]);
        WindowsFirewall.ServeIfAsked([WindowsFirewall.AllowArgument, "extra"]);
    }

    /// <summary>The keys that the firewall line and question pick by state. The repo-wide guard cannot find
    /// most of them, because they are not the first argument of a <c>Loc</c> call.</summary>
    [Fact]
    public void The_firewall_text_is_in_every_language()
    {
        (string Key, int Args)[] keys =
        [
            ("Firewall_CastBlocked", 0), ("Firewall_ShareBlocked", 0), ("Firewall_BlocksEverything", 0),
            ("Firewall_StillBlocked", 0), ("Firewall_Allow", 0), ("Firewall_OpenSecurity", 0),
            ("Firewall_AskTitle", 0), ("Firewall_AskBody", 1), ("Firewall_AskAllow", 0),
            ("Common_Cancel", 0),
        ];

        using var doc = JsonDocument.Parse(File.ReadAllText(SharedFiles.Path("strings.json")));
        var problems = new List<string>();

        foreach (var (key, args) in keys)
        {
            if (!doc.RootElement.TryGetProperty(key, out var entry))
            {
                problems.Add($"{key} is not in strings.json");
                continue;
            }

            foreach (var locale in new[] { "en", "fr", "es", "de", "zh-Hans" })
            {
                if (!entry.TryGetProperty(locale, out var value) || value.GetString() is not { Length: > 0 } text)
                {
                    problems.Add($"{key}: no {locale}");
                    continue;
                }

                if (text.IndexOfAny(['—', '–']) >= 0)
                    problems.Add($"{key}: {locale} has a dash");

                var used = Regex.Matches(text, @"\{(\d+)\}").Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
                if (!used.SetEquals(Enumerable.Range(0, args)))
                    problems.Add($"{key}: {locale} placeholders do not match {args} argument(s)");
            }
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
