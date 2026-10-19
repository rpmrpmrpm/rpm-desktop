using RPM.Core.Files;

namespace RPM.Core.Tests;

/// <summary>
/// File names in the order a person reads them. This decides the track order of a rip that has no
/// tags, and a rip in the wrong order matched against a release listing gives every file the name of
/// the file next to it.
/// </summary>
public class NaturalOrderTests
{
    private static string[] Sorted(params string[] names) =>
        [.. names.OrderBy(n => n, NaturalOrder.Comparer)];

    /// <summary>The main case: plain string order puts 10 before 2, which turns an album into
    /// 1, 10, 11, 12, 2, 3.</summary>
    [Fact]
    public void Ten_comes_after_two()
    {
        Assert.Equal(
            ["Track 1.flac", "Track 2.flac", "Track 9.flac", "Track 10.flac", "Track 12.flac"],
            Sorted("Track 10.flac", "Track 2.flac", "Track 12.flac", "Track 1.flac", "Track 9.flac"));
    }

    /// <summary>Both forms appear in one folder, and they are the same number on the sleeve.</summary>
    [Fact]
    public void Leading_zeros_do_not_change_the_number()
    {
        Assert.Equal(
            ["07 - a.flac", "8 - b.flac", "009 - c.flac"],
            Sorted("009 - c.flac", "8 - b.flac", "07 - a.flac"));
    }

    [Fact]
    public void Zero_is_still_a_number()
    {
        Assert.Equal(["00 - pregap.flac", "01 - one.flac"], Sorted("01 - one.flac", "00 - pregap.flac"));
    }

    [Fact]
    public void Text_decides_when_the_numbers_are_equal()
    {
        Assert.Equal(
            ["01 - Alpha.flac", "01 - Beta.flac"],
            Sorted("01 - Beta.flac", "01 - Alpha.flac"));
    }

    /// <summary>A name that is a prefix of another comes first, when there is nothing left to compare
    /// it on.</summary>
    [Fact]
    public void The_shorter_name_comes_first()
    {
        Assert.Equal(
            ["Track 1", "Track 1 (reprise)"],
            Sorted("Track 1 (reprise)", "Track 1"));
    }

    /// <summary>Full file names are compared character by character when the numbers are equal, so
    /// the extension is part of the answer: a space sorts before a dot. That is the normal rule, not a
    /// special case. Tested because it looks like the prefix rule above but is different.
    /// </summary>
    [Fact]
    public void The_extension_is_part_of_the_name()
    {
        Assert.Equal(
            ["Track 1 (reprise).flac", "Track 1.flac"],
            Sorted("Track 1.flac", "Track 1 (reprise).flac"));
    }

    [Fact]
    public void Case_does_not_decide_the_order()
    {
        Assert.Equal(["a.flac", "B.flac", "c.flac"], Sorted("c.flac", "B.flac", "a.flac"));
    }

    /// <summary>Several numbers in one name, each compared in its place: disc 1 track 10 comes after
    /// disc 1 track 2 and before disc 2 track 1.</summary>
    [Fact]
    public void Every_number_in_the_name_counts()
    {
        Assert.Equal(
            ["1-02.flac", "1-10.flac", "2-01.flac"],
            Sorted("2-01.flac", "1-10.flac", "1-02.flac"));
    }

    [Fact]
    public void Nulls_sort_to_the_front_without_throwing()
    {
        Assert.Equal([null, "a"], Sorted(null!, "a"));
        Assert.Equal(0, NaturalOrder.Comparer.Compare(null, null));
    }
}
