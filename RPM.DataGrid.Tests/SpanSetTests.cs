using RPM.DataGrid.Model;

namespace RPM.DataGrid.Tests;

public class SpanSetTests
{
    private static SpanSet Of(params (int First, int Last)[] spans)
    {
        var set = new SpanSet();
        foreach (var (a, b) in spans)
            set.Add(a, b);
        return set;
    }

    private static string Shape(SpanSet set) => set.ToString();

    [Fact]
    public void An_empty_set_holds_nothing()
    {
        var set = new SpanSet();

        Assert.True(set.IsEmpty);
        Assert.Equal(0, set.Count);
        Assert.Equal(-1, set.Min);
        Assert.Equal(-1, set.Max);
        Assert.False(set.Contains(0));
    }

    [Fact]
    public void One_run_holds_its_indices_and_nothing_either_side()
    {
        var set = Of((5, 9));

        Assert.False(set.Contains(4));
        Assert.True(set.Contains(5));
        Assert.True(set.Contains(9));
        Assert.False(set.Contains(10));
        Assert.Equal(5, set.Count);
        Assert.Equal(5, set.Min);
        Assert.Equal(9, set.Max);
    }

    [Fact]
    public void The_arguments_can_arrive_in_either_order()
    {
        Assert.Equal(Shape(Of((3, 7))), Shape(Of((7, 3))));
    }

    [Fact]
    public void A_negative_first_is_clamped_to_zero()
    {
        var set = Of((-5, 3));

        Assert.Equal("[0..3]", Shape(set));
    }

    [Fact]
    public void Runs_that_touch_become_one_run()
    {
        Assert.Equal("[0..9]", Shape(Of((0, 4), (5, 9))));
        Assert.Equal("[0..9]", Shape(Of((5, 9), (0, 4))));
    }

    [Fact]
    public void Runs_that_overlap_become_one_run()
    {
        Assert.Equal("[0..9]", Shape(Of((0, 6), (4, 9))));
    }

    [Fact]
    public void Runs_with_a_gap_stay_apart()
    {
        var set = Of((0, 4), (6, 9));

        Assert.Equal("[0..4] [6..9]", Shape(set));
        Assert.False(set.Contains(5));
        Assert.Equal(9, set.Count);
    }

    [Fact]
    public void A_run_that_bridges_a_gap_absorbs_both_sides()
    {
        Assert.Equal("[0..20]", Shape(Of((0, 4), (10, 14), (18, 20), (3, 19))));
    }

    [Fact]
    public void Adding_inside_an_existing_run_changes_nothing()
    {
        Assert.Equal("[0..9]", Shape(Of((0, 9), (3, 5))));
    }

    [Fact]
    public void Removing_the_middle_of_a_run_splits_it()
    {
        var set = Of((0, 9));
        set.Remove(4, 5);

        Assert.Equal("[0..3] [6..9]", Shape(set));
        Assert.Equal(8, set.Count);
    }

    [Fact]
    public void Removing_the_front_of_a_run_trims_it()
    {
        var set = Of((0, 9));
        set.Remove(0, 3);

        Assert.Equal("[4..9]", Shape(set));
    }

    [Fact]
    public void Removing_the_back_of_a_run_trims_it()
    {
        var set = Of((0, 9));
        set.Remove(6, 100);

        Assert.Equal("[0..5]", Shape(set));
    }

    [Fact]
    public void Removing_a_whole_run_takes_it_out()
    {
        var set = Of((0, 4), (10, 14));
        set.Remove(0, 4);

        Assert.Equal("[10..14]", Shape(set));
    }

    [Fact]
    public void Removing_across_several_runs_takes_the_parts_it_covers()
    {
        var set = Of((0, 4), (10, 14), (20, 24));
        set.Remove(3, 21);

        Assert.Equal("[0..2] [22..24]", Shape(set));
    }

    [Fact]
    public void Removing_what_is_not_there_changes_nothing()
    {
        var set = Of((10, 14));
        set.Remove(0, 5);
        set.Remove(20, 30);

        Assert.Equal("[10..14]", Shape(set));
    }

    [Fact]
    public void Removing_from_an_empty_set_is_safe()
    {
        var set = new SpanSet();
        set.Remove(0, 100);

        Assert.True(set.IsEmpty);
    }

