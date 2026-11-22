using RPM.Desktop.Presentation.Main.Library;

namespace RPM.Desktop.Tests;

/// <summary>
/// The album wall's rows share one card pool. A row whose cards are replaced gives every card back and then
/// empties its children (a repeater with a non-virtualizing layout does this). Another row must still be able
/// to take those cards. The framework's own pool throws there, inside the wall's layout pass.
/// </summary>
public class AlbumCardPoolTests
{
    [Fact]
    public void Another_row_takes_a_card_whose_row_emptied_its_children()
    {
        var (cards, first, second) = Rows();
        var card = first.Hold(new Card());
        cards.Put(card, "", first);
        first.Empty();

        Assert.Same(card, cards.Take("", second));
        Assert.Empty(first.Children);
        Assert.Null(card.Parent);
    }

    [Fact]
    public void A_card_still_inside_the_row_that_gave_it_back_leaves_that_row()
    {
        var (cards, first, second) = Rows();
        var card = first.Hold(new Card());
        cards.Put(card, "", first);

        Assert.Same(card, cards.Take("", second));
        Assert.DoesNotContain(card, first.Children);
        Assert.Null(card.Parent);
    }

    [Fact]
    public void A_row_takes_its_own_card_back_and_it_stays_in_place()
    {
        var (cards, first, second) = Rows();
        var theirs = second.Hold(new Card());
        var mine = first.Hold(new Card());
        cards.Put(theirs, "", second);
        cards.Put(mine, "", first);

        Assert.Same(mine, cards.Take("", first));
        Assert.Same(first, mine.Parent);
        Assert.Same(second, theirs.Parent);
    }

    [Fact]
    public void A_card_nobody_owns_comes_before_another_rows_card()
    {
        var (cards, first, second) = Rows();
        var theirs = first.Hold(new Card());
        var free = new Card();
        cards.Put(theirs, "", first);
        cards.Put(free, "", owner: null);

        Assert.Same(free, cards.Take("", second));
        Assert.Same(first, theirs.Parent);
    }

    [Fact]
    public void A_row_with_nothing_of_its_own_takes_the_card_given_back_last()
    {
        var (cards, first, second) = Rows();
        var earlier = first.Hold(new Card());
        var later = first.Hold(new Card());
        cards.Put(earlier, "", first);
        cards.Put(later, "", first);

        Assert.Same(later, cards.Take("", second));
        Assert.Same(earlier, cards.Take("", second));
        Assert.Null(cards.Take("", second));
    }

    [Fact]
    public void A_card_is_handed_out_only_for_its_own_reuse_key()
    {
        var (cards, first, _) = Rows();
        cards.Put(new Card(), "wide", first);

        Assert.Null(cards.Take("", first));
        Assert.NotNull(cards.Take("wide", first));
    }

    private static (AlbumCardPool.ReturnedCards<Card> Cards, Row First, Row Second) Rows()
        => (new AlbumCardPool.ReturnedCards<Card>(
                parentOf: card => card.Parent,
                removeFrom: (row, card) => ((Row)row).Remove(card)),
            new Row(), new Row());

    private sealed class Card
    {
        public Row? Parent { get; set; }
    }

    /// <summary>Stands in for a repeater: a panel with children.</summary>
    private sealed class Row
    {
        public List<Card> Children { get; } = [];

        public Card Hold(Card card)
        {
            Children.Add(card);
            card.Parent = this;
            return card;
        }

        public void Remove(Card card)
        {
            Assert.True(Children.Remove(card), "removed a card from a row that does not hold it");
            card.Parent = null;
        }

        /// <summary>What a repeater with a non-virtualizing layout does after giving its cards back.</summary>
        public void Empty()
        {
            foreach (var card in Children)
                card.Parent = null;
            Children.Clear();
        }
    }
}
