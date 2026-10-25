using System.ComponentModel;
using Microsoft.UI.Xaml;
using RPM.Desktop.Presentation.Main.Library.Models;

namespace RPM.Desktop.Presentation.Main.Home;

/// <summary>
/// The text sizes on the home screen, all multiplied by one figure.
///
/// <para>The main window leaves <see cref="Scale"/> at 1. Pegasus sets it to the figure its mini player
/// is sized by (PegasusChrome.QueueScale, 1.5 on a 1440 high screen), so home's text scales the same
/// way as the player above it. Pegasus is read from a distance, and at 1 the headings and the quick
/// selection would be too small to read.</para>
///
/// <para>Pegasus also sets <see cref="Couch"/>, which swaps the desktop's figures for the Android TV
/// app's couch sizes (headings 22, a card's two lines 17 and 15, the quick selection's 19 and 16 beside a
/// 64 square, the banner's kicker 18). The TV states its sizes in the desktop's units (its <c>ts</c> is
/// a desktop figure times the scale), so each is the TV's number times <see cref="Scale"/>, as the
/// desktop's are. The main window never sets it, so its sizes are the ones written first below.</para>
///
/// <para>There is one instance per <see cref="HomeView"/>. It is never replaced, and every card the view
/// builds holds it: the row, list and quick cards through their
/// <see cref="Library.Models.SimilarTileMetrics"/>. A card realized later reads the current size, and a
/// change of scale is one write that every bound line follows. It is not a DependencyObject, so cards
/// can still be built on the worker that reads the shelf.</para>
///
/// <para>It holds the text, the E badge, the arrow keys, and what has to grow with the text to hold it:
/// the quick selection's square and the boxes under a cover. The covers in the rows grow by the same
/// figure, through the size each row's <see cref="SimilarTileMetrics"/> aims at. The banner, the drawer
/// under a row and the empty and locked panels are scaled as whole blocks instead, by a
/// <see cref="Common.ScaleBox"/> that reads <see cref="Scale"/>, so none of their sizes is here.</para>
/// </summary>
[RpmBindable]
public sealed partial class HomeTextSizes : INotifyPropertyChanged
{
    private double _scale = 1;

    /// <summary>What every size below is multiplied by.</summary>
    public double Scale
    {
        get => _scale;
        set
        {
            if (value <= 0 || Math.Abs(_scale - value) < 0.001)
                return;
            _scale = value;
            foreach (var name in Sizes)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

            // The tile metrics whose title room depends on the badge's size. See Follow.
            foreach (var metrics in Followers())
                metrics.TextScaled();
        }
    }

    private bool _couch;

    /// <summary>
    /// Whether the text is at the Android TV app's couch sizes, Pegasus's profile, rather than the
    /// desktop's. Each size below names both: the desktop's first, the TV's second. The TV made its
    /// text larger to be read from across a room (FOBO 6f08f6b7c, 77d2d7f03, 974baa2b5, 8fad30cd8),
    /// and Pegasus is read from the same distance.
    /// </summary>
    public bool Couch
    {
        get => _couch;
        set
        {
            if (_couch == value)
                return;
            _couch = value;
            foreach (var name in Sizes)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            foreach (var metrics in Followers())
                metrics.TextScaled();
        }
    }

    /// <summary>The desktop's figure or the TV's, at the scale.</summary>
    private double At(double desk, double couch) => (_couch ? couch : desk) * _scale;

    // ── who else is told about a change of scale ──

    private readonly List<WeakReference<SimilarTileMetrics>> _followers = [];

    /// <summary>
    /// Registers a row's tile metrics, whose cover and <see cref="SimilarTileMetrics.TitleRoom"/> (the
    /// cover less the badge's room) both change with the scale. Called by the metrics' constructor, so
    /// every metrics built against these sizes is told, whether or not the view still lists its row.
    /// </summary>
    /// <remarks>
    /// Held weakly, because this object lives as long as the view and a metrics object lives only as
    /// long as one reading of the shelf. A strong reference would keep every reading's metrics, and the
    /// bindings on them, alive for the life of the view. Dead entries are dropped on the next
    /// registration. Locked because a card's own default metrics can be built on the worker that reads
    /// the shelf. Those carry their own sizes object, but nothing here should depend on that.
    /// </remarks>
    internal void Follow(SimilarTileMetrics metrics)
    {
        lock (_followers)
        {
            _followers.RemoveAll(w => !w.TryGetTarget(out _));
            _followers.Add(new WeakReference<SimilarTileMetrics>(metrics));
        }
    }

