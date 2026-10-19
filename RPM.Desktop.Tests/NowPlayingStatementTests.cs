using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Tests;

/// <summary>
/// What the system's media controls are told for each cause, with a card to show and without.
/// </summary>
public class NowPlayingStatementTests
{
    /// <summary>
    /// The order of events that causes a blank cover on macOS. The cover is read before the deck has
    /// opened the file, so it arrives with no card. A restatement then clears the panel and the cover
    /// with it, and the record plays with a blank sleeve. So nothing is said at this point. The cover
    /// waits for the next show.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_cover_that_lands_before_the_deck_has_a_card_clears_nothing(bool toldSomething)
    {
        Assert.Equal(Statement.Nothing,
            NowPlayingStatement.Next(StatementCause.CoverLanded, hasCard: false, toldSomething, needsRestating: false));
    }

    [Fact]
    public void A_cover_that_lands_with_a_card_is_shown_at_once()
    {
        Assert.Equal(Statement.Show,
            NowPlayingStatement.Next(StatementCause.CoverLanded, hasCard: true, toldSomething: true, needsRestating: false));
    }

    /// <summary>A load that finishes after the cover has arrived is a tick with a card, and nothing has
    /// been told since the deck changed. So it is shown, and the cover the system is holding goes with
    /// it.</summary>
    [Fact]
    public void The_first_tick_with_a_card_after_a_deck_change_is_shown()
    {
        Assert.Equal(Statement.Show,
            NowPlayingStatement.Next(StatementCause.Tick, hasCard: true, toldSomething: false, needsRestating: false));
    }

    [Fact]
    public void A_tick_that_changes_nothing_the_system_counts_for_itself_says_nothing()
    {
        Assert.Equal(Statement.Nothing,
            NowPlayingStatement.Next(StatementCause.Tick, hasCard: true, toldSomething: true, needsRestating: false));
        Assert.Equal(Statement.Show,
            NowPlayingStatement.Next(StatementCause.Tick, hasCard: true, toldSomething: true, needsRestating: true));
    }

    [Fact]
    public void A_deck_change_clears_the_old_record_even_when_the_new_one_is_not_loaded_yet()
    {
        Assert.Equal(Statement.Clear,
            NowPlayingStatement.Next(StatementCause.DeckChanged, hasCard: false, toldSomething: false, needsRestating: false));
        Assert.Equal(Statement.Show,
            NowPlayingStatement.Next(StatementCause.DeckChanged, hasCard: true, toldSomething: true, needsRestating: false));
    }

    [Fact]
    public void A_tick_after_the_deck_emptied_clears_only_what_was_shown()
    {
        Assert.Equal(Statement.Clear,
            NowPlayingStatement.Next(StatementCause.Tick, hasCard: false, toldSomething: true, needsRestating: false));
        Assert.Equal(Statement.Nothing,
            NowPlayingStatement.Next(StatementCause.Tick, hasCard: false, toldSomething: false, needsRestating: false));
    }
}
