using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// One playlist edited on two devices at the same time. What each device did has to survive the
/// other. A song must never disappear because both devices were editing.
/// </summary>
public class PlaylistMergeTests
{
    private static IReadOnlyList<string> Merge(string[] baseIds, string[] mine, string[] theirs)
        => PlaylistMerge.Songs(PlaylistBase.Published(baseIds), mine, theirs);

    // ── one side moved ──

    [Fact]
    public void Nothing_Changed_Anywhere_Is_The_Same_List()
        => Assert.Equal(["a", "b"], Merge(["a", "b"], ["a", "b"], ["a", "b"]));

    [Fact]
    public void Only_The_Other_Head_Moved_So_Its_List_Is_Taken_Whole()
        => Assert.Equal(["a", "b", "c"], Merge(["a", "b"], ["a", "b"], ["a", "b", "c"]));

    [Fact]
    public void Only_This_Head_Moved_So_Its_List_Stands()
        => Assert.Equal(["a", "b", "c"], Merge(["a", "b"], ["a", "b", "c"], ["a", "b"]));

    /// <summary>A reorder made here is not undone by a cloud copy that has not moved. A plain count
    /// would undo it, so the published order is the base the result is built on.</summary>
    [Fact]
    public void A_Reorder_Made_Here_Survives_An_Unchanged_Cloud()
        => Assert.Equal(["c", "a", "b"], Merge(["a", "b", "c"], ["c", "a", "b"], ["a", "b", "c"]));

    // ── both sides moved ──

    /// <summary>The phone added one song while the computer added a different one. Both are in the
    /// playlist afterwards: this is the case the merge exists for.</summary>
    [Fact]
    public void Two_Heads_Adding_Different_Songs_Keep_Both()
        => Assert.Equal(["a", "y", "x"], Merge(["a"], ["a", "x"], ["a", "y"]));

    [Fact]
    public void A_Removal_Here_Stands_Against_An_Addition_There()
        => Assert.Equal(["a", "y"], Merge(["a", "b"], ["a"], ["a", "b", "y"]));

    [Fact]
    public void A_Removal_There_Stands_Against_An_Addition_Here()
        => Assert.Equal(["a", "x"], Merge(["a", "b"], ["a", "b", "x"], ["a"]));

    [Fact]
    public void Both_Removing_The_Same_Song_Removes_It_Once()
        => Assert.Equal(["a"], Merge(["a", "b"], ["a"], ["a"]));

    [Fact]
    public void Both_Adding_The_Same_Song_Does_Not_Add_It_Twice()
        => Assert.Equal(["a", "x"], Merge(["a"], ["a", "x"], ["a", "x"]));

    /// <summary>Where both sides reordered there is no right answer, so the published order stands:
    /// every other device already uses it.</summary>
    [Fact]
    public void When_Both_Reordered_The_Published_Order_Wins()
        => Assert.Equal(["b", "c", "a"], Merge(["a", "b", "c"], ["c", "b", "a"], ["b", "c", "a"]));

    /// <summary>An addition made here goes after it, because the published order has not seen
    /// it.</summary>
    [Fact]
    public void An_Addition_Here_Is_Appended_After_The_Published_Order()
        => Assert.Equal(["b", "a", "x"], Merge(["a", "b"], ["a", "b", "x"], ["b", "a"]));

    // ── the same song twice ──

    /// <summary>A playlist may hold a song twice on purpose, so the merge counts copies. It does not
    /// treat the list as a set.</summary>
    [Fact]
    public void A_Second_Copy_Added_There_Is_Kept()
        => Assert.Equal(["a", "a", "b"], Merge(["a", "b"], ["a", "b"], ["a", "a", "b"]));

    [Fact]
    public void A_Second_Copy_Removed_Here_Goes()
        => Assert.Equal(["a", "y"], Merge(["a", "a"], ["a"], ["a", "a", "y"]));

    /// <summary>Both devices adding a second copy is ONE change made twice, not two changes. The list
    /// ends with two copies, which is what each device asked for, not three.</summary>
    [Fact]
    public void Each_Head_Adding_The_Same_Copy_Is_One_Change()
        => Assert.Equal(["a", "a"], Merge(["a"], ["a", "a"], ["a", "a"]));

    // ── emptying ──

    [Fact]
    public void A_Playlist_Emptied_Here_Empties()
        => Assert.Empty(Merge(["a", "b"], [], ["a", "b"]));

    [Fact]
    public void A_Playlist_Emptied_There_Empties()
        => Assert.Empty(Merge(["a", "b"], ["a", "b"], []));

    /// <summary>Emptied on one device while the other added a song: the added song stays. Clearing a
    /// list does not mean removing a song that the user had not seen yet.</summary>
    [Fact]
    public void Emptied_Here_Keeps_What_The_Other_Head_Added()
        => Assert.Equal(["y"], Merge(["a", "b"], [], ["a", "b", "y"]));

    // ── no ancestor ──

    /// <summary>
    /// A library upgrading from an older version has never recorded what it published. With no common
    /// ancestor, a removal cannot be told from an addition that has not been published yet. So
    /// nothing is removed: the two lists are combined, and no work is lost.
    /// </summary>
    [Fact]
    public void With_No_Ancestor_Nothing_Is_Removed()
    {
        var merged = PlaylistMerge.Songs(PlaylistBase.Unknown, ["a", "x"], ["a", "y"]);

        Assert.Equal(["a", "y", "x"], merged);
    }

    [Fact]
    public void With_No_Ancestor_A_Song_Held_Twice_On_One_Side_Is_Kept_Twice()
    {
        var merged = PlaylistMerge.Songs(PlaylistBase.Unknown, ["a", "a"], ["a"]);

        Assert.Equal(["a", "a"], merged);
    }

    [Fact]
    public void An_Unknown_Ancestor_Says_So()
    {
        Assert.False(PlaylistBase.Unknown.IsKnown);
        Assert.True(PlaylistBase.Published([]).IsKnown);
    }

    /// <summary>A KNOWN empty list is a real answer, and a different one: the playlist was published
    /// empty, so everything on both sides was added since.</summary>
    [Fact]
    public void A_Known_Empty_Ancestor_Adds_Both_Sides()
        => Assert.Equal(["y", "x"], Merge([], ["x"], ["y"]));
}
