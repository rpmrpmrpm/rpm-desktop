using Microsoft.UI.Xaml;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>What kind of stop an element is, for the look of the remote's focus (see
/// <see cref="FocusLift"/>).</summary>
public enum LiftPart
{
    /// <summary>A key or a row: every stop that is not marked. It grows by a few points and, without the
    /// focus, is drawn at <see cref="FocusLift.KeyLight"/>.</summary>
    None,

    /// <summary>A card: a record, an act, a genre, a list or a station in a row or on a wall. It grows by
    /// <see cref="FocusLift.CardGrow"/> and, without the focus, is drawn at
    /// <see cref="FocusLift.CardLight"/>. A line made around a card's button (RemoteLine.Around) takes the
    /// button's mark.</summary>
    Card,

    /// <summary>A stop that draws the focus itself: the album carousel's front cover, the artists' ladder
    /// of letters, radio's modes row. The host neither grows it nor dims it.</summary>
    Own,
}

/// <summary>
/// The look of the remote's focus in Pegasus, the Android TV app's (FOBO fd626f346 and 1f08936d3, then
/// 5122aa497; the TV's CardButton and deskFocus): the focused item grows about its middle and is drawn at
/// full brightness, and every other item on the screen in front is dimmed. There is no glow ring. A card
/// grows by <see cref="CardGrow"/> and the other cards stand at <see cref="CardLight"/>; a key or a row
/// grows by a few points (PegasusPlayer.Remote, RingGrowPoints) and the other keys and rows stand at
/// <see cref="KeyLight"/>, darker than a card. A focused key no larger than <see cref="GlowMost"/> on
/// either side (an icon key: the transport, the wall's sort) has one soft round glow behind it,
/// <see cref="GlowReach"/> past its edge. Nothing grows or dims until the remote is in use, so a screen
/// reached with the mouse is at full brightness. PegasusPlayer's focus tick draws all of it
/// (PegasusPlayer.Remote, OnRingFrame); the elements are only marked here.
///
/// <para><b>Marked by <see cref="PartProperty"/></b>: what kind of stop an element is. Unmarked stops are
/// keys and rows, which is most of them.</para>
///
/// <para><b>And by <see cref="FaceProperty"/></b>: for a stop that is an empty box laid over what it
/// stands for (home's banner plate, radio's banner card, the mini player, the tuner's dial), the element
/// that grows and dims in its place. An empty box grown or dimmed shows nothing.</para>
///
/// <para><b>Room for the grow.</b> On the Skia head a composition-scaled element is cut at its parent
/// panel's bounds (see CardRoomAround), so cards carry room round them. The round photograph of an act also
/// stands in a holder <see cref="RoomShare"/> of its side larger on every side, pulled back into place by a
/// negative margin, a room kept from the photograph zoom this look replaced.</para>
/// </summary>
public static class FocusLift
{
    /// <summary>How much a focused card grows: 8% (the TV's CARD_GROW).</summary>
    public const float CardGrow = 1.08f;

    /// <summary>How bright a card without the focus is drawn: 25% darker (the TV's CARD_LIGHT).</summary>
    public const double CardLight = 0.75;

    /// <summary>How bright a key or a row without the focus is drawn: ten points under a card (the TV's
    /// KEY_LIGHT, Chivan 2026-10-18: "dim other buttons even more").</summary>
    public const double KeyLight = CardLight - 0.10;

    /// <summary>The largest side of a key that glows when focused, and how far past its edge the glow
    /// reaches, before the view's scale (the TV's GLOW_MOST and GLOW_REACH).</summary>
    public const double GlowMost = 90, GlowReach = 26;

    /// <summary>The holder's room on each side of an act's round photograph, as a share of its side.</summary>
    public const double RoomShare = 0.07;

    public static readonly DependencyProperty PartProperty = DependencyProperty.RegisterAttached(
        "Part", typeof(LiftPart), typeof(FocusLift), new PropertyMetadata(LiftPart.None));

    public static LiftPart GetPart(DependencyObject element) => (LiftPart)element.GetValue(PartProperty);

    public static void SetPart(DependencyObject element, LiftPart value) => element.SetValue(PartProperty, value);

    /// <summary>The element that shows a stop's focus in its place: it grows while the stop has the focus
    /// and dims while it has not. Null, the default, for a stop that shows its own.</summary>
    public static readonly DependencyProperty FaceProperty = DependencyProperty.RegisterAttached(
        "Face", typeof(FrameworkElement), typeof(FocusLift), new PropertyMetadata(null));

    public static FrameworkElement? GetFace(DependencyObject element) => (FrameworkElement?)element.GetValue(FaceProperty);

    public static void SetFace(DependencyObject element, FrameworkElement? value) => element.SetValue(FaceProperty, value);

    /// <summary>The holder's margin for a photograph <paramref name="edge"/> across: its room, pulled
    /// back.</summary>
    public static Thickness HolderMargin(double edge) => new(-edge * RoomShare);

    /// <summary>The photograph's margin inside its holder: the room again, so it stands where it
    /// did.</summary>
    public static Thickness PhotoMargin(double edge) => new(edge * RoomShare);
}
