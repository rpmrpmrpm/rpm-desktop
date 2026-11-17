using RPM.Desktop.Presentation.Main;

namespace RPM.Desktop.Tests;

/// <summary>
/// <see cref="RowCover"/>: whether the library card is drawn under the import card.
///
/// <para>The import card's frost is a live blur of whatever is drawn under it while the backdrop moves. A
/// library card drawn under the import card shows through that blur. The other way round is worse: a
/// library card left undrawn with nothing over it is an empty row. These tests pin both directions.</para>
/// </summary>
public class RowCoverTests
{
    [Fact]
    public void InItsColumnTheLibraryIsDrawn()
    {
        var cover = new RowCover();

        Assert.False(cover.Stretched);
        Assert.True(cover.LibraryDrawn(cardShown: true));
    }

    /// <summary>The part of the row that the sliding card has not reached shows the library card.</summary>
    [Fact]
    public void WhileTheCardSlidesOutTheLibraryIsDrawn()
    {
        var cover = new RowCover();
        cover.SlideOut();

        Assert.True(cover.Stretched);
        Assert.True(cover.LibraryDrawn(cardShown: true));
    }

    [Fact]
    public void OnceTheCardHasLandedTheLibraryIsNotDrawn()
    {
        var cover = new RowCover();
        cover.SlideOut();
        cover.Landed();

        Assert.False(cover.LibraryDrawn(cardShown: true));
    }

    [Fact]
    public void AHiddenCardCoversNothing()
    {
        var cover = new RowCover();
        cover.SlideOut();
        cover.Landed();

        Assert.True(cover.LibraryDrawn(cardShown: false));
    }

    [Fact]
    public void TheLibraryIsDrawnBeforeTheCardSlidesBack()
    {
        var cover = new RowCover();
        cover.SlideOut();
        cover.Landed();
        cover.SlideBack();

        Assert.True(cover.SlidingBack);
        Assert.True(cover.LibraryDrawn(cardShown: true));
    }

    [Fact]
    public void ADroppedCardLeavesTheLibraryDrawn()
    {
        var cover = new RowCover();
        cover.SlideOut();
        cover.Landed();
        cover.InColumn();

        Assert.False(cover.Stretched);
        Assert.True(cover.LibraryDrawn(cardShown: true));
    }

    /// <summary>A slide out overtaken by a slide back, whose landing still arrives, must not hide the
    /// library card that the card is moving off.</summary>
    [Fact]
    public void ALateLandingAfterTheCardWasSentBackChangesNothing()
    {
        var cover = new RowCover();
        cover.SlideOut();
        cover.SlideBack();
        cover.Landed();

        Assert.True(cover.SlidingBack);
        Assert.True(cover.LibraryDrawn(cardShown: true));
    }

    [Fact]
    public void ALateLandingAfterTheCardWasDroppedChangesNothing()
    {
        var cover = new RowCover();
        cover.SlideOut();
        cover.InColumn();
        cover.Landed();

        Assert.False(cover.Stretched);
        Assert.True(cover.LibraryDrawn(cardShown: true));
    }

    /// <summary>Import pressed again while the card slides back: it turns round and lands over the
    /// row.</summary>
    [Fact]
    public void ACardTurnedRoundLandsOverTheRow()
    {
        var cover = new RowCover();
        cover.SlideOut();
        cover.Landed();
        cover.SlideBack();
        cover.SlideOut();

        Assert.False(cover.SlidingBack);
        Assert.True(cover.LibraryDrawn(cardShown: true));

        cover.Landed();

        Assert.False(cover.LibraryDrawn(cardShown: true));
    }
}
