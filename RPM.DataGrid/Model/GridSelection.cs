namespace RPM.DataGrid.Model;

/// <summary>The keys held down when a gesture happened.</summary>
[Flags]
public enum GridModifiers
{
    None = 0,
    Control = 1,
    Shift = 2,
}

/// <summary>
/// Which rows are selected, and what a click, a drag or a key does to that.
///
/// <para>Held by index, not by item. A click produces an index and a range is a run of indices. A
/// set of item objects would need a second, separate mode for "everything is selected". When a
/// selection has to survive the list being rebuilt, the state is stored on the items and the host
/// puts it back with <see cref="Set"/>. The queue's ticks work this way: they are stored on the
/// nodes, not in the grid.</para>
///
/// <para>Every method returns whether anything actually changed, so a drag that stays inside one row
/// does not repaint.</para>
/// </summary>
public sealed class GridSelection
{
    private readonly SpanSet _selected = new();
    private readonly SpanSet _before = new();

    /// <summary>What was selected when the sweep began, for a drag that adds to it.</summary>
    private readonly SpanSet _sweepBase = new();

    private int _sweepFrom = -1;
    private bool _sweepAdds;

    /// <summary>The selected rows, as runs.</summary>
    public IReadOnlyList<Span> Spans => _selected.Spans;

    /// <summary>How many rows are selected.</summary>
    public int Count => _selected.Count;

    public bool IsEmpty => _selected.IsEmpty;

    /// <summary>The row a shift-click or a shift-arrow measures from. -1 when there is none.</summary>
    public int Anchor { get; private set; } = -1;

    /// <summary>The row the keyboard is on. -1 when there is none.</summary>
    public int Focus { get; private set; } = -1;

    /// <summary>Whether a drag is selecting right now.</summary>
    public bool IsSweeping => _sweepFrom >= 0;

    /// <summary>
    /// What was held down for the change being reported right now.
    ///
    /// <para>For a host that treats changes differently by modifier. In the import queue a plain click
    /// means "these rows and no others" for the WHOLE queue, while ctrl or shift affect only the rows
    /// they touched. Read it from a handler of <see cref="SelectionChanged"/>; at any other time its
    /// value has no meaning.</para>
    /// </summary>
    public GridModifiers LastModifiers { get; private set; }

    /// <summary>Anything changed: the set, the anchor or the focus. The painter listens to this.</summary>
    public event Action? Changed;

    /// <summary>The set of selected rows changed. The host listens to this.</summary>
    public event Action? SelectionChanged;

    /// <summary>
    /// A sweep finished. Raised once, and only for a sweep that actually ran.
    ///
    /// <para>For a host whose work on a selection change is expensive. Such a host reads
    /// <see cref="IsSweeping"/> to delay that work while the pointer is down, and this event tells it
    /// the set is final so it can do the work once. Both are needed: the flag can be read at any
    /// time, and this event says when to read again.</para>
    /// </summary>
    public event Action? SweepEnded;

    public bool Contains(int row) => _selected.Contains(row);

    /// <summary>Every selected row index, lowest first. After a select-all over a million rows this
    /// yields a million numbers, so use it only when that is intended; <see cref="Spans"/> is usually
    /// enough.</summary>
    public IEnumerable<int> Rows() => _selected.Indices();

    /// <summary>
    /// A click on a row, with whatever was held down.
    ///
    /// <para>Plain replaces. Ctrl toggles that row and leaves the rest. Shift replaces with the run
    /// from the anchor. Ctrl and shift together add that run to what is there. A shift with no
    /// anchor to measure from is a plain click.</para>
    /// </summary>
    public bool Click(int row, GridModifiers modifiers, int count)
    {
        if (count <= 0 || row < 0 || row >= count)
            return false;

        Snapshot();
        LastModifiers = modifiers;

        var shift = (modifiers & GridModifiers.Shift) != 0 && Anchor >= 0 && Anchor < count;
        var control = (modifiers & GridModifiers.Control) != 0;

        if (shift)
        {
            if (!control)
                _selected.Clear();
            _selected.Add(Anchor, row);
        }
        else if (control)
        {
            _selected.Toggle(row, row);
            Anchor = row;
        }
        else
        {
            _selected.Set(row, row);
            Anchor = row;
        }

        Focus = row;
        return Settle();
    }

    /// <summary>
    /// A pointer press that may become a drag across rows. The press itself selects, the same as a
    /// click; <see cref="SweepTo"/> then extends it.
    /// </summary>
    public bool BeginSweep(int row, GridModifiers modifiers, int count)
    {
        if (count <= 0 || row < 0 || row >= count)
            return false;

        var moved = Click(row, modifiers, count);

        _sweepFrom = row;
        _sweepAdds = (modifiers & GridModifiers.Control) != 0;

        // The base is what the selection was AFTER the press, so a ctrl-drag that starts by toggling
        // a row off does not turn it back on at the first move.
        _sweepBase.CopyFrom(_selected);

        // A ctrl-drag adds, so the row it started on must not be removed again by the first
        // SweepTo: take it out of the base and let the run put it back.
        if (_sweepAdds)
            _sweepBase.Remove(row, row);

        return moved;
    }

