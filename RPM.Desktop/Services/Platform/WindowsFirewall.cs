using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Services.Platform;

/// <summary>What happened when rpm asked to be let through the firewall.</summary>
public enum FirewallChange
{
    /// <summary>The elevated helper ran to the end. Read the firewall again to see the result: the
    /// helper's exit code is not trusted for it.</summary>
    Ran,

    /// <summary>The person said no at the Windows prompt.</summary>
    Declined,

    /// <summary>The helper could not be started. Written to the ring.</summary>
    CouldNotStart,
}

/// <summary>
/// Windows Firewall, as far as rpm needs it: whether connections from the local network reach this
/// copy of rpm (<see cref="ReadAsync"/>), and adding the rule that lets them (<see cref="AllowAsync"/>).
///
/// <para><b>Reading</b> needs no administrator rights and goes through the firewall's documented COM
/// interface, <c>INetFwPolicy2</c>. The interfaces below are source-generated, so they work in a
/// Native AOT build.</para>
///
/// <para><b>Changing</b> needs administrator rights. rpm starts itself again, elevated, with
/// <see cref="AllowArgument"/>, so the Windows prompt names rpm and its signer rather than a system
/// tool. That process adds one rule and exits before any window, log or update check is set up
/// (<see cref="ServeIfAsked"/>).</para>
///
/// <para>The rule rpm adds allows inbound connections to this executable, on every network type, from the
/// local subnet only. Casting and sharing only ever talk to devices on the same network, so that is all the
/// rule opens. Block rules for this executable are turned off rather than deleted, so they stay visible in
/// the firewall's settings.</para>
/// </summary>
public static partial class WindowsFirewall
{
    /// <summary>The argument that makes rpm the elevated helper instead of the app.</summary>
    public const string AllowArgument = "--allow-through-firewall";

    /// <summary>The name and group of the rule rpm adds. The product name, so it needs no translation
    /// and reads the same as the rules Windows' own prompt makes for rpm.</summary>
    private const string RuleName = "rpm";

    private static readonly object Gate = new();
    private static Task<FirewallChange>? _changing;

    /// <summary>True once an elevated helper has run in this process. A later read that still says
    /// <see cref="FirewallStanding.Blocked"/> means the change did not hold, which is worth saying
    /// differently.
    /// </summary>
    public static bool HasTriedToAllow { get; private set; }

