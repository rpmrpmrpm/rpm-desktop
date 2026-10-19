using RPM.DataGrid.Model;

namespace RPM.DataGrid.Tests;

/// <summary>
/// What a click, a drag and a key do to the selection. The rules are the ones a list of files has
/// everywhere, which is also what the two grids have today with SelectionMode="Extended".
/// </summary>
public class GridSelectionTests
{
    private const int Count = 100;

    private static int[] Rows(GridSelection s) => [.. s.Rows()];

    [Fact]
    public void A_click_selects_one_row_and_drops_the_rest()
    {
        var s = new GridSelection();

        s.Click(5, GridModifiers.None, Count);
        s.Click(9, GridModifiers.None, Count);

        Assert.Equal([9], Rows(s));
        Assert.Equal(9, s.Anchor);
        Assert.Equal(9, s.Focus);
    }

    [Fact]
    public void A_click_outside_the_list_does_nothing()
    {
        var s = new GridSelection();

        Assert.False(s.Click(-1, GridModifiers.None, Count));
        Assert.False(s.Click(Count, GridModifiers.None, Count));
        Assert.False(s.Click(0, GridModifiers.None, 0));
        Assert.True(s.IsEmpty);
    }

    [Fact]
    public void Ctrl_click_adds_a_row_without_losing_the_others()
    {
        var s = new GridSelection();

        s.Click(2, GridModifiers.None, Count);
        s.Click(5, GridModifiers.Control, Count);
        s.Click(8, GridModifiers.Control, Count);

        Assert.Equal([2, 5, 8], Rows(s));
    }

    [Fact]
    public void Ctrl_click_on_a_selected_row_takes_it_out()
    {
        var s = new GridSelection();

        s.Click(2, GridModifiers.None, Count);
        s.Click(5, GridModifiers.Control, Count);
        s.Click(2, GridModifiers.Control, Count);

        Assert.Equal([5], Rows(s));
    }

    [Fact]
    public void Shift_click_selects_the_run_from_the_anchor()
    {
        var s = new GridSelection();

        s.Click(3, GridModifiers.None, Count);
        s.Click(6, GridModifiers.Shift, Count);

        Assert.Equal([3, 4, 5, 6], Rows(s));
        Assert.Equal(3, s.Anchor);
        Assert.Equal(6, s.Focus);
    }

    [Fact]
    public void Shift_click_backwards_selects_the_same_run()
    {
        var s = new GridSelection();

        s.Click(6, GridModifiers.None, Count);
        s.Click(3, GridModifiers.Shift, Count);

        Assert.Equal([3, 4, 5, 6], Rows(s));
        Assert.Equal(6, s.Anchor);
    }

    /// <summary>A second shift-click measures from the same anchor, so the run shrinks rather than
    /// growing from wherever the last one ended.</summary>
    [Fact]
    public void A_second_shift_click_still_measures_from_the_anchor()
    {
        var s = new GridSelection();

        s.Click(3, GridModifiers.None, Count);
        s.Click(9, GridModifiers.Shift, Count);
        s.Click(5, GridModifiers.Shift, Count);

        Assert.Equal([3, 4, 5], Rows(s));
    }

    [Fact]
    public void Shift_click_replaces_what_was_selected()
    {
        var s = new GridSelection();

        s.Click(20, GridModifiers.None, Count);
        s.Click(3, GridModifiers.None, Count);
        s.Click(5, GridModifiers.Shift, Count);

        Assert.Equal([3, 4, 5], Rows(s));
    }

    [Fact]
    public void Ctrl_shift_click_adds_the_run_to_what_is_there()
    {
        var s = new GridSelection();

        s.Click(20, GridModifiers.None, Count);
        s.Click(3, GridModifiers.Control, Count);
        s.Click(5, GridModifiers.Control | GridModifiers.Shift, Count);

        Assert.Equal([3, 4, 5, 20], Rows(s));
    }

    [Fact]
    public void Shift_with_no_anchor_is_a_plain_click()
    {
        var s = new GridSelection();

        s.Click(7, GridModifiers.Shift, Count);

        Assert.Equal([7], Rows(s));
        Assert.Equal(7, s.Anchor);
    }

    [Fact]
    public void A_sweep_selects_the_rows_it_passes_over()
    {
        var s = new GridSelection();

        s.BeginSweep(4, GridModifiers.None, Count);
        s.SweepTo(7, Count);

        Assert.Equal([4, 5, 6, 7], Rows(s));
        Assert.True(s.IsSweeping);

        s.EndSweep();
        Assert.False(s.IsSweeping);
        Assert.Equal([4, 5, 6, 7], Rows(s));
    }

