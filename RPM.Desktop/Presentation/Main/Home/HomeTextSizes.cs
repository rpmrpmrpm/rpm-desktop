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
    public double Heading => 12 * _scale;

    // ── a card in a row: a record or a list ──

    public double CardTitle => 12 * _scale;

    /// <summary>The box the title sits in. A minimum rather than a fixed height, so a descender is never cut.</summary>
    public double CardTitleBox => 16 * _scale;

    public double CardLine => 11 * _scale;

    public double CardLineBox => 15 * _scale;

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
    public double Badge => 12 * _scale;

    // ── the arrow keys ──

    /// <summary>The keys at each end of a row, the lists and the quick selection.</summary>
    public double KeyWidth => 36 * _scale;

    public double KeyHeight => 44 * _scale;

    /// <summary>The arrow drawn in each of those keys.</summary>
    public double KeyMark => 15 * _scale;

    // ── the quick selection ──

    public double QuickTitle => 12.5 * _scale;

    public double QuickLine => 11 * _scale;

    /// <summary>The square beside each entry. It grows with the text so the two lines still fit beside it.</summary>
    public double QuickSquare => 48 * _scale;

    /// <summary>The kind's mark, drawn in the square when there is no picture.</summary>
    public double QuickMark => 15 * _scale;

    public double PlayAll => 11.5 * _scale;

    public double PlayAllMark => 10 * _scale;

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
    ];

    public event PropertyChangedEventHandler? PropertyChanged;
}
