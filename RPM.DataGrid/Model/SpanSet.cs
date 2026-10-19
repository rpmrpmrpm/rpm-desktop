namespace RPM.DataGrid.Model;

/// <summary>An inclusive run of row indices.</summary>
public readonly record struct Span(int First, int Last)
{
    /// <summary>How many rows it covers.</summary>
    public int Count => Last - First + 1;

    public bool Contains(int i) => i >= First && i <= Last;

    public override string ToString() => First == Last ? $"[{First}]" : $"[{First}..{Last}]";
}

/// <summary>
/// A set of row indices held as runs rather than as members.
///
/// <para>This makes selecting a million rows almost free. A <c>HashSet</c> of a million objects
/// would defeat the virtualization, and would need a separate "everything is selected, except
/// these" mode. Here one run covers select-all, a run split by a ctrl-click covers the exclusions,
/// and select-all and a shift-click across five hundred thousand rows are the same
/// operation.</para>
///
/// <para>Runs are kept sorted, disjoint and non-touching: adding [0..4] then [5..9] leaves one run,
/// not two. So the same set always has exactly one representation.</para>
/// </summary>
public sealed class SpanSet
{
    private readonly List<Span> _spans = [];

    /// <summary>The runs, in order, disjoint and non-touching.</summary>
    public IReadOnlyList<Span> Spans => _spans;

    /// <summary>How many indices are in the set.</summary>
    public int Count
    {
        get
        {
            var n = 0;
            foreach (var s in _spans)
                n += s.Count;
            return n;
        }
    }

    /// <summary>Whether the set holds nothing at all.</summary>
    public bool IsEmpty => _spans.Count == 0;

    /// <summary>The lowest index in the set, or -1 when it is empty.</summary>
    public int Min => _spans.Count == 0 ? -1 : _spans[0].First;

    /// <summary>The highest index in the set, or -1 when it is empty.</summary>
    public int Max => _spans.Count == 0 ? -1 : _spans[^1].Last;

    public bool Contains(int index)
    {
        var i = IndexOfSpanAtOrBefore(index);
        return i >= 0 && _spans[i].Contains(index);
    }

    public void Clear() => _spans.Clear();

    /// <summary>Every index in the set, lowest first. After a select-all this yields a million
    /// numbers, so use it only when that is intended; <see cref="Spans"/> is usually enough.</summary>
    public IEnumerable<int> Indices()
    {
        foreach (var s in _spans)
            for (var i = s.First; i <= s.Last; i++)
                yield return i;
    }

    /// <summary>Puts [first..last] in the set. Order of the two arguments does not matter.</summary>
    public void Add(int first, int last)
    {
        Order(ref first, ref last);
        if (last < first)
            return;

        // Every run that touches or overlaps the new run is merged into it. "Touches", not only
        // "overlaps": [0..4] next to [5..9] is one run of ten. Leaving two runs would give the same
        // set two representations.
        var from = IndexOfSpanAtOrBefore(first);
        if (from < 0)
            from = 0;
        else if (_spans[from].Last < first - 1)
            from++;

        var to = from;
        while (to < _spans.Count && _spans[to].First <= last + 1)
        {
            first = Math.Min(first, _spans[to].First);
            last = Math.Max(last, _spans[to].Last);
            to++;
        }

        _spans.RemoveRange(from, to - from);
        _spans.Insert(from, new Span(first, last));
    }

