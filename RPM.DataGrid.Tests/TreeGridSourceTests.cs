using System.ComponentModel;
using RPM.DataGrid.Model;
using RPM.DataGrid.Tree;

namespace RPM.DataGrid.Tests;

/// <summary>
/// The tree read as a flat list of rows. What the grid sees; the index maths underneath it is
/// <see cref="VirtualTreeModelTests"/>' subject.
/// </summary>
public class TreeGridSourceTests
{
    private sealed class Group : ITreeGridRow, INotifyPropertyChanged
    {
        private bool _expanded = true;

        public required string Name { get; init; }
        public int Depth { get; init; }
        public List<Group> Children { get; } = [];
        public int LeafCount { get; set; }

        public bool HasChildren => Children.Count > 0 || LeafCount > 0;

        public bool IsExpanded
        {
            get => _expanded;
            set
            {
                if (_expanded == value)
                    return;
                _expanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public override string ToString() => $"group:{Name}";
    }

    private sealed record Leaf(string Group, int Index);

    private sealed record Placeholder(string Group);

    /// <summary>
    /// A tree with control over when a fetch finishes, so the placeholder state is observable rather
    /// than something that happens between two lines.
    /// </summary>
    private sealed class Harness : IDisposable
    {
        private readonly List<TaskCompletionSource<IReadOnlyList<object>>> _pending = [];

        public List<Group> Roots { get; } = [];
        public VirtualTreeModel Model { get; }
        public TreeGridSource Source { get; }
        public int Fetches { get; private set; }
        public bool HoldFetches { get; set; }

        public Harness(int pageSize = 4)
        {
            Model = new VirtualTreeModel(
                childrenOf: g => ((Group)g).Children,
                leafCountOf: g => g is Group grp ? grp.LeafCount : 0,
                fetchLeaves: (g, offset, limit, _) =>
                {
                    Fetches++;
                    var name = g is Group grp ? grp.Name : "<root>";
                    var rows = new List<object>();
                    for (var i = 0; i < limit; i++)
                        rows.Add(new Leaf(name, offset + i));

                    if (!HoldFetches)
                        return Task.FromResult<IReadOnlyList<object>>(rows);

                    var held = new TaskCompletionSource<IReadOnlyList<object>>();
                    _pending.Add(held);
                    return held.Task;
                },
                placeholderOf: g => new Placeholder(g is Group grp ? grp.Name : "<root>"),
                pageSize: pageSize);

            Source = new TreeGridSource(Model);
        }

        public Group AddGroup(string name, int leaves)
        {
            var g = new Group { Name = name, LeafCount = leaves };
            Roots.Add(g);
            return g;
        }

        public void Commit() => Model.SetRoots([.. Roots]);

        /// <summary>Lets every held fetch finish, in the order they were asked for.</summary>
        public void Deliver()
        {
            var held = _pending.ToList();
            _pending.Clear();
            foreach (var h in held)
            {
                var name = "held";
                var rows = new List<object>();
                for (var i = 0; i < 4; i++)
                    rows.Add(new Leaf(name, i));
                h.SetResult(rows);
            }
        }

        public void Dispose() => Source.Dispose();
    }

    [Fact]
    public void An_empty_tree_has_no_rows()
    {
        using var h = new Harness();
        h.Commit();

        Assert.Equal(0, h.Source.Count);
        Assert.Null(h.Source.Row(0));
    }

    [Fact]
    public void A_group_and_its_leaves_are_a_flat_list()
    {
        using var h = new Harness();
        h.AddGroup("A", 3);
        h.Commit();

        Assert.Equal(4, h.Source.Count);
        Assert.IsType<Group>(h.Source.Row(0));
        Assert.IsType<Leaf>(h.Source.Row(1));
        Assert.IsType<Leaf>(h.Source.Row(3));
    }

    [Fact]
    public void An_index_outside_the_list_is_nothing()
    {
        using var h = new Harness();
        h.AddGroup("A", 3);
        h.Commit();

        Assert.Null(h.Source.Row(-1));
        Assert.Null(h.Source.Row(4));
        Assert.Null(h.Source.Row(int.MaxValue));
    }

    /// <summary>
    /// A row whose page has not arrived is a placeholder, not a gap and not an error. The grid draws
    /// an empty line and asks again when the page lands.
    /// </summary>
    [Fact]
    public void A_row_whose_page_is_in_flight_reads_as_a_placeholder()
    {
        using var h = new Harness();
        h.AddGroup("A", 3);
        h.Commit();
        h.HoldFetches = true;

        var row = h.Source.Row(1);

        Assert.IsType<Placeholder>(row);

        h.Deliver();
        Assert.IsType<Leaf>(h.Source.Row(1));
    }

    [Fact]
    public void A_page_landing_says_which_rows_it_filled()
    {
        using var h = new Harness();
        h.AddGroup("A", 3);
        h.Commit();
        h.HoldFetches = true;

        var landed = new List<(int First, int End)>();
        h.Source.Landed += (a, b) => landed.Add((a, b));

        h.Source.Prefetch(0, 4);
        Assert.Empty(landed);

        h.Deliver();

        Assert.NotEmpty(landed);
        Assert.All(landed, l => Assert.True(l.First >= 1 && l.End <= 4, $"{l.First}..{l.End} is not inside the leaves"));
    }

    [Fact]
    public void Collapsing_a_group_takes_its_rows_away()
    {
        using var h = new Harness();
        var a = h.AddGroup("A", 3);
        h.AddGroup("B", 2);
        h.Commit();

        var removed = new List<(int First, int End)>();
        h.Source.Removed += (x, y) => removed.Add((x, y));

        Assert.Equal(7, h.Source.Count);

        a.IsExpanded = false;

        Assert.Equal(4, h.Source.Count);
        Assert.Equal([(1, 4)], removed);
    }

    [Fact]
    public void Expanding_a_group_brings_them_back()
    {
        using var h = new Harness();
        var a = h.AddGroup("A", 3);
        h.Commit();
        a.IsExpanded = false;

        var inserted = new List<(int First, int End)>();
        h.Source.Inserted += (x, y) => inserted.Add((x, y));

        a.IsExpanded = true;

        Assert.Equal(4, h.Source.Count);
        Assert.Equal([(1, 4)], inserted);
    }

    [Fact]
    public void A_new_skeleton_is_a_reset()
    {
        using var h = new Harness();
        h.AddGroup("A", 3);
        h.Commit();

        var resets = 0;
        h.Source.Reset += () => resets++;

        h.AddGroup("B", 1);
        h.Commit();

        Assert.Equal(1, resets);
        Assert.Equal(6, h.Source.Count);
    }

    [Fact]
    public void Prefetching_beyond_the_list_is_clamped()
    {
        using var h = new Harness();
        h.AddGroup("A", 3);
        h.Commit();

        h.Source.Prefetch(-50, 5000);
        h.Source.Prefetch(100, 200);

        Assert.Equal(4, h.Source.Count);
    }

    [Fact]
    public void Prefetching_an_empty_list_asks_for_nothing()
    {
        using var h = new Harness();
        h.Commit();

        h.Source.Prefetch(0, 40);

        Assert.Equal(0, h.Fetches);
    }

    /// <summary>
    /// Disposing takes the handlers off the model. A grid that is torn down and rebuilt against the
    /// same tree, which is what re-opening the pane does, must not leave the old one listening.
    /// </summary>
    [Fact]
    public void Disposing_stops_it_hearing_the_model()
    {
        var h = new Harness();
        var a = h.AddGroup("A", 3);
        h.Commit();

        var events = 0;
        h.Source.Removed += (_, _) => events++;

        h.Source.Dispose();
        a.IsExpanded = false;

        Assert.Equal(0, events);
    }
}
