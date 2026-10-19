using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// Which absent lists are admitted to, and what each one blames. The blame is the part that matters:
/// a mood list on a collection the measuring pass has barely started is not short of music, and
/// telling somebody to import more would send them to do work that changes nothing.
/// </summary>
public class LockedListsTests
{
    private static SmartPlaylistId Whole(SmartPlaylistKind kind) => SmartPlaylistId.WholeShelf(kind);

    /// <summary>The pass has finished, so nothing is waiting on it.</summary>
    private static readonly MeasuringProgress Done = new(1000, 1000);

    /// <summary>Barely started.</summary>
    private static readonly MeasuringProgress Early = new(50, 1000);

    [Fact]
    public void AnOpenListIsNotLocked()
    {
        var counts = new Dictionary<SmartPlaylistId, int>
        {
            [Whole(SmartPlaylistKind.Calm)] = SmartPlaylists.LeastToShow(Whole(SmartPlaylistKind.Calm)),
        };

        Assert.Empty(LockedLists.Choose(counts, Done));
    }

    [Fact]
    public void AShortListIsLockedAndCountsWhatIsMissing()
    {
        var id = Whole(SmartPlaylistKind.Calm);
        int want = SmartPlaylists.LeastToShow(id);

        var locked = Assert.Single(LockedLists.Choose(
            new Dictionary<SmartPlaylistId, int> { [id] = want - 3 }, Done));

        Assert.Equal(id, locked.Id);
        Assert.Equal(3, locked.Gate.ToGo);
        Assert.False(locked.Gate.Open);
    }

    /// <summary>The cap is what keeps a pane of absences from being worse than no explanation.</summary>
    [Fact]
    public void NoMoreThanThreeAreDrawn()
    {
        var counts = new Dictionary<SmartPlaylistId, int>();
        foreach (var genre in new[] { "Rock", "Jazz", "House", "Folk", "Punk", "Blues" })
            foreach (var kind in SmartPlaylists.BrokenOutByGenre)
                counts[SmartPlaylistId.Within(kind, genre)] = 1;

        Assert.True(counts.Count > LockedLists.AtOnce * 3, "the set under test should be much larger than the cap");
        Assert.Equal(LockedLists.AtOnce, LockedLists.Choose(counts, Done).Count);
    }

    /// <summary>Closest first, measured as a share of the way there rather than by the shortfall: a
    /// list missing three of ten is nearer than one missing twenty of sixty.</summary>
    [Fact]
    public void TheClosestToOpeningComeFirst()
    {
        var far = Whole(SmartPlaylistKind.Calm);          // wants 10
        var near = Whole(SmartPlaylistKind.Energetic);    // wants 10

        var chosen = LockedLists.Choose(
            new Dictionary<SmartPlaylistId, int> { [far] = 2, [near] = 9 }, Done);

        Assert.Equal(near, chosen[0].Id);
        Assert.Equal(far, chosen[1].Id);
    }

    /// <summary>A mood list on a shelf the pass has not walked blames the pass, not the shelf.</summary>
    [Fact]
    public void AMeasuredListBlamesTheMeasuringPassWhileItIsStillRunning()
    {
        var locked = Assert.Single(LockedLists.Choose(
            new Dictionary<SmartPlaylistId, int> { [Whole(SmartPlaylistKind.Calm)] = 2 }, Early));

        Assert.Equal(GateNeed.Measured, locked.Gate.Need);
    }

    /// <summary>Once the pass has covered the collection, the same list blames the collection.</summary>
    [Fact]
    public void TheSameListAsksForMusicOnceThePassHasFinished()
    {
        var locked = Assert.Single(LockedLists.Choose(
            new Dictionary<SmartPlaylistId, int> { [Whole(SmartPlaylistKind.Calm)] = 2 }, Done));

        Assert.Equal(GateNeed.SongsInList, locked.Gate.Need);
    }

    /// <summary>A list read off the listening log is never blamed on the measuring pass, however
    /// little of the collection has been measured: playing something is what fills it.</summary>
    [Fact]
    public void AListeningListAlwaysAsksForPlays()
    {
        var locked = Assert.Single(LockedLists.Choose(
            new Dictionary<SmartPlaylistId, int> { [Whole(SmartPlaylistKind.OnRepeat)] = 1 }, Early));

        Assert.Equal(GateNeed.Plays, locked.Gate.Need);
    }

    /// <summary>The pass leaves a tail of files it cannot read. A completeness test would leave every
    /// mood list permanently blaming the pass on any collection holding one broken file.</summary>
    [Fact]
    public void AlmostMeasuredCountsAsMeasured()
    {
        Assert.True(new MeasuringProgress(900, 1000).Enough);
        Assert.False(new MeasuringProgress(899, 1000).Enough);
    }

    /// <summary>Nothing to measure is not "still measuring".</summary>
    [Fact]
    public void AnEmptyCollectionIsNotWaitingOnThePass()
        => Assert.True(new MeasuringProgress(0, 0).Enough);
}
