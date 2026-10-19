using RPM.Core.Art;

namespace RPM.Desktop.Presentation.Main.Home;

/// <summary>
/// Layout numbers for the home screen's featured banner: the plate in front and the two neighbour
/// cards partly hidden behind it, computed from the width of the strip that holds them.
///
/// <para>Kept out of <c>HomeView</c> so the two edge cases can be unit tested: a strip too narrow to
/// show neighbours, and a strip so wide that the plate has to stop growing.</para>
/// </summary>
internal static class HomeBanner
{
    /// <summary>The plate's height. The neighbours' height, the photo crop (<see cref="Aspect"/>) and
    /// the scene are all derived from it.
    ///
    /// <para>On a short window this pushes the smart-list row below it out of view, so that row is
    /// reached by scrolling.</para></summary>
    public const double Height = 352;

    /// <summary>The square album cover on the plate.
    ///
    /// <para>Also set in <c>HomeView.xaml</c>, because a size inside a DataTemplate cannot read this
    /// constant. Change both.</para></summary>
    public const double Sleeve = 124;

    /// <summary>How much shorter the neighbours are than the plate. Without it the three cards look
    /// like three banners side by side.</summary>
    public const double PeekDrop = 22;

    /// <summary>The share of the strip the plate takes when neighbours are shown, before
    /// <see cref="Widest"/> applies.</summary>
    public const double Share = 0.68;

    /// <summary>The widest the plate is drawn. Beyond this the cover and text keep their position and
    /// the extra width is photo.</summary>
    public const double Widest = 760;

    // There is no minimum plate width: when the strip is too narrow for neighbours the plate takes the
    // whole strip (see LeastPeek and PlateWidth), so a minimum could never apply.

    /// <summary>How far the plate overlaps each neighbour.</summary>
    public const double Bite = 24;

    /// <summary>
    /// The narrowest a neighbour may be and still be drawn. Below this the strip shows no neighbours.
    /// </summary>
    /// <remarks>
    /// Equal to the cover size: a neighbour narrower than that reads as a cut-off sliver, not a card.
    ///
    /// <para>When there is no room for neighbours the plate fills the whole strip. The next card
    /// slides in from outside the strip. Use the arrow keys or the dots to move between cards.</para>
    /// </remarks>
    public const double LeastPeek = Sleeve;

    /// <summary>The padding at the plate's edges and between the cover and the text.</summary>
    public const double Margin = 22;

    /// <summary>
    /// How far from the plate's left edge the cover and text reach. The photo crop keeps faces to the
    /// right of this.
    ///
    /// <para>The cover width is fixed but the title length is not, so the text is estimated as a third
    /// of the plate. The 0.72 cap keeps some clear photo on a narrow plate.</para>
    /// </summary>
    public static double Written(double plate) =>
        Math.Min(plate * 0.72, Margin + Sleeve + Margin + (plate * 0.3));

    /// <summary>The width the cover and its margins take at the plate's left.</summary>
    public static double SleeveBand => Margin + Sleeve + Margin;

    /// <summary>
    /// Where the photo starts inside the plate. Zero: the photo covers the whole plate, with a blurred
    /// copy of the same photo behind it (see the background in <c>HomeView.xaml</c>).
    ///
    /// <para>Kept as a named value because <see cref="Slot"/>, <see cref="Aspect"/> and
    /// <see cref="Scene"/> are measured from it, and the markup uses the same value. Change both.</para>
    /// </summary>
    public static double PictureLeft => 0;

    /// <summary>
    /// The width available to the photo: the whole plate. An unmeasured plate (0) counts as
    /// <see cref="Widest"/>.
    /// </summary>
    public static double Slot(double plate) =>
        Math.Max(1, (plate > 0 ? plate : Widest) - PictureLeft);

    /// <summary>
    /// The plate described for the photo crop: what covers the left, what covers the right, and the
    /// overall shape. <see cref="ArtScene.FocusAcross"/> uses it to place the photo's subject clear of
    /// the text and away from the right edge.
    /// </summary>
    public static ArtScene Scene(double plate)
    {
        var wide = plate > 0 ? plate : Widest;
        var slot = Slot(wide);

        // The photo covers the whole plate, so the cover sits on top of it. Written() already includes
        // the cover, its margins and the title, which is everything covering the left side.
        //
        // The step keys in the bottom-right corner are small, so the right limit is the plate edge less
        // a margin, which keeps a face from being cut by the edge.
        var written = Math.Clamp(Written(wide) - PictureLeft, 0, slot * 0.72);

        return ArtScene.FromPanel(slot, Height, written, slot - Margin, slot);
    }

    /// <summary>
    /// How many of an artist's photos are ranked to pick the banner photo.
    ///
    /// <para>Each one has to be downloaded to know its shape, and the face detector runs on the widest
    /// few. Six photos are enough to choose from, and three cards need at most eighteen downloads.
    /// If the player has already ranked this artist's photos, that saved order is used and nothing
    /// is downloaded here.</para>
    /// </summary>
    public const int Considered = 6;

    /// <summary>How many photos from the top of the ranked list a card tries when downloads fail.
    /// After that the card shows no photo.</summary>
    public const int Tried = 3;

    /// <summary>How far the strip moves on one step, as a share of the plate width.</summary>
    public const double SlideShare = 0.3;

    /// <summary>The minimum step distance. Below this, a step on a narrow pane is too small to see
    /// as movement.</summary>
    public const double LeastSlide = 60;

