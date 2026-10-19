using RPM.Core.Library;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Main.RightPane;

/// <summary>The sentence under one generated list that is not there yet.
///
/// <para><b>There are three reasons a list can be locked, and each needs a different action.</b> <see
/// cref="GateNeed"/> says which one is holding a list up. This class turns that into the line that the user
/// reads. The three lines are not interchangeable. "Add more music" under a list that the measuring pass
/// has not reached yet would send somebody to import records that change nothing.</para>
///
/// <para>The key is returned as well as the words, so a test can check it against
/// <c>scripts/strings.json</c>. A key that is missing from that file is shown on screen as its own
/// name.</para>
/// </summary>
internal static class LockedListLine
{
    /// <summary>Which line this gate reads.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="GateNeed.Albums"/>. That is the home
    /// screen's gate, not a list's. A generated list is short of songs, either by listening or by
    /// measuring. It is never short of albums. Albums are deliberately not folded into the songs line,
    /// because that would count albums and print the number after the word "songs".</exception>
    public static string Key(FeatureGate gate) => gate.Need switch
    {
        GateNeed.Measured => "ListLock_Measuring",
        GateNeed.Plays => gate.ToGo == 1 ? "ListLock_PlaysOne" : "ListLock_Plays",
        GateNeed.SongsInList => gate.ToGo == 1 ? "ListLock_SongsOne" : "ListLock_Songs",
        _ => throw new ArgumentOutOfRangeException(nameof(gate), gate.Need, null),
    };

    /// <summary>The line itself.
    /// </summary>
    /// <param name="gate">What the list is waiting for, and how far along it is.</param>
    /// <param name="measured">How far the measuring pass has got. Only the measuring line reads it. The
    /// caller passes it in rather than this method fetching it. It is one reading that every line on the
    /// card shares. Three lines that read it at three different moments could disagree with each other on
    /// screen.</param>
    public static string Text(FeatureGate gate, MeasuringProgress measured) => gate.Need switch
    {
        GateNeed.Measured => Loc.Format(Key(gate), measured.Measured, measured.Total),
        GateNeed.Plays => gate.ToGo == 1 ? Loc.Get(Key(gate)) : Loc.Format(Key(gate), gate.ToGo),
        _ => gate.ToGo == 1
            ? Loc.Format(Key(gate), gate.Have, gate.Want)
            : Loc.Format(Key(gate), gate.Have, gate.Want, gate.ToGo),
    };
}