    /// <summary>The pointer has reached this row while a sweep is running.</summary>
    public bool SweepTo(int row, int count)
    {
        if (_sweepFrom < 0 || count <= 0)
            return false;

        row = Math.Clamp(row, 0, count - 1);

        Snapshot();

        // A sweep is still the gesture its press began: a plain drag means "these rows and no
        // others" the same way a plain click does.
        LastModifiers = _sweepAdds ? GridModifiers.Control : GridModifiers.None;

        _selected.CopyFrom(_sweepBase);
        _selected.Add(_sweepFrom, row);
        Focus = row;

        return Settle();
    }

    /// <summary>The pointer came up. The selection stays where the sweep left it.</summary>
    public void EndSweep()
    {
        // Called on every release, including ones with no sweep. The event is raised only for a sweep
        // that ran, so a handler can treat it as "the drag is over", not "a button was released".
        var swept = _sweepFrom >= 0;

        _sweepFrom = -1;
        _sweepAdds = false;
        _sweepBase.Clear();

        if (swept)
            SweepEnded?.Invoke();
    }

    /// <summary>
    /// The keyboard moved to a row.
    ///
    /// <para>Plain replaces and moves the anchor with it. Shift extends from the anchor. Ctrl on its
    /// own moves the focus and leaves the selection alone, so the user can move to a row and
    /// ctrl-space it without losing the current selection.</para>
    /// </summary>
    public bool KeyTo(int row, GridModifiers modifiers, int count)
    {
        if (count <= 0)
            return false;

        row = Math.Clamp(row, 0, count - 1);

        Snapshot();
        LastModifiers = modifiers;

        var shift = (modifiers & GridModifiers.Shift) != 0;
        var control = (modifiers & GridModifiers.Control) != 0;

        if (shift)
        {
            if (Anchor < 0 || Anchor >= count)
                Anchor = row;
            _selected.Set(Anchor, row);
        }
        else if (!control)
        {
            _selected.Set(row, row);
            Anchor = row;
        }

        Focus = row;
        return Settle();
    }

    /// <summary>Toggles the focused row and leaves everything else. Ctrl-space.</summary>
    public bool ToggleFocus(int count)
    {
        if (count <= 0 || Focus < 0 || Focus >= count)
            return false;

        Snapshot();
        _selected.Toggle(Focus, Focus);
        Anchor = Focus;
        return Settle();
    }

    /// <summary>Selects every row. Stored as one run, however long the list is.</summary>
    public bool SelectAll(int count)
    {
        if (count <= 0)
            return Clear();

        Snapshot();
        _selected.Set(0, count - 1);
        if (Anchor < 0)
            Anchor = 0;
        if (Focus < 0)
            Focus = 0;
        return Settle();
    }

    /// <summary>Selects nothing. The anchor and the focus stay where they are.</summary>
    public bool Clear()
    {
        if (_selected.IsEmpty)
            return false;

        Snapshot();
        _selected.Clear();
        return Settle();
    }

    /// <summary>
    /// Replaces the selection with the runs given, and puts the anchor and focus where the caller
    /// says. The host calls this after rebuilding the list, using state stored on the rows.
    /// </summary>
    public bool Set(IEnumerable<Span> spans, int count, int anchor = -1, int focus = -1)
    {
        ArgumentNullException.ThrowIfNull(spans);

        Snapshot();

        _selected.Clear();
        foreach (var s in spans)
            _selected.Add(s.First, s.Last);
        _selected.ClampTo(count);

        Anchor = anchor >= 0 && anchor < count ? anchor : -1;
        Focus = focus >= 0 && focus < count ? focus : -1;

        return Settle();
    }

    /// <summary>
    /// The list changed length. Anything past the end goes, and the anchor and focus come back
    /// inside it.
    ///
    /// <para>This is deliberately NOT a re-mapping. When the order changes (a different sort, a new
    /// filter), row 40 is a different record, and keeping the selection on index 40 would act on a
    /// track nobody chose. The control clears the selection on a reset and keeps it on a resize, and
    /// the source says which of the two happened.</para>
    /// </summary>
    public bool Resize(int count)
    {
        Snapshot();

        _selected.ClampTo(count);
        if (Anchor >= count)
            Anchor = count > 0 ? count - 1 : -1;
        if (Focus >= count)
            Focus = count > 0 ? count - 1 : -1;

        return Settle();
    }

    private int _wasAnchor = -1;
    private int _wasFocus = -1;

    private void Snapshot()
    {
        _before.CopyFrom(_selected);
        _wasAnchor = Anchor;
        _wasFocus = Focus;
    }

    /// <summary>Raises what actually changed, and says whether anything did.</summary>
    private bool Settle()
    {
        var sameSet = _selected.SameAs(_before);
        var moved = !sameSet || _wasAnchor != Anchor || _wasFocus != Focus;

        if (!moved)
            return false;

        if (!sameSet)
            SelectionChanged?.Invoke();

        Changed?.Invoke();
        return true;
    }
}
