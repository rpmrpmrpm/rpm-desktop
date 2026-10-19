using Microsoft.UI.Xaml;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>What a part of a round artist card does under the remote's focus (see
/// <see cref="FocusLift"/>).</summary>
public enum LiftPart
{
    None,

    /// <summary>The card that takes the focus. Marks it as a card shown as the tvOS app shows an artist,
    /// rather than with the glow.</summary>
    Card,

    /// <summary>The round photograph, which zooms about its middle.</summary>
    Photo,

    /// <summary>A line under the photograph, which moves down by what the photograph grew.</summary>
    Below,
}

/// <summary>
/// An artist's card under the remote's focus, as the tvOS app shows it: the round photograph itself zooms,
/// the lines under it move down to keep their distance from it, and no glow is drawn round the card. The
/// tvOS app gets this from the system's lockup (RPM.tvOS ArtistCard: a borderless button with a circular
/// border shape and `.hoverEffect(.highlight)` on the photograph). Here the parts are marked with
/// <see cref="PartProperty"/> and PegasusPlayer's focus tick moves them (PegasusPlayer.Remote, LiftOf).
///
/// <para><b>Room for the zoom.</b> On the Skia head a composition-scaled element is cut at its parent
/// panel's bounds (see CardRoomAround). So the photograph stands in a holder <see cref="RoomShare"/> of its
/// side larger on every side, pulled back into place by a negative margin. The card's own corner is square,
/// so its presenter, which clips to a rounded corner, does not cut the zoomed photograph either.</para>
/// </summary>
public static class FocusLift
{
    /// <summary>How much the photograph zooms, about what tvOS lifts a lockup's picture by, where the card
    /// is small enough for it (see <see cref="ScaleFor"/>).</summary>
    public const float Scale = 1.12f;

    /// <summary>The most the photograph grows on each side, in points: less than the 22 between two cards,
    /// so the zoomed photograph stays clear of its neighbours and the lines it pushes down stay clear of
    /// the row under them. On a 2560 by 1440 screen a card is about 480 across, and 1.12 would grow it 29 a
    /// side. That overlaps the photograph beside it and covers the count under the card above.</summary>
    public const double MostGrowth = 18;

    /// <summary>The zoom for a photograph <paramref name="edge"/> across: <see cref="Scale"/>, or less
    /// where that would grow it more than <see cref="MostGrowth"/> a side. Pegasus's own grow is capped the
    /// same way (PegasusPlayer.Remote, RingGrowPoints).</summary>
    public static float ScaleFor(double edge) =>
        (float)Math.Min(Scale, 1 + (2 * MostGrowth / Math.Max(1, edge)));

    /// <summary>The holder's room on each side of the photograph, as a share of its side: more than the
    /// half of <see cref="Scale"/>'s growth that falls on each side.</summary>
    public const double RoomShare = 0.07;

    public static readonly DependencyProperty PartProperty = DependencyProperty.RegisterAttached(
        "Part", typeof(LiftPart), typeof(FocusLift), new PropertyMetadata(LiftPart.None));

    public static LiftPart GetPart(DependencyObject element) => (LiftPart)element.GetValue(PartProperty);

    public static void SetPart(DependencyObject element, LiftPart value) => element.SetValue(PartProperty, value);

    /// <summary>The holder's margin for a photograph <paramref name="edge"/> across: its room, pulled
    /// back.</summary>
    public static Thickness HolderMargin(double edge) => new(-edge * RoomShare);

    /// <summary>The photograph's margin inside its holder: the room again, so it stands where it
    /// did.</summary>
    public static Thickness PhotoMargin(double edge) => new(edge * RoomShare);
}
