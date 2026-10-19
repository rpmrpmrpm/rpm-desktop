using System.Diagnostics;

using Microsoft.UI.Xaml;

namespace RPM.Desktop.Presentation.Main.Home;

/// <summary>
/// What a live pane resize costs this screen, and which of its fittings are held while one runs.
///
/// <para>Home sits beside the left pane, not behind it (see <c>PaneStage</c>). So opening or closing
/// the pane while home is shown is a LIVE RESIZE: real column widths are written up to 30 times a
/// second, and home is measured and arranged against each of them. The album wall holds its
/// passengers for the length of the move (<c>AlbumWallView.BeginLiveResize</c>). Home does the same
/// with its fittings, which this file controls.</para>
///
/// <para><b>The fittings themselves are not the cost, which is why the switch below exists.</b> All
/// six together take 12 to 15 ms of a 380 ms move. What is expensive is the layout pass that each one
/// CAUSES: a width written onto a row's metrics re-sizes every tile in it, and the framework then
/// re-measures those subtrees. The wall had the same problem, and holding the card SIZE fixed it,
/// because then nothing is dirty and no subtree is re-measured. <c>RPM_HOME_FREEZE</c> chooses which
/// of home's fittings to hold, so the choice can be made by measuring.</para>
/// </summary>
public sealed partial class HomeView
{
    /// <summary>The surfaces that re-fit from a width, in the order they are reported. The name is also
    /// what <c>RPM_HOME_FREEZE</c> takes, comma-separated, plus <c>all</c> and <c>none</c>.</summary>
    private static readonly string[] LiveParts =
        ["rows", "rowhems", "lists", "listhems", "quick", "quickhems", "banner"];

    private const int PartRows = 0;
    private const int PartRowHems = 1;
    private const int PartLists = 2;
    private const int PartListHems = 3;
    private const int PartQuick = 4;
    private const int PartQuickHems = 5;
    private const int PartBanner = 6;

    /// <summary>
    /// Which fittings are held for the length of a move, read once from <c>RPM_HOME_FREEZE</c>.
    ///
    /// <para>The default is what ships. A held fitting is not skipped: it runs once at the landing, at
    /// the width the screen actually ended at, which is the only width worth fitting to.</para>
    /// </summary>
    private static readonly bool[] Frozen = ReadFreeze();

    private static bool[] ReadFreeze()
    {
        var held = new bool[LiveParts.Length];

        // Nothing is held by default. Holding the two fittings that re-size tiles gives the move more
        // frames (about 8 of 8 instead of 4.5 of 8), but the tiles then keep their old size during the
        // slide and re-fit in one step at the end, so the screen jumps instead of moving. The other
        // pages re-proportion their content as the pane moves, and home does the same. The switch
        // stays so the cost of each fitting can be measured again.
        var asked = RPM.Core.Diagnostics.DevSwitch.Read("RPM_HOME_FREEZE") ?? "none";

        foreach (var name in asked.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (name.Equals("none", StringComparison.OrdinalIgnoreCase))
                return new bool[LiveParts.Length];

            if (name.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                Array.Fill(held, true);
                return held;
            }

            var at = Array.FindIndex(LiveParts, p => p.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
                held[at] = true;
        }

        return held;
    }

    private readonly double[] _liveMs = new double[LiveParts.Length];
    private readonly int[] _liveHits = new int[LiveParts.Length];
    private readonly int[] _liveHeld = new int[LiveParts.Length];

    /// <summary>The last held call of each fitting, to be run once at the landing. Keyed by the
    /// element it came from, because there are several rows and each has its own width.</summary>
    private readonly Dictionary<object, Action>[] _liveOwed =
        [.. Enumerable.Range(0, LiveParts.Length).Select(_ => new Dictionary<object, Action>())];

    /// <summary>Whether a pane move is running over this screen right now.</summary>
    private bool _liveResize;

    /// <summary>When the move started, so the log line can say what share of it this screen used.</summary>
    private long _liveFrom;

    /// <summary>A pane move has started over home. Called by the conductor's scene hold, which also
    /// holds the wall's passengers (see <c>PaneConductor.HoldScene</c>).</summary>
    public void BeginLiveResize()
    {
        if (_liveResize)
            return;
        _liveResize = true;
        _liveFrom = Stopwatch.GetTimestamp();


        Array.Clear(_liveMs);
        Array.Clear(_liveHits);
        Array.Clear(_liveHeld);
        foreach (var owed in _liveOwed)
            owed.Clear();
    }

    /// <summary>
    /// The move has landed. Every held fitting runs once, here, at the width the screen ended at, and
    /// what the move cost is logged on the same channel as the move's own line.
    /// </summary>
    /// <remarks>
    /// The held calls are replayed instead of waiting for the landing's own <c>SizeChanged</c>, because
    /// there may not be one: the last tick of a live resize usually writes the final width already,
    /// and a size that does not change raises nothing. Replaying is safe even when it was not needed:
    /// every fitting here writes a size that is already correct, and the framework drops a write that
    /// changes nothing.
    /// </remarks>
    public void EndLiveResize()
    {
        if (!_liveResize)
            return;
        _liveResize = false;

        var took = Stopwatch.GetElapsedTime(_liveFrom).TotalMilliseconds;

        var spent = Stopwatch.GetTimestamp();
        foreach (var owed in _liveOwed)
        {
            foreach (var work in owed.Values)
                work();
            owed.Clear();
        }
        var settle = Stopwatch.GetElapsedTime(spent).TotalMilliseconds;

        var mine = 0d;
        var line = "";
        for (var i = 0; i < LiveParts.Length; i++)
        {
            mine += _liveMs[i];
            if (_liveHits[i] > 0 || _liveHeld[i] > 0)
                line += $", {LiveParts[i]} {_liveMs[i]:F0}ms/{_liveHits[i]}"
                      + (_liveHeld[i] > 0 ? $" held {_liveHeld[i]}" : "");
        }

        RPM.Core.Diagnostics.Scope.Said(
            "panes",
            $"home {took:F0}ms of move, {mine:F0}ms fitting, {settle:F0}ms settling{line}");
    }

    /// <summary>
    /// Runs one of the width-driven fittings, or holds it for the landing if this part is frozen and
    /// a move is running. It is timed while a move is running, and costs nothing otherwise.
    /// </summary>
    /// <param name="key">What the held call belongs to, so each row keeps its own. This is the
    /// fitting's own element, or the view when there is only one.</param>
    private void Fitting(int part, object key, Action work)
    {
        if (!_liveResize)
        {
            work();
            return;
        }

        if (Frozen[part])
        {
            _liveOwed[part][key] = work;   // the last width asked for by this element is the one owed
            _liveHeld[part]++;
            return;
        }

        var from = Stopwatch.GetTimestamp();
        work();
        _liveMs[part] += Stopwatch.GetElapsedTime(from).TotalMilliseconds;
        _liveHits[part]++;
    }
}
