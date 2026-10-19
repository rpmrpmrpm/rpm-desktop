using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Tests;

/// <summary>
/// Which cover the system's media controls get, given a deck change, a cover load finishing, and a clear
/// arriving in any order.
/// </summary>
public class NowPlayingSleeveTests
{
    private const string A = @"C:\Music\Album\01.flac";
    private const string B = @"C:\Music\Album\02.flac";

    /// <summary>The order of events on Windows: a new deck is not loaded yet, so the service clears the
    /// panel before the cover arrives. The cover must still be shown.</summary>
    [Fact]
    public void A_clear_before_the_cover_lands_does_not_drop_it()
    {
        var sleeve = new NowPlayingSleeve();

        Assert.True(sleeve.Want(A));
        sleeve.Cleared();

        Assert.True(sleeve.Land(A));
    }

    [Fact]
    public void A_cover_for_a_record_that_was_replaced_is_not_shown()
    {
        var sleeve = new NowPlayingSleeve();

        sleeve.Want(A);
        sleeve.Want(B);

        Assert.False(sleeve.Land(A));
        Assert.True(sleeve.Land(B));
    }

    [Fact]
    public void The_same_record_again_needs_no_new_load()
    {
        var sleeve = new NowPlayingSleeve();

        sleeve.Want(A);
        sleeve.Land(A);

        Assert.False(sleeve.Want(A));
    }

    /// <summary>A clear drops the cover from the system, so the same record coming back reads it
    /// again.</summary>
    [Fact]
    public void A_clear_after_the_cover_landed_makes_the_same_record_load_again()
    {
        var sleeve = new NowPlayingSleeve();

        sleeve.Want(A);
        sleeve.Land(A);
        sleeve.Cleared();

        Assert.True(sleeve.Want(A));
    }

    [Fact]
    public void An_empty_deck_shows_no_cover()
    {
        var sleeve = new NowPlayingSleeve();

        sleeve.Want(A);
        Assert.True(sleeve.Want(null));

        Assert.False(sleeve.Land(null));
        Assert.False(sleeve.Land(A));
    }
}
