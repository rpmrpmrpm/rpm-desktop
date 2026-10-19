using RPM.Core.Diagnostics;

namespace RPM.Desktop.Strings;

/// <summary>A <see cref="Fault"/> in the user's language.</summary>
internal static class FaultText
{
    /// <summary>
    /// The fault's sentence. <see cref="Fault.None"/> has no sentence of its own and reads as the
    /// generic one: by the time a fault is being shown, something did go wrong.
    /// </summary>
    public static string Say(this Fault fault) => Loc.Get(fault.StringKey() ?? "Fault_Unknown");

    /// <summary>
    /// <paramref name="key"/> formatted with <paramref name="args"/>, where each <see cref="Fault"/>
    /// among them becomes its own sentence. For the closed trouble types (<c>CloudTrouble</c>,
    /// <c>SharingTrouble</c>), whose arguments are names, counts and faults.
    /// </summary>
    public static string Format(string key, IEnumerable<object> args) =>
        Loc.Format(key, args.Select(a => a is Fault fault ? fault.Say() : a).ToArray());
}