    /// <summary>
    /// Reads the firewall off the calling thread. Never throws: a firewall that cannot be read is
    /// <see cref="FirewallStanding.Unknown"/>, and that is written to the ring.
    /// </summary>
    public static Task<FirewallStanding> ReadAsync()
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { Length: > 0 } program)
            return Task.FromResult(FirewallStanding.Unknown);

        return Task.Run(() =>
        {
            try
            {
                var standing = InApartment(() => Read(program));
                Scope.Said("firewall", $"read: {standing}");
                return standing;
            }
            catch (Exception ex)
            {
                Faults.Note("firewall", "reading Windows Firewall", ex);
                return FirewallStanding.Unknown;
            }
        });
    }

    /// <summary>
    /// Asks Windows to let rpm through: starts rpm elevated as the helper and waits for it. One at a
    /// time: a second call while the first is running gets the same task, so two buttons pressed
    /// together show one Windows prompt.
    /// </summary>
    public static Task<FirewallChange> AllowAsync()
    {
        lock (Gate)
        {
            if (_changing is { IsCompleted: false } running)
                return running;

            return _changing = Task.Run(ElevateAsync);
        }
    }

    /// <summary>
    /// What the button under a firewall line does: asks Windows for the rule, or, when the firewall
    /// blocks every incoming connection, opens Windows Security, since no rule can change that.
    /// </summary>
    public static async Task FixAsync(FirewallStanding standing)
    {
        if (standing == FirewallStanding.BlocksEverything)
            OpenSecurity();
        else
            await AllowAsync();
    }

    /// <summary>Opens Windows Security at Firewall and network protection, where "block all incoming
    /// connections" is turned off. That setting is the person's to change, so rpm only takes them
    /// there.
    /// </summary>
    public static void OpenSecurity()
    {
        try
        {
            Process.Start(new ProcessStartInfo("windowsdefender://network/") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Faults.Note("firewall", "opening Windows Security", ex);
        }
    }

    /// <summary>
    /// Serves the helper run and ends the process, when <paramref name="args"/> asks for it. Returns
    /// without doing anything otherwise.
    ///
    /// <para>Called first thing in Main, before Velopack: Velopack applies a downloaded update on
    /// startup, and that must not happen in an elevated process. Nothing here writes to the app's
    /// local-data folder either, because files an elevated process creates there would belong to the
    /// administrator and the app could not write them afterwards. That rules out <c>Scope</c> and
    /// <c>Faults.Note</c> on this path: both write the ring.</para>
    /// </summary>
    public static void ServeIfAsked(string[] args)
    {
        if (args is not [AllowArgument])
            return;

        int code = 1;
        try
        {
            if (OperatingSystem.IsWindows() && Environment.ProcessPath is { Length: > 0 } program)
            {
                InApartment(() => { AllowHere(program); return 0; });
                code = 0;
            }
        }
        catch (Exception ex)
        {
            try { Console.Error.WriteLine(ex); } catch { }
        }

        Environment.Exit(code);
    }

    private static async Task<FirewallChange> ElevateAsync()
    {
        if (!OperatingSystem.IsWindows() || Environment.ProcessPath is not { Length: > 0 } program)
            return FirewallChange.CouldNotStart;

        Process? helper;
        try
        {
            helper = Process.Start(new ProcessStartInfo(program, AllowArgument)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            Scope.Said("firewall", "allow: declined at the Windows prompt");
            return FirewallChange.Declined;
        }
        catch (Exception ex)
        {
            Faults.Note("firewall", "starting the firewall helper", ex);
            return FirewallChange.CouldNotStart;
        }

        if (helper is null)
            return FirewallChange.CouldNotStart;

        using (helper)
        {
            await helper.WaitForExitAsync();
            HasTriedToAllow = true;

            try
            {
                Scope.Said("firewall", $"allow: the helper exited with {helper.ExitCode}");
            }
            catch
            {
                // The exit code is only for the ring. The caller reads the firewall again either way.
                Scope.Said("firewall", "allow: the helper exited; its exit code could not be read");
            }
        }

        return FirewallChange.Ran;
    }

    /// <summary>ERROR_CANCELLED: the person chose No at the Windows prompt.</summary>
    private const int ErrorCancelled = 1223;

    // ── reading and changing, on a thread in the multithreaded apartment ──

    private static FirewallStanding Read(string program)
    {
        var policy = CreatePolicy();

        // Rules set by the organization replace the local ones, so neither the reading below nor a
        // rule rpm adds would say anything true.
        if (policy.get_LocalPolicyModifyState() != ModifyStateOk)
            return FirewallStanding.Unknown;

        int types = policy.get_CurrentProfileTypes();
        var current = new List<FirewallProfile>(3);
        foreach (int type in new[] { FirewallJudge.Domain, FirewallJudge.Private, FirewallJudge.Public })
        {
            if ((types & type) == 0)
                continue;

            current.Add(new FirewallProfile(
                type,
                On: policy.get_FirewallEnabled(type) != 0,
                BlocksEverything: policy.get_BlockAllInboundTraffic(type) != 0,
                Notifies: policy.get_NotificationsDisabled(type) == 0,
                AllowsByDefault: policy.get_DefaultInboundAction(type) == ActionAllow));
        }

        var rules = new List<FirewallRule>();
        foreach (var rule in Rules(policy.get_Rules()))
        {
            // The program is read first and alone: almost every rule is about another program, and
            // the other properties are only worth reading for the few that name this one.
            var name = rule.get_ApplicationName();
            if (!FirewallJudge.SameProgram(name, program))
                continue;

            rules.Add(new FirewallRule(
                name ?? "",
                Inbound: rule.get_Direction() == DirectionIn,
                Enabled: rule.get_Enabled() != 0,
                Allows: rule.get_Action() == ActionAllow,
                Profiles: rule.get_Profiles(),
                Protocol: rule.get_Protocol()));
        }

        return FirewallJudge.Judge(program, current, rules);
    }

    /// <summary>
    /// The elevated helper's work: turns off every inbound block rule for <paramref name="program"/>,
    /// and adds rpm's allow rule unless an enabled one is already there.
    /// </summary>
    private static void AllowHere(string program)
    {
        var policy = CreatePolicy();
        var rules = policy.get_Rules();

        bool present = false;
        foreach (var rule in Rules(rules))
        {
            if (!FirewallJudge.SameProgram(rule.get_ApplicationName(), program))
                continue;
            if (rule.get_Direction() != DirectionIn || rule.get_Enabled() == 0)
                continue;

            // Profiles is tested by bits, not compared. The firewall can return high bits that are not set.
            // For example, a rule that Windows' prompt makes for Public reads as 0xFFFF0004 on some
            // machines.
            const int every = FirewallJudge.Domain | FirewallJudge.Private | FirewallJudge.Public;
            if (rule.get_Action() != ActionAllow)
                rule.put_Enabled(VariantFalse);
            else if (rule.get_Grouping() == RuleName && (rule.get_Profiles() & every) == every)
                present = true;
        }

        if (present)
            return;

        var added = Create<INetFwRule>(FwRuleClass);
        added.put_Name(RuleName);
        added.put_Grouping(RuleName);
        added.put_ApplicationName(program);
        added.put_Protocol(FirewallJudge.AnyProtocol);
        added.put_Direction(DirectionIn);
        added.put_Action(ActionAllow);
        added.put_Profiles(FirewallJudge.AllProfiles);
        added.put_RemoteAddresses("LocalSubnet");
        added.put_Enabled(VariantTrue);
        rules.Add(added);
    }

    /// <summary>
    /// Runs <paramref name="work"/> with COM initialised for the multithreaded apartment on this
    /// thread, and uninitialised again after. The firewall objects are registered as "Both", so they
    /// live in this apartment and need no message pump.
    /// </summary>
    private static T InApartment<T>(Func<T> work)
    {
        int hr = CoInitializeEx(0, CoinitMultithreaded);

        // S_OK and S_FALSE (already initialised) both need a matching CoUninitialize. A thread already
        // in the single-threaded apartment says RPC_E_CHANGED_MODE and is used as it is.
        bool balance = hr >= 0;
        try
        {
            return work();
        }
        finally
        {
            if (balance)
                CoUninitialize();
        }
    }

    private static INetFwPolicy2 CreatePolicy() => Create<INetFwPolicy2>(FwPolicy2Class);

    private static T Create<T>(Guid clsid) where T : class
    {
        var iid = typeof(T).GUID;
        Marshal.ThrowExceptionForHR(CoCreateInstance(clsid, 0, ClsctxInprocServer, iid, out var pointer));
        try
        {
            return (T)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.None);
        }
        finally
        {
            Marshal.Release(pointer);
        }
    }

    /// <summary>
    /// Every rule in the collection. The collection is only enumerable through <c>IEnumVARIANT</c>,
    /// whose items are VARIANTs holding an <c>IDispatch</c> for each rule.
    /// </summary>
    private static unsafe IEnumerable<INetFwRule> Rules(INetFwRules rules)
    {
        var unknown = rules.get__NewEnum();
        IEnumVARIANT items;
        try
        {
            items = (IEnumVARIANT)Wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.None);
        }
        finally
        {
            Marshal.Release(unknown);
        }

        const int Batch = 64;
        var found = new List<INetFwRule>();
        var buffer = new Variant[Batch];

        while (true)
        {
            uint fetched;
            int hr;
            fixed (Variant* first = buffer)
            {
                hr = items.Next(Batch, (nint)first, out fetched);
                try
                {
                    for (int i = 0; i < fetched; i++)
                    {
                        if (first[i].Type == VtDispatch && first[i].Value != 0)
                            found.Add((INetFwRule)Wrappers.GetOrCreateObjectForComInstance(
                                first[i].Value, CreateObjectFlags.None));
                    }
                }
                finally
                {
                    // Each VARIANT holds a reference of its own; the wrappers above took theirs.
                    for (int i = 0; i < fetched; i++)
                        VariantClear((nint)(first + i));
                }
            }

            Marshal.ThrowExceptionForHR(hr);
            if (hr != 0 || fetched < Batch)
                break;
        }

        return found;
    }

    private static readonly StrategyBasedComWrappers Wrappers = new();

    private static readonly Guid FwPolicy2Class = new("E2B3C97F-6AE1-41AC-817A-F6F92166D7DD");
    private static readonly Guid FwRuleClass = new("2C5BC43E-3369-4C33-AB0C-BE9469677AF4");

    private const uint ClsctxInprocServer = 0x1;
    private const uint CoinitMultithreaded = 0x0;
    private const ushort VtDispatch = 9;
    private const short VariantTrue = -1, VariantFalse = 0;
    private const int DirectionIn = 1;
    private const int ActionAllow = 1;
    private const int ModifyStateOk = 0;

    /// <summary>A VARIANT: a type tag, three reserved words, then a union two pointers wide. 24 bytes on
    /// 64-bit Windows, 16 on 32-bit.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Variant
    {
        public ushort Type;
        private ushort _reserved1, _reserved2, _reserved3;
        public nint Value;
        private nint _rest;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coinit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint pointer);

    [LibraryImport("oleaut32.dll")]
    private static partial int VariantClear(nint variant);

    // ── the interfaces, in the order netfw.idl declares them ──
    //
    // Each derives from IDispatch. The four IDispatch methods are declared and never called, so the
    // ones after them land in the right slots. Setters that are never used are declared for the same
    // reason. Booleans are VARIANT_BOOL: -1 for true, 0 for false.

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
    [Guid("98325047-C671-4174-8D81-DEFCD3F03186")]
    internal partial interface INetFwPolicy2
    {
        void GetTypeInfoCount();
        void GetTypeInfo();
        void GetIDsOfNames();
        void Invoke();

        int get_CurrentProfileTypes();
        short get_FirewallEnabled(int profile);
        void put_FirewallEnabled(int profile, short value);
        void get_ExcludedInterfaces();
        void put_ExcludedInterfaces();
        short get_BlockAllInboundTraffic(int profile);
        void put_BlockAllInboundTraffic(int profile, short value);
        short get_NotificationsDisabled(int profile);
        void put_NotificationsDisabled(int profile, short value);
        short get_UnicastResponsesToMulticastBroadcastDisabled(int profile);
        void put_UnicastResponsesToMulticastBroadcastDisabled(int profile, short value);
        INetFwRules get_Rules();
        void get_ServiceRestriction();
        void EnableRuleGroup();
        void IsRuleGroupEnabled();
        void RestoreLocalFirewallDefaults();
        int get_DefaultInboundAction(int profile);
        void put_DefaultInboundAction(int profile, int action);
        int get_DefaultOutboundAction(int profile);
        void put_DefaultOutboundAction(int profile, int action);
        void get_IsRuleGroupCurrentlyEnabled();
        int get_LocalPolicyModifyState();
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
    [Guid("9C4C6277-5027-441E-AFAE-CA1F542DA009")]
    internal partial interface INetFwRules
    {
        void GetTypeInfoCount();
        void GetTypeInfo();
        void GetIDsOfNames();
        void Invoke();

        int get_Count();
        void Add(INetFwRule rule);
        void Remove(string name);
        INetFwRule Item(string name);
        nint get__NewEnum();
    }

    [GeneratedComInterface(StringMarshalling = StringMarshalling.Custom, StringMarshallingCustomType = typeof(BStrStringMarshaller))]
    [Guid("AF230D27-BABA-4E42-ACED-F524F22CFCE2")]
    internal partial interface INetFwRule
    {
        void GetTypeInfoCount();
        void GetTypeInfo();
        void GetIDsOfNames();
        void Invoke();

        string? get_Name();
        void put_Name(string value);
        string? get_Description();
        void put_Description(string value);
        string? get_ApplicationName();
        void put_ApplicationName(string value);
        string? get_ServiceName();
        void put_ServiceName(string value);
        int get_Protocol();
        void put_Protocol(int value);
        string? get_LocalPorts();
        void put_LocalPorts(string value);
        string? get_RemotePorts();
        void put_RemotePorts(string value);
        string? get_LocalAddresses();
        void put_LocalAddresses(string value);
        string? get_RemoteAddresses();
        void put_RemoteAddresses(string value);
        string? get_IcmpTypesAndCodes();
        void put_IcmpTypesAndCodes(string value);
        int get_Direction();
        void put_Direction(int value);
        void get_Interfaces();
        void put_Interfaces();
        string? get_InterfaceTypes();
        void put_InterfaceTypes(string value);
        short get_Enabled();
        void put_Enabled(short value);
        string? get_Grouping();
        void put_Grouping(string value);
        int get_Profiles();
        void put_Profiles(int value);
        short get_EdgeTraversal();
        void put_EdgeTraversal(short value);
        int get_Action();
        void put_Action(int value);
    }

    [GeneratedComInterface]
    [Guid("00020404-0000-0000-C000-000000000046")]
    internal partial interface IEnumVARIANT
    {
        [PreserveSig]
        int Next(uint count, nint items, out uint fetched);

        [PreserveSig]
        int Skip(uint count);

        [PreserveSig]
        int Reset();

        nint Clone();
    }
}
