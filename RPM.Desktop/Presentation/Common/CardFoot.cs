using Microsoft.UI.Xaml;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// What a page's bottom edge is standing on, and the corners that follow from the answer.
///
/// <para>A page drawn inside one of the shell's cards uses the card's rounding at the corners that it
/// shares with the card. That is correct for the foot only while the card's foot is the page's own
/// foot. The library's message band takes that edge whenever it has something to say (see
/// MainPage.SyncLibraryBand). A page that kept its rounded corner there drew the card's bottom left
/// corner a band's height above the card's edge, with the band's square top showing underneath.</para>
///
/// <para>So the corner is not a fixed value in the markup. It is set in code, from the one fact that
/// decides it, at the one place that knows whether the band is shown.</para>
/// </summary>
public enum CardFoot
{
    /// <summary>Nothing is under the page. Its foot is the card's foot, and it is rounded like the
    /// card.</summary>
    CardEdge,

    /// <summary>A message band sits between the page and the card's bottom edge. The band has the
    /// rounded corners now, and the page ends square on top of the band.</summary>
    Band,
}

/// <summary>The corners a page wears inside a card, given what its foot is standing on.</summary>
public static class CardCorners
{
    /// <summary>The rounding of the shell's cards (ContentBorderStyle). Every page inside a card uses this
    /// value.</summary>
    public const double Radius = 8;

    /// <summary>Rounded on the left and square on the right. This is the shape of a page in the right-hand
    /// card, whose right edge is the rail's edge, not the card's edge. Only the foot corner varies. The
    /// head is always the card's rounding, whatever is below it.</summary>
    public static CornerRadius RoundedLeft(CardFoot foot)
        => new(Radius, 0, 0, foot == CardFoot.CardEdge ? Radius : 0);
}
