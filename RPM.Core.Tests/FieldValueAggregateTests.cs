using RPM.Core.Editing;

namespace RPM.Core.Tests;

/// <summary>
/// The per-field roll-up behind the mass-edit window: whether the selected tracks already agree on a
/// field (so it shows one value) or diverge (so it offers the distinct values to pick or type over).
/// </summary>
public class FieldValueAggregateTests
{
    [Fact]
    public void AllSameNonEmpty_IsAllSame_WithThatValue()
    {
        var a = FieldValueAggregate.Summarize(["Radiohead", "Radiohead", "Radiohead"]);

        Assert.True(a.AllSame);
        Assert.Equal("Radiohead", a.CommonValue);
        Assert.Equal(new[] { "Radiohead" }, a.DistinctValues);
    }

    [Fact]
    public void AllEmpty_IsAllSame_WithEmptyCommon_AndNoOptions()
    {
        var a = FieldValueAggregate.Summarize([null, "", "   "]);

        Assert.True(a.AllSame);
        Assert.Equal("", a.CommonValue);
        Assert.Empty(a.DistinctValues);
    }

    [Fact]
    public void Divergent_NotAllSame_ListsDistinctNonEmpty_InOrder()
    {
        var a = FieldValueAggregate.Summarize(["Rock", "Pop", "Rock", "Jazz"]);

        Assert.False(a.AllSame);
        Assert.Equal("", a.CommonValue);
        Assert.Equal(new[] { "Rock", "Pop", "Jazz" }, a.DistinctValues);
    }

    [Fact]
    public void SomeEmptySomeSet_IsDivergent_AndEmptyIsNotAnOption()
    {
        var a = FieldValueAggregate.Summarize(["2001", "", "2001"]);

        Assert.False(a.AllSame);
        Assert.Equal(new[] { "2001" }, a.DistinctValues);
    }

    [Fact]
    public void Trims_BeforeComparing()
    {
        var a = FieldValueAggregate.Summarize(["  Blur ", "Blur"]);

        Assert.True(a.AllSame);
        Assert.Equal("Blur", a.CommonValue);
    }

    [Fact]
    public void DedupesCaseInsensitively_KeepsFirstSeenCasing()
    {
        var a = FieldValueAggregate.Summarize(["Jazz", "JAZZ", "jazz"]);

        Assert.True(a.AllSame);
        Assert.Equal("Jazz", a.CommonValue);
        Assert.Equal(new[] { "Jazz" }, a.DistinctValues);
    }

    [Fact]
    public void Empty_Input_IsAllSame_Empty()
    {
        var a = FieldValueAggregate.Summarize([]);

        Assert.True(a.AllSame);
        Assert.Equal("", a.CommonValue);
        Assert.Empty(a.DistinctValues);
    }
}