    /// <summary>Takes [first..last] out of the set, splitting a run that straddles it.</summary>
    public void Remove(int first, int last)
    {
        Order(ref first, ref last);
        if (last < first || _spans.Count == 0)
            return;

        var i = IndexOfSpanAtOrBefore(first);
        if (i < 0)
            i = 0;

        while (i < _spans.Count && _spans[i].First <= last)
        {
            var s = _spans[i];

            if (s.Last < first)
            {
                i++;
                continue;
            }

            var keepLeft = s.First < first;
            var keepRight = s.Last > last;

            if (keepLeft && keepRight)
            {
                _spans[i] = new Span(s.First, first - 1);
                _spans.Insert(i + 1, new Span(last + 1, s.Last));
                return;                                  // the cut was wholly inside this run
            }

            if (keepLeft)
            {
                _spans[i] = new Span(s.First, first - 1);
                i++;
            }
            else if (keepRight)
            {
                _spans[i] = new Span(last + 1, s.Last);
                return;                                  // nothing after this run can overlap
            }
            else
            {
                _spans.RemoveAt(i);
            }
        }
    }

    /// <summary>Adds the run if its first index is not in the set, removes it if it is. What a
    /// ctrl-click does.</summary>
    public void Toggle(int first, int last)
    {
        Order(ref first, ref last);
        if (Contains(first))
            Remove(first, last);
        else
            Add(first, last);
    }

    /// <summary>Replaces the whole set with one run.</summary>
    public void Set(int first, int last)
    {
        _spans.Clear();
        Add(first, last);
    }

    /// <summary>Replaces the whole set with a copy of another.</summary>
    public void CopyFrom(SpanSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(this, other))
            return;

        _spans.Clear();
        _spans.AddRange(other._spans);
    }

    /// <summary>
    /// The runs this set holds that <paramref name="other"/> does not, in order.
    ///
    /// <para>For a host that acts on a selection CHANGE rather than on the current selection: it
    /// gives the rows just added, and (called the other way round) the rows just removed. It walks
    /// both sets together, so the cost depends on the number of runs, not the number of
    /// rows.</para>
    /// </summary>
    public void Difference(SpanSet other, List<Span> into)
    {
        ArgumentNullException.ThrowIfNull(other);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        var j = 0;
        foreach (var mine in _spans)
        {
            var from = mine.First;

            // Skip every run of theirs that ends before this one starts. Both lists are sorted, so
            // the cursor only ever moves forward across the whole walk.
            while (j < other._spans.Count && other._spans[j].Last < from)
                j++;

            var k = j;
            while (k < other._spans.Count && other._spans[k].First <= mine.Last)
            {
                var theirs = other._spans[k];

                if (theirs.First > from)
                    into.Add(new Span(from, theirs.First - 1));

                from = Math.Max(from, theirs.Last + 1);
                if (from > mine.Last)
                    break;

                k++;
            }

            if (from <= mine.Last)
                into.Add(new Span(from, mine.Last));
        }
    }

    /// <summary>Whether two sets hold the same indices.</summary>
    public bool SameAs(SpanSet other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (_spans.Count != other._spans.Count)
            return false;

        for (var i = 0; i < _spans.Count; i++)
            if (_spans[i] != other._spans[i])
                return false;

        return true;
    }

    /// <summary>Drops everything at or past <paramref name="count"/>. Used when the list gets
    /// shorter, for a selection held by index.</summary>
    public void ClampTo(int count)
    {
        if (count <= 0)
        {
            _spans.Clear();
            return;
        }

        while (_spans.Count > 0 && _spans[^1].First >= count)
            _spans.RemoveAt(_spans.Count - 1);

        if (_spans.Count > 0 && _spans[^1].Last >= count)
            _spans[^1] = new Span(_spans[^1].First, count - 1);
    }

    private static void Order(ref int a, ref int b)
    {
        if (a > b)
            (a, b) = (b, a);
        if (a < 0)
            a = 0;
    }

    /// <summary>The index of the last run starting at or before <paramref name="index"/>, or -1.</summary>
    private int IndexOfSpanAtOrBefore(int index)
    {
        var lo = 0;
        var hi = _spans.Count - 1;
        var found = -1;

        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_spans[mid].First <= index)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return found;
    }

    public override string ToString() => _spans.Count == 0 ? "{}" : string.Join(" ", _spans);
}