    [Fact]
    public void A_sweep_that_comes_back_shrinks_again()
    {
        var s = new GridSelection();

        s.BeginSweep(4, GridModifiers.None, Count);
        s.SweepTo(9, Count);
        s.SweepTo(5, Count);

        Assert.Equal([4, 5], Rows(s));
    }

    [Fact]
    public void A_sweep_upwards_works_the_same()
    {
        var s = new GridSelection();

        s.BeginSweep(9, GridModifiers.None, Count);
        s.SweepTo(6, Count);

        Assert.Equal([6, 7, 8, 9], Rows(s));
    }

    [Fact]
    public void A_sweep_replaces_what_was_selected()
    {
        var s = new GridSelection();

        s.Click(50, GridModifiers.None, Count);
        s.BeginSweep(4, GridModifiers.None, Count);
        s.SweepTo(6, Count);

        Assert.Equal([4, 5, 6], Rows(s));
    }

    [Fact]
    public void A_ctrl_sweep_adds_to_what_was_selected()
    {
        var s = new GridSelection();

        s.Click(50, GridModifiers.None, Count);
        s.BeginSweep(4, GridModifiers.Control, Count);
        s.SweepTo(6, Count);

        Assert.Equal([4, 5, 6, 50], Rows(s));
    }

    /// <summary>
    /// A ctrl-drag beginning on a row that was already selected toggles it off on the press. The
    /// first move must not put it straight back, or the gesture cannot deselect a run.
    /// </summary>
    [Fact]
    public void A_ctrl_sweep_from_a_selected_row_does_not_undo_its_own_press()
    {
        var s = new GridSelection();

        s.Click(4, GridModifiers.None, Count);
        s.BeginSweep(4, GridModifiers.Control, Count);
        Assert.True(s.IsEmpty);

        s.SweepTo(4, Count);
        Assert.Equal([4], Rows(s));
    }

    [Fact]
    public void A_sweep_past_the_end_of_the_list_stops_at_it()
    {
        var s = new GridSelection();

        s.BeginSweep(97, GridModifiers.None, Count);
        s.SweepTo(500, Count);

        Assert.Equal([97, 98, 99], Rows(s));
    }

    [Fact]
    public void Sweeping_without_beginning_does_nothing()
    {
        var s = new GridSelection();

        Assert.False(s.SweepTo(5, Count));
        Assert.True(s.IsEmpty);
    }

    [Fact]
    public void An_arrow_key_moves_the_selection_and_the_anchor()
    {
        var s = new GridSelection();

        s.Click(5, GridModifiers.None, Count);
        s.KeyTo(6, GridModifiers.None, Count);

        Assert.Equal([6], Rows(s));
        Assert.Equal(6, s.Anchor);
        Assert.Equal(6, s.Focus);
    }

    [Fact]
    public void Shift_and_an_arrow_key_extend_from_the_anchor()
    {
        var s = new GridSelection();

        s.Click(5, GridModifiers.None, Count);
        s.KeyTo(6, GridModifiers.Shift, Count);
        s.KeyTo(7, GridModifiers.Shift, Count);

        Assert.Equal([5, 6, 7], Rows(s));
        Assert.Equal(5, s.Anchor);
        Assert.Equal(7, s.Focus);
    }

    /// <summary>Shift back past the anchor collapses the run and grows it the other way, rather
    /// than keeping both halves.</summary>
    [Fact]
    public void Shift_back_past_the_anchor_turns_the_run_around()
    {
        var s = new GridSelection();

        s.Click(5, GridModifiers.None, Count);
        s.KeyTo(8, GridModifiers.Shift, Count);
        s.KeyTo(3, GridModifiers.Shift, Count);

        Assert.Equal([3, 4, 5], Rows(s));
    }

    /// <summary>Ctrl and an arrow key walk the focus without touching the selection, so somebody can
    /// reach a row and ctrl-space it.</summary>
    [Fact]
    public void Ctrl_and_an_arrow_key_move_only_the_focus()
    {
        var s = new GridSelection();

        s.Click(5, GridModifiers.None, Count);
        s.KeyTo(8, GridModifiers.Control, Count);

        Assert.Equal([5], Rows(s));
        Assert.Equal(8, s.Focus);

        s.ToggleFocus(Count);
        Assert.Equal([5, 8], Rows(s));
    }