    [Fact]
    public void Toggle_adds_what_is_absent_and_removes_what_is_present()
    {
        var set = new SpanSet();

        set.Toggle(5, 5);
        Assert.True(set.Contains(5));

        set.Toggle(5, 5);
        Assert.False(set.Contains(5));
    }

    /// <summary>Ctrl-clicking one row out of the middle of a selected run.</summary>
    [Fact]
    public void Toggling_one_row_out_of_a_run_splits_it()
    {
        var set = Of((0, 9));
        set.Toggle(4, 4);

        Assert.Equal("[0..3] [5..9]", Shape(set));
    }

    [Fact]
    public void Set_replaces_everything()
    {
        var set = Of((0, 4), (10, 14));
        set.Set(100, 200);

        Assert.Equal("[100..200]", Shape(set));
    }

    [Fact]
    public void Clamping_drops_what_is_past_the_end_of_the_list()
    {
        var set = Of((0, 4), (10, 14), (20, 24));
        set.ClampTo(12);

        Assert.Equal("[0..4] [10..11]", Shape(set));
    }

    [Fact]
    public void Clamping_to_nothing_empties_it()
    {
        var set = Of((0, 4));
        set.ClampTo(0);

        Assert.True(set.IsEmpty);
    }

    [Fact]
    public void Clamping_to_more_than_it_holds_changes_nothing()
    {
        var set = Of((0, 4));
        set.ClampTo(100);

        Assert.Equal("[0..4]", Shape(set));
    }

    [Fact]
    public void Copying_and_comparing()
    {
        var a = Of((0, 4), (10, 14));
        var b = new SpanSet();

        Assert.False(a.SameAs(b));

        b.CopyFrom(a);
        Assert.True(a.SameAs(b));

        b.Add(20, 20);
        Assert.False(a.SameAs(b));
    }

    [Fact]
    public void Copying_from_itself_is_safe()
    {
        var a = Of((0, 4));
        a.CopyFrom(a);

        Assert.Equal("[0..4]", Shape(a));
    }

    [Fact]
    public void Indices_come_out_in_order()
    {
        Assert.Equal([0, 1, 2, 10, 11], Of((0, 2), (10, 11)).Indices());
    }

    /// <summary>The point of the whole class: a million rows selected is one run, and asking about
    /// any of them is a binary search over that one run.</summary>
    [Fact]
    public void A_million_rows_is_one_run()
    {
        var set = Of((0, 999_999));

        Assert.Single(set.Spans);
        Assert.Equal(1_000_000, set.Count);
        Assert.True(set.Contains(500_000));

        set.Toggle(500_000, 500_000);

        Assert.Equal(2, set.Spans.Count);
        Assert.Equal(999_999, set.Count);
        Assert.False(set.Contains(500_000));
    }

    /// <summary>
    /// Runs are always sorted, disjoint and non-touching however they were built. Checked against a
    /// plain set of the same operations, because the invariants are easy to state and easy to break.
    /// </summary>
    [Fact]
    public void The_runs_stay_sorted_disjoint_and_apart_under_random_edits()
    {
        var rand = new Random(7);
        var set = new SpanSet();
        var plain = new HashSet<int>();

        for (var step = 0; step < 2000; step++)
        {
            var a = rand.Next(0, 60);
            var b = rand.Next(0, 60);
            var (lo, hi) = a <= b ? (a, b) : (b, a);

            switch (rand.Next(3))
            {
                case 0:
                    set.Add(lo, hi);
                    for (var i = lo; i <= hi; i++) plain.Add(i);
                    break;
                case 1:
                    set.Remove(lo, hi);
                    for (var i = lo; i <= hi; i++) plain.Remove(i);
                    break;
                default:
                    var had = plain.Contains(lo);
                    set.Toggle(lo, hi);
                    for (var i = lo; i <= hi; i++)
                        if (had) plain.Remove(i); else plain.Add(i);
                    break;
            }

            for (var i = 1; i < set.Spans.Count; i++)
            {
                Assert.True(set.Spans[i - 1].Last + 1 < set.Spans[i].First,
                    $"step {step}: runs {set.Spans[i - 1]} and {set.Spans[i]} touch or overlap");
            }

            foreach (var s in set.Spans)
                Assert.True(s.First <= s.Last, $"step {step}: {s} runs backwards");

            Assert.Equal(plain.Count, set.Count);
            Assert.Equal(plain.OrderBy(x => x), set.Indices());
        }
    }
}
