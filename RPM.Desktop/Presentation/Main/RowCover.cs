namespace RPM.Desktop.Presentation.Main;

/// <summary>
/// Where the import card is: in its own column, over the whole row (see <see cref="PaneStage.ImportOver"/>),
/// or sliding between the two. From that, whether the library card under it is drawn.
///
/// <para>The library card is not drawn while the import card lies over the whole row. The import card is
/// frosted glass. Most of the time the glass is a cut from a picture of the backdrop alone (see
/// <c>FrostPlate</c>). While anything behind the glass moves, the glass is a live blur of whatever is drawn
/// under the card, and over the row that is the library card. The album wall or home then shows, blurred,
/// behind the workbench. With the library card not drawn, only the backdrop is under the import card, and
/// the live blur and the cut from the picture show the same thing.</para>
///
/// <para>The two cards' frames also run along the same pixels at the top, the right and the foot. Each frame
/// is a faint, see-through line, so a library card drawn under the import card doubles the import card's
/// frame from where the library card starts.</para>
///
/// <para>The library card is drawn while the import card slides out or back, because the part of the row
/// that the import card has not reached shows the library card. It is drawn whenever the import card is not
/// shown, because nothing covers the row then.</para>
/// </summary>
internal sealed class RowCover
{
    private enum Place
    {
        InColumn,
        SlidingOut,
        Over,
        SlidingBack,
    }

    private Place _place = Place.InColumn;

    /// <summary>Whether the card is stretched over the row: sliding out, lying over it, or sliding
    /// back.</summary>
    public bool Stretched => _place != Place.InColumn;

    /// <summary>Whether the card is on its way back into its column.</summary>
    public bool SlidingBack => _place == Place.SlidingBack;

    /// <summary>The card is stretched over the row and starts to slide out to it. This also turns round a
    /// card that is sliding back.</summary>
    public void SlideOut() => _place = Place.SlidingOut;

    /// <summary>The slide out has finished, and the card covers the whole row. Only a card that is still
    /// sliding out lands. A landing that arrives after the card was sent back or dropped changes
    /// nothing.</summary>
    public void Landed()
    {
        if (_place == Place.SlidingOut)
            _place = Place.Over;
    }

    /// <summary>The card starts to slide back into its column.</summary>
    public void SlideBack() => _place = Place.SlidingBack;

    /// <summary>The card is back in its column.</summary>
    public void InColumn() => _place = Place.InColumn;

    /// <summary>Whether the library card is drawn.</summary>
    /// <param name="cardShown">Whether the import card is shown at all. A card that is not shown covers
    /// nothing, wherever it is.</param>
    public bool LibraryDrawn(bool cardShown) => _place != Place.Over || !cardShown;
}
