using RPM.Desktop.Services.Updates;

namespace RPM.Desktop.Tests;

/// <summary>
/// When the app may put the question up, and when it has already been answered.
///
/// <para>A reader who says Later has said it about a VERSION, not about updating: the update they were
/// offered installs when they quit, and the next one asks again. A rule that remembered only "they said
/// no" would silence every release after the first.</para>
/// </summary>
public class UpdateAskTests
{
    [Fact]
    public void A_reader_who_has_deferred_nothing_is_asked()
        => Assert.True(UpdateAsk.Due(new Version(0, 2, 0), deferred: null));

    [Fact]
    public void The_version_that_was_deferred_is_not_offered_again()
        => Assert.False(UpdateAsk.Due(new Version(0, 2, 0), new Version(0, 2, 0)));

    [Fact]
    public void A_newer_version_asks_again()
        => Assert.True(UpdateAsk.Due(new Version(0, 2, 1), new Version(0, 2, 0)));

    /// <summary>A stamp from a copy that was ahead of this one (a settings file carried from another
    /// machine, a downgrade) leaves the reader unasked rather than asked about a version they already
    /// turned down.</summary>
    [Fact]
    public void An_older_version_than_the_one_deferred_is_not_offered()
        => Assert.False(UpdateAsk.Due(new Version(0, 1, 9), new Version(0, 2, 0)));

    [Fact]
    public void A_stamp_that_is_not_a_version_counts_as_nothing_deferred()
    {
        Assert.Null(UpdateAsk.Deferred(null));
        Assert.Null(UpdateAsk.Deferred(""));
        Assert.Null(UpdateAsk.Deferred("soon"));
        Assert.Equal(new Version(0, 2, 0), UpdateAsk.Deferred("0.2.0"));
    }

    /// <summary>Velopack's version carries a build suffix that `Version` cannot read (0.2.0-beta.1).
    /// What is compared is the three numbers in front of it, which is what the app calls itself.</summary>
    [Fact]
    public void A_version_with_a_suffix_is_read_as_its_numbers()
        => Assert.Equal(new Version(0, 2, 0), UpdateAsk.Deferred("0.2.0-beta.1"));
}