    private List<SimilarTileMetrics> Followers()
    {
        lock (_followers)
            return [.. _followers.Select(w => w.TryGetTarget(out var m) ? m : null).OfType<SimilarTileMetrics>()];
    }

    // ── headings ──

    /// <summary>A section's name: "Quick selection", "Made for you", and each row's.</summary>
    public double Heading => At(12, 22);

    // ── a card in a row: a record or a list ──

    public double CardTitle => At(12, 17);

    /// <summary>The box the title sits in. A minimum rather than a fixed height, so a descender is never cut.</summary>
    public double CardTitleBox => At(16, 22);

    public double CardLine => At(11, 15);

    public double CardLineBox => At(15, 20);

    /// <summary>
    /// The row keys' margins. The bottom one is the height of the text under the cover, so the key,
    /// which stretches from the top of the row to that margin, is centred on the cover: the card's
    /// spacing of 7, the title's box, 7 again, the line's box.
    /// </summary>
    public Thickness RowKeyLeft => new(2, 0, 0, UnderSleeve);

    public Thickness RowKeyRight => new(0, 0, 2, UnderSleeve);

    private double UnderSleeve => 7 + CardTitleBox + 7 + CardLineBox;

    /// <summary>The E after a title, square. <see cref="SimilarTileMetrics.TitleRoom"/> keeps this
    /// much room free, plus the gap in front of it.</summary>
    public double Badge => At(12, 16);

    // ── the arrow keys ──

    /// <summary>The keys at each end of a row, the lists and the quick selection.</summary>
    public double KeyWidth => 36 * _scale;

    public double KeyHeight => 44 * _scale;

    /// <summary>The arrow drawn in each of those keys.</summary>
    public double KeyMark => 15 * _scale;

    // ── the quick selection ──

    public double QuickTitle => At(12.5, 19);

    public double QuickLine => At(11, 16);

    /// <summary>The square beside each entry. It grows with the text so the two lines still fit beside it.</summary>
    public double QuickSquare => At(48, 64);

    /// <summary>The kind's mark, drawn in the square when there is no picture.</summary>
    public double QuickMark => At(15, 20);

    public double PlayAll => At(11.5, 16);

    public double PlayAllMark => At(10, 14);

    // ── the banner ──
    //
    // Unscaled, unlike everything above: the banner is drawn larger by the ScaleBox it stands in (see
    // HomeView.FeatureScale), so a scaled size here would be scaled twice. Only Couch changes them. The
    // desktop's figures are the ones its markup had (the FeatureKicker and FeatureLine styles, the
    // station face's lines); the TV's are its banner's (Home.kt BannerFace and StationFace).

    /// <summary>The kicker over a card's title, the row it comes from.</summary>
    public double BannerKicker => _couch ? 18 : 11;

    /// <summary>The third line: the act's name or the line that puts the record forward.</summary>
    public double BannerLine => _couch ? 19 : 13.5;

    /// <summary>The height of that line's cell, and the most an act's lettering is drawn in it.</summary>
    public double BannerLogoHigh => _couch ? 34 : 26;

    public double BannerLogoWide => _couch ? 340 : 260;

    /// <summary>What a station is, on its card: two lines. The width keeps its proportion to the
    /// size, so StationBlurb.Longest still fits in the two lines.</summary>
    public double StationBlurb => _couch ? 18 : 13;

    public double StationBlurbLine => _couch ? 24 : 18;

    public double StationBlurbWide => _couch ? 420 : 300;

    /// <summary>What the tuned station is playing, under that.</summary>
    public double StationOn => _couch ? 16 : 11.5;

    public double StationOnWide => _couch ? 520 : 360;

    /// <summary>Every property that changes with <see cref="Scale"/>. Each is raised by name, one at a
    /// time, so nothing depends on how a binding engine reads an empty property name.</summary>
    private static readonly string[] Sizes =
    [
        nameof(Scale), nameof(Heading),
        nameof(CardTitle), nameof(CardTitleBox), nameof(CardLine), nameof(CardLineBox),
        nameof(RowKeyLeft), nameof(RowKeyRight), nameof(Badge),
        nameof(KeyWidth), nameof(KeyHeight), nameof(KeyMark),
        nameof(QuickTitle), nameof(QuickLine), nameof(QuickSquare), nameof(QuickMark),
        nameof(PlayAll), nameof(PlayAllMark),
        nameof(BannerKicker), nameof(BannerLine), nameof(BannerLogoHigh), nameof(BannerLogoWide),
        nameof(StationBlurb), nameof(StationBlurbLine), nameof(StationBlurbWide),
        nameof(StationOn), nameof(StationOnWide),
    ];

    public event PropertyChangedEventHandler? PropertyChanged;
}