    [Fact]
    public void A_key_past_the_ends_of_the_list_stops_at_them()
    {
        var s = new GridSelection();

        s.KeyTo(-5, GridModifiers.None, Count);
        Assert.Equal([0], Rows(s));

        s.KeyTo(500, GridModifiers.None, Count);
        Assert.Equal([99], Rows(s));
    }

    [Fact]
    public void Select_all_is_one_run()
    {
        var s = new GridSelection();

        s.SelectAll(1_000_000);

        Assert.Single(s.Spans);
        Assert.Equal(1_000_000, s.Count);
        Assert.True(s.Contains(999_999));
    }

    [Fact]
    public void Select_all_on_an_empty_list_selects_nothing()
    {
        var s = new GridSelection();

        s.SelectAll(0);

        Assert.True(s.IsEmpty);
    }

    [Fact]
    public void Ctrl_clicking_out_of_a_select_all_leaves_the_rest_selected()
    {
        var s = new GridSelection();

        s.SelectAll(1_000_000);
        s.Click(500_000, GridModifiers.Control, 1_000_000);

        Assert.Equal(999_999, s.Count);
        Assert.False(s.Contains(500_000));
        Assert.True(s.Contains(499_999));
    }

    [Fact]
    public void Clearing_leaves_the_anchor_and_focus_where_they_were()
    {
        var s = new GridSelection();

        s.Click(5, GridModifiers.None, Count);
        s.Clear();

        Assert.True(s.IsEmpty);
        Assert.Equal(5, s.Anchor);
        Assert.Equal(5, s.Focus);
    }

    [Fact]
    public void Clearing_an_empty_selection_changes_nothing()
    {
        var s = new GridSelection();

        Assert.False(s.Clear());
    }

    [Fact]
    public void A_shorter_list_drops_the_rows_that_went_and_brings_the_focus_back()
    {
        var s = new GridSelection();

        s.Click(10, GridModifiers.None, Count);
        s.Click(90, GridModifiers.Shift, Count);
        s.Resize(20);

        Assert.Equal(10, s.Count);
        Assert.Equal(19, s.Focus);
        Assert.Equal(10, s.Anchor);
    }

    [Fact]
    public void A_list_that_empties_takes_the_anchor_and_focus_with_it()
    {
        var s = new GridSelection();

        s.Click(10, GridModifiers.None, Count);
        s.Resize(0);

        Assert.True(s.IsEmpty);
        Assert.Equal(-1, s.Anchor);
        Assert.Equal(-1, s.Focus);
    }

    /// <summary>What the queue does after a rebuild: the ticks are on the nodes, and the grid is
    /// told what they say.</summary>
    [Fact]
    public void The_host_can_put_a_selection_back()
    {
        var s = new GridSelection();

        s.Set([new Span(2, 4), new Span(8, 8)], Count, anchor: 2, focus: 8);

        Assert.Equal([2, 3, 4, 8], Rows(s));
        Assert.Equal(2, s.Anchor);
        Assert.Equal(8, s.Focus);
    }

    [Fact]
    public void A_put_back_selection_is_clamped_to_the_list()
    {
        var s = new GridSelection();

        s.Set([new Span(0, 500)], 10, anchor: 900, focus: 900);

        Assert.Equal(10, s.Count);
        Assert.Equal(-1, s.Anchor);
        Assert.Equal(-1, s.Focus);
    }

    [Fact]
    public void The_set_event_fires_only_when_the_set_differs()
    {
        var s = new GridSelection();
        var sets = 0;
        var anything = 0;
        s.SelectionChanged += () => sets++;
        s.Changed += () => anything++;

        s.Click(5, GridModifiers.None, Count);
        Assert.Equal(1, sets);
        Assert.Equal(1, anything);

        // Same row again: nothing moved at all.
        s.Click(5, GridModifiers.None, Count);
        Assert.Equal(1, sets);
        Assert.Equal(1, anything);

        // The focus moves and the set does not.
        s.KeyTo(8, GridModifiers.Control, Count);
        Assert.Equal(1, sets);
        Assert.Equal(2, anything);
    }

    [Fact]
    public void A_click_that_changes_nothing_reports_nothing()
    {
        var s = new GridSelection();

        Assert.True(s.Click(5, GridModifiers.None, Count));
        Assert.False(s.Click(5, GridModifiers.None, Count));
    }
}