    /// <summary>The maximum step distance. Above this, a step on a wide pane takes too long.</summary>
    public const double MostSlide = 180;

    /// <summary>
    /// How far the strip moves on one step. The cards change immediately and slide in from the side
    /// of the step, so the user sees one movement and not three cards changing place.
    /// </summary>
    /// <remarks>
    /// Do not move a card a full card width here. The three cards are drawn in three hosts of different
    /// widths, each with its own clip, and a full-width move takes a card outside its host's drawn
    /// area. The transition between cards is handled with opacity instead; see <c>HomeView.Step</c>.
    /// </remarks>
    public static double Slide(double strip) =>
        Math.Clamp(PlateWidth(strip) * SlideShare, LeastSlide, MostSlide);

    /// <summary>
    /// The aspect ratio the saliency crop cuts a photo to for a plate of this width.
    ///
    /// <para>Uses the photo slot's shape so the crop matches what is drawn and the image is not cropped
    /// a second time. An unmeasured plate counts as <see cref="Widest"/>, which is the width in
    /// most windows.</para>
    /// </summary>
    public static double Aspect(double plate) => Slot(plate) / Height;

    /// <summary>Duration of the step slide, in milliseconds. Kept short because it responds to a key
    /// press and may be repeated several times in a row.</summary>
    public const int SlideMs = 220;

    /// <summary>
    /// Duration of the fade from the old card to the new one, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Longer than <see cref="SlideMs"/>, so the movement ends first and the image finishes a moment
    /// later. With the same duration as the slide, the change looks instant.
    /// </remarks>
    public const int FadeMs = 340;

    // Neighbours are drawn at full opacity with no dark overlay. Transparency lets the window
    // background show through, and a dark overlay on top of the card's own text scrim makes the left
    // neighbour almost black. PeekDrop and the plate's overlap are what make a neighbour look behind
    // the plate. See PeekLeft in HomeView.xaml.

    // The outgoing card fades in place and the strip moves both cards together. Moving the outgoing
    // card on its own draws its edge across the incoming card during the move. The two opacities
    // cross-fade over an opaque backing, so the window background never shows through mid-fade.
    // See HomeView.Step.

    /// <summary>
    /// How long a card stays before the banner steps to the next one, in milliseconds.
    /// </summary>
    /// <remarks>
    /// Seven seconds is enough to read the title, the artist and the reason line. The countdown
    /// restarts after every step, including manual ones, so the banner does not step again just
    /// after a key press.
    /// </remarks>
    public const int DwellMs = 7000;

    /// <summary>Duration of the fade-in for an artist photo once it has been downloaded, cropped and
    /// decoded, in milliseconds. The photo arrives seconds after the card is drawn, and showing it
    /// instantly would change the card suddenly. Longer than <see cref="SlideMs"/> because it does not
    /// respond to user input.</summary>
    public const int PhotoMs = 420;

    /// <summary>The plate width when neighbours are shown: <see cref="Share"/> of the strip, capped at
    /// <see cref="Widest"/>.</summary>
    private static double Shared(double strip) => Math.Min(strip * Share, Widest);

    /// <summary>The space left on each side of that plate, before the overlap.</summary>
    private static double SideRoom(double strip) => Math.Max(0, (strip - Shared(strip)) / 2);

    /// <summary>
    /// Whether the strip shows neighbours. Both <see cref="PlateWidth"/> and <see cref="PeekWidth"/>
    /// use this one check, so the plate cannot widen while neighbours are still drawn.
    /// </summary>
    public static bool Neighbours(double strip) => strip > 0 && SideRoom(strip) >= LeastPeek;

    /// <summary>The plate width for a strip of this width: <see cref="Shared"/> when neighbours are
    /// shown, otherwise the whole strip up to <see cref="Widest"/>. Never wider than the
    /// strip.</summary>
    public static double PlateWidth(double strip) =>
        strip <= 0 ? 0
        : Neighbours(strip) ? Shared(strip)
        : Math.Min(strip, Widest);

    /// <summary>The space for one neighbour, including the part under the plate's overlap.</summary>
    private static double Room(double strip) => SideRoom(strip) + Bite;

    /// <summary>
    /// The width of each neighbour's visible cut, or 0 when no neighbours are shown.
    /// </summary>
    /// <remarks>
    /// Capped at the plate width. A neighbour is a card as wide as the plate, shown through this cut,
    /// so a wider cut shows bare background next to the card. That happens once the plate reaches
    /// <see cref="Widest"/> and the strip keeps growing: for example, a 2,291 wide strip gives 789 of
    /// room for a 760 wide card. The leftover goes to <see cref="PeekInset"/>.
    /// </remarks>
    public static double PeekWidth(double strip) =>
        Neighbours(strip) ? Math.Min(Room(strip), PlateWidth(strip)) : 0;

    /// <summary>
    /// How far each neighbour is moved in from the strip's outer edge: the space left after the cut
    /// from <see cref="PeekWidth"/>.
    /// </summary>
    /// <remarks>
    /// The extra space goes on the outer side. A gap on the inner side, next to the plate, would show
    /// as a seam in the middle of the strip.
    /// </remarks>
    public static double PeekInset(double strip) =>
        Neighbours(strip) ? Math.Max(0, Room(strip) - PlateWidth(strip)) : 0;

    /// <summary>The neighbours' height.</summary>
    public static double PeekHeight => Height - PeekDrop;
}
