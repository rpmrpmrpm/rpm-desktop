using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>The spinning turntable record. It is split out of <c>ArtDisplay</c> so that the shell can host
/// it above the content, over the grids. The rest of the artwork stays behind it. ArtDisplay still decides
/// where the record is and how fast it turns. It does this through the parts exposed here.
///
/// <para>What the record wears is a different matter, and it is not exposed at all. The four layers that
/// make up a face are private. The one method that writes them is private. The only code that can call it
/// is <see cref="Turntable"/>, which is nested inside this class. That is what makes "only" true, and not
/// just agreed. A record's face changes where nobody can see it change. The way to keep a rule like that is
/// to leave nothing else a way to break it.</para>
/// </summary>
public sealed partial class RecordDisc : UserControl
{
    public RecordDisc()
    {
        InitializeComponent();
    }

    /// <summary>The disc's Viewbox. ArtDisplay sets its Width, Height and Margin to place it half off the
    /// right edge, centred on the band.</summary>
    public Viewbox Box => RecordBox;

    /// <summary>The soft halo behind the disc. It is sized and placed with the disc.</summary>
    public Grid ShadowHalo => RecordShadow;

    /// <summary>The rotation that the playback ticker drives.</summary>
    public RotateTransform Rotate => RecordRotate;

    /// <summary>The layer that actually turns: the label and the disc art. These are the off-centre parts
    /// that make a spin visible. The rings above them are round and unchanging, and they show no rotation
    /// at all.
    /// </summary>
    /// <remarks>It is handed out so that the compositor can turn it, instead of this thread. See
    /// <see cref="Common.Spin"/>. <see cref="Rotate"/> is the same rotation written as a XAML transform. It
    /// is what the Skia head still writes each frame. Only one of the two is in use at a time.
    /// </remarks>
    public FrameworkElement Turning => RotatingLayer;

    // ── who owns this record's face ────────────────────────────────────────────────────────────────
    //
    // There are two cases, and they follow different rules.
    //
    // A record in the HEADER is flown. It lifts off to the corner, changes what it wears up there, and
    // comes back down. Its face may change only while it is away. Turntable is for that, and it is why
    // nothing else can reach the layers.
    //
    // A record on a full SCREEN (the album page, the now-playing view) never leaves its place. There is no
    // away for it. So its pictures arrive where they are, and its owner dissolves them in on its own
    // timing. That is Standing.
    //
    // A disc is one or the other, never both. Asking for one makes the claim. A second owner that tries is
    // a programming error, not a state to recover from.

    private object? _owner;

    private void Claim(object by)
    {
        if (_owner is not null && !ReferenceEquals(_owner, by))
            throw new InvalidOperationException(
                "this record already has an owner: a disc is either flown by a Turntable or stood on a screen, never both");
        _owner = by;
    }

    private Standing? _standing;

    /// <summary>The record as a screen that never takes it off shows it. See the note above. The first use
    /// claims the disc. So a disc that a <see cref="Turntable"/> is flying cannot also be written by
    /// hand.</summary>
    public Standing Stand => _standing ??= new Standing(this);

    /// <summary>The face of a record that stays where it is. The pressing and the label are handed over as
    /// themselves, because their owner animates them. The disc art is set through <see cref="ShowCd"/>.
    ///
    /// <para>This is the OTHER door, not a way round the first one. A screen-sized record is not hiding
    /// anything while it changes. It is the picture on the page. So a dissolve there is the intended
    /// design, and the timing belongs to the screen that drew it.</para>
    ///
    /// <para>The disc art is set by a method, not by an image, because three layers change together. The
    /// drawn label goes away, and a whole CD wears <see cref="CdOverlay"/>. If the image were handed out, a
    /// caller could show a CD without its overlay.</para>
    /// </summary>
    public sealed class Standing
    {
        private readonly RecordDisc _disc;

        internal Standing(RecordDisc disc)
        {
            disc.Claim(this);
            _disc = disc;
            Vinyl = disc.Vinyl;
            Label = disc.LabelArt;
        }

        /// <summary>The vinyl itself. It is a rendered pressing, tinted for each record. See
        /// VinylPressing.</summary>
        public Image Vinyl { get; }

        /// <summary>The album art shown on the drawn label when there is no full disc art.</summary>
        public Image Label { get; }

        /// <summary>The disc art showing now, or null.</summary>
        public ImageSource? CdPicture => _disc.DiscArt.Source;

        /// <summary>Shows the album's own picture of its disc, or takes it down when
        /// <paramref name="picture"/> is null. The drawn label goes away while a picture is
        /// shown.</summary>
        /// <param name="wholeDisc">True when the picture is the whole disc. It then wears the CD overlay.
        /// False for a vinyl label. See <see cref="Services.Art.DiscStamp.Covers"/>.</param>
        public void ShowCd(ImageSource? picture, bool wholeDisc)
        {
            _disc.DiscArt.Source = picture;
            _disc.DiscArt.Opacity = picture is null ? 0 : 1;
            _disc.LabelHost.Opacity = picture is null ? 1 : 0;
            _disc.CdDress.Opacity = picture is not null && wholeDisc ? 1 : 0;
        }
    }

    /// <summary>Puts a whole face on at once: the pressing, the album art on the label, and the
    /// photographed CD over both. Or the drawn record comes back when this album has no picture.
    ///
    /// <para>This is private. It is called from one place only: <see cref="Turntable.Dress"/>. That is
    /// reached only through the <see cref="Deck.Off"/> branch of <see cref="RecordChange"/>. The changes
    /// are plain assignments, with no dissolves. Nothing needs dissolving while the record is off screen.
    /// There is also nothing on these layers to dissolve from. Nothing else animates them, so no layer is
    /// held at a value that an assignment cannot reset.</para>
    /// </summary>
    private void PutOn(ImageSource vinyl, BitmapImage? label, ScannedDisc? cd)
    {
        Vinyl.Source = vinyl;

        // The label wears the sleeve, or falls back to the rpm stamp pressed underneath it.
        LabelArt.Source = label;
        LabelArt.Opacity = label is null ? 0 : 1;

        // The album's own picture of its record goes over the drawn one. When there is none, its source is
        // left where it is. A picture under an opacity of 0 costs nothing. Clearing it would blank the disc
        // for a frame the next time this album comes round.
        if (cd is { } worn)
            DiscArt.Source = worn.Picture;
        DiscArt.Opacity = cd is null ? 0 : 1;
        LabelHost.Opacity = cd is null ? 1 : 0;

        // How much of the drawn record the picture replaces. This is the whole reason ScannedDisc exists.
        //
        // A photographed CD covers the whole face, and it takes the pressing with it. This is not just
        // tidiness. Opacity is applied to each layer in turn, not to the record as one body. So as soon as
        // the disc is less than fully opaque (which is the whole of its flight to the corner), every layer
        // under the CD shows through it. The record's colour comes up through the picture as the disc
        // shrinks away. The fix is to have nothing under the CD for it to come through.
        //
        // A vinyl LABEL is a picture of the middle third and nothing else. The pressing is the record the
        // label is stuck to. If the pressing were taken away, the label would turn in mid-air over the
        // disc's own shadow.
        Vinyl.Opacity = cd is { CoversRecord: true } ? 0 : 1;

        // A whole CD wears the even rim, the hub and the spindle. A label does not, because it sits on the
        // pressing, which has its own hole.
        CdDress.Opacity = cd is { CoversRecord: true } ? 1 : 0;
    }
}
